using System.Globalization;
using Mailcast.RaptorQ;
using Packet.Mailcast.Propagation;

namespace Packet.Mailcast;

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
    /// The symbol completed an object of a content type this receiver knows but does not handle
    /// (a DAPPS message): it is kept out of the BBS, and further frames of it are ignored.
    /// <see cref="AcceptResult.ContentType"/> says which.
    /// </summary>
    CompletedUnhandled,

    /// <summary>
    /// The symbol completed an object of a content type this receiver does not know (an
    /// experiment, or one assigned later): it is ignored, without error.
    /// </summary>
    CompletedUnknown,

    /// <summary>
    /// The frame, or the object it completed, failed a check; <see cref="AcceptResult.Detail"/>
    /// says which. Symbols are not thrown away for this: an object that rebuilds wrong keeps its
    /// pieces (less any found to be bad) and waits for more.
    /// </summary>
    Rejected,

    /// <summary>
    /// The symbol completed an ionosonde reading (content type 4), which is in the result. It is
    /// never for the BBS; the newest is kept in <see cref="ReceiverStore.Ionosphere"/>. Last, so
    /// the values before it are what they always were.
    /// </summary>
    CompletedIonosphere,
}

/// <summary>What became of a request to offer an archived bulletin to the BBS again.</summary>
public enum ResendOutcome
{
    /// <summary>It is back in the outbox.</summary>
    Resent,

    /// <summary>It is already in the outbox.</summary>
    AlreadyWaiting,

    /// <summary>Neither the outbox nor the archive has it.</summary>
    NotFound,

    /// <summary>
    /// <see cref="ReceiverStore.MaxResentWaiting"/> bulletins sent again are already waiting for
    /// the BBS; this one can go once it has answered for some of them.
    /// </summary>
    TooMany,
}

/// <summary>
/// What a <see cref="MailSnapshot"/> is made from, taken from the store in a moment so the
/// snapshot can be built afterwards on another thread, outside whatever lock guards the store.
/// </summary>
public sealed class MailParts
{
    internal MailParts(long version, List<(ulong Id, byte[] Serialized, Bulletin Bulletin, DateTimeOffset Written)> waiting, System.Collections.Immutable.ImmutableSortedSet<MailEntry> archived)
    {
        Version = version;
        Waiting = waiting;
        Archived = archived;
    }

    internal long Version { get; }

    internal List<(ulong Id, byte[] Serialized, Bulletin Bulletin, DateTimeOffset Written)> Waiting { get; }

    internal System.Collections.Immutable.ImmutableSortedSet<MailEntry> Archived { get; }
}

/// <summary>The result of offering a frame to a <see cref="ReceiverStore"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ObjectId">The frame's object, if it was a frame.</param>
/// <param name="Bulletin">The bulletin the frame completed, for <see cref="FrameOutcome.CompletedBulletin"/>.</param>
/// <param name="Directory">The directory the frame completed, for <see cref="FrameOutcome.CompletedDirectory"/>.</param>
/// <param name="Detail">Why, for <see cref="FrameOutcome.Rejected"/>.</param>
/// <param name="ContentType">The object's content type, for <see cref="FrameOutcome.CompletedUnhandled"/> and <see cref="FrameOutcome.CompletedUnknown"/>.</param>
/// <param name="Ionosphere">The reading the frame completed, for <see cref="FrameOutcome.CompletedIonosphere"/>.</param>
public sealed record AcceptResult(FrameOutcome Outcome, ulong? ObjectId = null, Bulletin? Bulletin = null, BroadcastDirectory? Directory = null, string? Detail = null, byte? ContentType = null, IonoReading? Ionosphere = null);

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
    /// After a rebuild that does not match its ID, the most extra decodes tried per arriving
    /// piece while looking for a set of pieces without the bad ones. The search carries on from
    /// where it stopped as more pieces arrive, so the work per frame stays small.
    /// </summary>
    public int RepairDecodesPerFrame { get; init; } = 3;

    /// <summary>
    /// How many seeded random sets of K + 1 pieces the search tries, after leaving out each piece
    /// in turn and after the newest K + 2. These are what find two or more bad pieces.
    /// </summary>
    public int RandomRepairSubsets { get; init; } = 24;

    /// <summary>
    /// How long a bulletin the BBS has answered for is kept in the archive, counted from the
    /// answer. Zero keeps none: an answered bulletin is simply removed, as before the archive.
    /// </summary>
    public TimeSpan ArchiveRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The most the archive's files may add up to, in bytes; past it the oldest go first. Zero keeps none.</summary>
    public long ArchiveMaxBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>
    /// Whether <see cref="ReceiverStore.Mail"/> is rebuilt at once after each change (the default).
    /// Turned off, the owner takes <see cref="ReceiverStore.CaptureMail"/> when it suits it, say
    /// once per delivery session, and builds the snapshot with <see cref="ReceiverStore.Publish"/>
    /// outside its own lock.
    /// </summary>
    public bool PublishMailOnChange { get; init; } = true;

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
/// archive/OOOOOOOOOOOOOOOO.mail                  an acknowledged bulletin and the BBS's answer (see MailArchive)
/// quarantine/                                    outbox and archive files that could not be read
/// directory.txt                                  the newest directory heard
/// </code>
/// <para>
/// A rebuilt bulletin is returned once, from the <see cref="Accept"/> call whose frame completed
/// it. It also stays in the outbox until <see cref="Acknowledge"/>, so one rebuilt just before a
/// crash is not lost: after a restart it is in <see cref="Pending"/>. The outbox file is on disk,
/// folder included, before the done marker is written.
/// </para>
/// <para>
/// <see cref="Acknowledge"/> moves a bulletin from the outbox to the archive with the BBS's
/// answer: the archive copy is on disk, folder included, before the outbox file is deleted.
/// <see cref="Resend"/> copies one back to the outbox, the archive keeping its copy until the
/// BBS answers again. A bulletin in both, after a resend or a crash between the two steps of an
/// acknowledgement, is waiting, and offered again, which the BBS answers by its BID.
/// </para>
/// <para>
/// The outbox and the archive's index are held in memory, read from disk only when the store
/// is opened, so <see cref="Pending"/> and <see cref="Mail"/> read no files.
/// </para>
/// <para>
/// Done markers, partial objects and archived bulletins expire (<see cref="ReceiverStoreOptions"/>),
/// so the store does not grow without bound. Expiry never touches the outbox.
/// </para>
/// <para>Not thread-safe: use one store from one thread, except <see cref="Mail"/> and
/// <see cref="ReadMail(ulong, out bool)"/>, which any thread may call.</para>
/// </remarks>
public sealed class ReceiverStore
{
    private static readonly TimeSpan ExpiryInterval = TimeSpan.FromHours(1);

    private readonly string _root;
    private readonly string _objects;
    private readonly string _doneDir;
    private readonly string _outbox;
    private readonly string _quarantine;
    private readonly MailArchive _archive;
    private readonly Dictionary<string, OutboxItem> _outboxItems = new(StringComparer.Ordinal);
    private volatile MailSnapshot _mail = MailSnapshot.Empty;
    private readonly object _publishGate = new();
    private long _mailVersion = 1;
    private long _capturedVersion;
    private long _publishedVersion;
    private long _mailBuilds;
    private long _diskReads;
    private readonly Compression _compression;
    private readonly ReceiverStoreOptions _options;
    private readonly Dictionary<string, Instance> _instances = [];
    private readonly Dictionary<ulong, DateTimeOffset> _done = [];
    private readonly HashSet<string> _unusable = [];
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
        var scheduleFile = Path.Combine(root, "schedule.txt");
        if (File.Exists(scheduleFile))
        {
            var lines = Bulletin.TextEncoding.GetString(File.ReadAllBytes(scheduleFile)).Split('\n');
            if (lines.Length >= 2
                && DateOnly.TryParseExact(lines[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var heard)
                && SlotTimetable.FromFields(lines[1].Split('\t')) is { } timetable)
            {
                HeardSchedule = timetable;
                HeardScheduleDate = heard;
            }
            else
            {
                Log("schedule.txt unreadable, removed");
                File.Delete(scheduleFile);
            }
        }
        LoadOutbox();
        _archive = new MailArchive(Path.Combine(root, "archive"), _quarantine, _options.ArchiveRetention, _options.ArchiveMaxBytes, _options.Time, _options.FlushToDisk, Log, CountRead);
        Publish(CaptureMail()!);
        var ionosphereFile = Path.Combine(root, IonosphereFile);
        if (File.Exists(ionosphereFile))
        {
            if (IonoRecord.TryDecode(File.ReadAllBytes(ionosphereFile), out var held))
            {
                Ionosphere = held;
            }
            else
            {
                Log($"{IonosphereFile} unreadable, removed");
                File.Delete(ionosphereFile);
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

    /// <summary>Where the newest ionosonde reading is kept, as the object that carried it, under the store's folder.</summary>
    public const string IonosphereFile = "ionosphere.bin";

    /// <summary>
    /// The ionosonde reading with the newest sounding heard (content type 4), kept across
    /// restarts; null until one has been heard. As sent: its age is as of then.
    /// </summary>
    public IonoReading? Ionosphere { get; private set; }

    /// <summary>
    /// The head end's timetable from the newest directory heard that gave one, kept across
    /// restarts in <c>schedule.txt</c>; null until one has been heard.
    /// </summary>
    public SlotTimetable? HeardSchedule { get; private set; }

    /// <summary>The date of the directory <see cref="HeardSchedule"/> came from.</summary>
    public DateOnly? HeardScheduleDate { get; private set; }

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
        if (_unusable.Contains(key))
        {
            return new AcceptResult(FrameOutcome.Rejected, frame.ObjectId, Detail: "object already rebuilt and found unusable");
        }
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
    /// Rebuilt bulletins not yet acknowledged, oldest first. Held in memory: the outbox folder is
    /// read once, when the store is opened, and an unreadable file in it is quarantined then.
    /// </summary>
    public IReadOnlyList<Bulletin> Pending() =>
        [.. _outboxItems.Values.OrderBy(o => o.Written).ThenBy(o => o.Name, StringComparer.Ordinal).Select(o => o.Bulletin)];

    /// <summary>
    /// The bulletins held, waiting or archived, newest first, as of the last change. Safe to read
    /// from any thread: it is replaced, never changed, and reading it touches no files.
    /// </summary>
    public MailSnapshot Mail => _mail;

    /// <summary>The most bulletins sent again that may wait for the BBS at once: a session offers at most 50.</summary>
    public const int MaxResentWaiting = 50;

    /// <summary>How many bulletins sent again are waiting: those in the outbox that the archive also has.</summary>
    public int ResentWaiting => _outboxItems.Values.Count(o => o.ObjectId is ulong id && _archive.Contains(id));

    /// <summary>How many times <see cref="Mail"/> has been rebuilt (for tests).</summary>
    public long MailBuilds => Interlocked.Read(ref _mailBuilds);

    /// <summary>How many times the store has read the disk for the outbox or the archive (for tests).</summary>
    public long DiskReads => Interlocked.Read(ref _diskReads);

    /// <summary>
    /// Takes a bulletin out of the outbox once the BBS has answered for it for good, and keeps it
    /// in the archive with that answer (unless the archive is turned off). Every outbox file with
    /// this BID goes. Its object stays marked as rebuilt.
    /// </summary>
    /// <remarks>
    /// The archive copy is a convenience: if it cannot be written for a bulletin the BBS has
    /// (accepted or already had), that is logged and the bulletin leaves the outbox anyway, or a
    /// full disk would have it offered again for ever. A refused one, which the BBS does not have,
    /// is moved from the outbox to the quarantine folder instead (a rename, which needs no space),
    /// so it is kept but no longer offered, and cannot hold up newer mail. This throws only if an
    /// outbox file cannot be removed or moved.
    /// </remarks>
    public void Acknowledge(string bid, BbsVerdict verdict, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(bid);
        try
        {
            foreach (var held in _outboxItems.Values.Where(o => string.Equals(o.Bulletin.Bid, bid, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (held.ObjectId is ulong id)
                {
                    try
                    {
                        _archive.Add(id, held.Serialized, held.Bulletin, verdict, detail); // on disk before the outbox file goes
                    }
                    catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && verdict == BbsVerdict.Refused)
                    {
                        System.IO.Directory.CreateDirectory(_quarantine);
                        File.Move(Path.Combine(_outbox, held.Name), Path.Combine(_quarantine, held.Name), overwrite: true);
                        _outboxItems.Remove(held.Name);
                        Log($"archive: cannot keep a copy of refused {bid}: {e.Message}; moved it from the outbox to quarantine/{held.Name} so it does not hold up newer mail");
                        continue;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Log($"archive: cannot keep a copy of {bid}: {e.Message}; the BBS has it, so it leaves the outbox all the same");
                    }
                }
                File.Delete(Path.Combine(_outbox, held.Name));
                _outboxItems.Remove(held.Name);
            }
        }
        finally
        {
            MailChanged();
        }
    }

    /// <summary>
    /// One bulletin as stored, from the outbox if it is waiting or else from the archive, with its
    /// entry; null if neither has it. Safe from any thread: it reads <see cref="Mail"/> and, for an
    /// archived one, its file. <paramref name="unreadable"/> says the archive file would not read;
    /// pass the ID to <see cref="ForgetUnreadable"/> (on the store's own thread) to quarantine it.
    /// </summary>
    public (MailEntry Entry, byte[] Serialized)? ReadMail(ulong objectId, out bool unreadable)
    {
        unreadable = false;
        var mail = _mail;
        if (mail.WaitingBytes(objectId) is { } waiting)
        {
            return (mail.NewestFirst.First(e => e.ObjectId == objectId), waiting);
        }
        try
        {
            CountRead();
            return MailArchive.ReadFile(objectId, _archive.PathOf(objectId));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (FormatException)
        {
            unreadable = true;
            return null;
        }
    }

    /// <summary>One bulletin as stored, as <see cref="ReadMail(ulong, out bool)"/>, quarantining an archive file that does not read.</summary>
    public (MailEntry Entry, byte[] Serialized)? ReadMail(ulong objectId)
    {
        var held = ReadMail(objectId, out bool unreadable);
        if (unreadable)
        {
            ForgetUnreadable(objectId);
        }
        return held;
    }

    /// <summary>
    /// Forgets an archived bulletin whose file would not read, and quarantines the file; but reads
    /// it again first, on the store's own thread, since a read from another thread may have caught
    /// it being rewritten, and a good copy must not be quarantined.
    /// </summary>
    public void ForgetUnreadable(ulong objectId)
    {
        if (!_archive.Contains(objectId))
        {
            return;
        }
        try
        {
            CountRead();
            _ = MailArchive.ReadFile(objectId, _archive.PathOf(objectId));
            return; // it reads now
        }
        catch (FormatException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // not a question of its content; leave it for the next look
        }
        _archive.Unreadable(objectId, "does not read as an archived bulletin");
        MailChanged();
    }

    /// <summary>
    /// Puts an archived bulletin back in the outbox, so the next session offers it to the BBS
    /// again. The archive keeps its copy, with the old answer, until the BBS answers again; until
    /// then the bulletin is listed as waiting.
    /// </summary>
    public (ResendOutcome Outcome, Bulletin? Bulletin) Resend(ulong objectId)
    {
        string name = OutboxName(objectId);
        if (_outboxItems.ContainsKey(name))
        {
            return (ResendOutcome.AlreadyWaiting, null);
        }
        if (!_archive.Contains(objectId))
        {
            return (ResendOutcome.NotFound, null);
        }
        if (ResentWaiting >= MaxResentWaiting)
        {
            return (ResendOutcome.TooMany, null);
        }
        byte[] serialized;
        try
        {
            CountRead();
            serialized = MailArchive.ReadFile(objectId, _archive.PathOf(objectId)).Serialized;
        }
        catch (FormatException)
        {
            ForgetUnreadable(objectId);
            return (ResendOutcome.NotFound, null);
        }
        var bulletin = Bulletin.Parse(serialized);
        var now = _options.Time.GetUtcNow();
        DurableFile.WriteAtomically(Path.Combine(_outbox, name), serialized, now, _options.FlushToDisk);
        _outboxItems[name] = new OutboxItem(name, objectId, serialized, bulletin, now);
        if (!_done.ContainsKey(objectId))
        {
            WriteDone(objectId); // as for any outbox file: later frames of it are not collected again
        }
        MailChanged();
        return (ResendOutcome.Resent, bulletin);
    }

    /// <summary>How far each entry of the newest directory has got.</summary>
    public IReadOnlyList<ObjectProgress> Progress()
    {
        if (Directory is null)
        {
            return [];
        }
        // The fullest instance of each object, found in one pass rather than once per entry.
        var fullest = new Dictionary<ulong, Instance>();
        foreach (var instance in _instances.Values)
        {
            if (!fullest.TryGetValue(instance.ObjectId, out var best) || instance.Symbols.Count > best.Symbols.Count)
            {
                fullest[instance.ObjectId] = instance;
            }
        }
        var result = new List<ObjectProgress>(Directory.Entries.Count);
        foreach (var entry in Directory.Entries)
        {
            if (_done.ContainsKey(entry.ObjectId))
            {
                result.Add(new ObjectProgress(entry, true, 0, 0));
                continue;
            }
            var held = fullest.GetValueOrDefault(entry.ObjectId);
            result.Add(held is null
                ? new ObjectProgress(entry, false, 0, 0)
                : new ObjectProgress(entry, false, held.Symbols.Count, held.Oti.SourceBlockSymbols(0)));
        }
        return result;
    }

    /// <summary>
    /// Forgets done markers older than <see cref="ReceiverStoreOptions.DoneRetention"/>, partial
    /// objects whose last piece is older than <see cref="ReceiverStoreOptions.PartialRetention"/>,
    /// and archived bulletins past <see cref="ReceiverStoreOptions.ArchiveRetention"/> or over
    /// <see cref="ReceiverStoreOptions.ArchiveMaxBytes"/>. Runs by itself at most hourly as frames
    /// arrive, and the archive is also pruned whenever it is added to.
    /// </summary>
    public void Expire()
    {
        var now = _options.Time.GetUtcNow();
        _lastExpiry = now;
        int archived = _archive.Count;
        _archive.Prune();
        if (_archive.Count != archived)
        {
            MailChanged();
        }
        foreach (var (id, when) in _done.ToList())
        {
            if (now - when > _options.DoneRetention && !_outboxItems.ContainsKey(OutboxName(id)))
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
        if (ObjectId.Of(instance.DictionaryId, data) == instance.ObjectId)
        {
            return Finish(instance, data);
        }

        // Some piece is wrong. A wrong rebuild agrees with every piece it used, so the pieces
        // cannot be told apart by checking them against it; instead try sets of pieces, a few
        // per arriving piece, until one rebuilds to the ID. Any set that does is safe to accept.
        instance.ResetDecoder();
        int k = instance.Oti.SourceBlockSymbols(0);
        if (instance.RepairCursor >= instance.RepairCandidates(_options.RandomRepairSubsets) && instance.Arrival.Count > instance.RepairPassPieces)
        {
            instance.RepairCursor = 0; // a piece arrived since the last search ran out: search again
            instance.RepairPassPieces = instance.Arrival.Count;
        }
        int total = instance.RepairCandidates(_options.RandomRepairSubsets);
        if (instance.RepairCursor == 0)
        {
            Log($"object {ObjectId.Format(instance.ObjectId)} rebuilt from {instance.Symbols.Count} pieces does not match its ID; looking for bad pieces");
        }
        int budget = _options.RepairDecodesPerFrame;
        while (budget > 0 && instance.RepairCursor < total)
        {
            var (subset, leftOut) = instance.RepairCandidate(instance.RepairCursor++, k, _options.RandomRepairSubsets);
            if (subset is null)
            {
                continue;
            }
            budget--;
            var decoder = new ObjectDecoder(instance.Oti);
            foreach (uint esi in subset)
            {
                decoder.Add(new PayloadId(0, esi), instance.Symbols[esi]);
            }
            var candidate = decoder.TryDecode();
            if (candidate is null || ObjectId.Of(instance.DictionaryId, candidate) != instance.ObjectId)
            {
                continue;
            }
            if (leftOut is { } bad)
            {
                Log($"object {ObjectId.Format(instance.ObjectId)}: piece {bad} was bad, dropped");
                instance.Symbols.Remove(bad);
                instance.Arrival.Remove(bad);
                File.Delete(SymbolPath(instance, bad));
            }
            else
            {
                Log($"object {ObjectId.Format(instance.ObjectId)}: rebuilt from {subset.Count} of its {instance.Symbols.Count} pieces");
            }
            return Finish(instance, candidate);
        }

        return new AcceptResult(FrameOutcome.Rejected, instance.ObjectId,
            Detail: $"rebuilt object does not match its ID; keeping its {instance.Symbols.Count} pieces and waiting for more");
    }

    /// <summary>Unpacks an object that matches its ID, by its content type.</summary>
    private AcceptResult? Finish(Instance instance, byte[] data)
    {
        if (!ContentType.TryRead(data, out byte type, out _, out _))
        {
            return Unusable(instance, "matches its ID but its content type or metadata block is cut short");
        }
        if (type == (byte)ObjectKind.Propagation)
        {
            // Observe only, never for the BBS. Marked done either way, so its later frames are ignored.
            MarkDone(instance);
            if (!IonoRecord.TryDecode(data, out var reading))
            {
                return new AcceptResult(FrameOutcome.CompletedUnknown, instance.ObjectId, ContentType: type);
            }
            if (Ionosphere?.SoundingTimeUtc is not { } held || reading.SoundingTimeUtc >= held)
            {
                DurableFile.WriteAtomically(Path.Combine(_root, IonosphereFile), data, flush: _options.FlushToDisk);
                Ionosphere = reading;
            }
            return new AcceptResult(FrameOutcome.CompletedIonosphere, instance.ObjectId, ContentType: type, Ionosphere: reading);
        }
        if (type != (byte)ObjectKind.Bulletin && type != (byte)ObjectKind.Directory)
        {
            // Not for the BBS, whatever it is. Marked done so its later frames are ignored.
            bool known = ContentType.IsKnown(type);
            MarkDone(instance);
            return new AcceptResult(known ? FrameOutcome.CompletedUnhandled : FrameOutcome.CompletedUnknown, instance.ObjectId, ContentType: type);
        }

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
            if (directory.Schedule is { } schedule && (HeardScheduleDate is null || directory.Date >= HeardScheduleDate))
            {
                if (schedule != HeardSchedule || directory.Date != HeardScheduleDate)
                {
                    DurableFile.WriteAtomically(Path.Combine(_root, "schedule.txt"), Bulletin.TextEncoding.GetBytes(
                        directory.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\n" + string.Join('\t', schedule.ToFields()) + "\n"), flush: _options.FlushToDisk);
                }
                HeardSchedule = schedule;
                HeardScheduleDate = directory.Date;
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

        var written = _options.Time.GetUtcNow();
        string name = OutboxName(instance.ObjectId);
        DurableFile.WriteAtomically(Path.Combine(_outbox, name), content, written, _options.FlushToDisk); // flushes the outbox folder too
        _outboxItems[name] = new OutboxItem(name, instance.ObjectId, content, bulletin, written);
        MailChanged();
        MarkDone(instance);
        return new AcceptResult(FrameOutcome.CompletedBulletin, instance.ObjectId, Bulletin: bulletin);
    }

    /// <summary>
    /// An object that matches its ID but cannot be used was sent that way, so more pieces would
    /// only rebuild it again. It is not marked done: its pieces are dropped and further frames
    /// of it are ignored until the store is next opened.
    /// </summary>
    private AcceptResult Unusable(Instance instance, string why)
    {
        string detail = $"object {ObjectId.Format(instance.ObjectId)} {why}";
        Log(detail);
        _unusable.Add(instance.Key);
        Remove(instance);
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
        var files = System.IO.Directory.EnumerateFiles(dir, "*.sym")
            .Select(f => (File: f, Written: new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero)))
            .OrderBy(f => f.Written)
            .ThenBy(f => f.File, StringComparer.Ordinal); // arrival order, as near as the files show it
        foreach (var (file, written) in files)
        {
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint esi)
                || esi > PayloadId.MaxEncodingSymbolId
                || !instance.TryAdd(esi, File.ReadAllBytes(file), out _))
            {
                File.Delete(file);
                continue;
            }
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

    private void CountRead() => Interlocked.Increment(ref _diskReads);

    /// <summary>Reads the outbox into memory, once, when the store is opened; unreadable files are quarantined and logged.</summary>
    private void LoadOutbox()
    {
        CountRead();
        foreach (var file in System.IO.Directory.EnumerateFiles(_outbox, "*.bulletin").ToList())
        {
            string name = Path.GetFileName(file);
            try
            {
                CountRead();
                var bytes = File.ReadAllBytes(file);
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                ulong? id = ObjectId.TryParse(Path.GetFileNameWithoutExtension(file), out ulong parsed) ? parsed : null;
                _outboxItems[name] = new OutboxItem(name, id, bytes, Bulletin.Parse(bytes), written);
            }
            catch (FormatException e)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(_quarantine);
                    File.Move(file, Path.Combine(_quarantine, name), overwrite: true);
                    Log($"outbox file {name} unreadable, quarantined: {e.Message}");
                }
                catch (Exception move) when (move is IOException or UnauthorizedAccessException)
                {
                    Log($"outbox file {name} unreadable ({e.Message}) and cannot be quarantined: {move.Message}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log($"outbox file {name} cannot be read, skipped until the next start: {e.Message}");
            }
        }
    }

    /// <summary>Notes a change to the outbox or the archive, and rebuilds <see cref="Mail"/> now unless the owner does it.</summary>
    private void MailChanged()
    {
        _mailVersion++;
        if (_options.PublishMailOnChange)
        {
            Publish(CaptureMail()!);
        }
    }

    /// <summary>
    /// What <see cref="Mail"/> would be made from now, or null if nothing has changed since the
    /// last capture. Cheap: a copy of the outbox's few entries and the archive's immutable index.
    /// On the store's thread, like any other change.
    /// </summary>
    public MailParts? CaptureMail()
    {
        if (_capturedVersion == _mailVersion)
        {
            return null;
        }
        _capturedVersion = _mailVersion;
        var waiting = _outboxItems.Values
            .Where(o => o.ObjectId is not null)
            .Select(o => (o.ObjectId!.Value, o.Serialized, o.Bulletin, o.Written))
            .ToList();
        return new MailParts(_mailVersion, waiting, _archive.Order);
    }

    /// <summary>
    /// Builds a new <see cref="Mail"/> from <paramref name="parts"/>, one merge of two lists in
    /// time order. Any thread, outside the store's lock; parts older than the snapshot already
    /// published are dropped, so snapshots never go back in time.
    /// </summary>
    public void Publish(MailParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        lock (_publishGate)
        {
            if (parts.Version <= _publishedVersion)
            {
                return;
            }
            var waiting = parts.Waiting
                .GroupBy(o => o.Id)
                .Select(g => g.First())
                .OrderByDescending(o => o.Written)
                .ThenByDescending(o => o.Id)
                .ToList();
            var bytes = waiting.ToDictionary(o => o.Id, o => o.Serialized);
            var result = new List<MailEntry>(waiting.Count + parts.Archived.Count);
            int w = 0;
            foreach (var archived in parts.Archived.Reverse())
            {
                if (bytes.ContainsKey(archived.ObjectId))
                {
                    continue; // sent again, or left in both by a crash: it is waiting
                }
                while (w < waiting.Count && waiting[w].Written >= archived.Time)
                {
                    result.Add(Entry(waiting[w++]));
                }
                result.Add(archived);
            }
            while (w < waiting.Count)
            {
                result.Add(Entry(waiting[w++]));
            }
            _mail = new MailSnapshot([.. result], waiting.Count, bytes);
            _publishedVersion = parts.Version;
            Interlocked.Increment(ref _mailBuilds);
        }

        static MailEntry Entry((ulong Id, byte[] Serialized, Bulletin Bulletin, DateTimeOffset Written) o) =>
            MailEntry.Of(o.Id, o.Bulletin, o.Serialized.Length, o.Written, null, null);
    }

    private static string OutboxName(ulong objectId) => ObjectId.Format(objectId) + ".bulletin";

    /// <summary>A bulletin in the outbox: its file name, its object if the name gives one, and when it went in.</summary>
    private sealed record OutboxItem(string Name, ulong? ObjectId, byte[] Serialized, Bulletin Bulletin, DateTimeOffset Written);

    private string DonePath(ulong objectId) => Path.Combine(_doneDir, ObjectId.Format(objectId));


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

        /// <summary>ESIs held, in the order they arrived.</summary>
        public List<uint> Arrival { get; } = [];

        public DateTimeOffset LastPiece { get; set; }

        /// <summary>Where the search for a good set of pieces has got to; see <see cref="RepairCandidate"/>.</summary>
        public int RepairCursor { get; set; } = int.MaxValue; // no search yet: the first mismatch starts one

        /// <summary>How many pieces were held when the current search began.</summary>
        public int RepairPassPieces { get; set; } = -1;

        /// <summary>How many candidate sets the current search has.</summary>
        public int RepairCandidates(int randomSubsets) => Math.Max(RepairPassPieces, 0) + 1 + randomSubsets;

        /// <summary>
        /// Candidate set number i of the current search, or null where it does not apply: first all
        /// pieces but one, for each piece held when the search began, in arrival order (a single
        /// bad piece); then the newest K + 2 (bad pieces in an early burst); then seeded random
        /// sets of K + 1 (two or more bad pieces anywhere). Pieces that arrive during a search
        /// join the sets; a new search starts when one runs out and more pieces have come.
        /// </summary>
        public (List<uint>? Subset, uint? LeftOut) RepairCandidate(int i, int k, int randomSubsets)
        {
            int pass = Math.Max(RepairPassPieces, 0);
            int n = Arrival.Count;
            if (i < pass)
            {
                return i < n && n - 1 >= k ? (Arrival.Where((_, j) => j != i).ToList(), Arrival[i]) : (null, null);
            }
            if (i == pass)
            {
                return n > k + 2 ? (Arrival.Skip(n - (k + 2)).ToList(), null) : (null, null);
            }
            if (i <= pass + randomSubsets && n > k + 1)
            {
                var rng = new Random(unchecked((int)ObjectId ^ (int)(ObjectId >> 32) ^ (i * 7919)));
                var pool = Arrival.ToArray();
                for (int j = 0; j < k + 1; j++)
                {
                    int pick = j + rng.Next(pool.Length - j);
                    (pool[j], pool[pick]) = (pool[pick], pool[j]);
                }
                return (pool.Take(k + 1).ToList(), null);
            }
            return (null, null);
        }

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
            Arrival.Add(esi);
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
