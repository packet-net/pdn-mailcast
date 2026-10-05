namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// A clock that only moves when nothing else can happen. With <see cref="Run{T}"/>, everything the
/// code under test awaits runs on one thread, and time jumps straight to the next timer whenever
/// that thread falls idle. Nothing waits on the wall clock, so nothing depends on how busy the
/// machine is.
/// </summary>
public sealed class VirtualTime : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<VirtualTimer> _timers = [];

    // One pump for the clock's whole life: work begun in one Run (a fake transmitter waiting for a
    // clear channel, say) carries on in the next.
    private readonly PumpContext _context = new();
    // Timers run on _now, the monotonic clock; GetUtcNow adds _wallStep, so the wall clock can be
    // stepped as NTP steps it without moving any timer.
    private DateTimeOffset _now;
    private TimeSpan _wallStep;
    private long _sequence;

    public VirtualTime(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now + _wallStep;
        }
    }

    private DateTimeOffset Monotonic
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>The monotonic clock, which a wall-clock step does not move.</summary>
    public override long GetTimestamp() => Monotonic.UtcTicks;

    /// <summary>Steps the wall clock, as a time sync after a boot with a stale clock does; timers keep their due times.</summary>
    public void StepWallClock(TimeSpan by)
    {
        lock (_gate)
        {
            _wallStep += by;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new VirtualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on without firing anything, for code that only reads it.</summary>
    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }

    /// <summary>Moves to the earliest pending timer and fires it. False if there is none.</summary>
    public bool FireNext()
    {
        VirtualTimer? next;
        lock (_gate)
        {
            next = _timers.Where(t => t.Due is not null).OrderBy(t => t.Due).ThenBy(t => t.Sequence).FirstOrDefault();
            if (next is null)
            {
                return false;
            }
            if (next.Due > _now)
            {
                _now = next.Due!.Value;
            }
            next.Due = next.Period is TimeSpan p && p > TimeSpan.Zero ? _now + p : null;
            next.Sequence = ++_sequence;
        }
        next.Fire();
        return true;
    }

    /// <summary>
    /// Runs <paramref name="body"/> to completion on a single-threaded context, firing timers
    /// whenever it is idle. Fails if virtual time passes <paramref name="limit"/> or everything
    /// stops with the task unfinished.
    /// </summary>
    public T Run<T>(Func<Task<T>> body, TimeSpan? limit = null)
    {
        var previous = SynchronizationContext.Current;
        var context = _context;
        SynchronizationContext.SetSynchronizationContext(context);
        DateTimeOffset start = Monotonic;
        try
        {
            Task<T> task = body();
            while (!task.IsCompleted)
            {
                if (context.RunPending())
                {
                    continue;
                }
                if (Monotonic - start > (limit ?? TimeSpan.FromHours(3)))
                {
                    throw new InvalidOperationException($"virtual time passed {limit} with the work unfinished");
                }
                if (!FireNext())
                {
                    // Work on another thread (a real socket, say) may still post back; give it a turn.
                    if (!context.WaitForWork(TimeSpan.FromSeconds(30)))
                    {
                        throw new InvalidOperationException("deadlock: nothing pending, no timers, task unfinished");
                    }
                }
            }
            context.RunPending();
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class VirtualTimer(VirtualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; set; }

        public long Sequence { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
                Sequence = ++owner._sequence;
            }
            return true;
        }

        public void Fire()
        {
            // Posted rather than called, so the callback runs on the pump like everything else.
            var context = SynchronizationContext.Current;
            if (context is not null)
            {
                context.Post(_ => callback(state), null);
            }
            else
            {
                callback(state);
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly object _gate = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate)
            {
                _queue.Enqueue((d, state));
                Monitor.PulseAll(_gate);
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public bool RunPending()
        {
            bool ran = false;
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        return ran;
                    }
                    item = _queue.Dequeue();
                }
                item.Callback(item.State);
                ran = true;
            }
        }

        public bool WaitForWork(TimeSpan limit)
        {
            lock (_gate)
            {
                return _queue.Count > 0 || (Monitor.Wait(_gate, limit) && _queue.Count > 0);
            }
        }
    }
}
