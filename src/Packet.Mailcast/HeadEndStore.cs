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
/// state.txt    Key: value lines: Bid, Title, Size, FirstSeen, Object, Dictionary, Oti, NextEsi (a count of pieces sent), EsiOrder, EsiStation and EsiFrequency (shuffled bulletins, issue #143),
///              FirstSlot once it has been carried, and EveningNextEsi once an evening slot has sent some of it (pieces counted
///              from <see cref="BroadcastScheduler.EveningFirstCount"/>, apart from NextEsi)
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
/// <para>And one file per object relayed from another station (issue #218), <c>relayed/OBJECTID.txt</c>, with its Day, FirstSlot,
/// and every ESI sent (Sent, hex), so that a restart never repeats one. An envelope object relayed with a bulletin has a file of its own, shared by every bulletin that links it. A bulletin this head end takes later that was relayed first carries on past every ESI relayed, and one it forgets leaves such a file behind (with EveningNext, its evening count, when evening slots sent some), so neither ever starts again from 0.</para>
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
    private readonly HashSet<string> _neverStartedLogged = new(StringComparer.OrdinalIgnoreCase);

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
        _relay.TryGetValue(objectId, out var r) ? new RelayProgress(new HashSet<uint>(r.Sent), r.FirstSlot) : RelayProgress.None;

    /// <summary>A relayed object's sent-state goes this many days after the last piece sent, whether or not anything still offers the object.</summary>
    public const int RelayStateMaxDays = 30;

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
    /// <see cref="ScheduleOptions.MaxBulletinSize"/>. <paramref name="takenIn"/> is when it was taken in (issue #234), the start of
    /// <paramref name="seen"/> if left out; it is nudged on by a millisecond at a time until no other bulletin has the same, so that
    /// the order bulletins were taken in is always kept.
    /// </summary>
    public CarriedBulletin? Offer(Bulletin bulletin, DateOnly seen, DateTimeOffset? takenIn = null)
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
        var shuffle = _options.Station is { } station ? EsiShuffle.For(station, transfer.ObjectId, _options.Details?.FrequencyHz) : null;
        // Relayed before it was taken (it came from the receivers first, or was forgotten and is back): carry on past every ESI sent,
        // with its first slot, so its age is not lost and none of its own fresh pieces is repeated. The count was kept under the order
        // in force when it was relayed; if the station's callsign or frequency has changed since, the new order avoids the old pieces
        // only by chance (about one in 16 million a piece), which costs at most a wasted frame.
        uint nextEsi = 0;
        DateTimeOffset? firstSlot = null;
        if (_relay.TryGetValue(transfer.ObjectId, out var relayed))
        {
            // Only this station's own fresh pieces move the count; another station's ESIs sent unchanged would unmap to anywhere.
            nextEsi = relayed.NextFresh;
            firstSlot = relayed.FirstSlot;
        }
        // And past any evening piece it sent before it was forgotten (kept apart: the ESIs sent may include other stations' too).
        uint eveningNext = relayed?.EveningNext ?? 0;
        // An own envelope counts up from a first ESI; start at the lowest one whose run of K pieces holds none already sent.
        uint envelopeNext = 0;
        if (envelopeObject is not null && _relay.TryGetValue(envelopeObject.ObjectId, out var relayedEnvelope))
        {
            for (bool moved = true; moved;)
            {
                moved = false;
                foreach (uint esi in relayedEnvelope.Sent.Where(e => e >= envelopeNext && e < envelopeNext + (uint)envelopeObject.SourceSymbols))
                {
                    envelopeNext = esi + 1;
                    moved = true;
                }
            }
        }
        var taken = new DateTimeOffset(((takenIn ?? BroadcastScheduler.Midnight(seen)).UtcTicks / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        while (_entries.Values.Any(e => e.Carried.TakenAt == taken))
        {
            taken = taken.AddMilliseconds(1);
        }
        var carried = new CarriedBulletin(shared.Bid, shared.Title, serialized.Length, seen, transfer, nextEsi, firstSlot, envelopeObject, envelopeNext, shuffle, bulletin.From, taken, eveningNext);
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
        if (relayed is not null)
        {
            // Its ESIs are in the bulletin's state now.
            File.Delete(RelayPath(transfer.ObjectId));
            _relay.Remove(transfer.ObjectId);
        }
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
    public IReadOnlyList<CarriedBulletin> InRotation(DateTimeOffset slot)
    {
        var inRotation = new List<CarriedBulletin>();
        foreach (var c in _entries.Values.Select(e => e.Carried))
        {
            if (!BroadcastScheduler.InRotation(c, slot, _options))
            {
                if (_options.Budget is not null && BroadcastScheduler.NeverStarted(c, slot)
                    && slot - BroadcastScheduler.UnstartedSince(c) < TimeSpan.FromHours(BroadcastScheduler.LeftoverHours) + TimeSpan.FromMinutes(_options.SlotMinutes)
                    && _neverStartedLogged.Add(c.Bid))
                {
                    _log?.Invoke($"retired unsent: {c.Bid} from {c.From ?? "unknown"} was taken in {BroadcastScheduler.UnstartedSince(c).UtcDateTime:yyyy-MM-dd HH:mm}Z and not started within {BroadcastScheduler.LeftoverHours} h");
                }
                continue;
            }
            inRotation.Add(c);
        }
        return inRotation;
    }

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
        // An evening slot's bulletin pieces are counted from EveningFirstCount, kept apart from the daylight count.
        var evening = new Dictionary<ulong, uint>();
        var bulletinIds = broadcast.Objects.Where(o => o.Bid is not null).Select(o => o.Transfer.ObjectId).ToHashSet();
        // A shuffled bulletin (issue #143) moves on by count, not by ESI: the count behind each frame sent.
        var shuffles = broadcast.Objects.Where(o => o.Shuffle is not null && o.Bid is not null).ToDictionary(o => o.Transfer.ObjectId, o => o.Shuffle!);
        foreach (var frame in broadcast.Frames.Take(framesSent))
        {
            uint count = shuffles.TryGetValue(frame.ObjectId, out var shuffle) ? shuffle.Unmap(frame.EncodingSymbolId) : frame.EncodingSymbolId;
            var into = bulletinIds.Contains(frame.ObjectId) && count >= BroadcastScheduler.EveningFirstCount ? evening : next;
            uint after = (into == evening ? count - BroadcastScheduler.EveningFirstCount : count) + 1;
            if (!into.TryGetValue(frame.ObjectId, out uint known) || after > known)
            {
                into[frame.ObjectId] = after;
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
        var sentFrames = broadcast.Frames.Take(framesSent).ToList();
        foreach (var r in broadcast.Relayed)
        {
            RecordRelayed(r.ObjectId, sentFrames, broadcast, r.Fresh);
            if (r.Envelope is { } relayEnvelope)
            {
                RecordRelayed(relayEnvelope.ObjectId, sentFrames, broadcast, fresh: true);
            }
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
            if (!_entries.TryGetValue(KeyOf(o.Bid), out var entry) || entry.Carried.Transfer.ObjectId != o.Transfer.ObjectId)
            {
                continue;
            }
            if (evening.TryGetValue(o.Transfer.ObjectId, out uint eveningNext) && eveningNext > entry.Carried.EveningNextEsi)
            {
                // Evening pieces move their own count only: never NextEsi or FirstSlot, which the daylight strategy works from.
                entry.Carried = entry.Carried with { EveningNextEsi = eveningNext };
                SaveState(entry);
            }
            if (!next.TryGetValue(o.Transfer.ObjectId, out uint nextEsi))
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
    public int Expire(DateOnly today, Func<ulong, bool>? stillOffered = null)
    {
        int removed = 0;
        foreach (var (key, entry) in _entries.ToList())
        {
            if (today.DayNumber - entry.Carried.FirstSeen.DayNumber >= _options.RememberDays)
            {
                // Leave its sent ESIs behind, so that if it comes back (offered again, or relayed from a receiver) it never starts from 0.
                if (entry.Carried.NextEsi > 0 || entry.Carried.EveningNextEsi > 0)
                {
                    var kept = _relay.TryGetValue(entry.Carried.Transfer.ObjectId, out var had) ? had : new RelayEntry { ObjectId = entry.Carried.Transfer.ObjectId, Day = today };
                    kept.Last = today;
                    kept.NextFresh = Math.Max(kept.NextFresh, entry.Carried.NextEsi);
                    for (uint n = 0; n < entry.Carried.NextEsi; n++)
                    {
                        kept.Sent.Add(entry.Carried.EsiOf(n));
                    }
                    for (uint n = 0; n < entry.Carried.EveningNextEsi; n++)
                    {
                        kept.Sent.Add(entry.Carried.EsiOf(BroadcastScheduler.EveningFirstCount + n));
                    }
                    kept.FirstSlot ??= entry.Carried.FirstSlot;
                    kept.EveningNext = Math.Max(kept.EveningNext, entry.Carried.EveningNextEsi);
                    _relay[kept.ObjectId] = kept;
                    SaveRelay(kept);
                }
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
            // A relayed object's sent-state stays as long as anything still offers the object (the catalogue, so a receiver), so it
            // can never be relayed again from scratch; past the remembering days it goes only once nobody offers it.
            // Or, whatever is offered, once nothing has been sent of it for RelayStateMaxDays.
            if (today.DayNumber - r.Last.DayNumber >= RelayStateMaxDays
                || (today.DayNumber - r.Day.DayNumber >= _options.RememberDays && stillOffered?.Invoke(id) != true))
            {
                File.Delete(RelayPath(id));
                _relay.Remove(id);
            }
        }
        return removed;
    }

    // The ESIs of an object (a relayed one or a relayed envelope) among the frames sent, added to what is remembered of it.
    private void RecordRelayed(ulong objectId, List<MailcastFrame> sentFrames, SlotBroadcast broadcast, bool fresh)
    {
        var sent = sentFrames.Where(f => f.ObjectId == objectId).Select(f => f.EncodingSymbolId).ToList();
        if (sent.Count == 0)
        {
            return;
        }
        if (!_relay.TryGetValue(objectId, out var entry))
        {
            _relay[objectId] = entry = new RelayEntry { ObjectId = objectId, Day = broadcast.Date };
        }
        entry.Sent.UnionWith(sent);
        entry.FirstSlot ??= broadcast.Slot;
        entry.Last = broadcast.Date;
        if (fresh)
        {
            var shuffle = _options.Station is { } station ? EsiShuffle.For(station, objectId, _options.Details?.FrequencyHz) : null;
            entry.NextFresh = Math.Max(entry.NextFresh, sent.Max(esi => shuffle?.Unmap(esi) ?? esi) + 1);
        }
        SaveRelay(entry);
    }

    private sealed class RelayEntry
    {
        public ulong ObjectId;
        public DateOnly Day;
        public DateTimeOffset? FirstSlot;
        public DateOnly Last;

        /// <summary>The count after the highest of this station's own fresh pieces (in its scrambled order) sent; unchanged ESIs of another station do not move it.</summary>
        public uint NextFresh;

        /// <summary>For a bulletin of this head end's that was forgotten, how many evening pieces it had sent (counted from <see cref="BroadcastScheduler.EveningFirstCount"/>).</summary>
        public uint EveningNext;
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
        text.Append(CultureInfo.InvariantCulture, $"Last: {r.Last:yyyy-MM-dd}\n");
        text.Append(CultureInfo.InvariantCulture, $"NextFresh: {r.NextFresh}\n");
        if (r.EveningNext > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"EveningNext: {r.EveningNext}\n");
        }
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
                Last = fields.TryGetValue("Last", out var lastText) ? DateOnly.ParseExact(lastText, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateOnly.ParseExact(fields["Day"], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                NextFresh = fields.TryGetValue("NextFresh", out var freshText) ? uint.Parse(freshText, CultureInfo.InvariantCulture) : 0,
                EveningNext = fields.TryGetValue("EveningNext", out var eveningText) ? uint.Parse(eveningText, CultureInfo.InvariantCulture) : 0,
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
        if (c.From is not null)
        {
            text.Append("From: ").Append(c.From).Append('\n');
        }
        if (c.TakenIn is DateTimeOffset takenIn)
        {
            text.Append(CultureInfo.InvariantCulture, $"TakenIn: {takenIn.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}\n");
        }
        text.Append("Object: ").Append(ObjectId.Format(c.Transfer.ObjectId)).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"Dictionary: {c.Transfer.DictionaryId}\n");
        text.Append("Oti: ").Append(Convert.ToHexStringLower(c.Transfer.Oti.ToBytes())).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"NextEsi: {c.NextEsi}\n");
        if (c.Shuffle is { } shuffle)
        {
            // NextEsi counts pieces sent; the ESI sent is the keyed shuffle of the count. The callsign and frequency are kept so
            // that a later change of either never changes the order of a bulletin part sent.
            text.Append("EsiOrder: shuffled\n");
            text.Append("EsiStation: ").Append(shuffle.Station).Append('\n');
            if (shuffle.FrequencyHz is { } hz)
            {
                text.Append(CultureInfo.InvariantCulture, $"EsiFrequency: {hz}\n");
            }
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
        if (c.EveningNextEsi > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"EveningNextEsi: {c.EveningNextEsi}\n");
        }
        DurableFile.WriteAtomically(Path.Combine(entry.Dir, "state.txt"), Bulletin.TextEncoding.GetBytes(text.ToString()));
    }

    // The sender of a bulletin held from before the state kept it, from the object itself (as the relay does), so that a held
    // digest of a bulk sender is bulk from the first slot. Null if the object is not a readable bulletin.
    private string? SenderOf(TransferObject transfer)
    {
        try
        {
            var (kind, content) = TransferObject.Unpack(transfer.Bytes, transfer.DictionaryId, _compression);
            return kind == ObjectKind.Bulletin ? Bulletin.Parse(content).From : null;
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or FormatException)
        {
            return null;
        }
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
                // A bulletin taken before the order was keyed by frequency too has no EsiFrequency, and keeps its old order.
                long? esiHz = fields.TryGetValue("EsiFrequency", out var hzText) ? long.Parse(hzText, NumberStyles.None, CultureInfo.InvariantCulture) : null;
                shuffle = EsiShuffle.For(fields["EsiStation"], transfer.ObjectId, esiHz);
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
                shuffle,
                fields.GetValueOrDefault("From") ?? SenderOf(transfer),
                fields.TryGetValue("TakenIn", out var takenText)
                    ? DateTimeOffset.ParseExact(takenText, "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                    : null,
                fields.TryGetValue("EveningNextEsi", out var eveningText) ? uint.Parse(eveningText, CultureInfo.InvariantCulture) : 0);
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
