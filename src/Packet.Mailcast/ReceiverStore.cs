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

    /// <summary>
    /// The symbol completed a PSK Reporter reading (content type 4, source 3), which is in the
    /// result. Never for the BBS; the newest is kept in <see cref="ReceiverStore.PskReporter"/>.
    /// </summary>
    CompletedPskReporter,

    /// <summary>
    /// The symbol completed a directory whose signature does not check, or is by a key this
    /// receiver does not trust (<see cref="AcceptResult.Check"/> says which): it is not used for
    /// anything, and <see cref="AcceptResult.Directory"/> is null.
    /// </summary>
    RejectedDirectory,

    /// <summary>
    /// The symbol completed a bulletin that only rejected directories list: it is held, not
    /// delivered, until a directory that is signed by a trusted key, or not signed, lists it.
    /// </summary>
    Withheld,

    /// <summary>
    /// The symbol completed a bulletin envelope (content type 5) that a trusted signed directory
    /// lists through an <c>env=</c> field and whose hash matches: it is kept (see
    /// <see cref="ReceiverStore.EnvelopesFor"/>) and nothing goes to the BBS. Last, so the values
    /// before it are what they always were.
    /// </summary>
    CompletedEnvelope,

    /// <summary>
    /// The symbol completed a canonical bulletin (issue #145) that has no envelope yet: it is
    /// kept, not delivered, until an envelope arrives, no directory lists it any more, or
    /// <see cref="ReceiverStore.DeliverNow"/> is called. <see cref="AcceptResult.Bulletin"/> is the canonical form.
    /// </summary>
    AwaitingEnvelope,
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
/// <param name="PskReporter">The reading the frame completed, for <see cref="FrameOutcome.CompletedPskReporter"/>.</param>
/// <param name="NoLongerSent">
/// Part-received bulletins this frame's directory (completed or repeated) showed the head end has
/// stopped sending, newly marked so (see <see cref="ReceiverStore.NoLongerSentRetention"/>); null if none.
/// </param>
/// <param name="Check">What the signature check of the directory found, for a directory completed, repeated or rejected.</param>
/// <param name="Released">
/// Bulletins held back (<see cref="FrameOutcome.Withheld"/>) that this frame's directory
/// (completed or repeated) lists, now delivered to the outbox; null if none.
/// </param>
public sealed record AcceptResult(FrameOutcome Outcome, ulong? ObjectId = null, Bulletin? Bulletin = null, BroadcastDirectory? Directory = null, string? Detail = null, byte? ContentType = null, IonoReading? Ionosphere = null, PskReading? PskReporter = null, IReadOnlyList<ulong>? NoLongerSent = null, DirectoryCheck? Check = null, IReadOnlyList<Bulletin>? Released = null);

/// <summary>The directory heard most recently, and what its signature check found.</summary>
/// <param name="Date">The directory's date.</param>
/// <param name="Check">What the check found.</param>
public sealed record HeardDirectoryCheck(DateOnly Date, DirectoryCheck Check);

/// <summary>How far one directory entry has got at this receiver.</summary>
/// <param name="Entry">The directory's entry.</param>
/// <param name="Complete">Whether it has been rebuilt.</param>
/// <param name="Received">Symbols held, if not complete.</param>
/// <param name="Needed">Symbols needed at least (K), if any have been received.</param>
public sealed record ObjectProgress(DirectoryEntry Entry, bool Complete, int Received, int Needed);

/// <summary>
/// One object the receiver currently holds and worth showing on its own list (issue #68): a
/// complete bulletin still relevant, or a part-received object, named by a remembered directory
/// or not. See <see cref="ReceiverStore.HeldBulletins"/>.
/// </summary>
/// <param name="ObjectId">The object's ID.</param>
/// <param name="Bid">
/// Its BID, from any directory still remembered that names the object, or, once it is complete,
/// from the bulletin itself; null if neither is known yet (a part-received object no known
/// directory names).
/// </param>
/// <param name="Title">Its title, the same way; null on the same terms as <paramref name="Bid"/>.</param>
/// <param name="Complete">Whether it has been rebuilt.</param>
/// <param name="Received">Symbols held, if not complete.</param>
/// <param name="Needed">Symbols needed at least (K), if any have been received.</param>
/// <param name="NoLongerSentSince">
/// For a part-received bulletin, when a directory showed the head end has stopped sending it, so it
/// can never be finished; null while it is still being sent, and for a complete one.
/// </param>
/// <param name="ClearedAt">When such an object is to be dropped (<see cref="ReceiverStore.NoLongerSentRetention"/> after <paramref name="NoLongerSentSince"/>); null otherwise.</param>
/// <param name="Signature">
/// What the directories still remembered that name it say about it, in a few words: "signed by
/// GB7RDG" only for a complete bulletin whose own hash matches the one a trusted signed directory
/// lists; "does not match GB7RDG's signed list" when it does not; "in GB7RDG's signed list" for
/// one not yet complete, or rebuilt before its hash was kept; "not signed" when only unsigned
/// directories name it; null when none does.
/// </param>
/// <param name="SignedBy">The trusted key's label, only when <paramref name="Signature"/> is "signed by" it.</param>
/// <param name="Withheld">
/// Whether it is rebuilt but held back because only directories that failed their signature
/// check list it (<see cref="FrameOutcome.Withheld"/>).
/// </param>
public sealed record HeldBulletin(ulong ObjectId, string? Bid, string? Title, bool Complete, int Received, int Needed, DateTimeOffset? NoLongerSentSince = null, DateTimeOffset? ClearedAt = null, string? Signature = null, string? SignedBy = null, bool Withheld = false);

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

    /// <summary>
    /// The transmitter keys pinned before any is learnt on the air: <see cref="TrustedKey.BuiltIn"/>
    /// unless the owner adds more, each pinned to its label. Others are pinned the first time
    /// their station's signed directory is heard (see <see cref="ReceiverStore"/>'s remarks).
    /// </summary>
    public IReadOnlyList<TrustedKey> TrustedKeys { get; init; } = TrustedKey.BuiltIn;

    /// <summary>Where the store says what it threw away and why. Plain ASCII.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// The callsign the receiver logs in to its BBS with, for the R: line it puts on a canonical
    /// bulletin (issue #145). Read at each delivery. Null or empty leaves the call out.
    /// </summary>
    public Func<string?>? DeliveryCall { get; init; }

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
/// done/OOOOOOOOOOOOOOOO                          the object has been rebuilt, and when (and a bulletin's long hash); later frames are ignored
/// stopped/OOOOOOOOOOOOOOOO                       a partial object the head end no longer sends, and since when
/// held/OOOOOOOOOOOOOOOO                          a rebuilt bulletin held back by its directory's signature, and since when
/// outbox/OOOOOOOOOOOOOOOO.bulletin               a rebuilt bulletin not yet acknowledged
/// archive/OOOOOOOOOOOOOOOO.mail                  an acknowledged bulletin and the BBS's answer (see MailArchive)
/// quarantine/                                    outbox and archive files that could not be read
/// directory.txt                                  the newest directory heard
/// pins.txt                                       each station's key, pinned when its first signed directory was heard
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
/// <para>
/// A directory lists every bulletin in rotation, so a partial bulletin a directory leaves out,
/// heard after the bulletin's last new piece, has been retired by the head end and can never be
/// finished. It is marked so, and dropped (given up) <see cref="NoLongerSentRetention"/> later
/// rather than after <see cref="ReceiverStoreOptions.PartialRetention"/>. A new piece of it, or
/// any directory that lists it again, takes the mark away.
/// </para>
/// <para>
/// Every directory's signature is checked against <see cref="ReceiverStoreOptions.TrustedKeys"/>
/// (<see cref="BroadcastDirectory.Check"/>). One signed by a trusted key, or not signed at all, is
/// used as always. One whose signature fails, or is by another key, is used for nothing: not as
/// the newest directory, not to name objects, and not to tell what the head end stopped sending.
/// It is remembered only so that a bulletin that it alone lists is held back rather than
/// delivered (<see cref="FrameOutcome.Withheld"/>), until a directory that may be used lists it
/// too. A bulletin a trusted signed directory lists is delivered only if it also matches the
/// long hash listed for it (<see cref="ObjectId.LongHash"/>).
/// </para>
/// <para>Not thread-safe: use one store from one thread, except <see cref="Mail"/> and
/// <see cref="ReadMail(ulong, out bool)"/>, which any thread may call.</para>
/// </remarks>
public sealed partial class ReceiverStore
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

    /// <summary>
    /// The long hash (<see cref="ObjectId.LongHash"/>) of each rebuilt bulletin, kept in its done
    /// marker, so that "signed by" is only ever said of a bulletin checked against a signed listing.
    /// </summary>
    private readonly Dictionary<ulong, string> _doneHashes = [];

    /// <summary>
    /// Bulletins held back (<see cref="FrameOutcome.Withheld"/>), kept in <c>held/</c> so that a
    /// restart, or the rejected directory being forgotten, never lets one through: only a
    /// directory that may be used, listing it, does.
    /// </summary>
    private readonly HashSet<ulong> _held = [];
    private readonly string _heldDir;
    private readonly string _envelopeDir;

    /// <summary>Delivered bulletins already logged as not matching a signed listing, so each is said once.</summary>
    private readonly HashSet<ulong> _mismatchSaid = [];
    private readonly string _stoppedDir;
    private readonly Dictionary<ulong, DateTimeOffset> _stopped = [];
    private readonly List<ulong> _givenUp = [];
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
        _stoppedDir = Path.Combine(root, "stopped");
        _heldDir = Path.Combine(root, "held");
        _envelopeDir = Path.Combine(root, "envelopes");
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
                (_done[id], string? hash) = ReadDone(marker);
                if (hash is not null)
                {
                    _doneHashes[id] = hash;
                }
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
        LoadAwaiting();
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
        var pskFile = Path.Combine(root, PskReporterFile);
        if (File.Exists(pskFile))
        {
            if (PskRecord.TryDecode(File.ReadAllBytes(pskFile), out var held))
            {
                PskReporter = held;
            }
            else
            {
                Log($"{PskReporterFile} unreadable, removed");
                File.Delete(pskFile);
            }
        }
        LoadPins();
        var directoryFile = Path.Combine(root, "directory.txt");
        if (File.Exists(directoryFile))
        {
            try
            {
                var newest = BroadcastDirectory.Parse(File.ReadAllBytes(directoryFile));
                var check = CheckDirectory(newest);
                if (check.Usable)
                {
                    Directory = newest;
                    NewestCheck = check;
                    LastDirectoryHeard = new HeardDirectoryCheck(newest.Date, check);
                }
                else
                {
                    Log($"directory.txt not used: {check.Reason}");
                }
            }
            catch (FormatException e)
            {
                Log($"directory.txt unreadable, removed: {e.Message}");
                File.Delete(directoryFile);
            }
        }
        LoadKnownDirectories();
        foreach (var (directory, _, check) in _knownDirectories.Values.OrderBy(k => k.Directory.Date).ThenBy(k => k.HeardAt))
        {
            NoteAdverts(directory, check);
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
        LoadStopped();
        if (System.IO.Directory.Exists(_heldDir))
        {
            var partial = new HashSet<ulong>(_instances.Values.Select(i => i.ObjectId));
            foreach (var file in System.IO.Directory.EnumerateFiles(_heldDir))
            {
                if (ObjectId.TryParse(Path.GetFileName(file), out ulong id) && partial.Contains(id))
                {
                    _held.Add(id);
                }
                else
                {
                    TryDelete(file);
                }
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

    /// <summary>The newest directory heard, if any, of those that may be used (see the class remarks).</summary>
    public BroadcastDirectory? Directory { get; private set; }

    /// <summary>What the signature check of <see cref="Directory"/> found: signed by a trusted key, or not signed.</summary>
    public DirectoryCheck? NewestCheck { get; private set; }

    /// <summary>
    /// The directory heard (completed or repeated) most recently, used or not, and what its
    /// signature check found; null until one has been heard.
    /// </summary>
    public HeardDirectoryCheck? LastDirectoryHeard { get; private set; }

    /// <summary>
    /// Every directory recognised in the last <see cref="KnownDirectoriesRetention"/>, by its own
    /// object ID, kept in <see cref="KnownDirectoriesFolder"/> across restarts. The two waveforms
    /// take turns resending the same day's rotation, and the mode each went out on is part of the
    /// object, so the same rotation has a different ID on each: without remembering more than
    /// just the newest, a repeat on the other waveform (<see cref="FrameOutcome.AlreadyComplete"/>)
    /// would not be recognised as "this slot heard the directory", and after three or more slots
    /// the oldest would never be recognised again at all.
    /// </summary>
    private readonly Dictionary<ulong, (BroadcastDirectory Directory, DateTimeOffset HeardAt, DirectoryCheck Check)> _knownDirectories = [];

    /// <summary>
    /// Directories whose signature check failed, by object ID, kept like <see cref="_knownDirectories"/>
    /// (in the same folder, and checked again when read back) but used only to hold back the
    /// bulletins that they alone list.
    /// </summary>
    private readonly Dictionary<ulong, (BroadcastDirectory Directory, DateTimeOffset HeardAt, DirectoryCheck Check)> _rejectedDirectories = [];

    /// <summary>Where each known directory (see <see cref="_knownDirectories"/>) is kept, one file per object ID in hex, under the store's folder.</summary>
    public const string KnownDirectoriesFolder = "directories";

    /// <summary>How long a directory is remembered by its object ID.</summary>
    public static readonly TimeSpan KnownDirectoriesRetention = TimeSpan.FromHours(48);

    /// <summary>
    /// How long a partial object is kept once a directory has shown the head end stopped sending
    /// it: long enough to see on the status page why it will never fill, no longer.
    /// </summary>
    public static readonly TimeSpan NoLongerSentRetention = TimeSpan.FromHours(24);

    /// <summary>Where the newest ionosonde reading is kept, as the object that carried it, under the store's folder.</summary>
    public const string IonosphereFile = "ionosphere.bin";

    /// <summary>
    /// The ionosonde reading with the newest sounding heard (content type 4), kept across
    /// restarts; null until one has been heard. As sent: its age is as of then.
    /// </summary>
    public IonoReading? Ionosphere { get; private set; }

    /// <summary>Where the newest PSK Reporter reading is kept, as the object that carried it, under the store's folder.</summary>
    public const string PskReporterFile = "pskreporter.bin";

    /// <summary>
    /// The newest PSK Reporter reading heard (content type 4, source 3), kept across restarts;
    /// null until one has been heard. As sent: its age is as of then.
    /// </summary>
    public PskReading? PskReporter { get; private set; }

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

    /// <summary>Reads every directory still within <see cref="KnownDirectoriesRetention"/> from <see cref="KnownDirectoriesFolder"/>.</summary>
    private void LoadKnownDirectories()
    {
        string dir = Path.Combine(_root, KnownDirectoriesFolder);
        if (!System.IO.Directory.Exists(dir))
        {
            return;
        }
        var now = _options.Time.GetUtcNow();
        foreach (string file in System.IO.Directory.EnumerateFiles(dir))
        {
            DateTimeOffset heardAt;
            try
            {
                heardAt = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (now - heardAt > KnownDirectoriesRetention)
            {
                TryDelete(file);
                continue;
            }
            if (!ObjectId.TryParse(Path.GetFileNameWithoutExtension(file), out ulong id))
            {
                continue;
            }
            try
            {
                var directory = BroadcastDirectory.Parse(File.ReadAllBytes(file));
                var check = CheckDirectory(directory);
                (check.Usable ? _knownDirectories : _rejectedDirectories)[id] = (directory, heardAt, check);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
                Log($"{file} unreadable, removed: {e.Message}");
                TryDelete(file);
            }
        }
    }

    /// <summary>
    /// Keeps <paramref name="directory"/> under its own object ID, so a later repeat of it
    /// (<see cref="FrameOutcome.AlreadyComplete"/>, as the other waveform resends it, or after a
    /// restart) is still recognised.
    /// </summary>
    private void RememberDirectory(ulong objectId, BroadcastDirectory directory, DirectoryCheck check)
    {
        var now = _options.Time.GetUtcNow();
        (check.Usable ? _knownDirectories : _rejectedDirectories)[objectId] = (directory, now, check);
        NoteAdverts(directory, check);
        LastDirectoryHeard = new HeardDirectoryCheck(directory.Date, check);
        try
        {
            System.IO.Directory.CreateDirectory(Path.Combine(_root, KnownDirectoriesFolder));
            DurableFile.WriteAtomically(Path.Combine(_root, KnownDirectoriesFolder, ObjectId.Format(objectId) + ".txt"), directory.Serialize(), now, _options.FlushToDisk);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"cannot remember the directory {ObjectId.Format(objectId)}: {e.Message}");
        }
    }

    /// <summary>Forgets directories heard more than <see cref="KnownDirectoriesRetention"/> ago.</summary>
    private void PruneKnownDirectories(DateTimeOffset now)
    {
        foreach (var known in new[] { _knownDirectories, _rejectedDirectories })
        {
            foreach (var id in known.Where(kv => now - kv.Value.HeardAt > KnownDirectoriesRetention).Select(kv => kv.Key).ToList())
            {
                known.Remove(id);
                TryDelete(Path.Combine(_root, KnownDirectoriesFolder, ObjectId.Format(id) + ".txt"));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Offers one received frame payload (an AX.25 UI frame's information field).</summary>
    /// <param name="payload">The frame's information field.</param>
    /// <param name="heardFrom">The station this frame is taken to come from (the slot's station), or null if not known; counted per object to choose whose envelope to use (issue #145).</param>
    public AcceptResult Accept(ReadOnlySpan<byte> payload, string? heardFrom = null)
    {
        var now = _options.Time.GetUtcNow();
        if (now - _lastExpiry >= ExpiryInterval)
        {
            Expire();
        }
        else
        {
            DropNoLongerSent(now);
        }
        if (!MailcastFrame.TryParse(payload, out var frame) || frame is null)
        {
            return new AcceptResult(FrameOutcome.NotAFrame);
        }
        if (_done.ContainsKey(frame.ObjectId))
        {
            // A repeat of a directory already held: still worth telling the caller which, so a
            // slot that only ever resends one already known (WN3/WN4 taking turns give the same
            // rotation a different ID each, or this is the first repeat since a restart) is not
            // treated as one where the directory was never heard. Issue #68 review: also
            // refreshes when it was last heard, so a directory repeated every so often (well
            // inside KnownDirectoriesRetention each time) keeps being "remembered" for as long
            // as it keeps being heard, rather than the clock running out 48 h after the one time
            // it was originally rebuilt.
            // A repeat of a directory that failed its signature check is remembered as heard
            // again, so it keeps holding back what only it lists, but is used for nothing else.
            BroadcastDirectory? repeated = null;
            IReadOnlyList<ulong>? stopped = null;
            IReadOnlyList<Bulletin>? released = null;
            DirectoryCheck? check = null;
            if (_knownDirectories.TryGetValue(frame.ObjectId, out var known))
            {
                repeated = known.Directory;
                check = known.Check;
                RememberDirectory(frame.ObjectId, known.Directory, known.Check);
                stopped = CheckRotation(known.Directory, known.Check, now);
                released = Merge(ReleaseWithheld(known.Directory), ReleaseAwaiting(known.Directory, known.Check));
                SayMismatches(known.Directory, known.Check);
            }
            else if (_rejectedDirectories.TryGetValue(frame.ObjectId, out var rejected))
            {
                check = rejected.Check;
                RememberDirectory(frame.ObjectId, rejected.Directory, rejected.Check);
            }
            return new AcceptResult(FrameOutcome.AlreadyComplete, frame.ObjectId, Directory: repeated, NoLongerSent: stopped, Check: check, Released: released);
        }
        if (!_compression.Knows(frame.DictionaryId))
        {
            return new AcceptResult(FrameOutcome.UnknownDictionary, frame.ObjectId);
        }
        CountFrame(frame.ObjectId, heardFrom);

        string key = KeyOf(frame.ObjectId, frame.DictionaryId, frame.Oti);
        if (_unusable.Contains(key))
        {
            return new AcceptResult(FrameOutcome.Rejected, frame.ObjectId, Detail: "object already rebuilt and found unusable");
        }
        bool isNew = !_instances.TryGetValue(key, out var instance);
        instance ??= new Instance(key, frame.ObjectId, frame.DictionaryId, frame.Oti, Path.Combine(_objects, key));
        if (instance.Withheld is not null)
        {
            // Rebuilt already, and held back: more pieces would only rebuild it again.
            return new AcceptResult(FrameOutcome.AlreadyComplete, frame.ObjectId);
        }
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
        if (_stopped.ContainsKey(frame.ObjectId))
        {
            Log($"partial object {Describe(frame.ObjectId)} has a new piece, so the head end is sending it again; kept");
            ForgetStopped(frame.ObjectId);
        }
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
                        _archive.Add(id, held.Serialized, held.Bulletin, verdict, detail, held.Written); // on disk before the outbox file goes
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
    /// Everything on the receiver's own bulletin list (issue #68): every complete bulletin still
    /// relevant - named by a directory still remembered (<see cref="KnownDirectoriesRetention"/>),
    /// or completed within that same window even when no remembered directory names it - and
    /// every part-received object currently held, named or not, so its progress bar shows whether
    /// or not the newest directory heard happens to name it. Title and BID come from any
    /// remembered directory naming the object, or, once it is complete, from the bulletin itself;
    /// a part-received object no known directory names gets a row with a null title and BID,
    /// which the page shows as "not yet named".
    /// </summary>
    public IReadOnlyList<HeldBulletin> HeldBulletins()
    {
        var cutoff = _options.Time.GetUtcNow() - KnownDirectoriesRetention;

        // The newest name for an object, from whichever remembered directory names it. Seeded
        // first from the one directory heard now (Directory itself never ages out: it stays
        // "the newest heard" until something replaces it, however long that takes), so a
        // station stuck on one directory for days still gets every title even once that
        // directory has fallen out of the 48 h cache below (issue #68 review).
        var named = new Dictionary<ulong, (string Bid, string Title, DateTimeOffset HeardAt)>();
        if (Directory is { } newest)
        {
            foreach (var entry in newest.Entries)
            {
                named[entry.ObjectId] = (entry.Bid, entry.Title, DateTimeOffset.MinValue);
            }
        }
        foreach (var (directory, heardAt, _) in _knownDirectories.Values)
        {
            foreach (var entry in directory.Entries)
            {
                if (!named.TryGetValue(entry.ObjectId, out var have) || heardAt > have.HeardAt)
                {
                    named[entry.ObjectId] = (entry.Bid, entry.Title, heardAt);
                }
            }
        }

        // The fullest instance of each partial object, found in one pass rather than once per entry.
        var fullest = new Dictionary<ulong, Instance>();
        foreach (var instance in _instances.Values)
        {
            if (!fullest.TryGetValue(instance.ObjectId, out var best) || instance.Symbols.Count > best.Symbols.Count)
            {
                fullest[instance.ObjectId] = instance;
            }
        }

        // Envelopes (issue #142) are not bulletins: they are linked from an entry's env= and never listed as rows.
        var envelopeIds = new HashSet<ulong>(UsableDirectories().SelectMany(d => d.Directory.Entries).Where(e => e.Envelope is not null).Select(e => e.Envelope!.ObjectId));
        var ids = new HashSet<ulong>(fullest.Where(kv => !envelopeIds.Contains(kv.Key) && !(kv.Value.Symbols.TryGetValue(0, out var first) && first.Length > 0 && (first[0] & ~ContentType.MetadataFollows) == (byte)ObjectKind.Envelope)
            && !(kv.Value.Withheld is { Length: > 0 } rebuilt && (rebuilt[0] & ~ContentType.MetadataFollows) == (byte)ObjectKind.Envelope)).Select(kv => kv.Key));
        var withheld = new HashSet<ulong>(_instances.Values.Where(i => i.Withheld is not null).Select(i => i.ObjectId));
        foreach (var id in named.Keys)
        {
            if (_done.ContainsKey(id))
            {
                ids.Add(id);
            }
        }
        // Newest answered (or, waiting, newest built) first; an entry's Completed time is never
        // after its Time (it cannot be answered before it is rebuilt), so once Time itself falls
        // behind the cutoff nothing further down the list can qualify either.
        foreach (var entry in Mail.NewestFirst)
        {
            if (entry.Time < cutoff)
            {
                break;
            }
            if ((entry.Completed ?? entry.Time) >= cutoff)
            {
                ids.Add(entry.ObjectId);
            }
        }

        var result = new List<HeldBulletin>(ids.Count);
        foreach (var id in ids)
        {
            bool complete = _done.ContainsKey(id);
            (string Bid, string Title, DateTimeOffset HeardAt)? name = named.TryGetValue(id, out var found) ? found : null;
            string? bid = name?.Bid;
            string? title = name?.Title;
            int received = 0, needed = 0;
            if (!complete && fullest.TryGetValue(id, out var held))
            {
                received = held.Symbols.Count;
                needed = held.Oti.SourceBlockSymbols(0);
            }
            if (bid is null && complete && Mail.ById(id) is { } mailEntry)
            {
                bid = mailEntry.Bid;
                title = mailEntry.Title;
            }
            DateTimeOffset? stoppedSince = !complete && _stopped.TryGetValue(id, out var since) ? since : null;
            var (signature, signedBy) = SignatureOf(id);
            result.Add(new HeldBulletin(id, bid, title, complete, received, needed, stoppedSince, stoppedSince + NoLongerSentRetention, signature, signedBy, withheld.Contains(id)));
        }
        return [.. result
            .OrderBy(r => r.Complete) // part-received first: the most actionable
            .ThenBy(r => r.NoLongerSentSince is not null) // and of those, the ones still being sent
            .ThenByDescending(r => r.Received)
            .ThenBy(r => r.Bid is null) // named rows before ones still "not yet named"
            .ThenBy(r => r.Bid, StringComparer.Ordinal)
            .ThenBy(r => r.ObjectId)];
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
                _doneHashes.Remove(id);
            }
        }
        PruneKnownDirectories(now);
        PruneEnvelopes(now);
        DropNoLongerSent(now);
        foreach (var instance in _instances.Values.ToList())
        {
            if (now - instance.LastPiece > _options.PartialRetention)
            {
                Log($"partial object {ObjectId.Format(instance.ObjectId)} expired with {instance.Symbols.Count} of {instance.Oti.SourceBlockSymbols(0)} pieces");
                Remove(instance);
            }
        }
    }

    /// <summary>
    /// Compares the partial objects held with a directory just heard (completed, or a repeat of
    /// one known), and returns those newly marked as no longer sent. The head end lists every
    /// bulletin in rotation, so a bulletin the directory leaves out, whose last new piece came
    /// before the directory did, has been retired and can never be finished.
    /// </summary>
    /// <remarks>
    /// <para>Only objects known to be bulletins are marked (<see cref="IsKnownBulletin"/>):
    /// directories and the propagation readings are never listed, so their absence says nothing.</para>
    /// <para>Any directory that lists a marked object takes the mark away, whatever its date, which
    /// undoes a mark a stray directory (issue #72: an old one, or another head end's) may have
    /// made. Only marking is limited by date: a directory older than the newest plausible one
    /// heard, or dated after today by this receiver's clock, says nothing about what is being
    /// sent now.</para>
    /// </remarks>
    private List<ulong>? CheckRotation(BroadcastDirectory directory, DirectoryCheck check, DateTimeOffset heardAt)
    {
        var listed = new HashSet<ulong>(directory.Entries.Select(e => e.ObjectId));
        DateOnly latest = LatestPlausibleDate(heardAt);
        // Issue #144: a station's directory speaks only for that station. A bulletin another
        // station's newest directory still lists is still being sent, and a directory that
        // belongs to no station gives nothing up.
        string? station = StationOf(directory, check);
        var others = station is null ? [] : StationDirectories().Where(d => d.Station != station).ToList();
        bool canMark = station is not null && directory.Date <= latest && directory.Date >= NewestDirectoryDate(latest, station);
        List<ulong>? marked = null;
        foreach (var held in _instances.Values.GroupBy(i => i.ObjectId).OrderBy(g => g.Key))
        {
            ulong id = held.Key;
            if (listed.Contains(id))
            {
                if (_stopped.ContainsKey(id))
                {
                    Log($"partial object {Describe(id)} is in the directory again, so the head end is still sending it; kept");
                    ForgetStopped(id);
                }
                continue;
            }
            if (!canMark || _stopped.ContainsKey(id) || held.Max(i => i.LastPiece) >= heardAt || !IsKnownBulletin(id, held)
                || others.Any(o => o.Directory.Find(id) is not null))
            {
                continue;
            }
            var fullest = held.MaxBy(i => i.Symbols.Count)!;
            WriteStopped(id, heardAt);
            Log($"partial object {Describe(id)} has {fullest.Symbols.Count} of {fullest.Oti.SourceBlockSymbols(0)} pieces and is no longer in the directory: "
                + $"the head end has stopped sending it, so it can't be finished; dropping it at {(heardAt + NoLongerSentRetention).UtcDateTime:yyyy-MM-dd HH:mm} UTC");
            (marked ??= []).Add(id);
        }
        return marked;
    }

    /// <summary>
    /// The latest directory date that can be about now: today by this receiver's clock, or
    /// tomorrow within the last hour of the UTC day, so a few minutes' difference between the two
    /// clocks around midnight does not count as a directory from the future.
    /// </summary>
    private static DateOnly LatestPlausibleDate(DateTimeOffset now) => DateOnly.FromDateTime((now + TimeSpan.FromHours(1)).UtcDateTime);

    /// <summary>
    /// The newest date of any directory heard from <paramref name="station"/> (the newest, or one
    /// still remembered) that is not after <paramref name="latest"/>.
    /// </summary>
    private DateOnly NewestDirectoryDate(DateOnly latest, string station)
    {
        var newest = DateOnly.MinValue;
        foreach (var (directory, check) in UsableDirectories())
        {
            if (directory.Date <= latest && directory.Date > newest && StationOf(directory, check) == station)
            {
                newest = directory.Date;
            }
        }
        return newest;
    }

    /// <summary>
    /// Whether a partial object is known to be a bulletin: a directory heard (the newest, or one
    /// still remembered) lists it as one, or its piece 0 is held. The frame header does not say
    /// what an object is, but RaptorQ is systematic: piece 0 is the object's first octets, and the
    /// first of those is its content type (<see cref="ContentType"/>). An object that is neither
    /// is left to the ordinary <see cref="ReceiverStoreOptions.PartialRetention"/>.
    /// </summary>
    private bool IsKnownBulletin(ulong objectId, IEnumerable<Instance> held)
    {
        foreach (var instance in held)
        {
            if (instance.Symbols.TryGetValue(0, out var first) && first.Length > 0)
            {
                return (first[0] & ~ContentType.MetadataFollows) == (byte)ObjectKind.Bulletin;
            }
        }
        if (Directory?.Find(objectId) is { } entry)
        {
            return entry.Type == (byte)ObjectKind.Bulletin;
        }
        foreach (var (directory, _, _) in _knownDirectories.Values)
        {
            if (directory.Find(objectId) is { } remembered)
            {
                return remembered.Type == (byte)ObjectKind.Bulletin;
            }
        }
        return false;
    }

    /// <summary>
    /// Drops every partial object marked as no longer sent more than
    /// <see cref="NoLongerSentRetention"/> ago. Runs with each frame, and cheaply: the marks are
    /// few. A caller that shows the list between frames calls it first, so nothing overdue shows.
    /// Each object dropped this way is given up: see <see cref="TakeGivenUp"/>.
    /// </summary>
    public void DropNoLongerSent() => DropNoLongerSent(_options.Time.GetUtcNow());

    /// <summary>
    /// The objects given up (dropped because the head end stopped sending them) since the last
    /// call, oldest first, so the owner can count them outside whatever lock guards the store.
    /// Held in memory only.
    /// </summary>
    public IReadOnlyList<ulong> TakeGivenUp()
    {
        if (_givenUp.Count == 0)
        {
            return [];
        }
        var taken = _givenUp.ToList();
        _givenUp.Clear();
        return taken;
    }

    private void DropNoLongerSent(DateTimeOffset now)
    {
        if (_stopped.Count == 0)
        {
            return;
        }
        foreach (var (id, since) in _stopped.Where(kv => now - kv.Value >= NoLongerSentRetention).OrderBy(kv => kv.Key).ToList())
        {
            var held = _instances.Values.Where(i => i.ObjectId == id).ToList();
            var fullest = held.MaxBy(i => i.Symbols.Count);
            Log(fullest is null
                ? $"partial object {Describe(id)} dropped: the head end stopped sending it at {since.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
                : $"partial object {Describe(id)} dropped with {fullest.Symbols.Count} of {fullest.Oti.SourceBlockSymbols(0)} pieces: the head end stopped sending it at {since.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
            foreach (var instance in held)
            {
                Remove(instance);
            }
            ForgetStopped(id); // Remove has, unless there was nothing to remove
            _givenUp.Add(id);
        }
    }

    private void WriteStopped(ulong objectId, DateTimeOffset since)
    {
        System.IO.Directory.CreateDirectory(_stoppedDir);
        DurableFile.WriteAtomically(StoppedPath(objectId), Bulletin.TextEncoding.GetBytes(since.ToString("o", CultureInfo.InvariantCulture)), flush: _options.FlushToDisk);
        _stopped[objectId] = since;
    }

    private void ForgetStopped(ulong objectId)
    {
        _stopped.Remove(objectId);
        TryDelete(StoppedPath(objectId));
    }

    /// <summary>
    /// Reads the no-longer-sent marks, once the partial objects are loaded: a mark whose object is
    /// no longer held goes, as does one that does not read.
    /// </summary>
    private void LoadStopped()
    {
        if (!System.IO.Directory.Exists(_stoppedDir))
        {
            return;
        }
        var held = new HashSet<ulong>(_instances.Values.Select(i => i.ObjectId));
        foreach (var file in System.IO.Directory.EnumerateFiles(_stoppedDir))
        {
            if (!ObjectId.TryParse(Path.GetFileName(file), out ulong id) || !held.Contains(id))
            {
                TryDelete(file);
                continue;
            }
            string text;
            try
            {
                text = File.ReadAllText(file).Trim();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since))
            {
                _stopped[id] = since;
            }
            else
            {
                Log($"stopped/{Path.GetFileName(file)} unreadable, removed");
                TryDelete(file);
            }
        }
    }

    /// <summary>An object's ID, with its BID when a directory still remembered names it: for log lines.</summary>
    private string Describe(ulong objectId)
    {
        string? bid = Directory?.Find(objectId)?.Bid;
        if (bid is null)
        {
            foreach (var (directory, _, _) in _knownDirectories.Values)
            {
                if (directory.Find(objectId) is { } entry)
                {
                    bid = entry.Bid;
                    break;
                }
            }
        }
        return bid is null ? ObjectId.Format(objectId) : $"{ObjectId.Format(objectId)} ({bid})";
    }

    private string StoppedPath(ulong objectId) => Path.Combine(_stoppedDir, ObjectId.Format(objectId));

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
            if (PskRecord.TryDecode(data, out var spots))
            {
                if (PskReporter?.ObservedUtc is not { } heard || spots.ObservedUtc >= heard)
                {
                    DurableFile.WriteAtomically(Path.Combine(_root, PskReporterFile), data, flush: _options.FlushToDisk);
                    PskReporter = spots;
                }
                return new AcceptResult(FrameOutcome.CompletedPskReporter, instance.ObjectId, ContentType: type, PskReporter: spots);
            }
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
        if (type == (byte)ObjectKind.Envelope)
        {
            return FinishEnvelope(instance, data);
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
            var check = CheckDirectory(directory);
            if (!check.Usable)
            {
                // Once per directory: it is marked done, so its repeats are not rebuilt again.
                string why = $"directory {ObjectId.Format(instance.ObjectId)} for {directory.Date:yyyy-MM-dd} not used: {check.Reason}";
                Log(why);
                MarkDone(instance);
                RememberDirectory(instance.ObjectId, directory, check);
                return new AcceptResult(FrameOutcome.RejectedDirectory, instance.ObjectId, Detail: why, Check: check);
            }
            if (Directory is null || directory.Date >= Directory.Date)
            {
                DurableFile.WriteAtomically(Path.Combine(_root, "directory.txt"), content, flush: _options.FlushToDisk);
                Directory = directory;
                NewestCheck = check;
            }
            RememberDirectory(instance.ObjectId, directory, check);
            var released = AfterNewPin() ?? [];
            if (directory.Schedule is { } schedule && StationOf(directory, check) == BuiltInStation && (HeardScheduleDate is null || directory.Date >= HeardScheduleDate))
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
            var stopped = CheckRotation(directory, check, _options.Time.GetUtcNow());
            released.AddRange(ReleaseWithheld(directory) ?? []);
            released.AddRange(ReleaseAwaiting(directory, check) ?? []);
            SayMismatches(directory, check);
            return new AcceptResult(FrameOutcome.CompletedDirectory, instance.ObjectId, Directory: directory, NoLongerSent: stopped, Check: check, Released: released.Count > 0 ? released : null);
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

        string longHash = ObjectId.LongHash(instance.DictionaryId, data);
        switch (Admit(instance.ObjectId, longHash))
        {
            case Admission.WrongHash:
                return Unusable(instance, $"({bulletin.Bid}) does not match the hash its signed directory lists for it, so it is not delivered");
            case Admission.Hold:
                if (_held.Add(instance.ObjectId))
                {
                    System.IO.Directory.CreateDirectory(_heldDir);
                    DurableFile.WriteAtomically(Path.Combine(_heldDir, ObjectId.Format(instance.ObjectId)),
                        Bulletin.TextEncoding.GetBytes(_options.Time.GetUtcNow().ToString("o", CultureInfo.InvariantCulture)), flush: _options.FlushToDisk);
                }
                if (instance.Withheld is null)
                {
                    instance.Withheld = data;
                    Log($"bulletin {bulletin.Bid} ({ObjectId.Format(instance.ObjectId)}) is complete, but only a directory that failed its signature check lists it: held, not delivered, until a good directory lists it");
                }
                return new AcceptResult(FrameOutcome.Withheld, instance.ObjectId, Detail: "held: only a directory that failed its signature check lists it");
            default:
                break;
        }

        if (bulletin.Canonical)
        {
            return Await(instance, bulletin, longHash);
        }

        var written = _options.Time.GetUtcNow();
        string name = OutboxName(instance.ObjectId);
        DurableFile.WriteAtomically(Path.Combine(_outbox, name), content, written, _options.FlushToDisk); // flushes the outbox folder too
        _outboxItems[name] = new OutboxItem(name, instance.ObjectId, content, bulletin, written);
        MailChanged();
        MarkDone(instance, longHash);
        return new AcceptResult(FrameOutcome.CompletedBulletin, instance.ObjectId, Bulletin: bulletin);
    }

    /// <summary>
    /// Checks and keeps an envelope (issue #142). Admitted only when a trusted signed directory
    /// lists it as some entry's <c>env=</c> and its long hash matches that; one that is not listed yet
    /// is held, like a bulletin only a rejected directory lists, until a directory lists it. Nothing
    /// goes to the BBS: delivery does not use envelopes yet.
    /// </summary>
    private AcceptResult FinishEnvelope(Instance instance, byte[] data)
    {
        BulletinEnvelope envelope;
        try
        {
            var (_, content) = TransferObject.Unpack(data, instance.DictionaryId, _compression);
            envelope = BulletinEnvelope.Parse(content);
        }
        catch (KeyNotFoundException)
        {
            Log($"object {ObjectId.Format(instance.ObjectId)} needs dictionary {instance.DictionaryId}, which this receiver does not have; skipped");
            instance.ResetDecoder();
            return new AcceptResult(FrameOutcome.UnknownDictionary, instance.ObjectId);
        }
        catch (Exception e) when (e is InvalidDataException or FormatException)
        {
            return Unusable(instance, "is an envelope that does not parse: " + e.Message);
        }

        string longHash = ObjectId.LongHash(instance.DictionaryId, data);
        bool listed = false;
        foreach (var (directory, check) in UsableDirectories())
        {
            if (check.State != SignatureState.Trusted)
            {
                continue;
            }
            foreach (var entry in directory.Entries)
            {
                if (entry.Envelope is not { } link || link.ObjectId != instance.ObjectId)
                {
                    continue;
                }
                listed = true;
                if (string.Equals(link.Hash, longHash, StringComparison.Ordinal))
                {
                    System.IO.Directory.CreateDirectory(_envelopeDir);
                    DurableFile.WriteAtomically(EnvelopePath(instance.ObjectId), envelope.Serialise(), _options.Time.GetUtcNow(), _options.FlushToDisk);
                    MarkDone(instance, longHash);
                    return new AcceptResult(FrameOutcome.CompletedEnvelope, instance.ObjectId, ContentType: (byte)ObjectKind.Envelope, Released: ReleaseAwaiting(null, null));
                }
            }
        }
        if (listed)
        {
            return Unusable(instance, "does not match the hash its signed directory lists for it, so it is not kept");
        }
        if (instance.Withheld is null)
        {
            instance.Withheld = data;
            Log($"envelope {ObjectId.Format(instance.ObjectId)} is complete, but no good directory lists it yet: held");
        }
        return new AcceptResult(FrameOutcome.Withheld, instance.ObjectId, Detail: "held: no trusted signed directory lists this envelope yet");
    }

    private string EnvelopePath(ulong objectId) => Path.Combine(_envelopeDir, ObjectId.Format(objectId));

    /// <summary>
    /// Every envelope kept for a bulletin, one per station whose trusted signed directory (still
    /// remembered) links one that has been heard and not pruned, in a fixed order (station name,
    /// then envelope object ID). Empty when there is none. Delivery does not use them yet (issue #142).
    /// </summary>
    public IReadOnlyList<(string Station, BulletinEnvelope Envelope)> EnvelopesFor(ulong bulletinObjectId)
    {
        var found = new List<(string Station, ulong ObjectId, BulletinEnvelope Envelope)>();
        foreach (var (directory, check) in UsableDirectories())
        {
            if (check.State != SignatureState.Trusted || directory.Find(bulletinObjectId)?.Envelope is not { } link)
            {
                continue;
            }
            string station = check.SignedBy ?? "";
            if (found.Any(f => f.ObjectId == link.ObjectId && f.Station == station))
            {
                continue;
            }
            if (ReadEnvelope(link.ObjectId) is { } envelope)
            {
                found.Add((station, link.ObjectId, envelope));
            }
        }
        return [.. found.OrderBy(f => f.Station, StringComparer.Ordinal).ThenBy(f => f.ObjectId).Select(f => (f.Station, f.Envelope))];
    }

    private BulletinEnvelope? ReadEnvelope(ulong envelopeObjectId)
    {
        string path = EnvelopePath(envelopeObjectId);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return BulletinEnvelope.Parse(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            Log($"envelope {ObjectId.Format(envelopeObjectId)} unreadable, removed");
            TryDelete(path);
            return null;
        }
    }

    /// <summary>
    /// Deletes kept envelopes older than the longest of <see cref="ReceiverStoreOptions.ArchiveRetention"/>,
    /// <see cref="ReceiverStoreOptions.DoneRetention"/> and <see cref="ReceiverStoreOptions.PartialRetention"/>
    /// (so archive.days 0 does not drop them within the hour), and forgets the done marker of each, so
    /// a resend can bring it back.
    /// </summary>
    private void PruneEnvelopes(DateTimeOffset now)
    {
        if (!System.IO.Directory.Exists(_envelopeDir))
        {
            return;
        }
        var keep = new[] { _options.ArchiveRetention, _options.DoneRetention, _options.PartialRetention }.Max();
        foreach (var file in System.IO.Directory.EnumerateFiles(_envelopeDir))
        {
            if (now - File.GetLastWriteTimeUtc(file) > keep)
            {
                TryDelete(file);
                if (ObjectId.TryParse(Path.GetFileName(file), out ulong id) && _done.Remove(id))
                {
                    _doneHashes.Remove(id);
                    TryDelete(DonePath(id));
                }
            }
        }
    }

    private enum Admission
    {
        Deliver,
        WrongHash,
        Hold,
    }

    /// <summary>
    /// Whether a rebuilt bulletin may go to the BBS: see the class remarks. A directory that may
    /// be used and lists it decides: a trusted signed one only if the long hash matches. Failing
    /// that, it is held if a rejected directory lists it, and delivered, as always, if nothing does.
    /// </summary>
    private Admission Admit(ulong objectId, string hash)
    {
        bool listed = false;
        bool signedListing = false;
        foreach (var (directory, check) in UsableDirectories())
        {
            if (directory.Find(objectId) is not { } entry)
            {
                continue;
            }
            listed = true;
            if (check.State == SignatureState.Trusted)
            {
                signedListing = true;
                if (string.Equals(entry.Hash, hash, StringComparison.Ordinal))
                {
                    return Admission.Deliver;
                }
            }
        }
        if (signedListing)
        {
            return Admission.WrongHash;
        }
        if (listed)
        {
            return Admission.Deliver;
        }
        return _held.Contains(objectId) || _rejectedDirectories.Values.Any(r => r.Directory.Find(objectId) is not null) ? Admission.Hold : Admission.Deliver;
    }

    /// <summary>The directories that may be used: the newest, and those remembered, with their checks.</summary>
    private IEnumerable<(BroadcastDirectory Directory, DirectoryCheck Check)> UsableDirectories()
    {
        var listings = _knownDirectories.Values.Select(k => (k.Directory, k.Check));
        return Directory is { } newest && NewestCheck is { } newestCheck ? listings.Append((newest, newestCheck)) : listings;
    }

    /// <summary>
    /// What the usable directories say about one object, as <see cref="HeldBulletin.Signature"/>
    /// and <see cref="HeldBulletin.SignedBy"/>: "signed by" only when its own kept hash matches a
    /// trusted signed listing.
    /// </summary>
    private (string? Words, string? SignedBy) SignatureOf(ulong objectId)
    {
        bool complete = _done.ContainsKey(objectId);
        string? hash = _doneHashes.GetValueOrDefault(objectId);
        string? signedList = null, mismatched = null;
        bool unsignedList = false;
        foreach (var (directory, check) in UsableDirectories())
        {
            if (directory.Find(objectId) is not { } entry)
            {
                continue;
            }
            if (check.State != SignatureState.Trusted)
            {
                unsignedList = true;
                continue;
            }
            if (complete && hash is not null)
            {
                if (string.Equals(entry.Hash, hash, StringComparison.Ordinal))
                {
                    return ($"signed by {check.SignedBy}", check.SignedBy);
                }
                mismatched ??= check.SignedBy;
            }
            signedList ??= check.SignedBy;
        }
        if (mismatched is not null)
        {
            return ($"does not match {mismatched}'s signed list", null);
        }
        if (signedList is not null)
        {
            return ($"in {signedList}'s signed list", null);
        }
        return (unsignedList ? "not signed" : null, null);
    }

    /// <summary>
    /// A trusted signed directory has just been heard: says once of each bulletin it lists that
    /// was delivered before (unchecked, as when no directory listed it yet) and does not match it.
    /// </summary>
    private void SayMismatches(BroadcastDirectory directory, DirectoryCheck check)
    {
        if (check.State != SignatureState.Trusted)
        {
            return;
        }
        foreach (var entry in directory.Entries)
        {
            if (_doneHashes.TryGetValue(entry.ObjectId, out string? hash) && !string.Equals(entry.Hash, hash, StringComparison.Ordinal) && _mismatchSaid.Add(entry.ObjectId))
            {
                Log($"bulletin {entry.Bid} ({ObjectId.Format(entry.ObjectId)}) was delivered before {check.SignedBy}'s signed directory listed it, and does not match the hash that directory gives for it");
            }
        }
    }

    /// <summary>
    /// Delivers, or rejects on its hash, every held-back bulletin that <paramref name="directory"/>
    /// (one that may be used) lists. Null if none was delivered.
    /// </summary>
    private List<Bulletin>? ReleaseWithheld(BroadcastDirectory directory)
    {
        List<Bulletin>? released = null;
        foreach (var instance in _instances.Values.Where(i => i.Withheld is not null && (directory.Find(i.ObjectId) is not null || directory.Entries.Any(e => e.Envelope?.ObjectId == i.ObjectId))).ToList())
        {
            if (!_instances.ContainsKey(instance.Key))
            {
                continue;
            }
            var result = Finish(instance, instance.Withheld!);
            if (result is { Outcome: FrameOutcome.CompletedEnvelope })
            {
                Log($"envelope {ObjectId.Format(instance.ObjectId)}, held back, is now listed by a good directory: kept");
            }
            if (result is { Outcome: FrameOutcome.CompletedBulletin, Bulletin: { } bulletin })
            {
                Log($"bulletin {bulletin.Bid} ({ObjectId.Format(instance.ObjectId)}), held back, is now listed by a good directory: delivered");
                (released ??= []).Add(bulletin);
            }
        }
        return released;
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

    private static List<Bulletin>? Merge(List<Bulletin>? a, List<Bulletin>? b) => a is null ? b : b is null ? a : [.. a, .. b];

    private void MarkDone(Instance instance, string? hash = null)
    {
        if (!_awaiting.ContainsKey(instance.ObjectId))
        {
            _heardFrames.Remove(instance.ObjectId);
        }
        WriteDone(instance.ObjectId, hash);
        foreach (var other in _instances.Values.Where(i => i.ObjectId == instance.ObjectId).ToList())
        {
            Remove(other);
        }
    }

    /// <summary>
    /// Writes a done marker: when, and for a bulletin, a second line with its long hash
    /// (<see cref="ObjectId.LongHash"/>). A receiver before signing reads only a time and, finding
    /// two lines, takes the file's own time instead, which is the same.
    /// </summary>
    private void WriteDone(ulong objectId, string? hash = null)
    {
        var now = _options.Time.GetUtcNow();
        string text = now.ToString("o", CultureInfo.InvariantCulture) + (hash is null ? "" : "\n" + hash);
        DurableFile.WriteAtomically(DonePath(objectId), Bulletin.TextEncoding.GetBytes(text), now, flush: _options.FlushToDisk);
        _done[objectId] = now;
        if (hash is not null)
        {
            _doneHashes[objectId] = hash;
        }
    }

    private static (DateTimeOffset When, string? Hash) ReadDone(string marker)
    {
        var lines = File.ReadAllText(marker).Trim().Split('\n');
        var when = DateTimeOffset.TryParse(lines[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : new DateTimeOffset(File.GetLastWriteTimeUtc(marker), TimeSpan.Zero);
        string? hash = lines.Length > 1 && lines[1].Trim() is { Length: ObjectId.ListedHashOctets * 2 } h && h.All(char.IsAsciiHexDigitLower) ? h : null;
        return (when, hash);
    }

    private void Remove(Instance instance)
    {
        _instances.Remove(instance.Key);
        if (System.IO.Directory.Exists(instance.Path))
        {
            System.IO.Directory.Delete(instance.Path, recursive: true);
        }
        if (_stopped.ContainsKey(instance.ObjectId) && !_instances.Values.Any(i => i.ObjectId == instance.ObjectId))
        {
            ForgetStopped(instance.ObjectId);
        }
        if (_held.Contains(instance.ObjectId) && !_instances.Values.Any(i => i.ObjectId == instance.ObjectId))
        {
            _held.Remove(instance.ObjectId);
            TryDelete(Path.Combine(_heldDir, ObjectId.Format(instance.ObjectId)));
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
            MailEntry.Of(o.Id, o.Bulletin, o.Serialized.Length, o.Written, null, null, o.Written);
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

        /// <summary>The rebuilt object, when it is held back (<see cref="FrameOutcome.Withheld"/>); null otherwise.</summary>
        public byte[]? Withheld { get; set; }

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
