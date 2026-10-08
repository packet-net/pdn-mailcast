using System.Collections.Immutable;
using System.Globalization;

namespace Mailcast.Receiver;

/// <summary>One slot as this receiver heard it.</summary>
/// <param name="Started">When the slot began: the start of its tone, or its first frame if no tone was heard.</param>
/// <param name="Tone">The tone's measurement, if it was heard.</param>
/// <param name="FramesHeard">Frames decoded in the slot.</param>
/// <param name="LastFrame">When the newest of them arrived.</param>
/// <param name="Scheduled">The scheduled start of the slot, when the receiver knows the schedule (not for a recording).</param>
public sealed record SlotSummary(DateTimeOffset Started, ToneReport? Tone, int FramesHeard, DateTimeOffset? LastFrame, DateTimeOffset? Scheduled = null)
{
    /// <summary>Frames heard on each waveform, by the modem's autobaud (<c>ms110d-wn4</c>). Frames whose waveform it could not say are not in it.</summary>
    public ImmutableSortedDictionary<string, int> FrameCounts { get; init; } = ImmutableSortedDictionary<string, int>.Empty;

    /// <summary>The waveform GB7RDG's directory said this slot went out on, if a directory completed in it.</summary>
    public string? ListedWaveform { get; init; }

    /// <summary>
    /// Issue #49: whether a directory completed while this slot was tracked, so the receiver
    /// knows what is in that day's rotation. Set even when the directory does not say its
    /// waveform (so <see cref="ListedWaveform"/> stays null).
    /// </summary>
    public bool DirectoryHeard { get; init; }

    /// <summary>
    /// Issue #49: whether the probe audio after this slot's tone (used for the channel
    /// measurement when no burst decodes) has been captured.
    /// </summary>
    public bool ProbeCaptured { get; init; }

    /// <summary>Issue #49: whether this slot's ionosonde reading has been received.</summary>
    public bool IonosphereHeard { get; init; }

    /// <summary>Issue #49: whether this slot's PSK Reporter reading has been received.</summary>
    public bool PskReporterHeard { get; init; }

    /// <summary>The waveform most of the slot's frames came on, <c>mixed</c> if two or more tie for most, or null if none is known.</summary>
    public string? Waveform
    {
        get
        {
            if (FrameCounts.IsEmpty)
            {
                return null;
            }
            int most = FrameCounts.Values.Max();
            var top = FrameCounts.Where(c => c.Value == most).Select(c => c.Key).ToList();
            return top.Count == 1 ? top[0] : Mixed;
        }
    }

    /// <summary>What <see cref="Waveform"/> says when no one waveform has the most frames.</summary>
    public const string Mixed = "mixed";
}

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

    /// <summary>A frame was heard, on <paramref name="waveform"/> by the modem's autobaud (null if it cannot say).</summary>
    public void OnFrame(string? waveform = null)
    {
        var now = _time.GetUtcNow();
        int frames;
        bool newWaveform = false;
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
            if (waveform is not null)
            {
                newWaveform = !_current.FrameCounts.TryGetValue(waveform, out int had);
                _current = _current with { FrameCounts = _current.FrameCounts.SetItem(waveform, had + 1) };
            }
            frames = _current.FramesHeard;
        }
        string on = waveform is null ? "" : $" on {Mailcast.Receiver.Waveform.Words(waveform)}";
        if (frames == 1)
        {
            _log($"slot: 1 frame heard{on}");
        }
        else if (newWaveform)
        {
            _log($"slot: frame {frames} heard{on}, a different waveform from the frames before it");
        }
        else if (frames % 100 == 0)
        {
            _log($"slot: {frames} frames heard");
        }
    }

    /// <summary>
    /// A directory was rebuilt, naming the waveform the slot went out on (<paramref name="mode"/>,
    /// null from a head end that does not say): kept beside the slot's frames as a cross-check.
    /// </summary>
    public void OnDirectory(string? mode)
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                _current = _current with { DirectoryHeard = true, ListedWaveform = mode ?? _current.ListedWaveform };
            }
        }
    }

    /// <summary>Issue #49: the probe audio after the slot's tone has been captured.</summary>
    public void OnProbeCaptured()
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                _current = _current with { ProbeCaptured = true };
            }
        }
    }

    /// <summary>Issue #49: this slot's ionosonde reading was received.</summary>
    public void OnIonosphereHeard()
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                _current = _current with { IonosphereHeard = true };
            }
        }
    }

    /// <summary>Issue #49: this slot's PSK Reporter reading was received.</summary>
    public void OnPskReporterHeard()
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                _current = _current with { PskReporterHeard = true };
            }
        }
    }
}
