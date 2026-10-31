using System.Globalization;
using System.Text;
using Mailcast.RaptorQ;

namespace Packet.Mailcast;

/// <summary>Where a kept piece came from.</summary>
/// <param name="Outside">False for a piece heard on air; true for one handed in from outside (a program posting to the API).</param>
/// <param name="Label">
/// On air, the station whose slot it was heard in, if known. From outside, the optional source label the
/// poster gave. Plain printable ASCII, at most <see cref="PieceKeep.MaxLabel"/> characters.
/// </param>
public readonly record struct PieceSource(bool Outside, string? Label = null);

/// <summary>One object in the receiver's keep: everything heard of it, whatever its content type.</summary>
/// <param name="ObjectId">The object's ID.</param>
/// <param name="DictionaryId">The zstd dictionary it was compressed with.</param>
/// <param name="Oti">How it is RaptorQ coded.</param>
/// <param name="ContentType">Its content type if known: from its rebuilt octets, else from piece 0; null otherwise.</param>
/// <param name="PiecesHeld">Pieces held.</param>
/// <param name="PiecesOutside">Of those, how many came from outside.</param>
/// <param name="Complete">Whether it has been rebuilt and checked against its ID.</param>
/// <param name="Cursor">The cursor value of its latest change; see <see cref="PieceKeep"/>.</param>
/// <param name="Updated">When it last changed.</param>
public sealed record KeptObject(ulong ObjectId, ushort DictionaryId, ObjectTransmissionInformation Oti, byte? ContentType, int PiecesHeld, int PiecesOutside, bool Complete, long Cursor, DateTimeOffset Updated);

/// <summary>One kept piece, as a whole frame.</summary>
/// <param name="EncodingSymbolId">The piece number (ESI).</param>
/// <param name="Frame">The frame payload exactly as sent: header, symbol and CRC.</param>
/// <param name="Heard">When it was kept.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Cursor">The cursor value of its latest change.</param>
public sealed record KeptPiece(uint EncodingSymbolId, byte[] Frame, DateTimeOffset Heard, PieceSource Source, long Cursor);

/// <summary>
/// The receiver's keep of pieces (issue #214): every frame stored for any object, of any content
/// type, with where it came from, and the rebuilt octets of every object that matched its ID. It is
/// kept apart from the delivery path so that what reaches the BBS does not depend on it; the
/// <see cref="ReceiverStore"/> feeds it and reads provenance back from it. Bounded by a byte limit
/// and a retention; the oldest go first.
/// </summary>
/// <remarks>
/// <para>Layout under the store's <c>keep/</c> folder: one folder for each object variant (named like
/// the partial object's, ID, dictionary and OTI), holding <c>EEEEEE.frm</c> per piece (a text line
/// <c>a</c> or <c>o</c>, a tab and the label, a newline, then the frame; its modification time is when it
/// was kept) and <c>object.bin</c>, the rebuilt octets, once there are any. <c>cursor</c> holds a ceiling
/// above every cursor value ever handed out.</para>
/// <para>The cursor is one counter for objects and pieces together. A change takes the next value; a
/// reader asks for what is above the last value it saw. At start every kept thing is given a fresh value
/// above the ceiling, so a reader's old cursor, from before a restart, still finds everything once more
/// and never misses a change. Not thread-safe: the owner calls it from one thread at a time.</para>
/// </remarks>
internal sealed class PieceKeep
{
    /// <summary>The folder under the store's root.</summary>
    public const string Folder = "keep";

    /// <summary>The longest label kept.</summary>
    public const int MaxLabel = 48;

    private const string CursorFile = "cursor";
    private const string ObjectFile = "object.bin";
    private const long CursorBlock = 1000;

    private readonly string _root;
    private readonly bool _flush;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, List<Entry>> _byId = [];
    private long _cursor;
    private long _ceiling;
    private long _bytes;

    public PieceKeep(string storeRoot, bool flush, Action<string>? log)
    {
        _root = Path.Combine(storeRoot, Folder);
        _flush = flush;
        _log = log;
        System.IO.Directory.CreateDirectory(_root);
        string cursorFile = Path.Combine(_root, CursorFile);
        if (File.Exists(cursorFile) && long.TryParse(File.ReadAllText(cursorFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long ceiling))
        {
            _cursor = ceiling;
        }
        Load();
    }

    /// <summary>Keys of part-received objects the delivery path still holds with pieces from outside; the keep never drops these (their provenance decides what may be delivered).</summary>
    public Func<string, bool> IsProtected { get; set; } = _ => false;

    /// <summary>Octets held elsewhere on behalf of the keep (symbol files of objects with nothing heard on air), counted against the limit.</summary>
    public Func<long> ExtraBytes { get; set; } = () => 0;

    /// <summary>Keys of part-received objects with nothing heard on air.</summary>
    public Func<string, bool> IsOutsideOnly { get; set; } = _ => false;

    /// <summary>Octets the keep holds for objects with nothing heard on air.</summary>
    public long OutsideOnlyEntryBytes() => _entries.Values.Where(e => IsOutsideOnly(e.Key)).Sum(e => e.Bytes);

    /// <summary>How many octets the keep holds.</summary>
    public long Bytes => _bytes;

    /// <summary>How many objects it holds.</summary>
    public int Objects => _entries.Count;

    /// <summary>How many pieces it holds, and how many of them came from outside.</summary>
    public (int Pieces, int Outside) PieceCounts()
    {
        int pieces = 0, outside = 0;
        foreach (var e in _entries.Values)
        {
            pieces += e.Pieces.Count;
            outside += e.Pieces.Values.Count(p => p.Source.Outside);
        }
        return (pieces, outside);
    }

    /// <summary>The latest cursor value handed out.</summary>
    public long Cursor => _cursor;

    /// <summary>Whether an object this size is kept at all.</summary>
    public static bool Fits(ObjectTransmissionInformation oti, long maxObjectBytes) => oti.TransferLength <= maxObjectBytes;

    /// <summary>A label cut down to printable ASCII without control characters, or null for none.</summary>
    public static string? CleanLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }
        var sb = new StringBuilder();
        foreach (char c in label.Trim())
        {
            sb.Append(c is >= ' ' and <= '~' ? c : '_');
            if (sb.Length >= MaxLabel)
            {
                break;
            }
        }
        return sb.ToString();
    }

    private long Next()
    {
        _cursor++;
        if (_cursor > _ceiling)
        {
            _ceiling = _cursor + CursorBlock;
            DurableFile.WriteAtomically(Path.Combine(_root, CursorFile), Encoding.ASCII.GetBytes(_ceiling.ToString(CultureInfo.InvariantCulture)), flush: _flush);
        }
        return _cursor;
    }

    private sealed class Entry(string key, ulong objectId, ushort dictionaryId, ObjectTransmissionInformation oti)
    {
        public string Key { get; } = key;

        public ulong ObjectId { get; } = objectId;

        public ushort DictionaryId { get; } = dictionaryId;

        public ObjectTransmissionInformation Oti { get; } = oti;

        public string Dir { get; set; } = "";

        public Dictionary<uint, Piece> Pieces { get; } = [];

        public bool Complete { get; set; }

        public long ObjectBytes { get; set; }

        public byte? Type { get; set; }

        public long Cursor { get; set; }

        public DateTimeOffset Updated { get; set; }

        public long Bytes { get; set; }

        /// <summary>Stations whose trusted signed directories have been seen listing it, since the receiver started.</summary>
        public HashSet<string> Listers { get; } = new(StringComparer.Ordinal);
    }

    private sealed record Piece(int Length, DateTimeOffset Heard, PieceSource Source, long Cursor);

    private static string PiecePath(Entry e, uint esi) => Path.Combine(e.Dir, esi.ToString("x6", CultureInfo.InvariantCulture) + ".frm");

    private static byte[] Encode(PieceSource source, ReadOnlySpan<byte> frame)
    {
        var head = Encoding.ASCII.GetBytes((source.Outside ? "o" : "a") + "\t" + (source.Label ?? "") + "\n");
        var bytes = new byte[head.Length + frame.Length];
        head.CopyTo(bytes, 0);
        frame.CopyTo(bytes.AsSpan(head.Length));
        return bytes;
    }

    private static bool TryDecode(byte[] file, out PieceSource source, out byte[] frame)
    {
        source = default;
        frame = [];
        int nl = Array.IndexOf(file, (byte)'\n');
        if (nl < 2 || nl > MaxLabel + 4 || file[1] != (byte)'\t' || file[0] is not ((byte)'a' or (byte)'o'))
        {
            return false;
        }
        string? label = CleanLabel(Encoding.ASCII.GetString(file, 2, nl - 2));
        source = new PieceSource(file[0] == (byte)'o', label);
        frame = file[(nl + 1)..];
        return true;
    }

    private void Load()
    {
        var found = new List<(Entry Entry, DateTimeOffset Updated)>();
        foreach (string dir in System.IO.Directory.EnumerateDirectories(_root))
        {
            var entry = TryOpen(dir);
            if (entry is not null)
            {
                found.Add((entry, entry.Updated));
            }
        }
        // Oldest first, so a restart keeps the order things changed in.
        foreach (var (entry, _) in found.OrderBy(f => f.Updated).ThenBy(f => f.Entry.Key, StringComparer.Ordinal))
        {
            entry.Cursor = Next();
            foreach (var esi in entry.Pieces.OrderBy(p => p.Value.Heard).ThenBy(p => p.Key).Select(p => p.Key).ToList())
            {
                entry.Pieces[esi] = entry.Pieces[esi] with { Cursor = Next() };
            }
            _entries[entry.Key] = entry;
            AddToIdIndex(entry);
            _bytes += entry.Bytes;
        }
    }

    private Entry? TryOpen(string dir)
    {
        string name = Path.GetFileName(dir);
        var parts = name.Split('-');
        if (parts.Length != 3
            || !ObjectId.TryParse(parts[0], out ulong objectId)
            || !ushort.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort dictionaryId)
            || parts[2].Length != ObjectTransmissionInformation.EncodedLength * 2)
        {
            return null;
        }
        ObjectTransmissionInformation oti;
        try
        {
            oti = ObjectTransmissionInformation.Read(Convert.FromHexString(parts[2]));
        }
        catch (FormatException)
        {
            return null;
        }
        var entry = new Entry(name, objectId, dictionaryId, oti) { Dir = dir };
        var last = DateTimeOffset.MinValue;
        foreach (string file in System.IO.Directory.EnumerateFiles(dir))
        {
            string leaf = Path.GetFileName(file);
            DateTimeOffset written = new(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            try
            {
                if (leaf == ObjectFile)
                {
                    var bytes = File.ReadAllBytes(file);
                    if (bytes.Length == 0)
                    {
                        File.Delete(file);
                        continue;
                    }
                    entry.Complete = true;
                    entry.ObjectBytes = bytes.Length;
                    entry.Type = (byte)(bytes[0] & ~ContentType.MetadataFollows);
                    entry.Bytes += bytes.Length;
                }
                else if (leaf.EndsWith(".frm", StringComparison.Ordinal)
                    && uint.TryParse(leaf[..^4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint esi)
                    && esi <= PayloadId.MaxEncodingSymbolId)
                {
                    var raw = File.ReadAllBytes(file);
                    if (!TryDecode(raw, out var source, out var frame) || !MailcastFrame.TryParse(frame, out var parsed) || parsed is null || parsed.EncodingSymbolId != esi || parsed.ObjectId != objectId)
                    {
                        File.Delete(file);
                        continue;
                    }
                    entry.Pieces[esi] = new Piece(raw.Length, written, source, 0);
                    entry.Bytes += raw.Length;
                    if (esi == 0 && entry.Type is null)
                    {
                        entry.Type = TypeOfFrame(frame);
                    }
                }
                else if (leaf.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    File.Delete(file);
                    continue;
                }
                else
                {
                    continue;
                }
                if (written > last)
                {
                    last = written;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"keep: {leaf} of {name} unreadable, skipped: {e.Message}");
            }
        }
        if (entry.Pieces.Count == 0 && !entry.Complete)
        {
            System.IO.Directory.Delete(dir, recursive: true);
            return null;
        }
        entry.Updated = last;
        return entry;
    }

    private static byte? TypeOfFrame(ReadOnlySpan<byte> frame) =>
        frame.Length > MailcastFrame.HeaderLength + MailcastFrame.CrcLength ? (byte)(frame[MailcastFrame.HeaderLength] & ~ContentType.MetadataFollows) : null;

    private void AddToIdIndex(Entry entry)
    {
        if (!_byId.TryGetValue(entry.ObjectId, out var list))
        {
            _byId[entry.ObjectId] = list = [];
        }
        list.Add(entry);
    }

    private Entry Open(string key, ulong objectId, ushort dictionaryId, ObjectTransmissionInformation oti, DateTimeOffset now)
    {
        if (_entries.TryGetValue(key, out var held))
        {
            return held;
        }
        var entry = new Entry(key, objectId, dictionaryId, oti) { Dir = Path.Combine(_root, key), Updated = now };
        System.IO.Directory.CreateDirectory(entry.Dir);
        _entries[key] = entry;
        AddToIdIndex(entry);
        return entry;
    }

    /// <summary>The source of a piece held, or null if it is not held.</summary>
    public PieceSource? SourceOf(string key, uint esi) =>
        _entries.TryGetValue(key, out var e) && e.Pieces.TryGetValue(esi, out var p) ? p.Source : null;

    /// <summary>Whether any piece of this object variant came from outside.</summary>
    public bool HasOutside(string key) => _entries.TryGetValue(key, out var e) && e.Pieces.Values.Any(p => p.Source.Outside);

    /// <summary>The ESIs of the pieces held for an object variant that came from outside, with their labels.</summary>
    public List<(uint Esi, string? Label)> OutsidePieces(string key) =>
        _entries.TryGetValue(key, out var e) ? [.. e.Pieces.Where(p => p.Value.Source.Outside).Select(p => (p.Key, p.Value.Source.Label))] : [];

    /// <summary>The pieces of this variant that were heard on air, with the station each was attributed to (null if none was).</summary>
    public List<(uint Esi, string? Label)> AirPieces(string key) =>
        _entries.TryGetValue(key, out var e) ? [.. e.Pieces.Where(p => !p.Value.Source.Outside).Select(p => (p.Key, p.Value.Source.Label))] : [];

    /// <summary>Whether this variant has its rebuilt octets.</summary>
    public bool IsComplete(string key) => _entries.TryGetValue(key, out var e) && e.Complete;

    /// <summary>Whether this variant holds this piece.</summary>
    public bool Holds(string key, uint esi) => _entries.TryGetValue(key, out var e) && e.Pieces.ContainsKey(esi);

    /// <summary>
    /// Keeps a frame (or replaces the one held for that piece number). Failures to write are logged
    /// and not thrown: the keep is not allowed to hold up delivery.
    /// </summary>
    public void Add(string key, ulong objectId, ushort dictionaryId, ObjectTransmissionInformation oti, uint esi, byte[] frame, DateTimeOffset now, PieceSource source, long maxBytes)
    {
        try
        {
            var entry = Open(key, objectId, dictionaryId, oti, now);
            var raw = Encode(source, frame);
            string path = PiecePath(entry, esi);
            DurableFile.WriteAtomically(path, raw, now, flush: false);
            if (entry.Pieces.TryGetValue(esi, out var old))
            {
                entry.Bytes -= old.Length;
                _bytes -= old.Length;
            }
            long cursor = Next();
            entry.Pieces[esi] = new Piece(raw.Length, now, source, cursor);
            entry.Bytes += raw.Length;
            _bytes += raw.Length;
            if (esi == 0 && entry.Type is null)
            {
                entry.Type = TypeOfFrame(frame);
            }
            entry.Updated = now;
            entry.Cursor = cursor;
            Enforce(maxBytes, entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"keep: cannot keep a piece of {ObjectId.Format(objectId)}: {e.Message}");
        }
    }

    /// <summary>Changes a held piece's source, keeping its frame (a piece heard on air that was already held from outside).</summary>
    public void SetSource(string key, uint esi, PieceSource source, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(key, out var entry) || !entry.Pieces.TryGetValue(esi, out var piece))
        {
            return;
        }
        try
        {
            string path = PiecePath(entry, esi);
            var raw = File.ReadAllBytes(path);
            if (!TryDecode(raw, out _, out var frame))
            {
                return;
            }
            var next = Encode(source, frame);
            DurableFile.WriteAtomically(path, next, piece.Heard, flush: false);
            entry.Bytes += next.Length - piece.Length;
            _bytes += next.Length - piece.Length;
            long cursor = Next();
            entry.Pieces[esi] = piece with { Length = next.Length, Source = source, Cursor = cursor };
            entry.Updated = now;
            entry.Cursor = cursor;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"keep: cannot change a piece of {key}: {e.Message}");
        }
    }

    /// <summary>Drops one piece (found to be bad).</summary>
    public void RemovePiece(string key, uint esi, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(key, out var entry) || !entry.Pieces.Remove(esi, out var piece))
        {
            return;
        }
        entry.Bytes -= piece.Length;
        _bytes -= piece.Length;
        try
        {
            File.Delete(PiecePath(entry, esi));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"keep: cannot remove a piece of {key}: {e.Message}");
        }
        entry.Updated = now;
        entry.Cursor = Next();
    }

    /// <summary>Keeps the rebuilt octets of an object that matched its ID.</summary>
    public void Complete(string key, ulong objectId, ushort dictionaryId, ObjectTransmissionInformation oti, byte[] data, DateTimeOffset now, long maxBytes)
    {
        if (data.Length == 0)
        {
            return;
        }
        try
        {
            var entry = Open(key, objectId, dictionaryId, oti, now);
            if (!entry.Complete)
            {
                DurableFile.WriteAtomically(Path.Combine(entry.Dir, ObjectFile), data, now, flush: _flush);
                entry.Complete = true;
                entry.ObjectBytes = data.Length;
                entry.Bytes += data.Length;
                _bytes += data.Length;
            }
            entry.Type = (byte)(data[0] & ~ContentType.MetadataFollows);
            entry.Updated = now;
            entry.Cursor = Next();
            Enforce(maxBytes, entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"keep: cannot keep object {ObjectId.Format(objectId)}: {e.Message}");
        }
    }

    /// <summary>
    /// Notes that a trusted signed directory of <paramref name="station"/> lists the object, giving it a
    /// new cursor value the first time for each station, so a reader of the index sees the change.
    /// </summary>
    public void NoteListed(ulong objectId, string station)
    {
        if (!_byId.TryGetValue(objectId, out var list))
        {
            return;
        }
        foreach (var entry in list)
        {
            if (entry.Listers.Add(station))
            {
                entry.Cursor = Next();
            }
        }
    }

    /// <summary>Forgets what is older than <paramref name="retention"/> since it last changed.</summary>
    public void Prune(DateTimeOffset now, TimeSpan retention)
    {
        foreach (var entry in _entries.Values.Where(e => now - e.Updated > retention).ToList())
        {
            Drop(entry);
        }
    }

    private void Enforce(long maxBytes, Entry added)
    {
        // Objects with nothing heard on air cannot push out the others below half the limit: they have their own cap.
        while (_bytes + ExtraBytes() > maxBytes && _bytes - OutsideOnlyEntryBytes() > maxBytes / 2)
        {
            // Rebuilt objects go before part-received ones, the oldest first: a part-received object may still be finished.
            var victim = _entries.Values.Where(e => e != added && !IsProtected(e.Key)).OrderByDescending(e => e.Complete).ThenBy(e => e.Updated).FirstOrDefault();
            if (victim is null)
            {
                if (!IsProtected(added.Key))
                {
                    _log?.Invoke($"keep: over its size limit; dropped {ObjectId.Format(added.ObjectId)} itself");
                    Drop(added);
                }
                return;
            }
            _log?.Invoke($"keep: over its size limit; dropped {ObjectId.Format(victim.ObjectId)}, last changed {victim.Updated:u}");
            Drop(victim);
        }
    }

    private void Drop(Entry entry)
    {
        _entries.Remove(entry.Key);
        if (_byId.TryGetValue(entry.ObjectId, out var list))
        {
            list.Remove(entry);
            if (list.Count == 0)
            {
                _byId.Remove(entry.ObjectId);
            }
        }
        _bytes -= entry.Bytes;
        try
        {
            if (System.IO.Directory.Exists(entry.Dir))
            {
                System.IO.Directory.Delete(entry.Dir, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"keep: cannot remove {entry.Key}: {e.Message}");
        }
    }

    /// <summary>The variant of an object to show: the rebuilt one, else the one with most pieces.</summary>
    private Entry? Best(ulong objectId) =>
        _byId.TryGetValue(objectId, out var list) ? list.OrderByDescending(e => e.Complete).ThenByDescending(e => e.Pieces.Count).ThenBy(e => e.Key, StringComparer.Ordinal).First() : null;

    private static KeptObject Row(Entry e) =>
        new(e.ObjectId, e.DictionaryId, e.Oti, e.Type, e.Pieces.Count, e.Pieces.Values.Count(p => p.Source.Outside), e.Complete, e.Cursor, e.Updated);

    /// <summary>
    /// One row for each object whose cursor is above <paramref name="since"/>, in cursor order, at most
    /// <paramref name="limit"/> of them; <paramref name="more"/> says there are more.
    /// </summary>
    public List<KeptObject> Index(long since, int limit, out bool more)
    {
        var rows = _byId.Keys.Select(Best).Where(e => e is not null && e.Cursor > since).Select(e => e!).OrderBy(e => e.Cursor).ToList();
        more = rows.Count > limit;
        return [.. rows.Take(limit).Select(Row)];
    }

    /// <summary>The row for one object, or null.</summary>
    public KeptObject? Find(ulong objectId) => Best(objectId) is { } e ? Row(e) : null;

    /// <summary>The kept pieces of an object whose cursor is above <paramref name="since"/>, in cursor order, with their frames read from disk; null if the object is not kept.</summary>
    public List<KeptPiece>? Pieces(ulong objectId, long since, int limit, out bool more)
    {
        more = false;
        if (Best(objectId) is not { } entry)
        {
            return null;
        }
        var chosen = entry.Pieces.Where(p => p.Value.Cursor > since).OrderBy(p => p.Value.Cursor).ToList();
        more = chosen.Count > limit;
        var result = new List<KeptPiece>();
        foreach (var (esi, piece) in chosen.Take(limit))
        {
            try
            {
                if (TryDecode(File.ReadAllBytes(PiecePath(entry, esi)), out _, out var frame))
                {
                    result.Add(new KeptPiece(esi, frame, piece.Heard, piece.Source, piece.Cursor));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"keep: cannot read a piece of {entry.Key}: {e.Message}");
            }
        }
        return result;
    }

    /// <summary>The rebuilt octets and dictionary of an object, or null if it is not rebuilt (or cannot be read).</summary>
    public (byte[] Data, ushort DictionaryId)? ObjectData(ulong objectId)
    {
        if (_byId.TryGetValue(objectId, out var list) && list.FirstOrDefault(e => e.Complete) is { } entry)
        {
            try
            {
                return (File.ReadAllBytes(Path.Combine(entry.Dir, ObjectFile)), entry.DictionaryId);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"keep: cannot read object {entry.Key}: {e.Message}");
            }
        }
        return null;
    }
}
