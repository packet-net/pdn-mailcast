using M0LTE.Dsp;

namespace Mailcast.Receiver;

/// <summary>What the receiver measured of one slot's opening tone.</summary>
/// <param name="FrequencyHz">The tone's audio frequency.</param>
/// <param name="OffsetHz">How far that is from where it should be (<see cref="OnAir.CentreAudioHz"/>); on USB, positive means the signal is high.</param>
/// <param name="SnrDb">Tone power over the noise in 3 kHz, the bandwidth MS110D's figures are quoted in.</param>
/// <param name="Duration">How long the tone lasted.</param>
public sealed record ToneReport(double FrequencyHz, double OffsetHz, double SnrDb, TimeSpan Duration);

/// <summary>
/// Finds the steady tone that opens each slot and measures its frequency and signal-to-noise
/// ratio.
/// </summary>
/// <remarks>
/// <para>The audio is brought down to 8 kHz and cut into blocks of 8192 samples (just over a
/// second, so the FFT's bins are just under 1 Hz apart). In each block the strongest bin near the
/// expected frequency is a tone if it stands well clear of the noise, which is the median of the
/// bins across the passband. A run of such blocks within a hertz or two of each other is the tone;
/// when the run ends, its frequency (refined between bins, then averaged) and its SNR are
/// reported.</para>
/// <para>The CW ident follows the tone on the same frequency, often within a second, and a block
/// of it has a strong line at 1800 Hz too. What tells them apart is the keying: a steady carrier
/// keeps nearly all its power in the few bins of its line, while keyed CW puts half or more into
/// sidebands either side (a dit at 20 wpm repeats about 8 times a second). So a block whose line
/// holds less than <see cref="SteadyShare"/> of the power within 25 Hz of it, noise taken away,
/// is keyed, and ends the run like silence does. Block power is no help here: the ident may be
/// sent louder than the tone, and a fading tone changes power by more than keying does.</para>
/// <para>A run is reported only if it lasted between <see cref="MinDuration"/> and
/// <see cref="MaxDuration"/>: the slot's tone is 10 seconds, and a carrier that goes on much
/// longer (a birdie, or a station tuning up) is not it. The blocks are just over a second, and a
/// block the tone only partly fills may or may not count, so a 10-second tone is measured as
/// between about 8 and 11.3 seconds.</para>
/// <para>Time here is counted in samples, so the result does not depend on how fast the audio
/// arrives. Not thread-safe: feed it from one thread.</para>
/// </remarks>
public sealed class ToneDetector
{
    /// <summary>The shortest run reported.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(7);

    /// <summary>The longest run reported.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The least share of the power near the line that the line itself holds in a block of
    /// steady carrier; keyed CW holds 80% or less, and a steady tone 95% or more down to about -12 dB SNR.
    /// </summary>
    public const double SteadyShare = 0.87;

    private const int Rate = 8000;
    private const int Size = 8192;
    private const double BinHz = (double)Rate / Size;
    private const double SearchHz = 100;
    private const double NoiseLowHz = 300;
    private const double NoiseHighHz = 3300;
    private const double NoiseBandwidthHz = 3000;
    private const double DetectDb = 15;
    private const double StableHz = 2;
    private const int LobeBins = 3;
    private const int ExcludeBins = 20;
    private const int SidebandBins = 26;

    private readonly double _expectedHz;
    private readonly Decimator _decimator;
    private readonly float[] _decimated;
    private readonly float[] _block = new float[Size];
    private readonly float[] _re = new float[Size];
    private readonly float[] _im = new float[Size];
    private readonly float[] _window = new float[Size];
    private readonly double[] _power = new double[Size / 2];
    private readonly double[] _noise;
    private int _filled;

    // The run in progress.
    private int _runBlocks;
    private double _runFrequencySum;
    private double _runSignalSum;
    private double _runNoiseSum;

    /// <summary>Creates a detector for audio at <paramref name="sampleRate"/>, a multiple of 8000.</summary>
    public ToneDetector(int sampleRate = OnAir.SampleRate, double expectedHz = OnAir.CentreAudioHz)
    {
        if (sampleRate % Rate != 0)
        {
            throw new ArgumentException($"The sample rate must be a multiple of {Rate}.", nameof(sampleRate));
        }
        _expectedHz = expectedHz;
        _decimator = new Decimator(sampleRate, sampleRate / Rate);
        _decimated = new float[_decimator.MaxOutput(sampleRate)];
        for (int i = 0; i < Size; i++)
        {
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / Size)));
        }
        _noise = new double[(int)((NoiseHighHz - NoiseLowHz) / BinHz) + 1];
    }

    /// <summary>Raised when a tone has ended, with what was measured.</summary>
    public event Action<ToneReport>? ToneMeasured;

    /// <summary>The tone's frequency while one is being heard, for the spectrogram's marker.</summary>
    public double? LiveFrequencyHz { get; private set; }

    /// <summary>Feeds audio.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        for (int offset = 0; offset < samples.Length;)
        {
            int chunk = Math.Min(samples.Length - offset, OnAir.SampleRate / 2);
            int produced = _decimator.Process(samples.Slice(offset, chunk), _decimated);
            offset += chunk;
            for (int i = 0; i < produced; i++)
            {
                _block[_filled++] = _decimated[i];
                if (_filled == Size)
                {
                    _filled = 0;
                    Analyse();
                }
            }
        }
    }

    private void Analyse()
    {
        for (int i = 0; i < Size; i++)
        {
            _re[i] = _block[i] * _window[i];
            _im[i] = 0;
        }
        Fft.Forward(_re, _im);
        for (int k = 0; k < _power.Length; k++)
        {
            _power[k] = ((double)_re[k] * _re[k]) + ((double)_im[k] * _im[k]);
        }

        int low = Bin(_expectedHz - SearchHz);
        int high = Bin(_expectedHz + SearchHz);
        int peak = low;
        for (int k = low + 1; k <= high; k++)
        {
            if (_power[k] > _power[peak])
            {
                peak = k;
            }
        }

        // Noise per bin: the median across the passband away from the peak, scaled to a mean
        // (noise power in a bin is exponentially distributed, whose median is ln 2 of its mean).
        int n = 0;
        for (int k = Bin(NoiseLowHz); k <= Bin(NoiseHighHz) && n < _noise.Length; k++)
        {
            if (Math.Abs(k - peak) > ExcludeBins)
            {
                _noise[n++] = _power[k];
            }
        }
        Array.Sort(_noise, 0, n);
        double noisePerBin = Math.Max(_noise[n / 2] / Math.Log(2), double.Epsilon);

        double signal = 0;
        for (int k = peak - LobeBins; k <= peak + LobeBins; k++)
        {
            signal += _power[k] - noisePerBin;
        }

        bool tone = signal > noisePerBin * Math.Pow(10, DetectDb / 10) && Steady(peak, signal, noisePerBin);
        double frequency = Refine(peak);
        if (tone && (_runBlocks == 0 || Math.Abs(frequency - (_runFrequencySum / _runBlocks)) <= StableHz))
        {
            _runBlocks++;
            _runFrequencySum += frequency;
            _runSignalSum += signal;
            _runNoiseSum += noisePerBin;
            LiveFrequencyHz = _runFrequencySum / _runBlocks;
            return;
        }

        EndRun();
        if (tone)
        {
            // A tone, but not the one we were following: start a new run with it.
            _runBlocks = 1;
            _runFrequencySum = frequency;
            _runSignalSum = signal;
            _runNoiseSum = noisePerBin;
            LiveFrequencyHz = frequency;
        }
    }

    /// <summary>Ends the run in progress, reporting it if it was the right length.</summary>
    private void EndRun()
    {
        if (_runBlocks > 0)
        {
            var duration = TimeSpan.FromSeconds(_runBlocks * Size / (double)Rate);
            if (duration >= MinDuration && duration <= MaxDuration)
            {
                double frequency = _runFrequencySum / _runBlocks;
                double noiseIn3k = _runNoiseSum / _runBlocks * (NoiseBandwidthHz / BinHz);
                double snr = 10 * Math.Log10(Math.Max(_runSignalSum / _runBlocks, double.Epsilon) / noiseIn3k);
                ToneMeasured?.Invoke(new ToneReport(frequency, frequency - _expectedHz, snr, duration));
            }
        }
        _runBlocks = 0;
        _runFrequencySum = _runSignalSum = _runNoiseSum = 0;
        LiveFrequencyHz = null;
    }

    /// <summary>
    /// Whether the line at <paramref name="peak"/> is a steady carrier rather than keyed: whether
    /// it holds at least <see cref="SteadyShare"/> of the power within 25 Hz of it, noise taken away.
    /// </summary>
    private bool Steady(int peak, double line, double noisePerBin)
    {
        double near = 0;
        for (int k = peak - SidebandBins; k <= peak + SidebandBins; k++)
        {
            near += _power[k] - noisePerBin;
        }
        return near <= 0 || line >= SteadyShare * near;
    }

    /// <summary>The peak's frequency between bins: a parabola through the log powers either side.</summary>
    private double Refine(int peak)
    {
        double a = Math.Log(Math.Max(_power[peak - 1], double.Epsilon));
        double b = Math.Log(Math.Max(_power[peak], double.Epsilon));
        double c = Math.Log(Math.Max(_power[peak + 1], double.Epsilon));
        double denominator = a - (2 * b) + c;
        double shift = denominator == 0 ? 0 : 0.5 * (a - c) / denominator;
        return (peak + Math.Clamp(shift, -0.5, 0.5)) * BinHz;
    }

    private static int Bin(double hz) => (int)Math.Round(hz / BinHz);
}
