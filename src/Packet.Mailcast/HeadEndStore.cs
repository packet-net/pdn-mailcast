using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mailcast.RaptorQ;

namespace Packet.Mailcast;

/// <summary>
/// The head end's memory of the bulletins in rotation. A bulletin is compressed and coded once,
/// when it is first offered, and the same object is sent in every later slot, continuing from
/// the next unsent ESI. Nothing is ever compressed twice, so a change of compression
/// settings or dictionary cannot make the pieces of one bulletin disagree.
/// </summary>
/// <remarks>
/// <para>One folder per bulletin under <c>bulletins/</c>, named from a hash of its BID in capitals:</para>
/// <code>
/// object.bin   the object's octets, exactly as coded
/// envelope.bin the envelope object's octets, for a bulletin first offered since issue #142 (none for an older one)
/// state.txt    Key: value lines: Bid, Title, Size, FirstSeen, Object, Dictionary, Oti, NextEsi (a count of pieces sent), EsiOrder and EsiStation (shuffled bulletins, issue #143),
///              and FirstSlot once it has been carried
/// </code>
/// <para>
/// A bulletin held from before the canonical form keeps its encoding and has no envelope; it is never coded again.
/// </para>
/// <para>
/// A state without FirstSlot but with pieces sent comes from a daily head end before slots had
/// times. Under the shares rule it counts as first carried at midnight UTC on its FirstSeen day;
/// under the budget rule it has no first slot yet, so it is back in rotation from its NextEsi and
/// its next slot is its first.
/// </para>
/// <para>And one file per directory object sent, <c>directories/OBJECTID.txt</c>, with its Day and
/// NextEsi, so a later plan with the same directory object does not repeat its pieces.</para>
/// <para>
/// Both are written to a temporary name and renamed, object first, so a folder whose state is
/// missing or does not match its object is an interrupted first offer and is discarded.
/// </para>
/// <para>Not thread-safe: use one store from one thread.</para>
/// </remarks>
public sealed class HeadEndStore
{
    private readonly string _bulletins;
    private readonly Compression _compression;
    private readonly ScheduleOptions _options;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly string _directories;
    private readonly Dictionary<ulong, (DateOnly Day, uint NextEsi)> _directoryEsis = [];
    private readonly string _relayed;
    private readonly Dictionary<ulong, RelayEntry> _relay = [];

    /// <summary>Opens or creates a store.</summary>
    public HeadEndStore(string root, Compression compression, ScheduleOptions? options = null, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(compression);
        _compression = compression;
        _options = options ?? new ScheduleOptions();
        _log = log;
        _bulletins = Path.Combine(root, "bulletins");
        Directory.CreateDirectory(_bulletins);
        foreach (var dir in Directory.EnumerateDirectories(_bulletins))
        {
            foreach (var tmp in Directory.EnumerateFiles(dir, "*.tmp"))
            {
                File.Delete(tmp);
            }
            var entry = Load(dir);
            if (entry is null)
            {
                _log?.Invoke($"head end store: discarding incomplete {Path.GetFileName(dir)}");
                Directory.Delete(dir, recursive: true);
                continue;
            }
            _entries[Path.GetFileName(dir)] = entry;
        }
        _directories = Path.Combine(root, "directories");
        Directory.CreateDirectory(_directories);
        foreach (var file in Directory.EnumerateFiles(_directories))
        {
            if (file.EndsWith(".tmp", StringComparison.Ordinal))
            {
                File.Delete(file);
                continue;
            }
            if (LoadDirectory(file) is { } d)
            {
                _directoryEsis[d.ObjectId] = (d.Day, d.NextEsi);
            }
        }
        _relayed = Path.Combine(root, "relayed");
        Directory.CreateDirectory(_relayed);
        foreach (var file in Directory.EnumerateFiles(_relayed))
        {
            if (file.EndsWith(".tmp", StringComparison.Ordinal))
            {
                File.Delete(file);
                continue;
            }
            if (LoadRelay(file) is { } r)
            {
                _relay[r.ObjectId] = r;
            }
        }
    }

    /// <summary>Whether this head end carries the object itself: a bulletin's object or envelope, whether in rotation or not.</summary>
    public bool CarriesObject(ulong objectId) =>
        _entries.Values.Any(e => e.Carried.Transfer.ObjectId == objectId || e.Carried.Envelope?.ObjectId == objectId);

    /// <summary>What has been sent of an object relayed from another station (issue #218); <see cref="RelayProgress.None"/> for one never sent.</summary>
    public RelayProgress GetRelayProgress(ulong objectId) =>
        _relay.TryGetValue(objectId, out var r) ? new RelayProgress(new HashSet<uint>(r.Sent), r.FirstSlot, r.EnvelopeNextEsi) : RelayProgress.None;

    /// <summary>How many relayed objects have something remembered.</summary>
    public int RelayCount => _relay.Count;

    /// <summary>Whether a bulletin with this BID is remembered, regardless of case.</summary>
    public bool Holds(string bid)
    {
        ArgumentException.ThrowIfNullOrEmpty(bid);
        return _entries.ContainsKey(KeyOf(bid));
    }

    /// <summary>
    /// The first unsent ESI of a directory object: 0 for one never sent. Pass this method to
    /// <see cref="BroadcastScheduler.Plan(IEnumerable{CarriedBulletin}, DateOnly, int, Compression, ScheduleOptions?, Func{ulong, uint}?)"/>
    /// so that a later plan with the same directory object does not repeat its pieces either.
    /// </summary>
    public uint DirectoryNextEsi(ulong objectId) =>
        _directoryEsis.TryGetValue(objectId, out var d) ? d.NextEsi : 0;

    /// <summary>How many bulletins the store remembers.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Offers a bulletin. The first time a BID is offered it is compressed, coded and kept, with
    /// <paramref name="seen"/> as its first-seen day and no first slot yet, as a canonical bulletin with its envelope (issue #142); later offers of the same BID return what
    /// was kept, unchanged, whatever their content. Returns null if the bulletin is over
    /// <see cref="ScheduleOptions.MaxBulletinSize"/>.
    /// </summary>
    public CarriedBulletin? Offer(Bulletin bulletin, DateOnly seen)
    {
        ArgumentNullException.ThrowIfNull(bulletin);
        string key = KeyOf(bulletin.Bid);
        if (_entries.TryGetValue(key, out var existing))
        {
            return existing.Carried;
        }
        // First offer: the canonical bulletin and the envelope, from the message text exactly as the BBS gave it.
        Bulletin shared = bulletin;
        BulletinEnvelope? envelope = null;
        if (!bulletin.Canonical)
        {
            (shared, envelope) = CanonicalForm.ToCanonicalBulletin(bulletin.Type, bulletin.From, bulletin.To, bulletin.At, bulletin.Bid, bulletin.Title,
                Bulletin.TextEncoding.GetBytes(bulletin.MessageText));
        }
        var serialized = shared.Serialize();
        if (serialized.Length > _options.MaxBulletinSize)
        {
            return null;
        }
        var transfer = TransferObject.ForBulletin(shared, _options.DictionaryId, _compression, MailcastFrame.FixedSymbolSize, MailcastFrame.StandardAlignment);
        var envelopeObject = envelope is null ? null : TransferObject.ForEnvelope(envelope, _options.DictionaryId, _compression);
        // A bulletin taken now is sent in this station's own piece order (issue #143); with no station named, in sequence.
        var shuffle = _options.Station is { } station ? EsiShuffle.For(station, transfer.ObjectId) : null;
        var carried = new CarriedBulletin(shared.Bid, shared.Title, serialized.Length, seen, transfer, 0, Envelope: envelopeObject, Shuffle: shuffle);
        var dir = Path.Combine(_bulletins, key);
        Directory.CreateDirectory(dir);
        DurableFile.WriteAtomically(Path.Combine(dir, "object.bin"), transfer.Bytes);
        if (envelopeObject is not null)
        {
            DurableFile.WriteAtomically(Path.Combine(dir, "envelope.bin"), envelopeObject.Bytes);
        }
        var entry = new Entry(dir, carried);
        SaveState(entry);
        _entries[key] = entry;
        return carried;
    }

    /// <summary>The bulletins in rotation in the slot at midnight UTC on <paramref name="today"/>.</summary>
    public IReadOnlyList<CarriedBulletin> InRotation(DateOnly today) => InRotation(BroadcastScheduler.Midnight(today));

    /// <summary>
    /// The bulletins in rotation in the slot starting at <paramref name="slot"/>: those not yet
    /// carried, and those still in their carrying slots (under the budget rule, those not yet
    /// retired for good, the leftover tier included: their tier follows from NextEsi and
    /// FirstSlot, so a restart puts each back where it was). Pass them to
    /// <see cref="BroadcastScheduler.Plan(IEnumerable{CarriedBulletin}, DateTimeOffset, int, Compression, ScheduleOptions?, Func{ulong, uint}?, SlotBudget?, string?)"/>.
    /// </summary>
    public IReadOnlyList<CarriedBulletin> InRotation(DateTimeOffset slot) =>
        _entries.Values
            .Select(e => e.Carried)
            .Where(c => BroadcastScheduler.InRotation(c, slot, _options))
            .ToList();

    /// <summary>Records that a plan's frames have been sent, so the next plan continues with fresh ESIs.</summary>
    public void Commit(SlotBroadcast broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        Commit(broadcast, broadcast.Frames.Count);
    }

    /// <summary>
    /// Records that the first <paramref name="framesSent"/> of a plan's frames have been sent (or
    /// may have been: a frame handed to the modem counts). Each object moves on past the highest ESI
    /// of its own among them, so nothing sent is ever sent again, and what was not sent is still
    /// owed: the next plan sends it on top of that slot's share. A bulletin with a frame among
    /// them that had no first slot gets this one.
    /// </summary>
    public void Commit(SlotBroadcast broadcast, int framesSent)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        ArgumentOutOfRangeException.ThrowIfNegative(framesSent);
        var next = new Dictionary<ulong, uint>();
        // A shuffled bulletin (issue #143) moves on by count, not by ESI: the count behind each frame sent.
        var shuffles = broadcast.Objects.Where(o => o.Shuffle is not null && o.Bid is not null).ToDictionary(o => o.Transfer.ObjectId, o => o.Shuffle!);
        foreach (var frame in broadcast.Frames.Take(framesSent))
        {
            uint after = (shuffles.TryGetValue(frame.ObjectId, out var shuffle) ? shuffle.Unmap(frame.EncodingSymbolId) : frame.EncodingSymbolId) + 1;
            if (!next.TryGetValue(frame.ObjectId, out uint known) || after > known)
            {
                next[frame.ObjectId] = after;
            }
        }
        foreach (ulong id in broadcast.ExtraObjects)
        {
            // An extra (the propagation reading): kept by object ID, like the directory.
            if (next.TryGetValue(id, out uint extraNext) && extraNext > DirectoryNextEsi(id))
            {
                _directoryEsis[id] = (broadcast.Date, extraNext);
                SaveDirectory(id, broadcast.Date, extraNext);
            }
        }
        foreach (var r in broadcast.Relayed)
        {
            var sent = broadcast.Frames.Take(framesSent).Where(f => f.ObjectId == r.ObjectId).Select(f => f.EncodingSymbolId).ToList();
            uint envelopeNext = r.Envelope is { } envelope && next.TryGetValue(envelope.ObjectId, out uint reached) ? reached : 0;
            if (sent.Count == 0 && envelopeNext == 0)
            {
                continue;
            }
            if (!_relay.TryGetValue(r.ObjectId, out var entry))
            {
                _relay[r.ObjectId] = entry = new RelayEntry { ObjectId = r.ObjectId, Day = broadcast.Date };
            }
            entry.Sent.UnionWith(sent);
            entry.FirstSlot ??= sent.Count > 0 ? broadcast.Slot : null;
            entry.EnvelopeNextEsi = Math.Max(entry.EnvelopeNextEsi, envelopeNext);
            SaveRelay(entry);
        }
        foreach (var o in broadcast.Objects)
        {
            if (o.EnvelopeFor is not null)
            {
                // Every bulletin holding this envelope moves on, since they share its one object ID and its ESIs.
                if (next.TryGetValue(o.Transfer.ObjectId, out uint envelopeNext))
                {
                    foreach (var holder in _entries.Values.Where(e => e.Carried.Envelope?.ObjectId == o.Transfer.ObjectId && envelopeNext > e.Carried.EnvelopeNextEsi).ToList())
                    {
                        holder.Carried = holder.Carried with { EnvelopeNextEsi = envelopeNext };
                        SaveState(holder);
                    }
                }
                continue;
            }
            if (o.Bid is null)
            {
                // The directory: kept by object ID, which is a hash of its content.
                ulong id = o.Transfer.ObjectId;
                if (next.TryGetValue(id, out uint directoryNext) && directoryNext > DirectoryNextEsi(id))
                {
                    _directoryEsis[id] = (broadcast.Date, directoryNext);
                    SaveDirectory(id, broadcast.Date, directoryNext);
                }
                continue;
            }
            if (!next.TryGetValue(o.Transfer.ObjectId, out uint nextEsi)
                || !_entries.TryGetValue(KeyOf(o.Bid), out var entry) || entry.Carried.Transfer.ObjectId != o.Transfer.ObjectId)
            {
                continue;
            }
            if (nextEsi > entry.Carried.NextEsi || entry.Carried.FirstSlot is null)
            {
                entry.Carried = entry.Carried with
                {
                    NextEsi = Math.Max(nextEsi, entry.Carried.NextEsi),
                    FirstSlot = entry.Carried.FirstSlot ?? broadcast.Slot,
                };
                SaveState(entry);
            }
        }
    }

    /// <summary>
    /// Forgets bulletins first seen <see cref="ScheduleOptions.RememberDays"/> or more days
    /// before <paramref name="today"/>. Returns how many.
    /// </summary>
    public int Expire(DateOnly today)
    {
        int removed = 0;
        foreach (var (key, entry) in _entries.ToList())
        {
            if (today.DayNumber - entry.Carried.FirstSeen.DayNumber >= _options.RememberDays)
            {
                Directory.Delete(entry.Dir, recursive: true);
                _entries.Remove(key);
                removed++;
            }
        }
        foreach (var (id, d) in _directoryEsis.ToList())
        {
            if (today.DayNumber - d.Day.DayNumber >= _options.RememberDays)
            {
                File.Delete(DirectoryPath(id));
                _directoryEsis.Remove(id);
            }
        }
        foreach (var (id, r) in _relay.ToList())
        {
            if (today.DayNumber - r.Day.DayNumber >= _options.RememberDays)
            {
                File.Delete(RelayPath(id));
                _relay.Remove(id);
            }
        }
        return removed;
    }

    private sealed class RelayEntry
    {
        public ulong ObjectId;
        public DateOnly Day;
        public DateTimeOffset? FirstSlot;
        public uint EnvelopeNextEsi;
        public readonly HashSet<uint> Sent = [];
    }

    private string RelayPath(ulong objectId) => Path.Combine(_relayed, ObjectId.Format(objectId) + ".txt");

    // Day, first slot, the envelope's next ESI and every ESI sent (hex, sorted): so a restart never repeats one.
    private void SaveRelay(RelayEntry r)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Day: {r.Day:yyyy-MM-dd}\n");
        if (r.FirstSlot is DateTimeOffset first)
        {
            text.Append(CultureInfo.InvariantCulture, $"FirstSlot: {first.UtcDateTime:yyyy-MM-ddTHH:mmZ}\n");
        }
        text.Append(CultureInfo.InvariantCulture, $"EnvelopeNextEsi: {r.EnvelopeNextEsi}\n");
        text.Append("Sent: ").Append(string.Join(',', r.Sent.Order().Select(e => e.ToString("x", CultureInfo.InvariantCulture)))).Append('\n');
        DurableFile.WriteAtomically(RelayPath(r.ObjectId), Bulletin.TextEncoding.GetBytes(text.ToString()));
    }

    private RelayEntry? LoadRelay(string path)
    {
        try
        {
            if (!ObjectId.TryParse(Path.GetFileNameWithoutExtension(path), out ulong id))
            {
                return null;
            }
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Bulletin.TextEncoding.GetString(File.ReadAllBytes(path)).Split('\n'))
            {
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0)
                {
                    fields[line[..colon]] = line[(colon + 2)..];
                }
            }
            var entry = new RelayEntry
            {
                ObjectId = id,
                Day = DateOnly.ParseExact(fields["Day"], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                EnvelopeNextEsi = uint.Parse(fields["EnvelopeNextEsi"], CultureInfo.InvariantCulture),
                FirstSlot = fields.TryGetValue("FirstSlot", out var slotText)
                    ? DateTimeOffset.ParseExact(slotText, "yyyy-MM-ddTHH:mmZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                    : null,
            };
            foreach (string esi in fields["Sent"].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                entry.Sent.Add(uint.Parse(esi, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            }
            return entry;
        }
        catch (Exception e) when (e is FormatException or KeyNotFoundException or OverflowException or IOException)
        {
            _log?.Invoke($"head end store: unreadable relay state {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    private string DirectoryPath(ulong objectId) => Path.Combine(_directories, ObjectId.Format(objectId) + ".txt");

    private void SaveDirectory(ulong objectId, DateOnly day, uint nextEsi) =>
        DurableFile.WriteAtomically(DirectoryPath(objectId), Bulletin.TextEncoding.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"Day: {day:yyyy-MM-dd}\nNextEsi: {nextEsi}\n")));

    private (ulong ObjectId, DateOnly Day, uint NextEsi)? LoadDirectory(string path)
    {
        try
        {
            if (!ObjectId.TryParse(Path.GetFileNameWithoutExtension(path), out ulong id))
            {
                return null;
            }
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Bulletin.TextEncoding.GetString(File.ReadAllBytes(path)).Split('\n'))
            {
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0)
                {
                    fields[line[..colon]] = line[(colon + 2)..];
                }
            }
            return (id, DateOnly.ParseExact(fields["Day"], "yyyy-MM-dd", CultureInfo.InvariantCulture), uint.Parse(fields["NextEsi"], CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is FormatException or KeyNotFoundException or OverflowException or IOException)
        {
            _log?.Invoke($"head end store: unreadable directory state {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    private static string KeyOf(string bid) =>
        Convert.ToHexStringLower(SHA256.HashData(Bulletin.TextEncoding.GetBytes(bid.ToUpperInvariant()))[..8]);

    private static void SaveState(Entry entry)
    {
        var c = entry.Carried;
        var text = new StringBuilder();
        text.Append("Bid: ").Append(c.Bid).Append('\n');
        text.Append("Title: ").Append(c.Title).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"Size: {c.Size}\n");
        text.Append(CultureInfo.InvariantCulture, $"FirstSeen: {c.FirstSeen:yyyy-MM-dd}\n");
        text.Append("Object: ").Append(ObjectId.Format(c.Transfer.ObjectId)).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"Dictionary: {c.Transfer.DictionaryId}\n");
        text.Append("Oti: ").Append(Convert.ToHexStringLower(c.Transfer.Oti.ToBytes())).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"NextEsi: {c.NextEsi}\n");
        if (c.Shuffle is { } shuffle)
        {
            // NextEsi counts pieces sent; the ESI sent is the keyed shuffle of the count. The callsign is kept so that a
            // later change of the station's callsign never changes the order of a bulletin part sent.
            text.Append("EsiOrder: shuffled\n");
            text.Append("EsiStation: ").Append(shuffle.Station).Append('\n');
        }
        if (c.Envelope is { } envelope)
        {
            text.Append("Envelope: ").Append(ObjectId.Format(envelope.ObjectId)).Append('\n');
            text.Append("EnvelopeOti: ").Append(Convert.ToHexStringLower(envelope.Oti.ToBytes())).Append('\n');
            text.Append(CultureInfo.InvariantCulture, $"EnvelopeNextEsi: {c.EnvelopeNextEsi}\n");
        }
        if (c.FirstSlot is DateTimeOffset firstSlot)
        {
            text.Append(CultureInfo.InvariantCulture, $"FirstSlot: {firstSlot.UtcDateTime:yyyy-MM-ddTHH:mmZ}\n");
        }
        DurableFile.WriteAtomically(Path.Combine(entry.Dir, "state.txt"), Bulletin.TextEncoding.GetBytes(text.ToString()));
    }

    private Entry? Load(string dir)
    {
        var statePath = Path.Combine(dir, "state.txt");
        var objectPath = Path.Combine(dir, "object.bin");
        if (!File.Exists(statePath) || !File.Exists(objectPath))
        {
            return null;
        }
        try
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Bulletin.TextEncoding.GetString(File.ReadAllBytes(statePath)).Split('\n'))
            {
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0)
                {
                    fields[line[..colon]] = line[(colon + 2)..];
                }
            }
            var oti = ObjectTransmissionInformation.Read(Convert.FromHexString(fields["Oti"]));
            var transfer = TransferObject.FromStored(File.ReadAllBytes(objectPath), ushort.Parse(fields["Dictionary"], CultureInfo.InvariantCulture), oti);
            if (ObjectId.Format(transfer.ObjectId) != fields["Object"])
            {
                return null;
            }
            var firstSeen = DateOnly.ParseExact(fields["FirstSeen"], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            uint nextEsi = uint.Parse(fields["NextEsi"], CultureInfo.InvariantCulture);
            DateTimeOffset? firstSlot = fields.TryGetValue("FirstSlot", out var slotText)
                ? DateTimeOffset.ParseExact(slotText, "yyyy-MM-ddTHH:mmZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                : nextEsi > 0 && _options.Budget is null ? BroadcastScheduler.Midnight(firstSeen) : null;
            TransferObject? envelope = null;
            uint envelopeNextEsi = 0;
            var envelopePath = Path.Combine(dir, "envelope.bin");
            if (fields.ContainsKey("Envelope") && !File.Exists(envelopePath))
            {
                _log?.Invoke($"head end store: envelope of {fields["Bid"]} does not match; sent without it");
            }
            else if (fields.TryGetValue("Envelope", out var envelopeId))
            {
                var envelopeOti = ObjectTransmissionInformation.Read(Convert.FromHexString(fields["EnvelopeOti"]));
                var stored = TransferObject.FromStored(File.ReadAllBytes(envelopePath), ushort.Parse(fields["Dictionary"], CultureInfo.InvariantCulture), envelopeOti);
                if (ObjectId.Format(stored.ObjectId) != envelopeId)
                {
                    _log?.Invoke($"head end store: envelope of {fields["Bid"]} does not match; sent without it");
                }
                else
                {
                    envelope = stored;
                    envelopeNextEsi = uint.Parse(fields["EnvelopeNextEsi"], CultureInfo.InvariantCulture);
                }
            }
            EsiShuffle? shuffle = null;
            if (fields.TryGetValue("EsiOrder", out var order) && order == "shuffled")
            {
                shuffle = EsiShuffle.For(fields["EsiStation"], transfer.ObjectId);
            }
            var carried = new CarriedBulletin(
                fields["Bid"],
                fields["Title"],
                int.Parse(fields["Size"], CultureInfo.InvariantCulture),
                firstSeen,
                transfer,
                nextEsi,
                firstSlot,
                envelope,
                envelopeNextEsi,
                shuffle);
            return new Entry(dir, carried);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or KeyNotFoundException or OverflowException)
        {
            _log?.Invoke($"head end store: unreadable state in {Path.GetFileName(dir)}: {e.Message}");
            return null;
        }
    }

    private sealed class Entry(string dir, CarriedBulletin carried)
    {
        public string Dir { get; } = dir;

        public CarriedBulletin Carried { get; set; } = carried;
    }
}
