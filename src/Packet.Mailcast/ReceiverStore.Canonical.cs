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

    private sealed record AwaitingItem(ulong ObjectId, Bulletin Bulletin, string LongHash, DateTimeOffset Since);

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

    /// <summary>Holds a complete canonical bulletin until an envelope arrives or it stops being listed.</summary>
    private AcceptResult Await(Instance instance, Bulletin bulletin, string longHash)
    {
        var now = _options.Time.GetUtcNow();
        var item = new AwaitingItem(instance.ObjectId, bulletin, longHash, now);
        System.IO.Directory.CreateDirectory(Path.Combine(_root, AwaitingFolder));
        DurableFile.WriteAtomically(AwaitingPath(instance.ObjectId), EncodeAwaiting(item), now, _options.FlushToDisk);
        _awaiting[instance.ObjectId] = item;
        MarkDone(instance, longHash);
        MailChanged();
        Log($"bulletin {bulletin.Bid} ({ObjectId.Format(instance.ObjectId)}) is complete, waiting for its station's details");
        // Its envelope may already be here.
        var released = ReleaseAwaiting(null, null);
        return new AcceptResult(FrameOutcome.AwaitingEnvelope, instance.ObjectId, Bulletin: bulletin, Released: released);
    }

    private byte[] EncodeAwaiting(AwaitingItem item)
    {
        string frames = _heardFrames.TryGetValue(item.ObjectId, out var counts)
            ? string.Join(';', counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + "=" + c.Value.ToString(CultureInfo.InvariantCulture)))
            : "";
        var head = Bulletin.TextEncoding.GetBytes(item.Since.ToString("o", CultureInfo.InvariantCulture) + "\n" + item.LongHash + "\n" + frames + "\n");
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
            try
            {
                var bytes = File.ReadAllBytes(file);
                int p = 0;
                var parts = new string[3];
                for (int i = 0; i < 3; i++)
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
                _awaiting[id] = new AwaitingItem(id, bulletin, parts[1], since);
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
    /// Delivers every waiting canonical bulletin that now has an envelope, and, when
    /// <paramref name="heard"/> is a directory that can speak for its station, those that no directory
    /// lists any more. Null if none was delivered.
    /// </summary>
    private List<Bulletin>? ReleaseAwaiting(BroadcastDirectory? heard, DirectoryCheck? check)
    {
        if (_awaiting.Count == 0)
        {
            return null;
        }
        bool canSpeak = false;
        if (heard is not null && check is not null && StationOf(heard, check) is { } station)
        {
            DateOnly latest = LatestPlausibleDate(_options.Time.GetUtcNow());
            canSpeak = heard.Date <= latest && heard.Date >= NewestDirectoryDate(latest, station);
        }
        List<Bulletin>? released = null;
        foreach (var item in _awaiting.Values.OrderBy(a => a.Since).ThenBy(a => a.ObjectId).ToList())
        {
            string? why = null;
            if (EnvelopesFor(item.ObjectId).Count > 0)
            {
                why = "its envelope arrived";
            }
            else if (canSpeak && heard!.Find(item.ObjectId) is null && !StationDirectories().Any(d => d.Directory.Find(item.ObjectId) is not null))
            {
                why = "no directory lists it any more";
            }
            if (why is not null)
            {
                (released ??= []).Add(DeliverAwaiting(item, why));
            }
        }
        return released;
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
        string text = canonical.MessageText;
        string title = canonical.Title;
        bool withEnvelope = false;
        if (envelopes.Count > 0)
        {
            var (station, envelope) = envelopes.OrderByDescending(e => Heard(e.Station)).ThenBy(e => e.Station, StringComparer.Ordinal).First();
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
        Bulletin delivered;
        try
        {
            delivered = Bulletin.FromMessageText(canonical.Type, canonical.From, canonical.To, canonical.At, canonical.Bid, title, canonical.Date, rLine + "\r\n" + text);
        }
        catch (ArgumentException e)
        {
            Log($"bulletin {canonical.Bid}: full title or text will not serialise ({e.Message}); delivered with the canonical title");
            delivered = Bulletin.FromMessageText(canonical.Type, canonical.From, canonical.To, canonical.At, canonical.Bid, canonical.Title, canonical.Date, rLine + "\r\n" + canonical.MessageText);
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
}
