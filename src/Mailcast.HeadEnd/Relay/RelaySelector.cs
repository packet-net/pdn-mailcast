using System.Globalization;
using Mailcast.RaptorQ;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Relay;

/// <summary>What the last look at the catalogue found, for the journal and the status page.</summary>
/// <param name="Candidates">Objects a trusted signed directory of an advertised station (or this station) lists, that this head end does not carry itself, including those left out.</param>
/// <param name="Complete">Of those, how many are complete and checked.</param>
/// <param name="Dropped">Of those, how many were left out: octets that do not match a listed hash, or a coding that is not certain.</param>
/// <param name="Listed">Of those, how many can be listed in our directory (complete and checked).</param>
public readonly record struct RelaySummary(int Candidates, int Complete, int Dropped, int Listed = 0);

/// <summary>
/// Picks from the catalogue the objects this head end may relay (issue #218) and says what it knows of each, for the scheduler to rank
/// with its own bulletins. Nothing here decides how much airtime anything gets.
/// </summary>
/// <remarks>
/// <para>A candidate is an object that a trusted signed directory, as a receiver vouches for it, lists for an advertised station or for
/// this one, of any content type except an envelope; objects not so listed are never relayed, and nor are ones this head end carries itself
/// (same object ID). Pieces handed to a receiver through its API are never relayed, and nor is their coding taken: before an object is
/// complete only the pieces a receiver heard on air go out, unchanged, and the coding is that of the frames on air, which must agree
/// with each other and with the receiver's row. Once it is complete and its octets match its object ID (and the long hash a trusted
/// directory lists for it, if any), fresh pieces are made from those octets, avoiding what has been heard, and the object is listed with
/// a hash computed from them, never one copied from a receiver.</para>
/// <para>A bulletin's envelope is the one a listing station links (<c>env=</c>, as the receivers report it), taken only if held complete
/// and matching both its object ID and the long hash listed; it goes with the bulletin and is linked from its listing.</para>
/// <para>Coverage counts every on-air piece heard from stations other than this one. A receiver stops keeping pieces once an object is
/// complete, so its count of frames heard on air (<c>air</c>) is used when it gives one; for one that does not, a complete object still
/// listed counts as covered to the leftover mark at least.</para>
/// </remarks>
public sealed class RelaySelector
{
    /// <summary>An object no receiver has heard on air for this long is not relayed: the retiring age, as nobody is still on it.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(BroadcastScheduler.LeftoverHours);

    private readonly ObjectCatalogue _catalogue;
    private readonly Compression _compression;
    private readonly string? _station;
    private readonly HashSet<string> _stations;
    private readonly BudgetRule _rule;
    private readonly TimeProvider _time;
    private readonly IJournal _journal;
    private readonly HashSet<ulong> _warned = [];
    private readonly Lock _gate = new();
    private RelaySummary _summary;
    private IReadOnlyList<RelayedObject> _chosen = [];

    /// <summary>Makes a selector.</summary>
    /// <param name="catalogue">What the receivers have given.</param>
    /// <param name="compression">Reads a rebuilt bulletin for its listing.</param>
    /// <param name="station">This head end's callsign, whose own directory counts and whose pieces heard back are not counted again; null if none.</param>
    /// <param name="adverts">The stations this head end's directory advertises.</param>
    /// <param name="time">The clock.</param>
    /// <param name="journal">Where a refusal goes, once for each object.</param>
    /// <param name="rule">The budget rule, whose leftover mark a complete object counts as covered to when a receiver does not count frames; the defaults if null.</param>
    public RelaySelector(ObjectCatalogue catalogue, Compression compression, string? station, IEnumerable<StationAdvert> adverts, TimeProvider time, IJournal journal, BudgetRule? rule = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(compression);
        ArgumentNullException.ThrowIfNull(adverts);
        _catalogue = catalogue;
        _compression = compression;
        _station = station?.ToUpperInvariant();
        _stations = new HashSet<string>(adverts.Select(a => a.Callsign), StringComparer.OrdinalIgnoreCase);
        if (station is not null)
        {
            _stations.Add(station);
        }
        _rule = rule ?? new BudgetRule();
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

    /// <summary>
    /// How many on-air pieces of an object were heard from stations other than this one: what counts towards the coverage of this head end's
    /// own bulletin of the same object. Zero if the catalogue knows nothing of it.
    /// </summary>
    public int HeardFromOthers(ulong objectId) => _catalogue.Get(objectId) is { } o ? Heard(o, labelledOnly: true) : 0;

    // Pieces heard on air from stations other than this one. For this head end's own bulletin only frames the receiver put a station to
    // count (the safer way: a frame of unknown origin might be our own); for a relayed object every frame but our own does.
    private int Heard(CatalogueObject o, bool labelledOnly = false)
    {
        int kept = o.Pieces.Count(p => p.OnAir && !IsOwn(p.HeardFrom) && (!labelledOnly || p.HeardFrom is not null));
        int own = o.AirByStation.Where(kv => IsOwn(kv.Key)).Sum(kv => kv.Value);
        int air = o.AirFrames is { } frames
            ? Math.Max(0, (labelledOnly ? o.AirByStation.Where(kv => !IsOwn(kv.Key)).Sum(kv => kv.Value) : frames - own))
            : 0;
        return Math.Max(kept, air);
    }

    private bool IsOwn(string? label) => _station is not null && label is not null && string.Equals(label, _station, StringComparison.OrdinalIgnoreCase);

    /// <summary>The objects to put in the rotation, each with what the receivers heard and what this head end has sent. Also remembered for <see cref="StatusView"/>.</summary>
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
        var all = _catalogue.All();
        var byId = all.ToDictionary(o => o.Id);
        // Envelopes are relayed with their bulletin, never on their own.
        var envelopeIds = new HashSet<ulong>(all.SelectMany(o => o.EnvelopeLinks.Values.Select(l => l.ObjectId)));
        foreach (var o in all)
        {
            if (envelopeIds.Contains(o.Id) || o.ContentType == (byte)ObjectKind.Envelope
                || (o.Data is { Length: > 0 } first && (first[0] & ~ContentType.MetadataFollows) == (byte)ObjectKind.Envelope))
            {
                continue;
            }
            if (o.K < 1 || o.LastHeard is not { } last || now - last > StaleAfter || carries(o.Id) || !o.ListedBy.Keys.Any(_stations.Contains))
            {
                continue;
            }
            var made = Make(o, progress, byId, out bool refused);
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
            _summary = new RelaySummary(found.Count + dropped, complete, dropped, found.Count(r => r.Listing is not null && r.Transfer is not null));
            _chosen = found;
        }
        return found;
    }

    private void Say(ulong id, string line)
    {
        bool first;
        lock (_gate)
        {
            first = _warned.Add(id);
        }
        if (first)
        {
            _journal.Write(line);
        }
    }

    private RelayedObject? Make(CatalogueObject o, Func<ulong, RelayProgress> progress, Dictionary<ulong, CatalogueObject> byId, out bool refused)
    {
        refused = false;
        string id = ObjectId.Format(o.Id);
        if (!o.OtiFromAir || o.OtiConflict)
        {
            // Only the coding of frames heard on air counts, and it must agree: a piece made to another would not add up with the rest.
            refused = true;
            Say(o.Id, $"relay: {id} left out: its coding is not certain from the frames heard on air");
            return null;
        }
        TransferObject? transfer = null;
        if (o.Data is { } data)
        {
            string computed = ObjectId.LongHash(o.DictionaryId, data);
            if (o.ListedBy.FirstOrDefault(l => l.Value is not null && !string.Equals(l.Value, computed, StringComparison.Ordinal)) is { Value: not null } bad)
            {
                refused = true;
                Say(o.Id, $"relay: {id} left out: its octets do not match the hash {bad.Key} lists");
                return null;
            }
            try
            {
                transfer = TransferObject.FromStored(data, o.DictionaryId, o.Oti);
            }
            catch (ArgumentException)
            {
                transfer = null; // the coding does not describe its octets; treated as not complete
            }
            if (transfer is not null && transfer.ObjectId != o.Id)
            {
                transfer = null;
            }
        }
        var heard = new List<MailcastFrame>();
        var heardEsis = new HashSet<uint>();
        foreach (var p in o.Pieces.Where(p => p.OnAir))
        {
            heardEsis.Add(p.Esi);
            if (MailcastFrame.TryParse(p.Frame, out var frame) && frame!.ObjectId == o.Id && frame.EncodingSymbolId == p.Esi && frame.Oti.ToBytes().AsSpan().SequenceEqual(o.Oti.ToBytes()))
            {
                heard.Add(frame);
            }
        }
        int others = Heard(o);
        if (o.AirFrames is null && o.Complete && o.ListedBy.Keys.Any(k => _stations.Contains(k) && !IsOwn(k)))
        {
            // A receiver that does not count frames heard after completion: a complete object still listed is covered to the leftover mark at least.
            others = Math.Max(others, BroadcastScheduler.LeftoverSymbols(o.K, _rule));
        }
        TransferObject? envelope = transfer is null ? null : EnvelopeOf(o, byId);
        return new RelayedObject
        {
            ObjectId = o.Id,
            DictionaryId = o.DictionaryId,
            Type = o.ContentType ?? (o.Data is { Length: > 0 } d ? (byte)(d[0] & ~ContentType.MetadataFollows) : (byte)0),
            Oti = o.Oti,
            HeardOnAir = others,
            HeardFrames = heard,
            Heard = heardEsis,
            FirstHeard = o.FirstHeard,
            Transfer = transfer,
            Listing = transfer is null ? null : ListingOf(transfer),
            Envelope = envelope,
            EnvelopeProgress = envelope is null ? RelayProgress.None : progress(envelope.ObjectId),
            EnvelopeHeard = envelope is null || !byId.TryGetValue(envelope.ObjectId, out var e) ? new HashSet<uint>() : new HashSet<uint>(e.Pieces.Where(p => p.OnAir).Select(p => p.Esi)),
            Progress = progress(o.Id),
        };
    }

    // The envelope a listing station links, if it is held complete, matches its ID and the listed hash, and is an envelope object.
    private static TransferObject? EnvelopeOf(CatalogueObject o, Dictionary<ulong, CatalogueObject> byId)
    {
        foreach (var link in o.EnvelopeLinks.Values.OrderBy(l => l.ObjectId))
        {
            if (byId.TryGetValue(link.ObjectId, out var e) && e.Data is { } data && e.OtiFromAir && !e.OtiConflict
                && ObjectId.LongHash(e.DictionaryId, data) == link.Hash)
            {
                try
                {
                    var envelope = TransferObject.FromStored(data, e.DictionaryId, e.Oti);
                    if (envelope.ObjectId == link.ObjectId && envelope.Kind == ObjectKind.Envelope)
                    {
                        return envelope;
                    }
                }
                catch (ArgumentException)
                {
                    // the coding does not describe the octets: not usable
                }
            }
        }
        return null;
    }

    // The fields a directory entry needs, from the checked object. A bulletin gives its own BID, title and length; any other type gets
    // its object ID as BID and a description of the type as title. What a directory cannot carry falls back to the generic form.
    private RelayListing ListingOf(TransferObject transfer)
    {
        string generic = ObjectId.Format(transfer.ObjectId);
        try
        {
            var (kind, content) = TransferObject.Unpack(transfer.Bytes, transfer.DictionaryId, _compression);
            if (kind == ObjectKind.Bulletin)
            {
                var bulletin = Bulletin.Parse(content);
                if (Valid(bulletin.Bid, bulletin.Title))
                {
                    return new RelayListing(bulletin.Bid, bulletin.Title, content.Length);
                }
                Say(transfer.ObjectId, $"relay: {generic} listed under its object ID: its BID or title cannot go in a directory");
                return new RelayListing(generic, ContentType.Describe((byte)kind), content.Length);
            }
            return new RelayListing(generic, ContentType.Describe((byte)kind), content.Length);
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or FormatException)
        {
            return new RelayListing(generic, ContentType.Describe((byte)transfer.Kind), transfer.Bytes.Length);
        }
    }

    // What BroadcastDirectory accepts of an entry: a BID with no white space and a title on one line.
    private static bool Valid(string bid, string title) =>
        bid.Length > 0 && !bid.Any(c => c is ' ' or '\t' or '\n' or '\r') && !title.Contains('\n', StringComparison.Ordinal) && !title.Contains('\r', StringComparison.Ordinal);

    /// <summary>The part of the status document about relaying: counts, and the first few objects briefly, as of the last plan (it does not look again).</summary>
    public object StatusView()
    {
        IReadOnlyList<RelayedObject> chosen;
        RelaySummary summary;
        lock (_gate)
        {
            chosen = _chosen;
            summary = _summary;
        }
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
        string.Create(CultureInfo.InvariantCulture, $"relay: {objects} objects, {frames} frames ({summary.Candidates} candidates, {summary.Complete} complete, {summary.Listed} listed)");
}
