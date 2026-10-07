using Packet.SoundModem.Ms110d;

namespace Mailcast.Receiver;

/// <summary>A decoded burst and the audio it came in, kept for the channel measurement.</summary>
/// <param name="Audio">The 48 kHz audio around the burst, as half-precision floats to keep it small.</param>
/// <param name="EndSample">Where in <paramref name="Audio"/> the modem reported the burst over.</param>
/// <param name="Lock">The waveform the modem locked to, and its frequency offset.</param>
/// <param name="PayloadBits">What the burst decoded to.</param>
/// <param name="Heard">When the modem reported it over.</param>
/// <param name="FirstSample">Which sample of the audio since the pipeline started <paramref name="Audio"/> begins at.</param>
public sealed record CapturedBurst(Half[] Audio, int EndSample, Ms110dLockInfo Lock, byte[] PayloadBits, DateTimeOffset Heard, long FirstSample)
{
    /// <summary>The audio as floats, for the measurement.</summary>
    internal float[] Samples()
    {
        var x = new float[Audio.Length];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = (float)Audio[i];
        }
        return x;
    }
}

/// <summary>The audio after a slot's tone, kept for measuring the channel probe that follows it.</summary>
/// <param name="Audio">The 48 kHz audio, as half-precision floats: about 13 s, 1.25 MB.</param>
/// <param name="Tone">The tone it follows.</param>
/// <param name="ToneEndSeconds">Where the tone detector put the tone's end, seconds into <paramref name="Audio"/>.</param>
/// <param name="Heard">When it was kept.</param>
/// <param name="FirstSample">Which sample of the audio since the pipeline started <paramref name="Audio"/> begins at.</param>
public sealed record CapturedProbe(Half[] Audio, ToneReport Tone, double ToneEndSeconds, DateTimeOffset Heard, long FirstSample)
{
    /// <summary>Audio kept before where the tone detector put the tone's end, seconds.</summary>
    public const double BeforeSeconds = 1;

    /// <summary>Audio kept after it, seconds: the gap, the probe, and the search either side of where it should be.</summary>
    public const double AfterSeconds = 12;

    /// <summary>The audio as floats, for the measurement.</summary>
    internal float[] Samples()
    {
        var x = new float[Audio.Length];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = (float)Audio[i];
        }
        return x;
    }
}

/// <summary>
/// Keeps the last minute or so of audio, and hands each decoded burst on with its audio, for the
/// channel measurement. Runs on the audio thread and does nothing there but copy: the
/// measurement itself is <see cref="ChannelWatch"/>'s, on its own thread.
/// </summary>
/// <remarks>
/// The MS110D receiver says a burst is over (<see cref="Ms110dDemodulator.BurstCompleted"/>) a
/// little after its audio has ended, by which time it has dropped its lock; so the lock is
/// remembered from the last block it decoded. The audio is taken
/// <see cref="AfterSeconds"/> later still, so the whole burst is in it however early the modem
/// spoke, together with the burst's own length and <see cref="BeforeSeconds"/> before it.
/// </remarks>
public sealed class BurstCapture
{
    /// <summary>Audio kept, seconds: enough for a 60 s burst and its margins.</summary>
    public const int RingSeconds = 75;

    /// <summary>Audio taken before the burst could have begun, seconds: decoder delay and quiet before it.</summary>
    public const double BeforeSeconds = 6.5;

    /// <summary>Audio waited for after the modem said the burst was over, seconds.</summary>
    public const double AfterSeconds = 1.5;

    private const int Rate = OnAir.SampleRate;

    private readonly Half[] _ring = new Half[RingSeconds * Rate];
    private readonly TimeProvider _time;
    private readonly Action<CapturedBurst> _sink;
    private readonly List<Pending> _pending = [];
    private readonly List<(long From, long Until, Action<Half[], long> Sink)> _windows = [];
    private long _written;
    private int _frames;
    private Ms110dLockInfo? _lastLock;
    private Ms110dDemodulator? _receiver;

    /// <summary>Hands each burst to <paramref name="sink"/>, on the audio thread.</summary>
    public BurstCapture(TimeProvider time, Action<CapturedBurst> sink)
    {
        _time = time;
        _sink = sink;
    }

    /// <summary>Raised on the audio thread, with its length in seconds, for a burst too long for the audio kept, so not measured.</summary>
    public event Action<double>? TooLong;

    /// <summary>Follows <paramref name="receiver"/>'s bursts. On the audio thread, before any audio.</summary>
    public void Attach(Ms110dDemodulator receiver)
    {
        _receiver = receiver;
        receiver.BlockDecoded += _ => _lastLock = receiver.Lock ?? _lastLock;
        receiver.BurstCompleted += OnBurst;
    }

    /// <summary>A frame was delivered: the burst it came in is GB7RDG's, not noise or another station's.</summary>
    public void OnFrame() => _frames++;

    /// <summary>Each block of audio, before the modem hears it.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        foreach (float s in samples)
        {
            _ring[_written++ % _ring.Length] = (Half)s;
        }
    }

    /// <summary>The samples written so far, which is where the next block will start.</summary>
    public long Written => _written;

    /// <summary>
    /// Hands <paramref name="sink"/> the audio from sample <paramref name="from"/> to
    /// <paramref name="until"/> once it is all in, on the audio thread: for the channel probe after
    /// a slot's tone. Whatever has already left the ring is left out of the start.
    /// </summary>
    public void Keep(long from, long until, Action<Half[], long> sink) => _windows.Add((from, until, sink));

    /// <summary>The audio has ended (a recording played through): hands on what there is of any window still being filled.</summary>
    public void Flush()
    {
        foreach (var (from, _, sink) in _windows)
        {
            long start = Math.Max(Math.Max(from, 0), _written - _ring.Length);
            if (start < _written)
            {
                sink(Copy(start, _written), start);
            }
        }
        _windows.Clear();
    }

    /// <summary>After each block: hands on any burst whose audio is all in now.</summary>
    public void AfterBlock()
    {
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            var (from, until, sink) = _windows[i];
            if (_written < until)
            {
                continue;
            }
            _windows.RemoveAt(i);
            from = Math.Max(Math.Max(from, 0), _written - _ring.Length);
            sink(Copy(from, until), from);
        }
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (_written < p.Until)
            {
                continue;
            }
            _pending.RemoveAt(i);
            long from = p.From;
            if (from < _written - _ring.Length || from < 0)
            {
                TooLong?.Invoke((p.End - from) / (double)Rate);
                continue;
            }
            _sink(new CapturedBurst(Copy(from, _written), (int)(p.End - from), p.Lock, p.Bits, p.Heard, from));
        }
    }

    /// <summary>The ring's samples from <paramref name="from"/> up to <paramref name="until"/>, all still in it.</summary>
    private Half[] Copy(long from, long until)
    {
        // At most two runs of the ring: to its end, then from its start.
        var audio = new Half[until - from];
        int start = (int)(from % _ring.Length);
        int first = Math.Min(audio.Length, _ring.Length - start);
        Array.Copy(_ring, start, audio, 0, first);
        Array.Copy(_ring, 0, audio, first, audio.Length - first);
        return audio;
    }

    private void OnBurst(Ms110dBurst burst)
    {
        var locked = _receiver?.Lock ?? _lastLock;
        int frames = _frames;
        _lastLock = null;
        _frames = 0;
        // Only a burst a frame was read from: anything else is a lock on noise, or another
        // MS110D station, and copying its audio here would be wasted on the audio thread.
        if (frames == 0 || locked is not { WaveformNumber: >= 1 } || burst.Blocks == 0 || burst.PayloadBits.Length == 0)
        {
            return;
        }
        double seconds;
        try
        {
            seconds = new Ms110dModulator(new Ms110dTxSettings
            {
                WaveformNumber = locked.WaveformNumber,
                Interleaver = locked.Interleaver,
                ConstraintLength = locked.ConstraintLength,
                PreambleSuperframes = BurstReference.MostSuperframes,
            }).BurstSeconds(burst.PayloadBits.Length);
        }
        catch (ArgumentException)
        {
            return;
        }
        long end = _written;
        long from = end - (long)((seconds + BeforeSeconds) * Rate);
        _pending.Add(new Pending(from, end, end + (long)(AfterSeconds * Rate), locked, burst.PayloadBits, _time.GetUtcNow()));
    }

    private sealed record Pending(long From, long End, long Until, Ms110dLockInfo Lock, byte[] Bits, DateTimeOffset Heard);
}
