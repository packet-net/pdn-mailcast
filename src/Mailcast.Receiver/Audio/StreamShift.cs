using System.Numerics;
using System.Runtime.InteropServices;
using M0LTE.Dsp;

namespace Mailcast.Receiver;

/// <summary>
/// Moves pdn-soundmodem's audio so a signal its dial puts outside 1000 to 2000 Hz lands on the
/// usual dial's 1800 Hz, MS110D's own centre: pdn-soundmodem's built-in receiver did the same (its
/// <c>MailcastReceiveShift</c>). It bandpasses to the signal's own width before shifting, never
/// after: shifting down audio that has not been filtered first folds the noise below the signal
/// onto it. The shift is the analytic-signal one (a Hilbert filter, then a mix), as
/// <see cref="FrequencyShifter"/> does.
/// </summary>
/// <remarks>
/// <para>Run at the channel's own rate, before a 12 kHz channel is brought up to 48 kHz, with
/// filters as long in time as the built-in's 639 taps at 48 kHz (so a quarter of the taps at
/// 12 kHz, on a quarter of the samples). The Hilbert filter's every other tap is zero, so only the
/// others are worked out, and both filters' sums are done several samples at a time
/// (<see cref="Vector{T}"/>). Allocates nothing per block.</para>
/// <para>Not thread-safe: one block at a time, from one thread.</para>
/// </remarks>
internal sealed class StreamShift
{
    /// <summary>The built-in's filter lengths at 48 kHz, for both the bandpass and the Hilbert filter.</summary>
    private const int TapsAt48k = 639;

    /// <summary>How far the bandpass opens beyond the signal's edges, as the built-in's: room for their skirts.</summary>
    private const double BandpassMarginHz = 300;

    private readonly float[] _bandpass;
    private readonly float[] _bandpassLine;
    private int _bandpassAt;

    /// <summary>The Hilbert filter's taps that are not zero, oldest sample first.</summary>
    private readonly float[] _hilbert;

    /// <summary>The bandpassed audio, split by sample parity: the Hilbert filter only ever reads every other sample.</summary>
    private readonly float[][] _lines;
    private readonly int[] _lineAt = new int[2];
    private int _parity;

    /// <summary>Where in the other parity's line the sample half the Hilbert filter's length back is.</summary>
    private readonly int _delayIndex;

    private readonly double _stepCos;
    private readonly double _stepSin;
    private double _cos = 1;
    private double _sin;

    /// <param name="sampleRate">The audio's rate, Hz.</param>
    /// <param name="centreHz">Where the signal's centre is in the audio as it comes.</param>
    /// <param name="toHz">Where it is moved to.</param>
    /// <param name="halfWidthHz">Half the signal's width, for the bandpass.</param>
    public StreamShift(int sampleRate, double centreHz, double toHz, double halfWidthHz)
    {
        int taps = TapsFor(sampleRate);
        double[] bandpass = Array.ConvertAll(FilterDesign.BandPass(
            Math.Max(50, centreHz - halfWidthHz - BandpassMarginHz),
            Math.Min((sampleRate / 2.0) - 50, centreHz + halfWidthHz + BandpassMarginHz),
            sampleRate, taps), t => (double)t);
        // Kept oldest sample first, so each output is one straight sum against the line.
        _bandpass = new float[taps];
        for (int i = 0; i < taps; i++)
        {
            _bandpass[i] = (float)bandpass[taps - 1 - i];
        }
        _bandpassLine = new float[2 * taps];

        // A type III Hilbert filter, Blackman-windowed: 2/(pi m) at odd m from its centre, zero at
        // even m. Its length is 3 more than a multiple of 4, so the centre is odd and the taps
        // that count are the even ones, (taps + 1) / 2 of them.
        int centre = (taps - 1) / 2;
        int kept = (taps + 1) / 2;
        _hilbert = new float[kept];
        for (int j = 0; j < kept; j++)
        {
            int n = 2 * j;
            int m = n - centre;
            double window = 0.42 - (0.5 * Math.Cos(2 * Math.PI * n / (taps - 1))) + (0.08 * Math.Cos(4 * Math.PI * n / (taps - 1)));
            // Tap n reads the sample n back; the line holds the oldest (n = taps - 1) first.
            _hilbert[kept - 1 - j] = (float)(2 / (Math.PI * m) * window);
        }
        _lines = [new float[2 * kept], new float[2 * kept]];
        // The in-phase half is the bandpassed audio delayed by the Hilbert filter's centre, an odd
        // number of samples, so it is in the other parity's line, (centre - 1) / 2 back from its newest.
        _delayIndex = kept - 1 - ((centre - 1) / 2);

        double step = 2 * Math.PI * (toHz - centreHz) / sampleRate;
        _stepCos = Math.Cos(step);
        _stepSin = Math.Sin(step);
    }

    /// <summary>
    /// The filter length at <paramref name="sampleRate"/>: as long in time as the built-in's 639
    /// taps at 48 kHz, kept 3 more than a multiple of 4 (see the Hilbert filter).
    /// </summary>
    internal static int TapsFor(int sampleRate)
    {
        int taps = (int)Math.Round((double)TapsAt48k * sampleRate / 48000);
        taps -= ((taps - 3) % 4 + 4) % 4;
        return Math.Max(taps, 3);
    }

    /// <summary>
    /// Forgets the audio so far, so nothing before a hole in it rings on after, and is as if newly
    /// made: called on a gap, as the modem is made to listen afresh.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_bandpassLine);
        Array.Clear(_lines[0]);
        Array.Clear(_lines[1]);
        _bandpassAt = 0;
        _lineAt[0] = _lineAt[1] = 0;
        _parity = 0;
        _cos = 1;
        _sin = 0;
    }

    /// <summary>Moves <paramref name="samples"/> into <paramref name="output"/>, which may be the same span.</summary>
    public void Process(ReadOnlySpan<float> samples, Span<float> output)
    {
        int taps = _bandpass.Length;
        int kept = _hilbert.Length;
        for (int i = 0; i < samples.Length; i++)
        {
            _bandpassAt = _bandpassAt + 1 == taps ? 0 : _bandpassAt + 1;
            _bandpassLine[_bandpassAt] = samples[i];
            _bandpassLine[_bandpassAt + taps] = samples[i];
            float banded = Dot(_bandpass, _bandpassLine.AsSpan(_bandpassAt + 1, taps));

            float[] line = _lines[_parity];
            int at = _lineAt[_parity] = _lineAt[_parity] + 1 == kept ? 0 : _lineAt[_parity] + 1;
            line[at] = banded;
            line[at + kept] = banded;
            float quadrature = Dot(_hilbert, line.AsSpan(at + 1, kept));
            int other = 1 - _parity;
            float inPhase = _lines[other][_lineAt[other] + 1 + _delayIndex];
            _parity = other;

            output[i] = (float)((inPhase * _cos) - (quadrature * _sin));
            double cos = (_cos * _stepCos) - (_sin * _stepSin);
            _sin = (_sin * _stepCos) + (_cos * _stepSin);
            _cos = cos;
        }
        // Held to unit length, so rounding never grows or shrinks the output.
        double length = Math.Sqrt((_cos * _cos) + (_sin * _sin));
        _cos /= length;
        _sin /= length;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..a.Length]);
        var sum = Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++)
        {
            sum += va[i] * vb[i];
        }
        float total = Vector.Sum(sum);
        for (int i = va.Length * Vector<float>.Count; i < a.Length; i++)
        {
            total += a[i] * b[i];
        }
        return total;
    }
}
