using System.Threading.Channels;
using Mailcast.Core;

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
    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<string> _log;
    private readonly Task _worker;
    private long _framesHeard;
    private long _framesStored;

    /// <summary>Opens the store under <paramref name="stateDirectory"/>/store.</summary>
    public Intake(string stateDirectory, Action<string> log)
    {
        _log = log;
        _store = new ReceiverStore(Path.Combine(stateDirectory, "store"), Compression.Default);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>A bulletin has been rebuilt and is waiting in the outbox.</summary>
    public event Action<Bulletin>? BulletinCompleted;

    /// <summary>A broadcast frame was heard (raised on the worker).</summary>
    public event Action? FrameHeard;

    /// <summary>Broadcast frames heard since start.</summary>
    public long FramesHeard => Interlocked.Read(ref _framesHeard);

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
        _queue.Writer.TryWrite(new Item(payload, null));
        return true;
    }

    /// <summary>Waits until every frame offered so far has been through the store.</summary>
    public async Task DrainAsync(CancellationToken cancellation)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(new Item(default, done));
        await done.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>Rebuilt bulletins not yet handed to the BBS, oldest first.</summary>
    public IReadOnlyList<Bulletin> Pending()
    {
        lock (_gate)
        {
            return _store.Pending();
        }
    }

    /// <summary>Takes a bulletin out of the outbox once the BBS has answered for it.</summary>
    public void Acknowledge(string bid)
    {
        lock (_gate)
        {
            _store.Acknowledge(bid);
        }
    }

    /// <summary>The newest directory heard, and how far each of its bulletins has got.</summary>
    public (BroadcastDirectory? Directory, IReadOnlyList<ObjectProgress> Progress) Progress()
    {
        lock (_gate)
        {
            return (_store.Directory, _store.Progress());
        }
    }

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
        await foreach (var (payload, done) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (done is not null)
            {
                done.TrySetResult();
                continue;
            }

            AcceptResult result;
            try
            {
                lock (_gate)
                {
                    result = _store.Accept(payload.Span);
                }
            }
            catch (IOException e)
            {
                _log($"store: cannot keep a piece: {Ascii.Clean(e.Message)}");
                continue;
            }

            FrameHeard?.Invoke();
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

    /// <summary>A frame's payload, or with <paramref name="Done"/> set, a point in the queue for <see cref="DrainAsync"/>.</summary>
    private readonly record struct Item(ReadOnlyMemory<byte> Payload, TaskCompletionSource? Done);
}
