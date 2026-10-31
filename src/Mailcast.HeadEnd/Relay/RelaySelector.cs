using System.Globalization;
using Mailcast.RaptorQ;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Relay;

/// <summary>What the last look at the catalogue found, for the journal and the status page.</summary>
/// <param name="Candidates">Objects a trusted signed directory of an advertised station (or this station) lists, that this head end does not carry itself.</param>
/// <param name="Complete">Of those, how many are complete and checked.</param>
/// <param name="Dropped">Of those, how many were left out because the octets do not match the hash a trusted directory lists.</param>
public readonly record struct RelaySummary(int Candidates, int Complete, int Dropped);

/// <summary>
/// Picks from the catalogue the objects this head end may relay (issue #218) and says what it knows of each, for the scheduler to rank
/// with its own bulletins. Nothing here decides how much airtime anything gets.
/// </summary>
/// <remarks>
/// <para>A candidate is an object that a trusted signed directory, as a receiver vouches for it, lists for an advertised station or for
/// this one, of any content type; objects not so listed are never relayed, and nor are ones this head end carries itself (same object
/// ID). Pieces handed to a receiver through its API are never relayed: before an object is complete only the pieces a receiver heard on air
/// go out, unchanged. Once it is complete and its octets match its object ID (and the long hash a trusted directory lists for it, if
/// any), fresh pieces are made from those octets, and the object is listed with a hash computed from them, never one copied from a
/// receiver.</para>
/// <para>An envelope is not relayed yet: the receivers' API does not say which envelope object belongs to a bulletin, so none is ever
/// held and checked here.</para>
/// </remarks>
public sealed class RelaySelector
{
    /// <summary>An object no receiver has said anything new about for this long is not relayed: the retiring age, as nobody is still on it.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(BroadcastScheduler.LeftoverHours);

    private readonly ObjectCatalogue _catalogue;
    private readonly Compression _compression;
    private readonly string? _station;
    private readonly HashSet<string> _stations;
    private readonly TimeProvider _time;
    private readonly IJournal _journal;
    private readonly HashSet<ulong> _warned = [];
    private readonly Lock _gate = new();
    private RelaySummary _summary;

    /// <summary>Makes a selector.</summary>
    /// <param name="catalogue">What the receivers have given.</param>
    /// <param name="compression">Reads a rebuilt bulletin for its listing.</param>
    /// <param name="station">This head end's callsign, whose own directory counts and whose pieces heard back are not counted again; null if none.</param>
    /// <param name="adverts">The stations this head end's directory advertises.</param>
    /// <param name="time">The clock.</param>
    /// <param name="journal">Where a refusal goes, once for each object.</param>
    public RelaySelector(ObjectCatalogue catalogue, Compression compression, string? station, IEnumerable<StationAdvert> adverts, TimeProvider time, IJournal journal)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(compression);
        ArgumentNullException.ThrowIfNull(adverts);
        _catalogue = catalogue;
        _compression = compression;
        _station = station;
        _stations = new HashSet<string>(adverts.Select(a => a.Callsign), StringComparer.OrdinalIgnoreCase);
        if (station is not null)
        {
            _stations.Add(station);
        }
        _time = time;
        _journal = journal;
    }

    /// <summary>What the last <see cref="Select"/> found.</summary>
    public RelaySummary Summary
    {
        get
        {
            lock (_gate)
            {
                return _summary;
            }
        }
    }

    /// <summary>The objects to put in the rotation, each with what the receivers heard and what this head end has sent.</summary>
    /// <param name="progress">What has been sent of an object, by ID.</param>
    /// <param name="carries">Whether this head end carries the object itself.</param>
    public IReadOnlyList<RelayedObject> Select(Func<ulong, RelayProgress> progress, Func<ulong, bool> carries)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(carries);
        var now = _time.GetUtcNow();
        var found = new List<RelayedObject>();
        int complete = 0;
        int dropped = 0;
        foreach (var o in _catalogue.All())
        {
            if (o.K < 1 || now - o.Updated > StaleAfter || carries(o.Id) || !o.ListedBy.Keys.Any(_stations.Contains))
            {
                continue;
            }
            var made = Make(o, progress(o.Id), out bool refused);
            if (refused)
            {
                dropped++;
            }
            if (made is null)
            {
                continue;
            }
            found.Add(made);
            if (made.Complete)
            {
                complete++;
            }
        }
        lock (_gate)
        {
            _summary = new RelaySummary(found.Count + dropped, complete, dropped);
        }
        return found;
    }

    private RelayedObject? Make(CatalogueObject o, RelayProgress progress, out bool refused)
    {
        refused = false;
        TransferObject? transfer = null;
        if (o.Data is { } data)
        {
            string computed = ObjectId.LongHash(o.DictionaryId, data);
            if (o.ListedBy.FirstOrDefault(l => l.Value is not null && !string.Equals(l.Value, computed, StringComparison.Ordinal)) is { Value: not null } bad)
            {
                refused = true;
                if (_warned.Add(o.Id))
                {
                    _journal.Write($"relay: {ObjectId.Format(o.Id)} left out: its octets do not match the hash {bad.Key} lists");
                }
                return null;
            }
            try
            {
                transfer = TransferObject.FromStored(data, o.DictionaryId, o.Oti);
            }
            catch (ArgumentException)
            {
                transfer = null; // the receiver's OTI does not describe its octets; treated as not complete
            }
            if (transfer is not null && transfer.ObjectId != o.Id)
            {
                transfer = null;
            }
        }
        var heard = new List<MailcastFrame>();
        int others = 0;
        foreach (var p in o.Pieces.Where(p => p.OnAir))
        {
            if (_station is null || !string.Equals(p.HeardFrom, _station, StringComparison.OrdinalIgnoreCase))
            {
                others++;
            }
            if (MailcastFrame.TryParse(p.Frame, out var frame) && frame!.ObjectId == o.Id && frame.EncodingSymbolId == p.Esi)
            {
                heard.Add(frame);
            }
        }
        return new RelayedObject
        {
            ObjectId = o.Id,
            DictionaryId = o.DictionaryId,
            Type = o.ContentType ?? (o.Data is { Length: > 0 } d ? (byte)(d[0] & ~ContentType.MetadataFollows) : (byte)0),
            Oti = o.Oti,
            HeardOnAir = others,
            HeardFrames = heard,
            Transfer = transfer,
            Listing = transfer is null ? null : ListingOf(transfer),
            Progress = progress,
        };
    }

    // The fields a directory entry needs, from the checked object. A bulletin gives its own BID, title and length; any other type gets
    // its object ID as BID and a description of the type as title.
    private RelayListing ListingOf(TransferObject transfer)
    {
        string generic = ObjectId.Format(transfer.ObjectId);
        try
        {
            var (kind, content) = TransferObject.Unpack(transfer.Bytes, transfer.DictionaryId, _compression);
            if (kind == ObjectKind.Bulletin)
            {
                var bulletin = Bulletin.Parse(content);
                return new RelayListing(bulletin.Bid, bulletin.Title, content.Length);
            }
            return new RelayListing(generic, ContentType.Describe((byte)kind), content.Length);
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or FormatException)
        {
            return new RelayListing(generic, ContentType.Describe((byte)transfer.Kind), transfer.Bytes.Length);
        }
    }

    /// <summary>The part of the status document about relaying: counts, and the first few objects briefly.</summary>
    /// <param name="progress">What has been sent of an object, by ID.</param>
    /// <param name="carries">Whether this head end carries the object itself.</param>
    public object StatusView(Func<ulong, RelayProgress> progress, Func<ulong, bool> carries)
    {
        var chosen = Select(progress, carries);
        var summary = Summary;
        return new
        {
            candidates = summary.Candidates,
            complete = summary.Complete,
            leftOut = summary.Dropped,
            objects = chosen
                .OrderBy(r => r.Total / (double)r.K)
                .Take(20)
                .Select(r => new
                {
                    id = ObjectId.Format(r.ObjectId),
                    type = r.Type,
                    k = r.K,
                    heardOnAir = r.HeardOnAir,
                    sent = r.Progress.Sent.Count,
                    complete = r.Complete,
                    listed = r.Listing is not null,
                    coverage = Math.Round(r.Total / (double)r.K, 2),
                })
                .ToList(),
        };
    }

    /// <summary>One short ASCII line for the slot's journal.</summary>
    public static string Line(int objects, int frames, RelaySummary summary) =>
        string.Create(CultureInfo.InvariantCulture, $"relay: {objects} objects, {frames} frames ({summary.Candidates} heard on the LAN, {summary.Complete} complete)");
}
