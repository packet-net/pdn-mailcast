using System.Threading.Channels;
using Packet.Mailcast;

namespace Mailcast.Receiver;

/// <summary>
/// Takes broadcast frames from the modem and keeps them in the <see cref="ReceiverStore"/>,
/// which persists every piece so bulletins add up across days and restarts.
/// </summary>
/// <remarks>
/// <para>Frames arrive on the audio thread. Storing a piece writes and flushes a file, which is
/// not something to do there, so frames go through a queue to a worker of their own. Every call
/// into the store holds one lock, because the store is not thread-safe and the delivery side
/// reads it too.</para>
/// </remarks>
public sealed class Intake : IAsyncDisposable
{
    private readonly ReceiverStore _store;
    private readonly object _gate = new();
    // Bounded: frames come a few a minute, so a full queue means the store has stopped, and
    // memory should not be what finds that out. Frames are dropped, and counted, rather than waited for.
    private readonly Channel<ReadOnlyMemory<byte>> _queue;
    private readonly object _settledGate = new();
    private TaskCompletionSource _settledChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _offered;
    private long _settled;
    private readonly Action<string> _log;
    private readonly Task _worker;
    /// <summary>Frames that may wait for the store at once.</summary>
    public const int QueueLength = 4096;

    private long _framesHeard;
    private long _framesDropped;
    private long _framesStored;

    /// <summary>
    /// Opens the store under <paramref name="stateDirectory"/>/store. <paramref name="options"/>
    /// gives the clock and the archive's limits; the store's own log lines go to <paramref name="log"/>.
    /// </summary>
    public Intake(string stateDirectory, Action<string> log, ReceiverStoreOptions? options = null)
        : this(stateDirectory, log, QueueLength, options)
    {
    }

    /// <summary>For tests: a store with a shorter queue.</summary>
    internal Intake(string stateDirectory, Action<string> log, int queueLength, ReceiverStoreOptions? options = null)
    {
        _log = log;
        // With DropWrite a write to a full queue still says it succeeded; this is the only place
        // a dropped frame shows.
        _queue = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(queueLength) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite },
            _ => Dropped());
        _store = new ReceiverStore(Path.Combine(stateDirectory, "store"), Compression.Default,
            (options ?? new ReceiverStoreOptions()) with { Log = line => log("store: " + Ascii.Clean(line)) });
        _heardSchedule = _store.HeardSchedule;
        _progress = (_store.Directory, _store.Progress());
        _worker = Task.Run(RunAsync);
    }

    private SlotTimetable? _heardSchedule;
    private volatile Tuple<BroadcastDirectory?, IReadOnlyList<ObjectProgress>> _progressHeld = Tuple.Create<BroadcastDirectory?, IReadOnlyList<ObjectProgress>>(null, []);

    /// <summary>The published progress: a reference swapped whole, so a reader never sees half of one.</summary>
    private (BroadcastDirectory? Directory, IReadOnlyList<ObjectProgress> Progress) _progress
    {
        get => (_progressHeld.Item1, _progressHeld.Item2);
        set => _progressHeld = Tuple.Create(value.Directory, value.Progress);
    }

    /// <summary>
    /// The head end's timetable from the newest directory that gave one, kept across restarts;
    /// null until one has been heard.
    /// </summary>
    public SlotTimetable? HeardSchedule
    {
        get
        {
            lock (_gate)
            {
                return _heardSchedule;
            }
        }
    }

    /// <summary>A directory gave a timetable different from the one held (raised on the worker).</summary>
    public event Action<SlotTimetable>? ScheduleHeard;

    /// <summary>A bulletin has been rebuilt and is waiting in the outbox.</summary>
    public event Action<Bulletin>? BulletinCompleted;

    /// <summary>A broadcast frame was heard (raised on the worker).</summary>
    public event Action? FrameHeard;

    /// <summary>Broadcast frames heard since start.</summary>
    public long FramesHeard => Interlocked.Read(ref _framesHeard);

    /// <summary>Broadcast frames dropped because the store was not keeping up.</summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Broadcast frames that added a piece the store did not have.</summary>
    public long FramesStored => Interlocked.Read(ref _framesStored);

    /// <summary>
    /// Offers a decoded AX.25 frame. Anything that is not a broadcast frame is ignored. Safe to
    /// call from any thread; returns at once.
    /// </summary>
    public bool Offer(byte[] ax25Frame)
    {
        if (!BroadcastFrame.TryGetPayload(ax25Frame, out var payload))
        {
            return false;
        }
        Interlocked.Increment(ref _framesHeard);
        Interlocked.Increment(ref _offered);
        if (!_queue.Writer.TryWrite(payload))
        {
            Settle(); // only after DisposeAsync
        }
        return true;
    }

    /// <summary>
    /// Waits until every frame offered so far has been through the store or been dropped. Counted
    /// rather than marked with a queue entry, because a full queue would drop the marker too.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellation)
    {
        long target = Interlocked.Read(ref _offered);
        while (true)
        {
            Task changed;
            lock (_settledGate)
            {
                if (_settled >= target)
                {
                    return;
                }
                changed = _settledChanged.Task;
            }
            await changed.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }

    private void Dropped()
    {
        if (Interlocked.Increment(ref _framesDropped) % 100 == 1)
        {
            _log($"store: the store is not keeping up; {Interlocked.Read(ref _framesDropped)} frame(s) dropped");
        }
        Settle();
    }

    /// <summary>One more offered frame has been stored or dropped.</summary>
    private void Settle()
    {
        TaskCompletionSource changed;
        lock (_settledGate)
        {
            _settled++;
            changed = _settledChanged;
            _settledChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    /// <summary>Rebuilt bulletins not yet handed to the BBS, oldest first. Read from memory.</summary>
    public IReadOnlyList<Bulletin> Pending()
    {
        lock (_gate)
        {
            return _store.Pending();
        }
    }

    /// <summary>Moves a bulletin from the outbox to the archive once the BBS has answered for it for good.</summary>
    public void Acknowledge(string bid, BbsVerdict verdict, string? detail = null)
    {
        lock (_gate)
        {
            _store.Acknowledge(bid, verdict, detail);
        }
    }

    /// <summary>
    /// Every bulletin held, waiting or archived, newest first, as of the last change. Takes no
    /// lock and reads no files, so the web page never waits for the store or makes it wait.
    /// </summary>
    public MailSnapshot Mail() => _store.Mail;

    /// <summary>
    /// One bulletin as stored, with its entry, or null. Reads the archive file without the
    /// store's lock; only an unreadable one takes it, to quarantine the file.
    /// </summary>
    public (MailEntry Entry, byte[] Serialized)? ReadMail(ulong objectId)
    {
        var held = _store.ReadMail(objectId, out bool unreadable);
        if (unreadable)
        {
            lock (_gate)
            {
                _store.ForgetUnreadable(objectId);
            }
        }
        return held;
    }

    /// <summary>Puts an archived bulletin back in the outbox, to be offered to the BBS again.</summary>
    public (ResendOutcome Outcome, Bulletin? Bulletin) Resend(ulong objectId)
    {
        ResendOutcome outcome;
        Bulletin? bulletin;
        lock (_gate)
        {
            (outcome, bulletin) = _store.Resend(objectId);
            _progress = (_store.Directory, _store.Progress());
        }
        return (outcome, bulletin);
    }

    /// <summary>
    /// The newest directory heard, and how far each of its bulletins has got, as of the last
    /// frame that changed it. Takes no lock: the store's thread keeps it up to date.
    /// </summary>
    public (BroadcastDirectory? Directory, IReadOnlyList<ObjectProgress> Progress) Progress() => _progress;

    /// <summary>For tests: how many times the store has read the outbox or the archive from disk.</summary>
    internal long StoreDiskReads => _store.DiskReads;

    /// <summary>How many objects have pieces held but are not yet rebuilt.</summary>
    public int PartialObjects
    {
        get
        {
            lock (_gate)
            {
                return _store.PartialObjects;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        await foreach (var payload in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Accept(payload);
            }
            finally
            {
                Settle();
            }
        }
    }

    private void Accept(ReadOnlyMemory<byte> payload)
    {
        AcceptResult result;
        try
        {
            lock (_gate)
            {
                result = _store.Accept(payload.Span);
                if (result.Outcome is not (FrameOutcome.NotAFrame or FrameOutcome.AlreadyComplete or FrameOutcome.Duplicate or FrameOutcome.UnknownDictionary))
                {
                    _progress = (_store.Directory, _store.Progress());
                }
            }
        }
        catch (Exception e)
        {
            // The worker must outlive anything one frame does to the store, or every frame
            // after it is lost without a word.
            _log($"store: cannot keep a piece: {Ascii.Clean(e.Message)}");
            return;
        }

        try
        {
            FrameHeard?.Invoke();
            Report(result);
        }
        catch (Exception e)
        {
            _log($"store: a listener failed: {Ascii.Clean(e.Message)}");
        }
    }

    private void Report(AcceptResult result)
    {
        switch (result.Outcome)
        {
            case FrameOutcome.Stored:
                Interlocked.Increment(ref _framesStored);
                break;
            case FrameOutcome.CompletedBulletin when result.Bulletin is { } bulletin:
                Interlocked.Increment(ref _framesStored);
                _log($"bulletin complete: {Ascii.Clean(bulletin.Bid)} from {Ascii.Clean(bulletin.From)} to {Ascii.Clean(bulletin.To)}"
                    + $"{(bulletin.At.Length > 0 ? "@" + Ascii.Clean(bulletin.At) : "")}, \"{Ascii.Clean(bulletin.Title)}\"");
                BulletinCompleted?.Invoke(bulletin);
                break;
            case FrameOutcome.CompletedDirectory when result.Directory is { } directory:
                Interlocked.Increment(ref _framesStored);
                _log($"directory for {directory.Date:yyyy-MM-dd}: {directory.Entries.Count} bulletins in rotation");
                SlotTimetable? changed = null;
                lock (_gate)
                {
                    if (_store.HeardSchedule is { } heard && heard != _heardSchedule)
                    {
                        _heardSchedule = heard;
                        changed = heard;
                    }
                }
                if (changed is not null)
                {
                    ScheduleHeard?.Invoke(changed);
                }
                break;
            case FrameOutcome.CompletedUnhandled when result.ContentType is { } type:
                // Once per object: it is marked done, so its later frames are not rebuilt again.
                _log($"object {ObjectId.Format(result.ObjectId ?? 0)} is a {ContentType.Describe(type)}, which this receiver does not handle; kept out of the BBS");
                break;
            case FrameOutcome.UnknownDictionary:
                _log("store: a frame uses a compression dictionary this receiver does not have; a newer receiver may be needed");
                break;
            case FrameOutcome.Rejected:
                _log($"store: a frame or a rebuilt object failed its check: {Ascii.Clean(result.Detail)}");
                break;
            default:
                break;
        }
    }
}
