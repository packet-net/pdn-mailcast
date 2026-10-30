using System.Globalization;

namespace Packet.Mailcast;

/// <summary>A canonical bulletin that is complete but not yet delivered, waiting for its station's envelope (issue #145).</summary>
/// <param name="ObjectId">The bulletin's object ID.</param>
/// <param name="Bulletin">The canonical bulletin as received.</param>
/// <param name="Since">When it was completed.</param>
public sealed record AwaitingBulletin(ulong ObjectId, Bulletin Bulletin, DateTimeOffset Since);

public sealed partial class ReceiverStore
{
    private const string AwaitingFolder = "awaiting";

    private sealed record AwaitingItem(ulong ObjectId, Bulletin Bulletin, string LongHash, DateTimeOffset Since)
    {
        /// <summary>The stations seen listing this bulletin in a usable directory.</summary>
        public HashSet<string> Listed { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Bulletins delivered from the wait since the caller last took them (see <see cref="TakeDelivered"/>).</summary>
    private readonly List<Bulletin> _delivered = [];

    /// <summary>How long a canonical bulletin waits for an envelope before it is delivered bare whatever else is true.</summary>
    public static TimeSpan EnvelopeWait => KnownDirectoriesRetention;

    /// <summary>Takes the bulletins delivered from the wait since the last call: they are in the outbox, not newly completed.</summary>
    public IReadOnlyList<Bulletin> TakeDelivered()
    {
        var taken = _delivered.ToArray();
        _delivered.Clear();
        return taken;
    }

    private readonly Dictionary<ulong, AwaitingItem> _awaiting = [];

    /// <summary>Frames heard per object and per station, for choosing whose envelope to use (issue #145).</summary>
    private readonly Dictionary<ulong, Dictionary<string, int>> _heardFrames = [];

    /// <summary>Canonical bulletins that are complete and waiting for an envelope, oldest first.</summary>
    public IReadOnlyList<AwaitingBulletin> Awaiting() =>
        [.. _awaiting.Values.OrderBy(a => a.Since).ThenBy(a => a.ObjectId).Select(a => new AwaitingBulletin(a.ObjectId, a.Bulletin, a.Since))];

    private string AwaitingPath(ulong objectId) => Path.Combine(_root, AwaitingFolder, ObjectId.Format(objectId));

    private void CountFrame(ulong objectId, string? station)
    {
        if (station is null || _done.ContainsKey(objectId))
        {
            return;
        }
        if (!_heardFrames.TryGetValue(objectId, out var counts))
        {
            _heardFrames[objectId] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
        }
        counts[station] = counts.GetValueOrDefault(station) + 1;
    }

    /// <summary>
    /// A complete canonical bulletin: delivered at once if nothing is worth waiting for, else held until an
    /// envelope arrives or it stops being listed.
    /// </summary>
    private AcceptResult Await(Instance instance, Bulletin bulletin, string longHash)
    {
        var now = _options.Time.GetUtcNow();
        var item = new AwaitingItem(instance.ObjectId, bulletin, longHash, now);
        foreach (var d in StationDirectories().Where(d => d.Directory.Find(instance.ObjectId) is not null))
        {
            item.Listed.Add(d.Station);
        }
        System.IO.Directory.CreateDirectory(Path.Combine(_root, AwaitingFolder));
        DurableFile.WriteAtomically(AwaitingPath(instance.ObjectId), EncodeAwaiting(item), now, _options.FlushToDisk);
        _awaiting[instance.ObjectId] = item;
        MarkDone(instance, longHash);
        var now2 = Release(null, null, instance.ObjectId);
        if (now2.Count > 0)
        {
            return new AcceptResult(FrameOutcome.CompletedBulletin, instance.ObjectId, Bulletin: now2[0]);
        }
        MailChanged();
        Log($"bulletin {bulletin.Bid} ({ObjectId.Format(instance.ObjectId)}) is complete, waiting for its station's details");
        return new AcceptResult(FrameOutcome.AwaitingEnvelope, instance.ObjectId, Bulletin: bulletin);
    }

    private byte[] EncodeAwaiting(AwaitingItem item)
    {
        string frames = _heardFrames.TryGetValue(item.ObjectId, out var counts)
            ? string.Join(';', counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + "=" + c.Value.ToString(CultureInfo.InvariantCulture)))
            : "";
        string listed = string.Join(';', item.Listed.OrderBy(l => l, StringComparer.Ordinal));
        var head = Bulletin.TextEncoding.GetBytes(item.Since.ToString("o", CultureInfo.InvariantCulture) + "\n" + item.LongHash + "\n" + frames + "\n" + listed + "\n");
        return [.. head, .. item.Bulletin.Serialize()];
    }

    private void LoadAwaiting()
    {
        string dir = Path.Combine(_root, AwaitingFolder);
        if (!System.IO.Directory.Exists(dir))
        {
            return;
        }
        foreach (var file in System.IO.Directory.EnumerateFiles(dir))
        {
            if (!ObjectId.TryParse(Path.GetFileName(file), out ulong id))
            {
                continue;
            }
            if (_outboxItems.ContainsKey(OutboxName(id)))
            {
                TryDelete(file); // already delivered; the file is left from a crash between the two steps
                continue;
            }
            try
            {
                var bytes = File.ReadAllBytes(file);
                int p = 0;
                var parts = new string[4];
                for (int i = 0; i < 4; i++)
                {
                    int nl = Array.IndexOf(bytes, (byte)'\n', p);
                    if (nl < 0)
                    {
                        throw new FormatException("cut short");
                    }
                    parts[i] = Bulletin.TextEncoding.GetString(bytes, p, nl - p);
                    p = nl + 1;
                }
                var bulletin = Bulletin.Parse(bytes.AsSpan(p));
                var since = DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var loaded = new AwaitingItem(id, bulletin, parts[1], since);
                foreach (var station in parts[3].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    loaded.Listed.Add(station);
                }
                _awaiting[id] = loaded;
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var pair in parts[2].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.LastIndexOf('=');
                    if (eq > 0 && int.TryParse(pair[(eq + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                    {
                        counts[pair[..eq]] = n;
                    }
                }
                if (counts.Count > 0)
                {
                    _heardFrames[id] = counts;
                }
                if (!_done.ContainsKey(id))
                {
                    WriteDone(id, parts[1]);
                }
            }
            catch (Exception e) when (e is FormatException or IOException or ArgumentException)
            {
                Log($"awaiting file {Path.GetFileName(file)} unreadable, removed: {e.Message}");
                TryDelete(file);
            }
        }
    }

    /// <summary>
    /// <see cref="Release"/>, the bulletins delivered being queued for <see cref="TakeDelivered"/>.
    /// </summary>
    private void ReleaseAwaiting(BroadcastDirectory? heard, DirectoryCheck? check) => _delivered.AddRange(Release(heard, check, null));

    /// <summary>
    /// Delivers the waiting canonical bulletins (or just <paramref name="only"/>) that should go now:
    /// those whose envelope is here; those a directory lists but no trusted directory that lists them links
    /// an envelope for; those no station that listed them lists any more (judged only when
    /// <paramref name="heard"/> is a directory that can speak for its station); and those that have waited
    /// <see cref="EnvelopeWait"/>.
    /// </summary>
    private List<Bulletin> Release(BroadcastDirectory? heard, DirectoryCheck? check, ulong? only)
    {
        var released = new List<Bulletin>();
        if (_awaiting.Count == 0)
        {
            return released;
        }
        var now = _options.Time.GetUtcNow();
        string? heardStation = heard is not null && check is not null ? StationOf(heard, check) : null;
        bool canSpeak = false;
        if (heardStation is not null)
        {
            DateOnly latest = LatestPlausibleDate(now);
            canSpeak = heard!.Date <= latest && heard.Date >= NewestDirectoryDate(latest, heardStation);
        }
        var current = canSpeak || only is not null ? StationDirectories() : [];
        foreach (var item in _awaiting.Values.Where(a => only is null || a.ObjectId == only).OrderBy(a => a.Since).ThenBy(a => a.ObjectId).ToList())
        {
            if (canSpeak && heard!.Find(item.ObjectId) is not null && item.Listed.Add(heardStation!))
            {
                DurableFile.WriteAtomically(AwaitingPath(item.ObjectId), EncodeAwaiting(item), now, _options.FlushToDisk);
            }
            string? why = null;
            var listing = UsableDirectories().Where(d => d.Directory.Find(item.ObjectId) is not null).ToList();
            if (EnvelopesFor(item.ObjectId).Count > 0)
            {
                why = "its envelope arrived";
            }
            else if (listing.Count > 0 && !listing.Any(d => d.Check.State == SignatureState.Trusted && d.Directory.Find(item.ObjectId)!.Envelope is not null))
            {
                why = "no signed directory offers an envelope for it";
            }
            else if (canSpeak && item.Listed.Count > 0 && item.Listed.All(l => StationDirectories().FirstOrDefault(d => d.Station == l) is { } d && d.Directory.Find(item.ObjectId) is null))
            {
                why = "no directory lists it any more";
            }
            else if (now - item.Since >= EnvelopeWait)
            {
                why = $"no envelope came in {(int)EnvelopeWait.TotalHours} hours";
            }
            if (why is not null)
            {
                released.Add(DeliverAwaiting(item, why));
            }
        }
        return released;
    }

    /// <summary>Delivers the waiting bulletins that have waited too long (run from <see cref="Expire"/> and when the store opens).</summary>
    private void ReleaseExpiredAwaiting()
    {
        var now = _options.Time.GetUtcNow();
        foreach (var item in _awaiting.Values.Where(a => now - a.Since >= EnvelopeWait).OrderBy(a => a.Since).ThenBy(a => a.ObjectId).ToList())
        {
            _delivered.Add(DeliverAwaiting(item, $"no envelope came in {(int)EnvelopeWait.TotalHours} hours"));
        }
    }

    /// <summary>
    /// Delivers a waiting canonical bulletin at once (the status page's "Deliver now"), with an envelope if
    /// one has arrived, else bare. Null if no such bulletin is waiting.
    /// </summary>
    public Bulletin? DeliverNow(ulong objectId) =>
        _awaiting.TryGetValue(objectId, out var item) ? DeliverAwaiting(item, "asked for from the web page") : null;

    private Bulletin DeliverAwaiting(AwaitingItem item, string why)
    {
        var canonical = item.Bulletin;
        var envelopes = EnvelopesFor(item.ObjectId);
        _heardFrames.TryGetValue(item.ObjectId, out var counts);
        int Heard(string station) => counts is not null && counts.TryGetValue(station, out int n) ? n : 0;

        var now = _options.Time.GetUtcNow();
        string? via = null;
        var date = item.Since;
        string text = canonical.MessageText;
        string title = canonical.Title;
        bool withEnvelope = false;
        if (envelopes.Count > 0)
        {
            var (station, envelope) = envelopes.OrderByDescending(e => Heard(e.Station)).ThenBy(e => e.Station, StringComparer.Ordinal).First();
            date = RoutingTime(envelope.Top.Span) ?? date;
            try
            {
                var (rebuilt, fullTitle) = CanonicalForm.FromCanonicalBulletin(canonical, envelope);
                text = Bulletin.TextEncoding.GetString(rebuilt);
                title = fullTitle;
                withEnvelope = true;
                via = station;
            }
            catch (ArgumentException e)
            {
                Log($"bulletin {canonical.Bid}: envelope of {station} does not rebuild it ({e.Message}); delivered bare");
            }
        }
        if (via is null)
        {
            var listing = StationDirectories().Where(d => d.Directory.Find(item.ObjectId) is not null).Select(d => d.Station);
            via = (counts?.Keys ?? Enumerable.Empty<string>()).Concat(listing).Distinct(StringComparer.Ordinal)
                .OrderByDescending(Heard).ThenBy(s => s, StringComparer.Ordinal).FirstOrDefault();
        }

        string call = _options.DeliveryCall?.Invoke() ?? "";
        string rLine = "R:" + now.UtcDateTime.ToString("yyMMdd/HHmm", CultureInfo.InvariantCulture) + "Z"
            + (call.Length > 0 ? " @:" + call : "")
            + " [pdn-mailcast" + (via is null ? "" : " via " + via) + "]";
        string shown = title.Replace('\r', ' ').Replace('\n', ' ');
        Bulletin delivered;
        try
        {
            delivered = Bulletin.FromMessageText(canonical.Type, canonical.From, canonical.To, canonical.At, canonical.Bid, shown, date, rLine + "\r\n" + text);
        }
        catch (ArgumentException)
        {
            Log($"bulletin {canonical.Bid}: the full title will not serialise; used the canonical title");
            delivered = Bulletin.FromMessageText(canonical.Type, canonical.From, canonical.To, canonical.At, canonical.Bid, canonical.Title, date, rLine + "\r\n" + text);
        }

        string name = OutboxName(item.ObjectId);
        byte[] content = delivered.Serialize();
        DurableFile.WriteAtomically(Path.Combine(_outbox, name), content, now, _options.FlushToDisk);
        _outboxItems[name] = new OutboxItem(name, item.ObjectId, content, delivered, now);
        _awaiting.Remove(item.ObjectId);
        _heardFrames.Remove(item.ObjectId);
        TryDelete(AwaitingPath(item.ObjectId));
        MailChanged();
        Log($"bulletin {delivered.Bid} ({ObjectId.Format(item.ObjectId)}) delivered {(withEnvelope ? "with the envelope of " + via : "bare")}: {why}");
        return delivered;
    }

    private static readonly System.Text.RegularExpressions.Regex RoutingLineTime =
        new(@"^R:(\d\d)(\d\d)(\d\d)/(\d\d)(\d\d)[Zz]? ", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The time (UTC, year 20yy) on the last routing line in <paramref name="top"/>, or null if there is none or it is not a real time.</summary>
    private static DateTimeOffset? RoutingTime(ReadOnlySpan<byte> top)
    {
        System.Text.RegularExpressions.Match? last = null;
        foreach (var line in Bulletin.TextEncoding.GetString(top).Split('\n', '\r'))
        {
            var m = RoutingLineTime.Match(line);
            if (m.Success)
            {
                last = m;
            }
        }
        if (last is null)
        {
            return null;
        }
        int Part(int group) => int.Parse(last.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        try
        {
            return new DateTimeOffset(2000 + Part(1), Part(2), Part(3), Part(4), Part(5), 0, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
