using System.Globalization;

namespace Mailcast.Receiver;

/// <summary>One slot as this receiver heard it.</summary>
/// <param name="Started">When the slot began: the start of its tone, or its first frame if no tone was heard.</param>
/// <param name="Tone">The tone's measurement, if it was heard.</param>
/// <param name="FramesHeard">Frames decoded in the slot.</param>
/// <param name="LastFrame">When the newest of them arrived.</param>
/// <param name="Scheduled">The scheduled start of the slot, when the receiver knows the schedule (not for a recording).</param>
public sealed record SlotSummary(DateTimeOffset Started, ToneReport? Tone, int FramesHeard, DateTimeOffset? LastFrame, DateTimeOffset? Scheduled = null);

/// <summary>
/// Groups what the receiver hears into GB7RDG's slots and keeps the latest.
/// </summary>
/// <remarks>
/// <para>With a <see cref="Schedule"/>, each frame belongs to the slot whose start most recently
/// passed (allowing <see cref="ClockAllowance"/> for a receiver clock that is a little slow), so
/// a long slot is not split and a late one is not merged with the one before. A tone is only
/// taken as a slot's if it began within <see cref="ToneWindow"/> of a slot's start: anything
/// else near 1800 Hz is someone tuning up, not GB7RDG.</para>
/// <para>Without one (a recording, whose time of day is unknown) a slot starts with a tone, or
/// with the first frame after <see cref="Gap"/> of quiet.</para>
/// </remarks>
public sealed class SlotTracker
{
    /// <summary>Without a schedule: frames this long after the slot's last one start a new slot.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(10);

    /// <summary>How far from a slot's start a tone may begin and still be that slot's.</summary>
    public static readonly TimeSpan ToneWindow = TimeSpan.FromMinutes(5);

    /// <summary>How early, by this receiver's clock, a frame may arrive and still count for the coming slot.</summary>
    public static readonly TimeSpan ClockAllowance = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private SlotSummary? _current;
    private SlotSchedule? _schedule;

    /// <summary>Creates a tracker.</summary>
    public SlotTracker(TimeProvider time, Action<string> log, SlotSchedule? schedule = null)
    {
        _time = time;
        _log = log;
        _schedule = schedule;
    }

    /// <summary>GB7RDG's slots, or null when listening to a recording.</summary>
    public SlotSchedule? Schedule
    {
        get
        {
            lock (_gate)
            {
                return _schedule;
            }
        }
        set
        {
            lock (_gate)
            {
                _schedule = value;
            }
        }
    }

    /// <summary>The slot in progress or most recently heard, if any.</summary>
    public SlotSummary? Last
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>A tone has been measured: if it is at a slot's start, that slot begins with it.</summary>
    public void OnTone(ToneReport tone)
    {
        var began = _time.GetUtcNow() - tone.Duration;
        string measured = string.Create(CultureInfo.InvariantCulture,
            $"{tone.FrequencyHz:F1} Hz, {tone.OffsetHz:+0.0;-0.0;0.0} Hz from {OnAir.CentreAudioHz:F0} Hz, SNR {tone.SnrDb:F1} dB in 3 kHz, {tone.Duration.TotalSeconds:F0} s");
        lock (_gate)
        {
            DateTimeOffset? slot = null;
            if (_schedule is { } schedule)
            {
                var nearest = schedule.NearestStart(began);
                var off = (began - nearest).Duration();
                if (off > ToneWindow)
                {
                    _log(string.Create(CultureInfo.InvariantCulture,
                        $"tone: {measured}, but it began at {began.UtcDateTime:HH:mm:ss} UTC, {off.TotalMinutes:F0} min from any slot, so it is not GB7RDG's: ignored"));
                    return;
                }
                slot = nearest;
            }
            _current = _current is { Scheduled: not null } current && current.Scheduled == slot
                ? current with { Started = began, Tone = tone }
                : new SlotSummary(began, tone, 0, null, slot);
        }
        _log($"tone: {measured}");
    }

    /// <summary>A frame was heard.</summary>
    public void OnFrame()
    {
        var now = _time.GetUtcNow();
        int frames;
        lock (_gate)
        {
            if (_schedule is { } schedule)
            {
                var slot = schedule.LatestStart(now + ClockAllowance);
                if (_current?.Scheduled != slot)
                {
                    _current = new SlotSummary(now, null, 0, null, slot);
                }
            }
            else if (_current is null || now - (_current.LastFrame ?? _current.Started) > Gap)
            {
                _current = new SlotSummary(now, null, 0, null);
            }
            _current = _current with { FramesHeard = _current.FramesHeard + 1, LastFrame = now };
            frames = _current.FramesHeard;
        }
        if (frames == 1 || frames % 100 == 0)
        {
            _log($"slot: {frames} frame{(frames == 1 ? "" : "s")} heard");
        }
    }
}
