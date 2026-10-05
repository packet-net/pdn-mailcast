namespace Mailcast.Receiver;

/// <summary>One daily slot as this receiver heard it.</summary>
/// <param name="Started">When the slot began: the start of its tone, or its first frame if no tone was heard.</param>
/// <param name="Tone">The tone's measurement, if it was heard.</param>
/// <param name="FramesHeard">Broadcast frames decoded in the slot.</param>
/// <param name="LastFrame">When the newest of them arrived.</param>
public sealed record SlotSummary(DateTimeOffset Started, ToneReport? Tone, int FramesHeard, DateTimeOffset? LastFrame);

/// <summary>
/// Groups what the receiver hears into slots: a slot starts with its tone, or with the first frame
/// after a long silence, and counts the frames heard until the next.
/// </summary>
public sealed class SlotTracker
{
    /// <summary>Frames this long after the slot's last one start a new slot.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private SlotSummary? _current;

    /// <summary>Creates a tracker.</summary>
    public SlotTracker(TimeProvider time, Action<string> log)
    {
        _time = time;
        _log = log;
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

    /// <summary>A slot's tone has been measured: a new slot starts.</summary>
    public void OnTone(ToneReport tone)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _current = new SlotSummary(now - tone.Duration, tone, 0, null);
        }
        _log($"tone: {tone.FrequencyHz:F1} Hz, {tone.OffsetHz:+0.0;-0.0;0.0} Hz from {OnAir.CentreAudioHz:F0} Hz, "
            + $"SNR {tone.SnrDb:F1} dB in 3 kHz, {tone.Duration.TotalSeconds:F0} s");
    }

    /// <summary>A broadcast frame was heard.</summary>
    public void OnFrame()
    {
        var now = _time.GetUtcNow();
        int frames;
        lock (_gate)
        {
            if (_current is null || now - (_current.LastFrame ?? _current.Started) > Gap)
            {
                _current = new SlotSummary(now, null, 0, null);
            }
            _current = _current with { FramesHeard = _current.FramesHeard + 1, LastFrame = now };
            frames = _current.FramesHeard;
        }
        if (frames == 1 || frames % 100 == 0)
        {
            _log($"slot: {frames} broadcast frame{(frames == 1 ? "" : "s")} heard");
        }
    }
}
