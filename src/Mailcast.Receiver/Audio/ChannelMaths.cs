using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using M0LTE.Dsp;

namespace Mailcast.Receiver;

/// <summary>
/// The signal processing under the channel measurement: an FFT, the receive front end (48 kHz
/// USB audio to complex baseband at 9600 Hz on the 1800 Hz carrier), the MS110D pulse and its
/// matched filter, and the small linear algebra the estimates need.
/// </summary>
/// <remarks>
/// A port of dsp.py and chanest.py in the passive analysis (mailcast-test/probe-step1/passive),
/// kept numerically the same so the two can be checked against each other. Double precision
/// throughout except the front end, which is single precision with vector dot products: it is
/// the costliest step and float is far finer than the noise it filters.
/// </remarks>
internal static class ChannelMaths
{
    /// <summary>The analysis rate: 4 samples a symbol at 2400 Bd.</summary>
    public const int Rate = 9600;

    /// <summary>MS110D's symbol rate.</summary>
    public const int Baud = 2400;

    /// <summary>Samples a symbol at <see cref="Rate"/>.</summary>
    public const int Sps = Rate / Baud;

    /// <summary>The audio rate the front end takes, and the decimation to <see cref="Rate"/>.</summary>
    public const int AudioRate = OnAir.SampleRate;

    private const int Decimation = AudioRate / Rate;

    /// <summary>The front end's low-pass: 2100 Hz cut, 401 taps, Blackman, as dsp.py's.</summary>
    private const int FrontTaps = 401;

    private static readonly float[] FrontFilter = LowPass(2100, AudioRate, FrontTaps);

    /// <summary>MS110D's transmit pulse at 4 samples a symbol, unit energy: 65 taps of SRRC 0.35.</summary>
    public static readonly double[] Pulse = DesignPulse(0.35, 16);

    /// <summary>Index of the pulse's centre: a shaped burst's symbol 0 peaks here.</summary>
    public static int PulsePeak => (Pulse.Length - 1) / 2;

    /// <summary>A windowed-sinc low-pass with unity DC gain (Blackman), as numpy's np.blackman.</summary>
    internal static float[] LowPass(double cutHz, double rateHz, int taps)
    {
        var h = new double[taps];
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double n = i - ((taps - 1) / 2.0);
            double w = 0.42 - (0.5 * Math.Cos(2 * Math.PI * i / (taps - 1))) + (0.08 * Math.Cos(4 * Math.PI * i / (taps - 1)));
            h[i] = 2 * cutHz / rateHz * Sinc(2 * cutHz / rateHz * n) * w;
            sum += h[i];
        }
        var f = new float[taps];
        for (int i = 0; i < taps; i++)
        {
            f[i] = (float)(h[i] / sum);
        }
        return f;
    }

    private static double[] DesignPulse(double rollOff, int spanSymbols)
    {
        int taps = (spanSymbols * Sps) + 1;
        var p = new double[taps];
        double energy = 0;
        for (int i = 0; i < taps; i++)
        {
            p[i] = FilterDesign.RootRaisedCosine((i - ((taps - 1) / 2.0)) / Sps, rollOff);
            energy += p[i] * p[i];
        }
        double norm = 1 / Math.Sqrt(energy);
        for (int i = 0; i < taps; i++)
        {
            p[i] *= norm;
        }
        return p;
    }

    /// <summary>sin(pi x)/(pi x), as numpy's.</summary>
    public static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    /// <summary>
    /// The raised cosine (the SRRC pulse through its matched filter) at <paramref name="seconds"/>
    /// from its peak, for symbols at <paramref name="baud"/> with roll-off <paramref name="rollOff"/>.
    /// This is what one propagation path looks like in a channel estimate.
    /// </summary>
    public static double RaisedCosine(double seconds, double baud = Baud, double rollOff = 0.35)
    {
        double x = seconds * baud;
        double den = 1 - ((2 * rollOff * x) * (2 * rollOff * x));
        if (Math.Abs(den) < 1e-8)
        {
            return Math.PI / 4 * Sinc(1 / (2 * rollOff));
        }
        return Sinc(x) * Math.Cos(Math.PI * rollOff * x) / den;
    }

    /// <summary>
    /// 48 kHz USB audio to complex baseband at 9600 Hz centred on <paramref name="centreHz"/>:
    /// mixed down, low-passed (delay compensated) and decimated by 5, as dsp.py's audio48_to_bb
    /// (which assumed the signal's usual 1800 Hz centre; a sound card behind a narrow rig filter
    /// can be on a dial that puts it somewhere else, from about 1000 to 2000 Hz). Output sample m
    /// is at input sample 5m.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static Complex[] ToBaseband(ReadOnlySpan<float> audio, double centreHz = OnAir.CentreAudioHz)
    {
        int n = audio.Length;
        // Mixed by 2 exp(-j 2 pi centreHz t), from a table exact for centreHz rounded to the
        // nearest Hz: its period is the shortest number of samples that is a whole number of
        // both the mixer's cycles and the sample rate's (80 samples, 3 cycles, at 1800 Hz; 32
        // samples, 1 cycle, at 1500 Hz), found from their greatest common divisor.
        var re = new float[n + FrontTaps];
        var im = new float[n + FrontTaps];
        int period = Period(centreHz);
        long cycles = (long)Math.Round(centreHz) / Gcd((long)Math.Round(centreHz), AudioRate);
        // Heap, not stackalloc: an unusual centre (not a tidy fraction of 48 kHz) can make the
        // period thousands of samples long.
        var cos = new float[period];
        var sin = new float[period];
        for (int i = 0; i < period; i++)
        {
            double phase = 2 * Math.PI * cycles * i / period;
            cos[i] = (float)(2 * Math.Cos(phase));
            sin[i] = (float)(-2 * Math.Sin(phase));
        }
        // Padded with the filter's half length of zeros at each end, so every output is a
        // straight dot product: out[m] = sum_k h[k] y[5m + 200 - k].
        const int Half = (FrontTaps - 1) / 2;
        for (int i = 0; i < n; i++)
        {
            int p = i % period;
            re[i + Half] = audio[i] * cos[p];
            im[i + Half] = audio[i] * sin[p];
        }
        // The filter is symmetric, so convolution is correlation with it as it is.
        int outputs = (n + Decimation - 1) / Decimation;
        var bb = new Complex[outputs];
        var h = FrontFilter.AsSpan();
        for (int m = 0; m < outputs; m++)
        {
            int s = m * Decimation;
            bb[m] = new Complex(Dot(h, re.AsSpan(s, FrontTaps)), Dot(h, im.AsSpan(s, FrontTaps)));
        }
        return bb;
    }

    /// <summary>
    /// The shortest whole number of samples at <see cref="AudioRate"/> that a mixer at
    /// <paramref name="centreHz"/> (rounded to the nearest Hz) repeats in: 80 at the usual 1800
    /// Hz, 32 at 1500 Hz.
    /// </summary>
    private static int Period(double centreHz)
    {
        long hz = Math.Abs((long)Math.Round(centreHz));
        long g = Gcd(hz, AudioRate);
        return g == 0 ? AudioRate : (int)(AudioRate / g);
    }

    /// <summary>The greatest common divisor of two non-negative integers (Euclid's algorithm).</summary>
    private static long Gcd(long a, long b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        // Two accumulators and unchecked loads: this is the costliest loop of the measurement.
        int n = Math.Min(a.Length, b.Length);
        int w = Vector<float>.Count;
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        var acc0 = Vector<float>.Zero;
        var acc1 = Vector<float>.Zero;
        int i = 0;
        for (; i <= n - (2 * w); i += 2 * w)
        {
            acc0 += Vector.LoadUnsafe(ref ra, (nuint)i) * Vector.LoadUnsafe(ref rb, (nuint)i);
            acc1 += Vector.LoadUnsafe(ref ra, (nuint)(i + w)) * Vector.LoadUnsafe(ref rb, (nuint)(i + w));
        }
        float sum = Vector.Sum(acc0 + acc1);
        for (; i < n; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    /// <summary>The SRRC matched filter at 9600 Hz, delay compensated, as dsp.py's matched.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static Complex[] Matched(ReadOnlySpan<Complex> bb)
    {
        int half = PulsePeak;
        var y = new Complex[bb.Length];
        for (int n = 0; n < bb.Length; n++)
        {
            double re = 0, im = 0;
            int lo = Math.Max(0, half - n);
            int hi = Math.Min(Pulse.Length, bb.Length - n + half);
            for (int j = lo; j < hi; j++)
            {
                var x = bb[n + j - half];
                re += Pulse[j] * x.Real;
                im += Pulse[j] * x.Imaginary;
            }
            y[n] = new Complex(re, im);
        }
        return y;
    }

    /// <summary>
    /// Symbols shaped at 9600 Hz exactly as the MS110D modulator shapes them, before its mixer:
    /// symbol n's pulse peaks at index 4n + <see cref="PulsePeak"/>. The length is the symbols'
    /// times 4 plus the pulse's.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static Complex[] Shape(ReadOnlySpan<Complex> symbols)
    {
        var up = new Complex[(symbols.Length * Sps) + Pulse.Length];
        for (int n = 0; n < symbols.Length; n++)
        {
            var s = symbols[n];
            int start = n * Sps;
            for (int k = 0; k < Pulse.Length && start + k < up.Length; k++)
            {
                up[start + k] += s * Pulse[k];
            }
        }
        return up;
    }

    /// <summary>|a . b*| / (|a| |b|): how alike two stretches of signal are, 0 to 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double NormalisedCorrelation(ReadOnlySpan<Complex> a, ReadOnlySpan<Complex> b)
    {
        int n = Math.Min(a.Length, b.Length);
        Complex dot = Complex.Zero;
        double ea = 0, eb = 0;
        for (int i = 0; i < n; i++)
        {
            dot += Complex.Conjugate(b[i]) * a[i];
            ea += Norm(a[i]);
            eb += Norm(b[i]);
        }
        return dot.Magnitude / Math.Sqrt((ea * eb) + 1e-30);
    }

    /// <summary>|z|^2.</summary>
    public static double Norm(Complex z) => (z.Real * z.Real) + (z.Imaginary * z.Imaginary);

    /// <summary>
    /// Where <paramref name="pattern"/> best matches <paramref name="signal"/>: c[k] = sum_i
    /// signal[k + i] conj(pattern[i]) for every k with the pattern wholly inside the signal.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double[] CorrelationMagnitude(ReadOnlySpan<Complex> signal, ReadOnlySpan<Complex> pattern)
    {
        int valid = signal.Length - pattern.Length + 1;
        if (valid <= 0)
        {
            return [];
        }
        int size = 1;
        while (size < signal.Length + pattern.Length)
        {
            size <<= 1;
        }
        var a = new Complex[size];
        var b = new Complex[size];
        signal.CopyTo(a);
        // The pattern conjugated and reversed, so the product is a correlation.
        for (int i = 0; i < pattern.Length; i++)
        {
            b[i] = Complex.Conjugate(pattern[pattern.Length - 1 - i]);
        }
        Fft(a, false);
        Fft(b, false);
        for (int i = 0; i < size; i++)
        {
            a[i] *= b[i];
        }
        Fft(a, true);
        var c = new double[valid];
        for (int k = 0; k < valid; k++)
        {
            c[k] = a[k + pattern.Length - 1].Magnitude;
        }
        return c;
    }

    /// <summary>In-place radix-2 complex FFT; the inverse is scaled by 1/n.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Fft(Complex[] x, bool inverse)
    {
        int n = x.Length;
        if ((n & (n - 1)) != 0)
        {
            throw new ArgumentException("the FFT length must be a power of two", nameof(x));
        }
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;
            if (i < j)
            {
                (x[i], x[j]) = (x[j], x[i]);
            }
        }
        // Twiddles from a table, not a running product: faster, and no error builds up.
        var (cos, sin) = Twiddles(n);
        double sign = inverse ? 1 : -1;
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len / 2, stride = n / len;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0; k < half; k++)
                {
                    double wr = cos[k * stride], wi = sign * sin[k * stride];
                    var v = x[i + k + half];
                    double vr = (v.Real * wr) - (v.Imaginary * wi), vi = (v.Real * wi) + (v.Imaginary * wr);
                    var u = x[i + k];
                    x[i + k] = new Complex(u.Real + vr, u.Imaginary + vi);
                    x[i + k + half] = new Complex(u.Real - vr, u.Imaginary - vi);
                }
            }
        }
        if (inverse)
        {
            double scale = 1.0 / n;
            for (int i = 0; i < n; i++)
            {
                x[i] *= scale;
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (double[] Cos, double[] Sin)> TwiddleTables = new();

    private static (double[] Cos, double[] Sin) Twiddles(int n) => TwiddleTables.GetOrAdd(n, size =>
    {
        var c = new double[size / 2];
        var s = new double[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            c[k] = Math.Cos(2 * Math.PI * k / size);
            s[k] = Math.Sin(2 * Math.PI * k / size);
        }
        return (c, s);
    });

    /// <summary>
    /// Solves A x = b in place for a Hermitian positive definite A (n by n, row major) by
    /// Cholesky, for several right-hand sides at once (b is n by m, row major). False if A is not
    /// positive definite.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool CholeskySolve(Complex[] a, int n, Complex[] b, int m)
    {
        // A = L L^H, L lower, written over A's lower triangle.
        for (int j = 0; j < n; j++)
        {
            double d = a[(j * n) + j].Real;
            for (int k = 0; k < j; k++)
            {
                d -= Norm(a[(j * n) + k]);
            }
            if (!(d > 0))
            {
                return false;
            }
            d = Math.Sqrt(d);
            a[(j * n) + j] = d;
            for (int i = j + 1; i < n; i++)
            {
                var s = a[(i * n) + j];
                for (int k = 0; k < j; k++)
                {
                    s -= a[(i * n) + k] * Complex.Conjugate(a[(j * n) + k]);
                }
                a[(i * n) + j] = s / d;
            }
        }
        for (int c = 0; c < m; c++)
        {
            // L y = b, then L^H x = y.
            for (int i = 0; i < n; i++)
            {
                var s = b[(i * m) + c];
                for (int k = 0; k < i; k++)
                {
                    s -= a[(i * n) + k] * b[(k * m) + c];
                }
                b[(i * m) + c] = s / a[(i * n) + i].Real;
            }
            for (int i = n - 1; i >= 0; i--)
            {
                var s = b[(i * m) + c];
                for (int k = i + 1; k < n; k++)
                {
                    s -= Complex.Conjugate(a[(k * n) + i]) * b[(k * m) + c];
                }
                b[(i * m) + c] = s / a[(i * n) + i].Real;
            }
        }
        return true;
    }

    /// <summary>Solves the small real system G x = b (n by n, row major) by Gaussian elimination with pivoting; null if singular.</summary>
    public static double[]? SolveReal(double[] g, double[] b, int n)
    {
        var a = (double[])g.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++)
            {
                if (Math.Abs(a[(r * n) + c]) > Math.Abs(a[(p * n) + c]))
                {
                    p = r;
                }
            }
            if (Math.Abs(a[(p * n) + c]) < 1e-14)
            {
                return null;
            }
            if (p != c)
            {
                for (int k = 0; k < n; k++)
                {
                    (a[(c * n) + k], a[(p * n) + k]) = (a[(p * n) + k], a[(c * n) + k]);
                }
                (x[c], x[p]) = (x[p], x[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = a[(r * n) + c] / a[(c * n) + c];
                for (int k = c; k < n; k++)
                {
                    a[(r * n) + k] -= f * a[(c * n) + k];
                }
                x[r] -= f * x[c];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = x[r];
            for (int k = r + 1; k < n; k++)
            {
                s -= a[(r * n) + k] * x[k];
            }
            x[r] = s / a[(r * n) + r];
        }
        return x;
    }

    /// <summary>The median of the finite values given (the mean of the middle two for an even count); NaN for none.</summary>
    public static double Median(IEnumerable<double> values)
    {
        var v = values.Where(double.IsFinite).OrderBy(d => d).ToArray();
        if (v.Length == 0)
        {
            return double.NaN;
        }
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[(v.Length / 2) - 1] + v[v.Length / 2]) / 2;
    }
}
