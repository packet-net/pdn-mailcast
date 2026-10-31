using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mailcast.RaptorQ;

namespace Packet.Mailcast;

/// <summary>One row of the receiver's index of objects (issue #214).</summary>
/// <param name="Kept">What the keep holds of it.</param>
/// <param name="ContentType">Its content type if known: from its rebuilt octets, from piece 0, or from a directory that lists it.</param>
/// <param name="ListedBy">The stations whose trusted signed directories, still remembered, list it.</param>
/// <param name="Listings">The same stations with the long hash (<see cref="DirectoryEntry.Hash"/>, 16 hex digits) each lists it with; null where a listing carries none.</param>
public sealed record ObjectRow(KeptObject Kept, byte? ContentType, IReadOnlyList<string> ListedBy, IReadOnlyList<ObjectListing>? Listings = null);

/// <summary>One trusted signed directory's listing of an object.</summary>
/// <param name="Station">The station whose directory it is.</param>
/// <param name="Hash">The long hash it lists, or null.</param>
public sealed record ObjectListing(string Station, string? Hash);

/// <summary>A page of <see cref="ObjectRow"/>s.</summary>
/// <param name="Rows">The rows whose cursor is above the one asked for, in cursor order.</param>
/// <param name="Next">The cursor to ask from next time: the last row's, or the one asked for when there is none.</param>
/// <param name="More">Whether more rows wait above <paramref name="Next"/>.</param>
public sealed record ObjectPage(IReadOnlyList<ObjectRow> Rows, long Next, bool More);

/// <summary>A page of one object's kept pieces.</summary>
/// <param name="Kept">The object's row.</param>
/// <param name="Pieces">The pieces whose cursor is above the one asked for, in cursor order.</param>
/// <param name="Next">The cursor to ask from next time.</param>
/// <param name="More">Whether more wait above <paramref name="Next"/>.</param>
public sealed record PiecePage(ObjectRow Kept, IReadOnlyList<KeptPiece> Pieces, long Next, bool More);

/// <summary>What the keep holds, for the status page.</summary>
/// <param name="Objects">Objects kept.</param>
/// <param name="Pieces">Pieces kept.</param>
/// <param name="OutsidePieces">Of those, pieces that came from outside.</param>
/// <param name="Bytes">Octets on disk.</param>
public sealed record KeepStats(int Objects, int Pieces, int OutsidePieces, long Bytes);

public sealed partial class ReceiverStore
{
    private readonly PieceKeep _keep;

    /// <summary>
    /// What the objects with nothing heard on air hold, the keep's copy and the delivery path's: pieces from
    /// outside may take up to half of <see cref="ReceiverStoreOptions.KeepMaxBytes"/>, no more.
    /// </summary>
    private long OutsideOnlyTotal() => OutsideOnlyBytes() + _keep.OutsideOnlyEntryBytes();

    /// <summary>The octets the delivery path holds for part-received objects with nothing heard on air; they count against the keep's limit.</summary>
    private long OutsideOnlyBytes() => _instances.Values.Where(i => !i.HasAir).Sum(i => i.Symbols.Values.Sum(v => (long)v.Length));

    /// <summary>Where the API key is kept, under the store's folder.</summary>
    public const string ApiKeyFile = "api-key.txt";

    private string? _apiKey;

    /// <summary>What the keep holds.</summary>
    public KeepStats KeepStats()
    {
        var (pieces, outside) = _keep.PieceCounts();
        return new KeepStats(_keep.Objects, pieces, outside, _keep.Bytes);
    }

    /// <summary>
    /// The index of objects kept (issue #214): every object any piece of which was kept, of any content
    /// type, in cursor order, those changed since <paramref name="since"/>, at most <paramref name="limit"/>.
    /// </summary>
    public ObjectPage KeptObjects(long since, int limit)
    {
        var listed = ListedByStation();
        var kept = _keep.Index(since, limit, out bool more);
        var rows = kept.Select(k => Row(k, listed)).ToList();
        return new ObjectPage(rows, rows.Count > 0 ? rows[^1].Kept.Cursor : since, more);
    }

    /// <summary>One object's kept pieces (issue #214), or null if the object is not kept.</summary>
    public PiecePage? KeptPieces(ulong objectId, long since, int limit)
    {
        if (_keep.Find(objectId) is not { } kept)
        {
            return null;
        }
        var pieces = _keep.Pieces(objectId, since, limit, out bool more) ?? [];
        return new PiecePage(Row(kept, ListedByStation()), pieces, pieces.Count > 0 ? pieces[^1].Cursor : since, more);
    }

    /// <summary>The rebuilt octets of an object and the dictionary ID they were compressed with, only once it matched its ID; else null.</summary>
    public (byte[] Data, ushort DictionaryId)? KeptData(ulong objectId)
    {
        if (_keep.ObjectData(objectId) is not { } held)
        {
            return null;
        }
        return ObjectId.Of(held.DictionaryId, held.Data) == objectId ? held : null; // checked again: the file may have been damaged on disk
    }

    private ObjectRow Row(KeptObject kept, Dictionary<ulong, SortedDictionary<string, string?>> listed)
    {
        listed.TryGetValue(kept.ObjectId, out var by);
        byte? type = kept.ContentType ?? TypeFromDirectories(kept.ObjectId);
        return new ObjectRow(kept, type, by is null ? [] : [.. by.Keys], by is null ? [] : [.. by.Select(b => new ObjectListing(b.Key, b.Value))]);
    }

    private IEnumerable<(BroadcastDirectory Directory, string Station)> ListingDirectories()
    {
        foreach (var (directory, _, check) in _knownDirectories.Values)
        {
            if (StationOf(directory, check) is { } station)
            {
                yield return (directory, station);
            }
        }
    }

    private Dictionary<ulong, SortedDictionary<string, string?>> ListedByStation()
    {
        var listed = new Dictionary<ulong, SortedDictionary<string, string?>>();
        void Note(ulong id, string station, string? hash)
        {
            if (!listed.TryGetValue(id, out var set))
            {
                listed[id] = set = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            }
            set[station] = hash;
        }
        foreach (var (directory, station) in ListingDirectories())
        {
            foreach (var entry in directory.Entries)
            {
                Note(entry.ObjectId, station, entry.Hash);
                if (entry.Envelope is { } link)
                {
                    Note(link.ObjectId, station, link.Hash);
                }
            }
        }
        return listed;
    }

    private byte? TypeFromDirectories(ulong objectId)
    {
        foreach (var (directory, _) in ListingDirectories())
        {
            if (directory.Find(objectId) is { } entry)
            {
                return entry.Type;
            }
            if (directory.Entries.Any(e => e.Envelope?.ObjectId == objectId))
            {
                return (byte)ObjectKind.Envelope;
            }
        }
        return null;
    }

    /// <summary>Keeps the rebuilt octets of an object that matched its ID, and makes sure the pieces behind it are kept too.</summary>
    private void KeepObject(Instance instance, byte[] data) =>
        _keep.Complete(instance.Key, instance.ObjectId, instance.DictionaryId, instance.Oti, data, _options.Time.GetUtcNow(), _options.KeepMaxBytes);

    /// <summary>Keeps the pieces of a partial object loaded from before the keep existed, as heard on air.</summary>
    private void BackfillKeep(Instance instance)
    {
        foreach (var (esi, symbol) in instance.Symbols)
        {
            if (_keep.Holds(instance.Key, esi))
            {
                continue;
            }
            try
            {
                var frame = new MailcastFrame(instance.ObjectId, instance.DictionaryId, instance.Oti, esi, symbol).ToBytes();
                _keep.Add(instance.Key, instance.ObjectId, instance.DictionaryId, instance.Oti, esi, frame, instance.LastPiece, new PieceSource(false), _options.KeepMaxBytes);
            }
            catch (ArgumentException)
            {
                // Not a symbol the format can carry; the delivery path has it, the keep need not.
            }
        }
    }

    /// <summary>
    /// A rebuild failed its ID check and some pieces came from outside: try again without them all, then
    /// without each label's, before waiting for more (issue #214). If one set rebuilds to the ID, the pieces
    /// from outside that do not agree with the true object are dropped. Null when it does not help.
    /// </summary>
    private byte[]? RebuildWithoutOutside(Instance instance)
    {
        var outside = _keep.OutsidePieces(instance.Key).Where(p => instance.Symbols.ContainsKey(p.Esi)).ToList();
        if (outside.Count == 0 || instance.OutsideTried == instance.Symbols.Count)
        {
            return null;
        }
        instance.OutsideTried = instance.Symbols.Count;
        int k = instance.Oti.SourceBlockSymbols(0);
        var attempts = new List<HashSet<uint>> { new(outside.Select(p => p.Esi)) };
        foreach (var label in outside.Select(p => p.Label).Distinct().Order(StringComparer.Ordinal))
        {
            var group = outside.Where(p => p.Label == label).Select(p => p.Esi).ToHashSet();
            if (group.Count != outside.Count)
            {
                attempts.Add(group);
            }
        }
        foreach (var left in attempts)
        {
            var use = instance.Arrival.Where(e => !left.Contains(e) && instance.Symbols.ContainsKey(e)).ToList();
            if (use.Count < k)
            {
                continue;
            }
            var decoder = new ObjectDecoder(instance.Oti);
            foreach (uint esi in use)
            {
                decoder.Add(new PayloadId(0, esi), instance.Symbols[esi]);
            }
            var candidate = decoder.TryDecode();
            if (candidate is null || ObjectId.Of(instance.DictionaryId, candidate) != instance.ObjectId)
            {
                continue;
            }
            // The true object is known now, so each piece from outside can be checked against it.
            var encoder = new ObjectEncoder(candidate, instance.Oti);
            var bad = outside.Where(p => !encoder.Encode(new PayloadId(0, p.Esi)).AsSpan().SequenceEqual(instance.Symbols[p.Esi])).ToList();
            foreach (var (esi, _) in bad)
            {
                instance.Symbols.Remove(esi);
                instance.Arrival.Remove(esi);
                TryDelete(SymbolPath(instance, esi));
                _keep.RemovePiece(instance.Key, esi, _options.Time.GetUtcNow());
            }
            instance.ResetDecoder();
            Log($"object {ObjectId.Format(instance.ObjectId)}: {bad.Count} bad piece(s) from outside ({string.Join(',', bad.Select(b => b.Label ?? "?").Distinct().Order(StringComparer.Ordinal))}) dropped; rebuilt without them");
            return candidate;
        }
        return null;
    }

    // ---- the API key

    /// <summary>
    /// The key that lets a program hand pieces in (issue #214), or null when none is set. Kept in the
    /// store, never in the config file, and shown on the status page.
    /// </summary>
    public string? ApiKey => _apiKey;

    private void LoadApiKey()
    {
        string path = Path.Combine(_root, ApiKeyFile);
        if (File.Exists(path))
        {
            string text = File.ReadAllText(path).Trim();
            _apiKey = text.Length > 0 && text.All(c => c is >= '!' and <= '~') ? text : null;
        }
    }

    /// <summary>Makes a new key, replacing any, and returns it.</summary>
    public string NewApiKey()
    {
        string key = "mcr_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
        DurableFile.WriteAtomically(Path.Combine(_root, ApiKeyFile), Encoding.ASCII.GetBytes(key + "\n"), flush: _options.FlushToDisk);
        TrySecure(Path.Combine(_root, ApiKeyFile));
        _apiKey = key;
        return key;
    }

    /// <summary>Removes the key, so that handing pieces in is refused until a new one is made.</summary>
    public void ClearApiKey()
    {
        TryDelete(Path.Combine(_root, ApiKeyFile));
        _apiKey = null;
    }

    private static void TrySecure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether <paramref name="given"/> is the key, compared in constant time. False when no key is set.</summary>
    public bool ApiKeyMatches(string? given) =>
        _apiKey is { } key && given is not null
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// Whether a part-received object has a piece from outside and is, by its first piece, neither a bulletin
    /// nor a directory: it is kept to pass on, not listed with the bulletins. (A partial object heard only on
    /// air is listed as it always was.)
    /// </summary>
    private bool IsOtherTypeFromOutside(Instance instance) =>
        _keep.HasOutside(instance.Key)
        && instance.Symbols.TryGetValue(0, out var first) && first.Length > 0
        && (first[0] & ~ContentType.MetadataFollows) is not ((int)ObjectKind.Bulletin or (int)ObjectKind.Directory);
}
