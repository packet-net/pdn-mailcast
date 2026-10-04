using System.Globalization;
using Mailcast.RaptorQ;

namespace Mailcast.Core;

/// <summary>What became of a frame offered to a <see cref="ReceiverStore"/>.</summary>
public enum FrameOutcome
{
    /// <summary>Not a mailcast frame of a version this code reads, or its CRC failed.</summary>
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
    /// The frame, or the object it completed, failed a check; <see cref="AcceptResult.Detail"/>
    /// says which. Symbols are not thrown away for this: an object that rebuilds wrong keeps its
    /// pieces (less any found to be bad) and waits for more.
    /// </summary>
    Rejected,
}

/// <summary>The result of offering a frame to a <see cref="ReceiverStore"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ObjectId">The frame's object, if it was a frame.</param>
/// <param name="Bulletin">The bulletin the frame completed, for <see cref="FrameOutcome.CompletedBulletin"/>.</param>
/// <param name="Directory">The directory the frame completed, for <see cref="FrameOutcome.CompletedDirectory"/>.</param>
/// <param name="Detail">Why, for <see cref="FrameOutcome.Rejected"/>.</param>
public sealed record AcceptResult(FrameOutcome Outcome, ulong? ObjectId = null, Bulletin? Bulletin = null, BroadcastDirectory? Directory = null, string? Detail = null);

/// <summary>How far one directory entry has got at this receiver.</summary>
/// <param name="Entry">The directory's entry.</param>
/// <param name="Complete">Whether it has been rebuilt.</param>
/// <param name="Received">Symbols held, if not complete.</param>
/// <param name="Needed">Symbols needed at least (K), if any have been received.</param>
public sealed record ObjectProgress(DirectoryEntry Entry, bool Complete, int Received, int Needed);

/// <summary>Settings for a <see cref="ReceiverStore"/>.</summary>
public sealed record ReceiverStoreOptions
{
    /// <summary>
    /// How long an object is remembered as rebuilt, so later frames of it are ignored. Past
    /// that, a frame of it starts collecting again; the BBS's BID check is the real filter.
    /// </summary>
    public TimeSpan DoneRetention { get; init; } = TimeSpan.FromDays(14);

    /// <summary>How long a partial object is kept after its last new piece.</summary>
    public TimeSpan PartialRetention { get; init; } = TimeSpan.FromDays(14);

    /// <summary>The most partial objects kept; past this the one with the oldest last piece goes.</summary>
    public int MaxPartials { get; init; } = 500;

    /// <summary>
    /// The most pieces for which a wrong rebuild is retried leaving out each piece in turn, to
    /// find a bad one. Each retry is a full decode.
    /// </summary>
    public int MaxLeaveOneOut { get; init; } = 300;

    /// <summary>Where the store says what it threw away and why. Plain ASCII.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>The clock, for expiry.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Whether writes are flushed to disk. Only tests turn this off, for speed.</summary>
    internal bool FlushToDisk { get; init; } = true;
}

/// <summary>
/// A receiver's store of broadcast symbols. It keeps every symbol on disk until its object is
/// rebuilt, so an object's pieces add up across days and restarts; it rebuilds each object as
/// soon as it can, checks it against its own ID, and hands each bulletin over once.
/// </summary>
/// <remarks>
/// <para>Files under the root directory, each written to a temporary name, flushed and renamed:</para>
/// <code>
/// objects/OOOOOOOOOOOOOOOO-DDDD-OTI/EEEEEE.sym   one symbol: object ID, dictionary ID, OTI in hex; ESI in hex
/// done/OOOOOOOOOOOOOOOO                          the object has been rebuilt, and when; later frames are ignored
/// outbox/OOOOOOOOOOOOOOOO.bulletin               a rebuilt bulletin not yet acknowledged
/// quarantine/                                    outbox files that could not be read
/// directory.txt                                  the newest directory heard
/// </code>
/// <para>
/// A rebuilt bulletin is returned once, from the <see cref="Accept"/> call whose frame completed
/// it. It also stays in the outbox until <see cref="Acknowledge"/>, so one rebuilt just before a
/// crash is not lost: after a restart it is in <see cref="Pending"/>. The outbox file is on disk,
/// folder included, before the done marker is written.
/// </para>
/// <para>
/// Done markers and partial objects expire (<see cref="ReceiverStoreOptions"/>), so the store
/// does not grow without bound.
/// </para>
/// <para>Not thread-safe: use one store from one thread.</para>
/// </remarks>
public sealed class ReceiverStore
{
    private static readonly TimeSpan ExpiryInterval = TimeSpan.FromHours(1);

    private readonly string _root;
    private readonly string _objects;
    private readonly string _doneDir;
    private readonly string _outbox;
    private readonly string _quarantine;
    private readonly Compression _compression;
    private readonly ReceiverStoreOptions _options;
    private readonly Dictionary<string, Instance> _instances = [];
    private readonly Dictionary<ulong, DateTimeOffset> _done = [];
    private DateTimeOffset _lastExpiry;

    /// <summary>Opens or creates a store, recovering from any interrupted write.</summary>
    public ReceiverStore(string root, Compression compression, ReceiverStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(compression);
        _root = root;
        _compression = compression;
        _options = options ?? new ReceiverStoreOptions();
        _objects = Path.Combine(root, "objects");
        _doneDir = Path.Combine(root, "done");
        _outbox = Path.Combine(root, "outbox");
        _quarantine = Path.Combine(root, "quarantine");
        System.IO.Directory.CreateDirectory(_objects);
        System.IO.Directory.CreateDirectory(_doneDir);
        System.IO.Directory.CreateDirectory(_outbox);

        foreach (var tmp in System.IO.Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories))
        {
            File.Delete(tmp);
        }
        foreach (var marker in System.IO.Directory.EnumerateFiles(_doneDir))
        {
            if (ObjectId.TryParse(Path.GetFileName(marker), out ulong id))
            {
                _done[id] = ReadDoneTime(marker);
            }
        }
        foreach (var file in System.IO.Directory.EnumerateFiles(_outbox, "*.bulletin"))
        {
            if (ObjectId.TryParse(Path.GetFileNameWithoutExtension(file), out ulong id) && !_done.ContainsKey(id))
            {
                WriteDone(id);
            }
        }
        var directoryFile = Path.Combine(root, "directory.txt");
        if (File.Exists(directoryFile))
        {
            try
            {
                Directory = BroadcastDirectory.Parse(File.ReadAllBytes(directoryFile));
            }
            catch (FormatException e)
            {
                Log($"directory.txt unreadable, removed: {e.Message}");
                File.Delete(directoryFile);
            }
        }

        foreach (var dir in System.IO.Directory.EnumerateDirectories(_objects).Order(StringComparer.Ordinal).ToList())
        {
            try
            {
                LoadInstance(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log($"skipped unreadable partial object {Path.GetFileName(dir)}: {e.Message}");
            }
        }
        Expire();
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

    /// <summary>Whether an object has been rebuilt (within <see cref="ReceiverStoreOptions.DoneRetention"/>).</summary>
    public bool IsComplete(ulong objectId) => _done.ContainsKey(objectId);

    /// <summary>Offers one received frame payload (an AX.25 UI frame's information field).</summary>
    public AcceptResult Accept(ReadOnlySpan<byte> payload)
    {
        var now = _options.Time.GetUtcNow();
        if (now - _lastExpiry >= ExpiryInterval)
        {
            Expire();
        }
        if (!MailcastFrame.TryParse(payload, out var frame) || frame is null)
        {
            return new AcceptResult(FrameOutcome.NotAFrame);
        }
        if (_done.ContainsKey(frame.ObjectId))
        {
            return new AcceptResult(FrameOutcome.AlreadyComplete, frame.ObjectId);
        }
        if (!_compression.Knows(frame.DictionaryId))
        {
            return new AcceptResult(FrameOutcome.UnknownDictionary, frame.ObjectId);
        }

        string key = KeyOf(frame.ObjectId, frame.DictionaryId, frame.Oti);
        bool isNew = !_instances.TryGetValue(key, out var instance);
        instance ??= new Instance(key, frame.ObjectId, frame.DictionaryId, frame.Oti, Path.Combine(_objects, key));
        if (instance.Symbols.ContainsKey(frame.EncodingSymbolId))
        {
            return new AcceptResult(FrameOutcome.Duplicate, frame.ObjectId);
        }
        var symbol = frame.Symbol.ToArray();
        if (!instance.TryAdd(frame.EncodingSymbolId, symbol, out string? problem))
        {
            return new AcceptResult(FrameOutcome.Rejected, frame.ObjectId, Detail: "bad symbol: " + problem);
        }

        // Only now, with the symbol accepted, does the object get a folder.
        if (isNew)
        {
            System.IO.Directory.CreateDirectory(instance.Path);
            _instances.Add(key, instance);
        }
        DurableFile.WriteAtomically(SymbolPath(instance, frame.EncodingSymbolId), symbol, now, _options.FlushToDisk);
        instance.LastPiece = now;
        if (isNew)
        {
            EnforcePartialLimit(instance);
        }

        return TryComplete(instance) ?? new AcceptResult(FrameOutcome.Stored, frame.ObjectId);
    }

    /// <summary>
    /// Rebuilt bulletins not yet acknowledged, oldest file first. An outbox file that cannot be
    /// read is moved to the quarantine folder and logged.
    /// </summary>
    public IReadOnlyList<Bulletin> Pending()
    {
        var result = new List<Bulletin>();
        var files = System.IO.Directory.EnumerateFiles(_outbox, "*.bulletin")
            .Select(f => (File: f, Time: File.GetLastWriteTimeUtc(f)))
            .OrderBy(f => f.Time)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ToList();
        foreach (var (file, _) in files)
        {
            try
            {
                result.Add(Bulletin.Parse(File.ReadAllBytes(file)));
            }
            catch (FormatException e)
            {
                System.IO.Directory.CreateDirectory(_quarantine);
                File.Move(file, Path.Combine(_quarantine, Path.GetFileName(file)), overwrite: true);
                Log($"outbox file {Path.GetFileName(file)} unreadable, quarantined: {e.Message}");
            }
        }
        return result;
    }

    /// <summary>Removes a bulletin from the outbox, once the BBS has it. Its object stays marked as rebuilt.</summary>
    public void Acknowledge(string bid)
    {
        ArgumentException.ThrowIfNullOrEmpty(bid);
        foreach (var file in System.IO.Directory.EnumerateFiles(_outbox, "*.bulletin").ToList())
        {
            try
            {
                if (string.Equals(Bulletin.Parse(File.ReadAllBytes(file)).Bid, bid, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
            catch (FormatException)
            {
                // Pending() quarantines these.
            }
        }
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
            if (_done.ContainsKey(entry.ObjectId))
            {
                result.Add(new ObjectProgress(entry, true, 0, 0));
                continue;
            }
            var held = _instances.Values.Where(i => i.ObjectId == entry.ObjectId).OrderByDescending(i => i.Symbols.Count).FirstOrDefault();
            result.Add(held is null
                ? new ObjectProgress(entry, false, 0, 0)
                : new ObjectProgress(entry, false, held.Symbols.Count, held.Oti.SourceBlockSymbols(0)));
        }
        return result;
    }

    /// <summary>
    /// Forgets done markers older than <see cref="ReceiverStoreOptions.DoneRetention"/> and
    /// partial objects whose last piece is older than <see cref="ReceiverStoreOptions.PartialRetention"/>.
    /// Runs by itself at most hourly as frames arrive.
    /// </summary>
    public void Expire()
    {
        var now = _options.Time.GetUtcNow();
        _lastExpiry = now;
        foreach (var (id, when) in _done.ToList())
        {
            if (now - when > _options.DoneRetention && !File.Exists(OutboxPath(id)))
            {
                File.Delete(DonePath(id));
                _done.Remove(id);
            }
        }
        foreach (var instance in _instances.Values.ToList())
        {
            if (now - instance.LastPiece > _options.PartialRetention)
            {
                Log($"partial object {ObjectId.Format(instance.ObjectId)} expired with {instance.Symbols.Count} of {instance.Oti.SourceBlockSymbols(0)} pieces");
                Remove(instance);
            }
        }
    }

    private void EnforcePartialLimit(Instance keep)
    {
        while (_instances.Count > _options.MaxPartials)
        {
            var oldest = _instances.Values.Where(i => i != keep).MinBy(i => i.LastPiece);
            if (oldest is null)
            {
                return;
            }
            Log($"too many partial objects; dropped {ObjectId.Format(oldest.ObjectId)}, last piece {oldest.LastPiece:u}");
            Remove(oldest);
        }
    }

    private AcceptResult? TryComplete(Instance instance)
    {
        var data = instance.TryDecode();
        if (data is null)
        {
            return null;
        }
        if (ObjectId.Of(data) == instance.ObjectId)
        {
            return Finish(instance, data);
        }

        // Some piece is wrong. Pieces that disagree with the rebuild are the first suspects.
        Log($"object {ObjectId.Format(instance.ObjectId)} rebuilt from {instance.Symbols.Count} pieces does not match its ID; looking for a bad piece");
        var encoder = new ObjectEncoder(data, instance.Oti);
        var disagree = instance.Symbols
            .Where(s => !encoder.Encode(new PayloadId(0, s.Key)).AsSpan(0, s.Value.Length).SequenceEqual(s.Value))
            .Select(s => s.Key)
            .ToList();
        if (disagree.Count > 0 && TryWithout(instance, disagree) is { } fixedData)
        {
            return Finish(instance, fixedData);
        }

        // A bad piece the rebuild used agrees with the wrong rebuild, so try leaving out each in turn.
        int k = instance.Oti.SourceBlockSymbols(0);
        if (instance.Symbols.Count > k && instance.Symbols.Count <= _options.MaxLeaveOneOut)
        {
            foreach (uint esi in instance.Symbols.Keys.Order().ToList())
            {
                if (TryWithout(instance, [esi]) is { } repaired)
                {
                    return Finish(instance, repaired);
                }
            }
        }

        instance.ResetDecoder();
        return new AcceptResult(FrameOutcome.Rejected, instance.ObjectId,
            Detail: $"rebuilt object does not match its ID; keeping its {instance.Symbols.Count} pieces and waiting for more");
    }

    /// <summary>Decodes leaving out some pieces; if that matches the ID, deletes those pieces for good.</summary>
    private byte[]? TryWithout(Instance instance, List<uint> leaveOut)
    {
        int k = instance.Oti.SourceBlockSymbols(0);
        if (instance.Symbols.Count - leaveOut.Count < k)
        {
            return null;
        }
        var decoder = new ObjectDecoder(instance.Oti);
        foreach (var (esi, symbol) in instance.Symbols)
        {
            if (!leaveOut.Contains(esi))
            {
                decoder.Add(new PayloadId(0, esi), symbol);
            }
        }
        var data = decoder.TryDecode();
        if (data is null || ObjectId.Of(data) != instance.ObjectId)
        {
            return null;
        }
        foreach (uint esi in leaveOut)
        {
            Log($"object {ObjectId.Format(instance.ObjectId)}: piece {esi} was bad, dropped");
            instance.Symbols.Remove(esi);
            File.Delete(SymbolPath(instance, esi));
        }
        return data;
    }

    /// <summary>Unpacks an object that matches its ID.</summary>
    private AcceptResult? Finish(Instance instance, byte[] data)
    {
        ObjectKind kind;
        byte[] content;
        try
        {
            (kind, content) = TransferObject.Unpack(data, instance.DictionaryId, _compression);
        }
        catch (KeyNotFoundException)
        {
            // Possible only for pieces stored before a dictionary was withdrawn. Keep them.
            Log($"object {ObjectId.Format(instance.ObjectId)} needs dictionary {instance.DictionaryId}, which this receiver does not have; skipped");
            instance.ResetDecoder();
            return new AcceptResult(FrameOutcome.UnknownDictionary, instance.ObjectId);
        }
        catch (InvalidDataException e)
        {
            return Unusable(instance, "matches its ID but does not decompress: " + e.Message);
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
                return Unusable(instance, "is a directory that does not parse: " + e.Message);
            }
            if (Directory is null || directory.Date >= Directory.Date)
            {
                DurableFile.WriteAtomically(Path.Combine(_root, "directory.txt"), content, flush: _options.FlushToDisk);
                Directory = directory;
            }
            MarkDone(instance);
            return new AcceptResult(FrameOutcome.CompletedDirectory, instance.ObjectId, Directory: directory);
        }

        Bulletin bulletin;
        try
        {
            bulletin = Bulletin.Parse(content);
        }
        catch (FormatException e)
        {
            return Unusable(instance, "is a bulletin that does not parse: " + e.Message);
        }

        DurableFile.WriteAtomically(OutboxPath(instance.ObjectId), content, flush: _options.FlushToDisk); // flushes the outbox folder too
        MarkDone(instance);
        return new AcceptResult(FrameOutcome.CompletedBulletin, instance.ObjectId, Bulletin: bulletin);
    }

    /// <summary>
    /// An object that matches its ID but cannot be used was sent that way; more pieces would
    /// rebuild the same thing. Mark it done so it is not collected again.
    /// </summary>
    private AcceptResult Unusable(Instance instance, string why)
    {
        string detail = $"object {ObjectId.Format(instance.ObjectId)} {why}";
        Log(detail);
        MarkDone(instance);
        return new AcceptResult(FrameOutcome.Rejected, instance.ObjectId, Detail: detail);
    }

    private void MarkDone(Instance instance)
    {
        WriteDone(instance.ObjectId);
        foreach (var other in _instances.Values.Where(i => i.ObjectId == instance.ObjectId).ToList())
        {
            Remove(other);
        }
    }

    private void WriteDone(ulong objectId)
    {
        var now = _options.Time.GetUtcNow();
        DurableFile.WriteAtomically(DonePath(objectId), Bulletin.TextEncoding.GetBytes(now.ToString("o", CultureInfo.InvariantCulture)), flush: _options.FlushToDisk);
        _done[objectId] = now;
    }

    private static DateTimeOffset ReadDoneTime(string marker)
    {
        var text = File.ReadAllText(marker).Trim();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when
            : new DateTimeOffset(File.GetLastWriteTimeUtc(marker), TimeSpan.Zero);
    }

    private void Remove(Instance instance)
    {
        _instances.Remove(instance.Key);
        if (System.IO.Directory.Exists(instance.Path))
        {
            System.IO.Directory.Delete(instance.Path, recursive: true);
        }
    }

    private void LoadInstance(string dir)
    {
        var name = Path.GetFileName(dir);
        var parts = name.Split('-');
        if (parts.Length != 3
            || !ObjectId.TryParse(parts[0], out ulong objectId)
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
        catch (FormatException)
        {
            return;
        }
        if (oti.SourceBlocks != 1)
        {
            return;
        }
        if (_done.ContainsKey(objectId))
        {
            System.IO.Directory.Delete(dir, recursive: true);
            return;
        }
        if (!_compression.Knows(dictionaryId))
        {
            Log($"partial object {ObjectId.Format(objectId)} needs dictionary {dictionaryId}, which this receiver does not have; left as it is");
            return;
        }
        var instance = new Instance(name, objectId, dictionaryId, oti, dir);
        var last = DateTimeOffset.MinValue;
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.sym").Order(StringComparer.Ordinal))
        {
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint esi)
                || esi > PayloadId.MaxEncodingSymbolId
                || !instance.TryAdd(esi, File.ReadAllBytes(file), out _))
            {
                File.Delete(file);
                continue;
            }
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            if (written > last)
            {
                last = written;
            }
        }
        if (instance.Symbols.Count == 0)
        {
            System.IO.Directory.Delete(dir, recursive: true);
            return;
        }
        instance.LastPiece = last;
        _instances[name] = instance;
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    private string DonePath(ulong objectId) => Path.Combine(_doneDir, ObjectId.Format(objectId));

    private string OutboxPath(ulong objectId) => Path.Combine(_outbox, ObjectId.Format(objectId) + ".bulletin");

    private static string KeyOf(ulong objectId, ushort dictionaryId, ObjectTransmissionInformation oti) =>
        $"{ObjectId.Format(objectId)}-{dictionaryId:x4}-{Convert.ToHexStringLower(oti.ToBytes())}";

    private static string SymbolPath(Instance instance, uint esi) =>
        Path.Combine(instance.Path, esi.ToString("x6", CultureInfo.InvariantCulture) + ".sym");

    private sealed class Instance(string key, ulong objectId, ushort dictionaryId, ObjectTransmissionInformation oti, string path)
    {
        private ObjectDecoder _decoder = new(oti);

        public string Key { get; } = key;

        public ulong ObjectId { get; } = objectId;

        public ushort DictionaryId { get; } = dictionaryId;

        public ObjectTransmissionInformation Oti { get; } = oti;

        public string Path { get; } = path;

        public Dictionary<uint, byte[]> Symbols { get; } = [];

        public DateTimeOffset LastPiece { get; set; }

        public bool TryAdd(uint esi, byte[] symbol, out string? problem)
        {
            try
            {
                _decoder.Add(new PayloadId(0, esi), symbol);
            }
            catch (ArgumentException e)
            {
                problem = e.Message;
                return false;
            }
            Symbols[esi] = symbol;
            problem = null;
            return true;
        }

        public byte[]? TryDecode() => _decoder.TryDecode();

        /// <summary>Starts the decoder again from the pieces held, after a rebuild that was no use.</summary>
        public void ResetDecoder()
        {
            _decoder = new ObjectDecoder(Oti);
            foreach (var (esi, symbol) in Symbols)
            {
                _decoder.Add(new PayloadId(0, esi), symbol);
            }
        }
    }
}
