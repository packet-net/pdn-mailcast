using System.Globalization;

namespace Packet.Mailcast;

/// <summary>
/// A second object that carries a bulletin already delivered (issue #147): the same BID, listed
/// by a directory (often another station's, when canonical form did not make the two objects
/// identical). It is not collected, not listed as progress and not offered to the BBS.
/// </summary>
/// <param name="ObjectId">This copy's object ID.</param>
/// <param name="Bid">The shared BID.</param>
/// <param name="Station">The station it was heard from or listed by; null if not known.</param>
/// <param name="Time">When it was recognised as another copy.</param>
/// <param name="KeptObjectId">The object ID of the copy that was delivered.</param>
public sealed record OtherCopy(ulong ObjectId, string Bid, string? Station, DateTimeOffset Time, ulong KeptObjectId);

public sealed partial class ReceiverStore
{
    private const string OtherCopiesFolder = "othercopies";

    private readonly Dictionary<ulong, OtherCopy> _otherCopies = [];

    private volatile IReadOnlyList<OtherCopy> _otherCopiesView = [];

    /// <summary>The other copies on record, newest first. Safe to read from any thread: it is replaced, never changed.</summary>
    public IReadOnlyList<OtherCopy> OtherCopies => _otherCopiesView;

    /// <summary>Whether an object is known to be another copy of a bulletin already delivered. Used by tests; reads the published view, so safe from any thread.</summary>
    public bool IsOtherCopy(ulong objectId) => _otherCopiesView.Any(c => c.ObjectId == objectId);

    /// <summary>How many other copies were recognised on a UTC day, per station (those with no known station are left out).</summary>
    public IReadOnlyDictionary<string, int> OtherCopyCounts(DateOnly day) =>
        _otherCopiesView
            .Where(c => c.Station is not null && DateOnly.FromDateTime(c.Time.UtcDateTime) == day)
            .GroupBy(c => c.Station!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    private string OtherCopyPath(ulong objectId) => Path.Combine(_root, OtherCopiesFolder, ObjectId.Format(objectId));

    private object? _deliveredFor;

    private Dictionary<string, ulong> _deliveredBids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The object that was delivered for this BID (accepted, or already had by the BBS), if any.</summary>
    private ulong? DeliveredObject(string bid)
    {
        var order = _archive.Order; // immutable: a new one whenever the archive changes
        if (!ReferenceEquals(order, _deliveredFor))
        {
            var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in order)
            {
                if (entry.Verdict is BbsVerdict.Accepted or BbsVerdict.AlreadyHad)
                {
                    map.TryAdd(entry.Bid, entry.ObjectId);
                }
            }
            _deliveredBids = map;
            _deliveredFor = order;
        }
        return _deliveredBids.TryGetValue(bid, out ulong id) ? id : null;
    }

    /// <summary>The station whose pieces of an object were heard most, else the first station whose directory lists it.</summary>
    private string? StationOfObject(ulong objectId)
    {
        if (_heardFrames.TryGetValue(objectId, out var counts) && counts.Count > 0)
        {
            return counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First().Key;
        }
        return StationDirectories().Where(d => d.Directory.Find(objectId) is not null).Select(d => d.Station).FirstOrDefault();
    }

    /// <summary>
    /// Records <paramref name="objectId"/> as another copy of the delivered <paramref name="keptId"/>: marks it done so
    /// its later pieces are ignored, drops anything stored of it, and logs one line.
    /// </summary>
    private void NoteOtherCopy(ulong objectId, string bid, ulong keptId, string? station)
    {
        if (_otherCopies.ContainsKey(objectId))
        {
            return;
        }
        var now = _options.Time.GetUtcNow();
        foreach (var held in _instances.Values.Where(i => i.ObjectId == objectId).ToList())
        {
            Remove(held);
        }
        _heardFrames.Remove(objectId);
        if (_awaiting.Remove(objectId))
        {
            TryDelete(AwaitingPath(objectId));
        }
        var copy = new OtherCopy(objectId, bid, station, now, keptId);
        try
        {
            System.IO.Directory.CreateDirectory(Path.Combine(_root, OtherCopiesFolder));
            DurableFile.WriteAtomically(OtherCopyPath(objectId), Bulletin.TextEncoding.GetBytes(
                now.ToString("o", CultureInfo.InvariantCulture) + "\n" + bid + "\n" + (station ?? "-") + "\n" + ObjectId.Format(keptId) + "\n"), now, _options.FlushToDisk);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"cannot remember that {ObjectId.Format(objectId)} is another copy of {bid}: {e.Message}");
        }
        _otherCopies[objectId] = copy;
        WriteDone(objectId); // after the record: a restart restores a missing marker from it, never the other way round
        PublishOtherCopies();
        MailChanged();
        Log($"bulletin {bid}: other copy ({ObjectId.Format(objectId)}){(station is null ? "" : " from " + station)} not needed, already delivered");
    }

    private void PublishOtherCopies() =>
        _otherCopiesView = [.. _otherCopies.Values.OrderByDescending(c => c.Time).ThenBy(c => c.ObjectId)];

    /// <summary>Forgets the record of an other copy whose done marker has expired.</summary>
    private void ForgetOtherCopy(ulong objectId)
    {
        if (_otherCopies.Remove(objectId))
        {
            TryDelete(OtherCopyPath(objectId));
            PublishOtherCopies();
        }
    }

    private void LoadOtherCopies()
    {
        string dir = Path.Combine(_root, OtherCopiesFolder);
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
                var lines = File.ReadAllText(file).Split('\n');
                if (lines.Length < 4 || !ObjectId.TryParse(lines[3].Trim(), out ulong kept))
                {
                    throw new FormatException("cut short");
                }
                var time = DateTimeOffset.Parse(lines[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                string station = lines[2].Trim();
                _otherCopies[id] = new OtherCopy(id, lines[1].Trim(), station == "-" ? null : _options.Stations.Current(station), time, kept);
                if (!_done.ContainsKey(id))
                {
                    WriteDone(id);
                }
            }
            catch (Exception e) when (e is FormatException or IOException or ArgumentException)
            {
                Log($"othercopies file {Path.GetFileName(file)} unreadable, removed: {e.Message}");
                TryDelete(file);
            }
        }
        PublishOtherCopies();
    }

    /// <summary>
    /// Marks as other copies the objects in the usable directories that are not done and list a BID
    /// that has been delivered under another object ID.
    /// </summary>
    private void MarkOtherCopies()
    {
        foreach (var (directory, check) in UsableDirectories().ToList())
        {
            foreach (var entry in directory.Entries)
            {
                if ((_done.ContainsKey(entry.ObjectId) && !_awaiting.ContainsKey(entry.ObjectId)) || _outboxItems.ContainsKey(OutboxName(entry.ObjectId)))
                {
                    continue;
                }
                if (DeliveredObject(entry.Bid) is ulong kept && kept != entry.ObjectId)
                {
                    NoteOtherCopy(entry.ObjectId, entry.Bid, kept, StationOfObject(entry.ObjectId) ?? StationOf(directory, check));
                }
            }
        }
    }

    /// <summary>
    /// A bulletin just rebuilt whose BID was already delivered under another object ID: it is
    /// another copy, not a new bulletin and not a failure.
    /// </summary>
    private AcceptResult? CompletedOtherCopy(Instance instance, Bulletin bulletin)
    {
        if (DeliveredObject(bulletin.Bid) is not ulong kept || kept == instance.ObjectId)
        {
            return null;
        }
        string? station = StationOfObject(instance.ObjectId);
        MarkDone(instance);
        NoteOtherCopy(instance.ObjectId, bulletin.Bid, kept, station);
        return new AcceptResult(FrameOutcome.CompletedOtherCopy, instance.ObjectId, Bulletin: bulletin);
    }
}
