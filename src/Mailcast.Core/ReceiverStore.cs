using System.Globalization;
using Mailcast.RaptorQ;

namespace Mailcast.Core;

/// <summary>What became of a frame offered to a <see cref="ReceiverStore"/>.</summary>
public enum FrameOutcome
{
    /// <summary>Not a mailcast frame of a version this code reads.</summary>
    NotAFrame,

    /// <summary>The object was already rebuilt; the frame is not needed.</summary>
    AlreadyComplete,

    /// <summary>The object was compressed with a dictionary this receiver does not have.</summary>
    UnknownDictionary,

    /// <summary>This symbol was already held.</summary>
    Duplicate,

    /// <summary>The symbol was stored; the object is not yet rebuilt.</summary>
    Stored,

    /// <summary>The symbol completed a bulletin, which is in the result.</summary>
    CompletedBulletin,

    /// <summary>The symbol completed a directory, which is in the result.</summary>
    CompletedDirectory,

    /// <summary>
    /// The frame, or the object it completed, failed a check. A rebuilt object that fails is
    /// thrown away with all its symbols, so it can be collected again.
    /// </summary>
    Rejected,
}

/// <summary>The result of offering a frame to a <see cref="ReceiverStore"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ObjectId">The frame's object, if it was a frame.</param>
/// <param name="Bulletin">The bulletin the frame completed, for <see cref="FrameOutcome.CompletedBulletin"/>.</param>
/// <param name="Directory">The directory the frame completed, for <see cref="FrameOutcome.CompletedDirectory"/>.</param>
/// <param name="Detail">Why, for <see cref="FrameOutcome.Rejected"/>.</param>
public sealed record AcceptResult(FrameOutcome Outcome, uint? ObjectId = null, Bulletin? Bulletin = null, BroadcastDirectory? Directory = null, string? Detail = null);

/// <summary>How far one directory entry has got at this receiver.</summary>
/// <param name="Entry">The directory's entry.</param>
/// <param name="Complete">Whether it has been rebuilt.</param>
/// <param name="Received">Symbols held, if not complete.</param>
/// <param name="Needed">Symbols needed at least (K), if any have been received.</param>
public sealed record ObjectProgress(DirectoryEntry Entry, bool Complete, int Received, int Needed);

/// <summary>
/// A receiver's store of broadcast symbols. It keeps every symbol on disk until its object is
/// rebuilt, so an object's pieces add up across days and restarts; it rebuilds each object as
/// soon as it can, checks it, and hands each bulletin over exactly once.
/// </summary>
/// <remarks>
/// <para>Files under the root directory, each written to a temporary name and then renamed:</para>
/// <code>
/// objects/OOOOOOOO-DDDD-OTI/EEEEEE.sym   one symbol: object ID, dictionary ID, OTI in hex; ESI in hex
/// done/OOOOOOOO                          the object has been rebuilt; later frames are ignored
/// outbox/OOOOOOOO.bulletin               a rebuilt bulletin not yet acknowledged
/// directory.txt                          the newest directory heard
/// </code>
/// <para>
/// A rebuilt bulletin is returned once, from the <see cref="Accept"/> call whose frame completed
/// it. It also stays in the outbox until <see cref="Acknowledge"/>, so one rebuilt just before a
/// crash is not lost: after a restart it is in <see cref="Pending"/>. A receiver delivers what
/// Accept returns, or what Pending lists at start-up, and acknowledges each once the BBS has
/// taken it. Objects whose symbols suffice when the store opens are rebuilt then, straight into
/// the outbox.
/// </para>
/// <para>
/// Symbols are kept per object ID, dictionary ID and OTI together, since a bulletin compressed
/// again with another dictionary is a different octet string and its symbols must not mix.
/// </para>
/// <para>Not thread-safe: use one store from one thread.</para>
/// </remarks>
public sealed class ReceiverStore
{
    private readonly string _root;
    private readonly string _objects;
    private readonly string _doneDir;
    private readonly string _outbox;
    private readonly Compression _compression;
    private readonly Dictionary<string, Instance> _instances = [];
    private readonly HashSet<uint> _done = [];

    /// <summary>Opens or creates a store, recovering from any interrupted write.</summary>
    public ReceiverStore(string root, Compression compression)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(compression);
        _root = root;
        _compression = compression;
        _objects = Path.Combine(root, "objects");
        _doneDir = Path.Combine(root, "done");
        _outbox = Path.Combine(root, "outbox");
        System.IO.Directory.CreateDirectory(_objects);
        System.IO.Directory.CreateDirectory(_doneDir);
        System.IO.Directory.CreateDirectory(_outbox);

        foreach (var tmp in System.IO.Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories))
        {
            File.Delete(tmp);
        }
        foreach (var marker in System.IO.Directory.EnumerateFiles(_doneDir))
        {
            if (TryParseObjectId(Path.GetFileName(marker), out uint id))
            {
                _done.Add(id);
            }
        }
        foreach (var file in System.IO.Directory.EnumerateFiles(_outbox, "*.bulletin"))
        {
            if (TryParseObjectId(Path.GetFileNameWithoutExtension(file), out uint id) && _done.Add(id))
            {
                WriteAtomically(Path.Combine(_doneDir, ObjectId.Format(id)), []);
            }
        }
        var directoryFile = Path.Combine(root, "directory.txt");
        if (File.Exists(directoryFile))
        {
            try
            {
                Directory = BroadcastDirectory.Parse(File.ReadAllBytes(directoryFile));
            }
            catch (FormatException)
            {
                File.Delete(directoryFile);
            }
        }

        foreach (var dir in System.IO.Directory.EnumerateDirectories(_objects).Order(StringComparer.Ordinal).ToList())
        {
            LoadInstance(dir);
        }
        foreach (var instance in _instances.Values.ToList())
        {
            if (_instances.ContainsKey(instance.Key))
            {
                TryComplete(instance);
            }
        }
    }

    /// <summary>The newest directory heard, if any.</summary>
    public BroadcastDirectory? Directory { get; private set; }

    /// <summary>How many objects have symbols held but are not yet rebuilt.</summary>
    public int PartialObjects => _instances.Values.Select(i => i.ObjectId).Distinct().Count();

    /// <summary>Whether an object has been rebuilt.</summary>
    public bool IsComplete(uint objectId) => _done.Contains(objectId);

    /// <summary>Offers one received frame payload (an AX.25 UI frame's information field).</summary>
    public AcceptResult Accept(ReadOnlySpan<byte> payload)
    {
        if (!MailcastFrame.TryParse(payload, out var frame) || frame is null)
        {
            return new AcceptResult(FrameOutcome.NotAFrame);
        }
        if (_done.Contains(frame.ObjectId))
        {
            return new AcceptResult(FrameOutcome.AlreadyComplete, frame.ObjectId);
        }
        if (!_compression.Knows(frame.DictionaryId))
        {
            return new AcceptResult(FrameOutcome.UnknownDictionary, frame.ObjectId);
        }

        string key = KeyOf(frame.ObjectId, frame.DictionaryId, frame.Oti);
        if (!_instances.TryGetValue(key, out var instance))
        {
            instance = new Instance(key, frame.ObjectId, frame.DictionaryId, frame.Oti, Path.Combine(_objects, key));
            System.IO.Directory.CreateDirectory(instance.Path);
            _instances.Add(key, instance);
        }
        if (instance.Esis.Contains(frame.EncodingSymbolId))
        {
            return new AcceptResult(FrameOutcome.Duplicate, frame.ObjectId);
        }
        try
        {
            instance.Decoder.Add(new PayloadId(0, frame.EncodingSymbolId), frame.Symbol.Span);
        }
        catch (ArgumentException e)
        {
            return new AcceptResult(FrameOutcome.Rejected, frame.ObjectId, Detail: "bad symbol: " + e.Message);
        }
        instance.Esis.Add(frame.EncodingSymbolId);
        WriteAtomically(SymbolPath(instance, frame.EncodingSymbolId), frame.Symbol.Span);

        return TryComplete(instance) ?? new AcceptResult(FrameOutcome.Stored, frame.ObjectId);
    }

    /// <summary>Rebuilt bulletins not yet acknowledged, oldest file first.</summary>
    public IReadOnlyList<Bulletin> Pending() =>
        System.IO.Directory.EnumerateFiles(_outbox, "*.bulletin")
            .Select(f => (File: f, Time: File.GetLastWriteTimeUtc(f)))
            .OrderBy(f => f.Time)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .Select(f => Bulletin.Parse(File.ReadAllBytes(f.File)))
            .ToList();

    /// <summary>Removes a bulletin from the outbox, once the BBS has it. It stays marked as rebuilt.</summary>
    public void Acknowledge(string bid)
    {
        var file = Path.Combine(_outbox, ObjectId.Format(ObjectId.ForBid(bid)) + ".bulletin");
        File.Delete(file);
    }

    /// <summary>How far each entry of the newest directory has got.</summary>
    public IReadOnlyList<ObjectProgress> Progress()
    {
        if (Directory is null)
        {
            return [];
        }
        var result = new List<ObjectProgress>();
        foreach (var entry in Directory.Entries)
        {
            if (_done.Contains(entry.ObjectId))
            {
                result.Add(new ObjectProgress(entry, true, 0, 0));
                continue;
            }
            var held = _instances.Values.Where(i => i.ObjectId == entry.ObjectId).OrderByDescending(i => i.Esis.Count).FirstOrDefault();
            result.Add(held is null
                ? new ObjectProgress(entry, false, 0, 0)
                : new ObjectProgress(entry, false, held.Esis.Count, held.Oti.SourceBlockSymbols(0)));
        }
        return result;
    }

    private AcceptResult? TryComplete(Instance instance)
    {
        var data = instance.Decoder.TryDecode();
        if (data is null)
        {
            return null;
        }

        ObjectKind kind;
        byte[] content;
        try
        {
            (kind, content) = TransferObject.Unpack(data, instance.DictionaryId, _compression);
        }
        catch (InvalidDataException e)
        {
            return Reject(instance, "rebuilt object does not decompress: " + e.Message);
        }

        if (kind == ObjectKind.Directory)
        {
            BroadcastDirectory directory;
            try
            {
                directory = BroadcastDirectory.Parse(content);
            }
            catch (FormatException e)
            {
                return Reject(instance, "rebuilt directory does not parse: " + e.Message);
            }
            if (directory.ObjectId != instance.ObjectId)
            {
                return Reject(instance, "rebuilt directory's date does not match its object ID");
            }
            if (Directory is null || directory.Date >= Directory.Date)
            {
                WriteAtomically(Path.Combine(_root, "directory.txt"), content);
                Directory = directory;
            }
            MarkDone(instance.ObjectId);
            return new AcceptResult(FrameOutcome.CompletedDirectory, instance.ObjectId, Directory: directory);
        }

        Bulletin bulletin;
        try
        {
            bulletin = Bulletin.Parse(content);
        }
        catch (FormatException e)
        {
            return Reject(instance, "rebuilt bulletin does not parse: " + e.Message);
        }
        if (ObjectId.ForBid(bulletin.Bid) != instance.ObjectId)
        {
            return Reject(instance, $"rebuilt bulletin's BID {bulletin.Bid} does not match its object ID");
        }
        var listed = Directory?.Find(instance.ObjectId);
        if (listed is not null && (listed.Size != content.Length || listed.ContentHash != DirectoryEntry.HashOf(content)))
        {
            return Reject(instance, $"rebuilt bulletin {bulletin.Bid} does not match the directory's hash");
        }

        WriteAtomically(Path.Combine(_outbox, ObjectId.Format(instance.ObjectId) + ".bulletin"), content);
        MarkDone(instance.ObjectId);
        return new AcceptResult(FrameOutcome.CompletedBulletin, instance.ObjectId, Bulletin: bulletin);
    }

    private AcceptResult Reject(Instance instance, string detail)
    {
        _instances.Remove(instance.Key);
        System.IO.Directory.Delete(instance.Path, recursive: true);
        return new AcceptResult(FrameOutcome.Rejected, instance.ObjectId, Detail: detail);
    }

    private void MarkDone(uint objectId)
    {
        WriteAtomically(Path.Combine(_doneDir, ObjectId.Format(objectId)), []);
        _done.Add(objectId);
        foreach (var other in _instances.Values.Where(i => i.ObjectId == objectId).ToList())
        {
            _instances.Remove(other.Key);
            System.IO.Directory.Delete(other.Path, recursive: true);
        }
    }

    private void LoadInstance(string dir)
    {
        var name = Path.GetFileName(dir);
        var parts = name.Split('-');
        if (parts.Length != 3
            || !TryParseObjectId(parts[0], out uint objectId)
            || !ushort.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort dictionaryId)
            || parts[2].Length != ObjectTransmissionInformation.EncodedLength * 2)
        {
            return; // not ours; leave it alone
        }
        ObjectTransmissionInformation oti;
        try
        {
            oti = ObjectTransmissionInformation.Read(Convert.FromHexString(parts[2]));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return;
        }
        if (_done.Contains(objectId))
        {
            System.IO.Directory.Delete(dir, recursive: true);
            return;
        }
        var instance = new Instance(name, objectId, dictionaryId, oti, dir);
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.sym").Order(StringComparer.Ordinal))
        {
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint esi)
                || esi > PayloadId.MaxEncodingSymbolId)
            {
                continue;
            }
            try
            {
                instance.Decoder.Add(new PayloadId(0, esi), File.ReadAllBytes(file));
                instance.Esis.Add(esi);
            }
            catch (ArgumentException)
            {
                File.Delete(file);
            }
        }
        _instances[name] = instance;
    }

    private static string KeyOf(uint objectId, ushort dictionaryId, ObjectTransmissionInformation oti) =>
        $"{ObjectId.Format(objectId)}-{dictionaryId:x4}-{Convert.ToHexStringLower(oti.ToBytes())}";

    private static string SymbolPath(Instance instance, uint esi) =>
        Path.Combine(instance.Path, esi.ToString("x6", CultureInfo.InvariantCulture) + ".sym");

    private static bool TryParseObjectId(string text, out uint id) =>
        uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id) && text.Length == 8;

    /// <summary>Writes a file so that it is either absent or complete: a temporary file, flushed, then renamed over.</summary>
    private static void WriteAtomically(string path, ReadOnlySpan<byte> content)
    {
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class Instance(string key, uint objectId, ushort dictionaryId, ObjectTransmissionInformation oti, string path)
    {
        public string Key { get; } = key;

        public uint ObjectId { get; } = objectId;

        public ushort DictionaryId { get; } = dictionaryId;

        public ObjectTransmissionInformation Oti { get; } = oti;

        public string Path { get; } = path;

        public ObjectDecoder Decoder { get; } = new(oti);

        public HashSet<uint> Esis { get; } = [];
    }
}
