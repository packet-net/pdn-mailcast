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
    private long _written;
    private Ms110dLockInfo? _lastLock;
    private Ms110dDemodulator? _receiver;

    /// <summary>Hands each burst to <paramref name="sink"/>, on the audio thread.</summary>
    public BurstCapture(TimeProvider time, Action<CapturedBurst> sink)
    {
        _time = time;
        _sink = sink;
    }

    /// <summary>Bursts too long for the audio kept, so not measured.</summary>
    public int TooLong { get; private set; }

    /// <summary>Follows <paramref name="receiver"/>'s bursts. On the audio thread, before any audio.</summary>
    public void Attach(Ms110dDemodulator receiver)
    {
        _receiver = receiver;
        receiver.BlockDecoded += _ => _lastLock = receiver.Lock ?? _lastLock;
        receiver.BurstCompleted += OnBurst;
    }

    /// <summary>Each block of audio, before the modem hears it.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        foreach (float s in samples)
        {
            _ring[_written++ % _ring.Length] = (Half)s;
        }
    }

    /// <summary>After each block: hands on any burst whose audio is all in now.</summary>
    public void AfterBlock()
    {
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
                TooLong++;
                continue;
            }
            var audio = new Half[_written - from];
            for (long k = from; k < _written; k++)
            {
                audio[k - from] = _ring[k % _ring.Length];
            }
            _sink(new CapturedBurst(audio, (int)(p.End - from), p.Lock, p.Bits, p.Heard, from));
        }
    }

    private void OnBurst(Ms110dBurst burst)
    {
        var locked = _receiver?.Lock ?? _lastLock;
        _lastLock = null;
        if (locked is not { WaveformNumber: >= 1 } || burst.Blocks == 0 || burst.PayloadBits.Length == 0)
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
