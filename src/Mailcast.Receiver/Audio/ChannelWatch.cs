using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

namespace Mailcast.Receiver;

/// <summary>
/// Measures the radio path from GB7RDG after each slot, from the bursts the receiver decoded,
/// and keeps a day of the results for the status page's Channel tile.
/// </summary>
/// <remarks>
/// <para>Bursts arrive from the audio thread (<see cref="Offer"/>) with the slot they belong to,
/// and are only kept: nothing is measured while the slot is on. A slot is measured once it is
/// over, which is when a burst for another slot arrives, when nothing more has come for
/// <see cref="Quiet"/>, or when the audio stops (<see cref="SlotOver"/>). The measurement runs
/// on a thread of its own, one burst at a time, resting as long again after each so it never
/// takes more than half of one core.</para>
/// <para>Memory is bounded: at most <see cref="MostBursts"/> bursts and
/// <see cref="MostSeconds"/> seconds of audio are kept for a slot, as half-precision floats
/// (about 100 kB a second).</para>
/// <para>A sounding probe, later, will hand its own <see cref="PathPicture"/>s in to the same
/// summary, so the tile and the API stay the same with <c>basis: "probe"</c>.</para>
/// </remarks>
public sealed class ChannelWatch : IAsyncDisposable
{
    /// <summary>No burst for this long and the slot is taken as over.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);

    /// <summary>How long the results are kept and shown.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromHours(24);

    /// <summary>The most bursts kept for one slot.</summary>
    public const int MostBursts = 12;

    /// <summary>The most seconds of audio kept for one slot.</summary>
    public const double MostSeconds = 240;

    /// <summary>The file in the state directory the results are kept in.</summary>
    public const string FileName = "channel.json";

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly string? _path;
    private readonly Func<GroundPlace?> _place;
    private readonly Channel<object> _inbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private List<ChannelReport> _history = [];
    private int _waiting;
    private int _measuring;
    private TaskCompletionSource _idle = NewIdle();

    /// <summary>
    /// Starts the watch. <paramref name="statePath"/> is where the day's results are kept (null
    /// keeps them in memory only); <paramref name="place"/> says where the receiver is, for
    /// naming the hops.
    /// </summary>
    public ChannelWatch(TimeProvider time, Action<string> log, string? statePath, Func<GroundPlace?> place)
    {
        _time = time;
        _log = log;
        _path = statePath;
        _place = place;
        Load();
        _loop = RunAsync(_stop.Token);
    }

    /// <summary>Rest after each burst as long as it took, to stay under half a core. Tests turn it off.</summary>
    internal bool Rest { get; init; } = true;

    /// <summary>For tests: raised each time the loop has set its quiet timer on the clock.</summary>
    internal event Action? QuietWaiting;

    /// <summary>Raised on the worker after each slot is measured.</summary>
    public event Action<ChannelReport>? Measured;

    /// <summary>Bursts kept and not yet measured.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Whether a slot is being measured now.</summary>
    public bool Measuring => Volatile.Read(ref _measuring) != 0;

    /// <summary>The newest slot measured, if any in the last <see cref="Kept"/>.</summary>
    public ChannelReport? Latest => History is { Count: > 0 } h ? h[^1] : null;

    /// <summary>The slots measured in the last <see cref="Kept"/>, oldest first.</summary>
    public IReadOnlyList<ChannelReport> History
    {
        get
        {
            var since = _time.GetUtcNow() - Kept;
            lock (_gate)
            {
                return [.. _history.Where(r => r.Slot >= since)];
            }
        }
    }

    /// <summary>A decoded burst, from the audio thread, and the slot it was heard in.</summary>
    public void Offer(CapturedBurst burst, DateTimeOffset slot)
    {
        Interlocked.Increment(ref _waiting);
        _inbox.Writer.TryWrite(new Offered(burst, slot));
    }

    /// <summary>The audio has stopped: measure what is kept now.</summary>
    public void SlotOver() => _inbox.Writer.TryWrite(Flush.Instance);

    /// <summary>Completes once everything offered so far has been measured.</summary>
    public Task IdleAsync()
    {
        lock (_gate)
        {
            return _waiting == 0 && _measuring == 0 ? Task.CompletedTask : _idle.Task;
        }
    }

    private static TaskCompletionSource NewIdle() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<CapturedBurst> _batch = [];
    private DateTimeOffset? _batchSlot;
    private double _batchSeconds;
    private int _batchOffered;

    private async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            Task<bool>? ready = null;
            while (!cancellation.IsCancellationRequested)
            {
                // One wait on the inbox at a time, kept across a quiet timer that fires first.
                ready ??= _inbox.Reader.WaitToReadAsync(cancellation).AsTask();
                if (_batchSlot is not null)
                {
                    using var quietStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    var quiet = Task.Delay(Quiet, _time, quietStop.Token);
                    QuietWaiting?.Invoke();
                    if (await Task.WhenAny(ready, quiet).ConfigureAwait(false) == quiet)
                    {
                        await MeasureBatchAsync().ConfigureAwait(false);
                        continue;
                    }
                    await quietStop.CancelAsync().ConfigureAwait(false);
                }
                if (!await ready.ConfigureAwait(false))
                {
                    return;
                }
                ready = null;
                while (_inbox.Reader.TryRead(out var message))
                {
                    if (message is Offered offered)
                    {
                        if (_batchSlot is { } slot && slot != offered.Slot)
                        {
                            await MeasureBatchAsync().ConfigureAwait(false);
                        }
                        Keep(offered);
                    }
                    else
                    {
                        await MeasureBatchAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Keep(Offered offered)
    {
        _batchSlot = offered.Slot;
        _batchOffered++;
        double seconds = offered.Burst.Audio.Length / (double)OnAir.SampleRate;
        if (_batch.Count < MostBursts && _batchSeconds + seconds <= MostSeconds)
        {
            _batch.Add(offered.Burst);
            _batchSeconds += seconds;
        }
        else
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private async Task MeasureBatchAsync()
    {
        if (_batchSlot is not { } slot)
        {
            SignalIdle();
            return;
        }
        var bursts = _batch.ToArray();
        int offered = _batchOffered;
        _batch.Clear();
        _batchSlot = null;
        _batchSeconds = 0;
        _batchOffered = 0;
        Volatile.Write(ref _measuring, 1);
        try
        {
            // A thread of its own: the measurement is a few seconds of arithmetic.
            var report = await Task.Factory.StartNew(() => Measure(slot, bursts, offered), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            Remember(report);
            _log(string.Create(CultureInfo.InvariantCulture,
                $"channel: the {Hhmm(report.Slot)} slot, from {report.Measurements} of {report.Kept} bursts, measured in {report.ComputeSeconds:F1} s: {report.Words}"));
            Measured?.Invoke(report);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Anything at all: the measurement is a nicety and must never stop the receiver.
            _log($"channel: could not measure the {Hhmm(slot)} slot: {Ascii.Clean(e.Message)}");
        }
        finally
        {
            Interlocked.Add(ref _waiting, -bursts.Length);
            Volatile.Write(ref _measuring, 0);
            SignalIdle();
        }
    }

    private void SignalIdle()
    {
        lock (_gate)
        {
            if (_waiting == 0 && _batch.Count == 0)
            {
                var done = _idle;
                _idle = NewIdle();
                done.TrySetResult();
            }
        }
    }

    private static string Hhmm(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Measures a slot's bursts, one at a time, and sums them up. On the worker thread.</summary>
    private ChannelReport Measure(DateTimeOffset slot, CapturedBurst[] bursts, int offered)
    {
        // A slot measured before that went on (a quiet spell inside it): its pictures so far
        // are added to, not replaced.
        var pictures = _earlierSlot == slot ? new List<PathPicture>(_earlier) : [];
        int kept = (_earlierSlot == slot ? _earlierKept : 0) + offered;
        double busy = 0;
        foreach (var burst in bursts)
        {
            var one = Stopwatch.StartNew();
            var series = BurstChannel.Measure(burst.Samples(), burst.EndSample, burst.Lock, burst.PayloadBits);
            if (series is not null && ChannelAnalysis.Analyse(series) is { } picture)
            {
                pictures.Add(picture);
            }
            one.Stop();
            busy += one.Elapsed.TotalSeconds;
            if (Rest)
            {
                Thread.Sleep(one.Elapsed);
            }
        }
        _earlierSlot = slot;
        _earlier = pictures;
        _earlierKept = kept;
        var report = ChannelReport.Summarise(slot, _time.GetUtcNow(), pictures, kept, _place());
        return report with { ComputeSeconds = Math.Round(busy, 2) };
    }

    private DateTimeOffset? _earlierSlot;
    private List<PathPicture> _earlier = [];
    private int _earlierKept;

    private void Remember(ChannelReport report)
    {
        List<ChannelReport> keep;
        lock (_gate)
        {
            var since = _time.GetUtcNow() - Kept;
            _history = [.. _history.Where(r => r.Slot >= since && r.Slot != report.Slot).Append(report).OrderBy(r => r.Slot)];
            keep = _history;
        }
        Save(keep);
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path), ReceiverConfig.Json);
            if (saved?.Slots is { } slots)
            {
                _history = [.. slots.Where(r => r is not null).OrderBy(r => r.Slot)];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"channel: could not read {_path}, starting afresh: {Ascii.Clean(e.Message)}");
        }
    }

    private void Save(List<ChannelReport> history)
    {
        if (_path is null)
        {
            return;
        }
        try
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Saved(history), ReceiverConfig.Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"channel: could not write {_path}: {Ascii.Clean(e.Message)}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _inbox.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private sealed record Offered(CapturedBurst Burst, DateTimeOffset Slot);

    private sealed class Flush
    {
        public static readonly Flush Instance = new();
    }

    /// <summary>The state file.</summary>
    private sealed record Saved(List<ChannelReport> Slots);
}
