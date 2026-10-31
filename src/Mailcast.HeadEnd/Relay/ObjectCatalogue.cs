using Mailcast.RaptorQ;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Relay;

/// <summary>One piece the head end holds: the whole frame, where it came from.</summary>
/// <param name="Esi">The encoding symbol ID.</param>
/// <param name="Frame">The frame as it goes on the air.</param>
/// <param name="OnAir">Whether a receiver heard it on air (from any station), as against being handed in from outside.</param>
/// <param name="HeardFrom">For a piece heard on air: the station it was heard from, if the receiver said.</param>
/// <param name="Heard">When it was kept.</param>
public sealed record CataloguePiece(uint Esi, byte[] Frame, bool OnAir, string? HeardFrom, DateTimeOffset Heard);

/// <summary>A copy of what the catalogue holds of one object, at the moment it was asked for.</summary>
public sealed class CatalogueObject
{
    /// <summary>The object ID.</summary>
    public required ulong Id { get; init; }

    /// <summary>The content type, if known.</summary>
    public byte? ContentType { get; init; }

    /// <summary>The zstd dictionary ID.</summary>
    public ushort DictionaryId { get; init; }

    /// <summary>The RaptorQ coding parameters: those of the frames heard on air when there are any, else the receiver's row.</summary>
    public ObjectTransmissionInformation Oti { get; init; }

    /// <summary>Whether <see cref="Oti"/> comes from frames heard on air (never from one handed in).</summary>
    public bool OtiFromAir { get; init; }

    /// <summary>Whether the frames heard on air, or a receiver's row, disagree about the coding: the object is not safe to make pieces of.</summary>
    public bool OtiConflict { get; init; }

    /// <summary>Frames of the object heard on air, most reported by any receiver (they hear the same transmissions, so not summed), kept or not; null if no receiver says.</summary>
    public int? AirFrames { get; init; }

    /// <summary>Of <see cref="AirFrames"/>, by the station they were heard from.</summary>
    public IReadOnlyDictionary<string, int> AirByStation { get; init; } = new Dictionary<string, int>();

    /// <summary>When the first on-air piece was heard (kept pieces and receivers' reports); null if none.</summary>
    public DateTimeOffset? FirstHeard { get; init; }

    /// <summary>When the latest on-air piece was heard; null if none.</summary>
    public DateTimeOffset? LastHeard { get; init; }

    /// <summary>Pieces needed to rebuild it (source symbols in block 0).</summary>
    public int K { get; init; }

    /// <summary>The pieces held, deduplicated across receivers, in piece number order.</summary>
    public required IReadOnlyList<CataloguePiece> Pieces { get; init; }

    /// <summary>
    /// How many of the pieces were heard on air, from any station: what an airtime rule counts as
    /// already sent. Pieces only handed in from outside do not count.
    /// </summary>
    public int OnAirPieces => Pieces.Count(p => p.OnAir);

    /// <summary>The stations the on-air pieces were heard from.</summary>
    public IReadOnlyList<string> HeardFrom => [.. Pieces.Where(p => p.OnAir && p.HeardFrom is not null).Select(p => p.HeardFrom!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The stations whose trusted signed directories, as the receivers remember them, list the object,
    /// each with the long hash it lists (null if that listing carried none). Receivers vouch for a directory
    /// only when it is signed by a key they trust, so every station here was checked by at least one receiver.
    /// </summary>
    public required IReadOnlyDictionary<string, string?> ListedBy { get; init; }

    /// <summary>The envelope each listing station links for the object (<c>env=</c>), by station; empty if none does.</summary>
    public IReadOnlyDictionary<string, EnvelopeLink> EnvelopeLinks { get; init; } = new Dictionary<string, EnvelopeLink>();

    /// <summary>The rebuilt object (kind octet and zstd frame), checked against the object ID; null until a receiver has it complete.</summary>
    public byte[]? Data { get; init; }

    /// <summary>Whether <see cref="Data"/> is there.</summary>
    public bool Complete => Data is not null;

    /// <summary>When a receiver last said anything new about it.</summary>
    public DateTimeOffset Updated { get; init; }
}

/// <summary>What the catalogue holds, in all.</summary>
public readonly record struct CatalogueTotals(int Objects, int Complete, int Pieces, int OnAirPieces, long Bytes);

/// <summary>
/// What the head end knows of other stations' objects, from the receivers it reads (issue #218): per
/// object its content type, coding, the pieces available (once each, however many receivers have it), how many
/// of them were heard on air, which stations' directories list it and with what hash, and the rebuilt object once
/// a receiver has it. Held in memory only, and bounded: past <see cref="MaxBytes"/> or <see cref="MaxObjects"/>
/// the object that changed longest ago goes. Nothing is saved to disk, because the receivers keep the pieces
/// and give them again whenever asked, so a restarted head end is back to full strength after its first read.
/// Safe to use from several threads.
/// </summary>
public sealed class ObjectCatalogue(TimeProvider time, long maxBytes = ObjectCatalogue.DefaultMaxBytes, int maxObjects = ObjectCatalogue.DefaultMaxObjects)
{
    /// <summary>The default for <see cref="MaxBytes"/>: 64 MiB of frames and rebuilt objects.</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    /// <summary>The default for <see cref="MaxObjects"/>.</summary>
    public const int DefaultMaxObjects = 10_000;

    private sealed class Entry
    {
        public ulong Id;
        public byte? Type;
        public ushort Dictionary;
        public ObjectTransmissionInformation Oti;
        public bool OtiFromAir;
        public bool OtiConflict;
        public int? AirFrames;
        public readonly Dictionary<string, int> AirByStation = new(StringComparer.Ordinal);
        public DateTimeOffset? AirFirst;
        public DateTimeOffset? AirLast;
        public int K;
        public readonly SortedDictionary<uint, CataloguePiece> Pieces = [];
        public readonly Dictionary<string, string?> Listings = new(StringComparer.Ordinal);
        public readonly Dictionary<string, EnvelopeLink> EnvelopeLinks = new(StringComparer.Ordinal);
        public byte[]? Data;
        public DateTimeOffset Updated;
        public long Bytes;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, Entry> _objects = [];
    private long _bytes;

    /// <summary>The most octets of frames and rebuilt objects held.</summary>
    public long MaxBytes { get; } = maxBytes;

    /// <summary>The most objects held.</summary>
    public int MaxObjects { get; } = maxObjects;

    /// <summary>Whether the catalogue has ever held anything since start: until a receiver has been read, what it does not hold says nothing about what is on offer.</summary>
    public bool EverHeld { get; private set; }

    /// <summary>Whether a receiver has been read successfully since start.</summary>
    public bool ReadOnce { get; private set; }

    /// <summary>Notes a successful read of a receiver.</summary>
    public void MarkRead() => ReadOnce = true;

    /// <summary>Raised, outside the lock, with the ID of each object dropped to keep within the limits, so readers forget what they know of it.</summary>
    public event Action<ulong>? Evicted;

    /// <summary>
    /// Notes what a receiver says about an object: its type, coding and the listings of it. Only for an object already held (by
    /// a piece or its rebuilt octets): a row alone makes no entry, so an object with nothing in it can never push out one that has.
    /// </summary>
    /// <remarks>
    /// The listings here are what a receiver claims; the catalogue does not check them. Part 2 of issue 218 must relay only from
    /// checked data: take the object from rebuilt octets that match the ID, and compute the long hash from those octets itself
    /// (<see cref="ObjectId.LongHash"/>), never copying a receiver's hash into a directory it signs.
    /// </remarks>
    public void Note(RemoteObject row)
    {
        lock (_gate)
        {
            var e = Find(row.Id, create: false);
            if (e is null)
            {
                return;
            }
            e.Type ??= row.Type;
            if (e.OtiFromAir)
            {
                // The coding the frames on air carry stands; a receiver's row that says otherwise makes the object unsafe to relay.
                e.OtiConflict |= !SameOti(e.Oti, row.Oti) || e.Dictionary != row.Dictionary;
            }
            else
            {
                e.Dictionary = row.Dictionary;
                e.Oti = row.Oti;
                e.K = row.K;
            }
            if (row.Air is { } air)
            {
                e.AirFrames = Math.Max(e.AirFrames ?? 0, air.Frames);
                foreach (var (station, n) in air.ByStation)
                {
                    e.AirByStation[station] = Math.Max(e.AirByStation.GetValueOrDefault(station), n);
                }
                if (air.First > DateTimeOffset.MinValue && (e.AirFirst is null || air.First < e.AirFirst))
                {
                    e.AirFirst = air.First;
                }
                if (air.Last > DateTimeOffset.MinValue && (e.AirLast is null || air.Last > e.AirLast))
                {
                    e.AirLast = air.Last;
                }
                e.Updated = time.GetUtcNow();
            }
            foreach (var l in row.Listings)
            {
                if (l.Station.Length > 0 && l.Envelope is { } link && (!e.EnvelopeLinks.TryGetValue(l.Station, out var known) || known != link))
                {
                    e.EnvelopeLinks[l.Station] = link;
                    e.Updated = time.GetUtcNow();
                }
                if (l.Station.Length > 0 && (!e.Listings.TryGetValue(l.Station, out var had) || (had is null && l.Hash is not null)))
                {
                    e.Listings[l.Station] = l.Hash;
                    e.Updated = time.GetUtcNow();
                }
            }
        }
    }

    /// <summary>
    /// Adds a piece of an object. A piece already held (by piece number) is not added again; a copy heard on
    /// air upgrades one that was only handed in, and its frame replaces the handed-in one if they differ.
    /// False for a frame that is not a frame of this object, or one already held.
    /// </summary>
    public bool AddPiece(ulong objectId, RemotePiece piece)
    {
        if (!MailcastFrame.TryParse(piece.Frame, out var frame) || frame!.ObjectId != objectId || frame.EncodingSymbolId != piece.Esi)
        {
            return false;
        }
        bool added;
        List<ulong> dropped;
        lock (_gate)
        {
            var e = Find(objectId, create: true)!;
            EverHeld = true;
            if (e.Pieces.TryGetValue(piece.Esi, out var had))
            {
                if (had.OnAir || !piece.OnAir)
                {
                    return false;
                }
                _bytes += piece.Frame.Length - had.Frame.Length;
                e.Bytes += piece.Frame.Length - had.Frame.Length;
                e.Pieces[piece.Esi] = new CataloguePiece(piece.Esi, piece.Frame, true, piece.Label, piece.Heard);
                NoteAirCoding(e, frame);
                added = false;
            }
            else
            {
                e.Pieces[piece.Esi] = new CataloguePiece(piece.Esi, piece.Frame, piece.OnAir, piece.OnAir ? piece.Label : null, piece.Heard);
                _bytes += piece.Frame.Length;
                e.Bytes += piece.Frame.Length;
                // The coding is taken from frames heard on air only, never from one handed in.
                if (piece.OnAir)
                {
                    NoteAirCoding(e, frame);
                }
                added = true;
            }
            e.Updated = time.GetUtcNow();
            dropped = Trim(objectId);
        }
        Raise(dropped);
        return added;
    }

    private static bool SameOti(ObjectTransmissionInformation a, ObjectTransmissionInformation b) => a.ToBytes().AsSpan().SequenceEqual(b.ToBytes());

    // Under the lock.
    private static void NoteAirCoding(Entry e, MailcastFrame frame)
    {
        if (!e.OtiFromAir)
        {
            e.OtiFromAir = true;
            e.OtiConflict |= e.K > 0 && !SameOti(e.Oti, frame.Oti); // a row that came first said otherwise
            e.Oti = frame.Oti;
            e.Dictionary = frame.DictionaryId;
            e.K = frame.Oti.SourceBlockSymbols(0);
        }
        else if (!SameOti(e.Oti, frame.Oti) || e.Dictionary != frame.DictionaryId)
        {
            e.OtiConflict = true;
        }
    }

    /// <summary>Keeps the rebuilt octets of an object, if they match its ID with its dictionary. False (and nothing kept) if not.</summary>
    public bool SetData(ulong objectId, ushort dictionaryId, byte[] data)
    {
        if (ObjectId.Of(dictionaryId, data) != objectId)
        {
            return false;
        }
        List<ulong> dropped = [];
        lock (_gate)
        {
            var e = Find(objectId, create: true)!;
            if (e.Data is null)
            {
                e.Data = data;
                e.Dictionary = dictionaryId;
                e.Bytes += data.Length;
                _bytes += data.Length;
                e.Updated = time.GetUtcNow();
                dropped = Trim(objectId);
            }
        }
        Raise(dropped);
        return true;
    }

    /// <summary>Whether the rebuilt object is held.</summary>
    public bool HasData(ulong objectId)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(objectId, out var e) && e.Data is not null;
        }
    }

    /// <summary>A copy of one object as held now; null if not held.</summary>
    public CatalogueObject? Get(ulong objectId)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(objectId, out var e) ? Snapshot(e) : null;
        }
    }

    /// <summary>A copy of every object held, most recently changed first.</summary>
    public IReadOnlyList<CatalogueObject> All()
    {
        lock (_gate)
        {
            return [.. _objects.Values.OrderByDescending(e => e.Updated).Select(Snapshot)];
        }
    }

    /// <summary>Counts, for the status page.</summary>
    public CatalogueTotals Totals()
    {
        lock (_gate)
        {
            return new CatalogueTotals(
                _objects.Count,
                _objects.Values.Count(e => e.Data is not null),
                _objects.Values.Sum(e => e.Pieces.Count),
                _objects.Values.Sum(e => e.Pieces.Values.Count(p => p.OnAir)),
                _bytes);
        }
    }

    private static CatalogueObject Snapshot(Entry e)
    {
        var onAir = e.Pieces.Values.Where(p => p.OnAir).Select(p => p.Heard).ToList();
        DateTimeOffset? first = onAir.Count > 0 ? onAir.Min() : null;
        DateTimeOffset? last = onAir.Count > 0 ? onAir.Max() : null;
        if (e.AirFirst is { } af && (first is null || af < first))
        {
            first = af;
        }
        if (e.AirLast is { } al && (last is null || al > last))
        {
            last = al;
        }
        return Make(e, first, last);
    }

    private static CatalogueObject Make(Entry e, DateTimeOffset? first, DateTimeOffset? last) => new()
    {
        Id = e.Id,
        ContentType = e.Type,
        DictionaryId = e.Dictionary,
        Oti = e.Oti,
        OtiFromAir = e.OtiFromAir,
        OtiConflict = e.OtiConflict,
        AirFrames = e.AirFrames,
        AirByStation = new Dictionary<string, int>(e.AirByStation, StringComparer.Ordinal),
        FirstHeard = first,
        LastHeard = last,
        K = e.K,
        Pieces = [.. e.Pieces.Values],
        ListedBy = new Dictionary<string, string?>(e.Listings, StringComparer.Ordinal),
        EnvelopeLinks = new Dictionary<string, EnvelopeLink>(e.EnvelopeLinks, StringComparer.Ordinal),
        Data = e.Data,
        Updated = e.Updated,
    };

    private Entry? Find(ulong id, bool create)
    {
        if (!_objects.TryGetValue(id, out var e) && create)
        {
            _objects[id] = e = new Entry { Id = id, Updated = time.GetUtcNow() };
        }
        return e;
    }

    // Drops the object changed longest ago, again and again, while over a limit; never the one just changed. Under the lock.
    private List<ulong> Trim(ulong keep)
    {
        var dropped = new List<ulong>();
        while (_objects.Count > 1 && (_bytes > MaxBytes || _objects.Count > MaxObjects))
        {
            var oldest = _objects.Values.Where(e => e.Id != keep).MinBy(e => e.Updated);
            if (oldest is null)
            {
                break;
            }
            _objects.Remove(oldest.Id);
            _bytes -= oldest.Bytes;
            dropped.Add(oldest.Id);
        }
        return dropped;
    }

    private void Raise(List<ulong> dropped)
    {
        foreach (ulong id in dropped)
        {
            Evicted?.Invoke(id);
        }
    }
}
