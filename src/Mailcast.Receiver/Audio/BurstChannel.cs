using System.Numerics;
using System.Runtime.CompilerServices;
using Packet.SoundModem.Ms110d;

namespace Mailcast.Receiver;

/// <summary>
/// The radio path measured from one decoded burst: the burst made again from what it decoded to,
/// found in its audio, and a least-squares estimate of the channel over every data frame, as
/// chanest.py's segment_ls does.
/// </summary>
internal static class BurstChannel
{
    /// <summary>The estimate's delays, in symbols: from 6 before the main arrival to 20 after (2.5 ms before to 8.6 ms after), at quarter symbols.</summary>
    private const int LagLow = -6;

    private const int LagHigh = 20;

    /// <summary>A correlation peak at least this far above its median is the burst; noise makes about 4.</summary>
    public const double FoundRatio = 10;

    /// <summary>
    /// Measures one burst. <paramref name="audio"/> is the 48 kHz audio around it, and
    /// <paramref name="endSample"/> the sample in it at which the modem reported the burst over.
    /// Null if the burst could not be found in its audio.
    /// </summary>
    public static ChannelSnapshots? Measure(ReadOnlySpan<float> audio, int endSample, Ms110dLockInfo locked, byte[] payloadBits) =>
        Measure(audio, endSample, locked, payloadBits, out _);

    /// <summary>As <see cref="Measure(ReadOnlySpan{float}, int, Ms110dLockInfo, byte[])"/>, saying where the burst was found.</summary>
    public static ChannelSnapshots? Measure(ReadOnlySpan<float> audio, int endSample, Ms110dLockInfo locked, byte[] payloadBits, out BurstReference.Aligned? found)
    {
        found = null;
        if (locked.WaveformNumber < 1 || payloadBits.Length == 0)
        {
            return null;
        }
        var bb = ChannelMaths.ToBaseband(audio);
        // The modem's lock offset out first, as align.py and analyze.py do.
        double w = -2 * Math.PI * locked.CfoHz / ChannelMaths.Rate;
        for (int n = 0; n < bb.Length; n++)
        {
            bb[n] *= Complex.FromPolarCoordinates(1, w * n);
        }
        found = BurstReference.Align(bb, endSample / (ChannelMaths.AudioRate / ChannelMaths.Rate), locked, payloadBits);
        if (found is null || found.PeakRatio < FoundRatio)
        {
            return null;
        }
        var y = ChannelMaths.Matched(bb);
        return Estimate(y, found.Start, found.Symbols, BurstReference.FrameSymbols(locked.WaveformNumber), locked.CfoHz);
    }

    /// <summary>
    /// The channel over each run of <paramref name="seg"/> symbols, from the matched-filtered
    /// baseband <paramref name="y"/> whose shaped burst begins at <paramref name="start"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ChannelSnapshots Estimate(Complex[] y, int start, Complex[] sym, int seg, double offsetHz)
    {
        const int P = LagHigh - LagLow + 1;
        int peak = ChannelMaths.PulsePeak;
        int nseg = sym.Length / seg;
        var estimates = new List<Complex[]>(nseg);
        var signal = new List<double>(nseg);
        var residual = new List<double>(nseg);
        var received = new List<double>(nseg);
        var r = new Complex[P * P];
        var rhs = new Complex[P * 4];
        Complex S(int i) => i >= 0 && i < sym.Length ? sym[i] : Complex.Zero;
        for (int j = 0; j < nseg; j++)
        {
            int n0 = j * seg;
            int idx0 = start + peak + (4 * n0);
            if (idx0 < 0 || idx0 + (4 * (seg - 1)) + 4 >= y.Length)
            {
                break;
            }
            // S[i, p] = sym[n0 + i - m], m = LagLow + p. S^H S is Toeplitz-like: its first row
            // directly, then each diagonal by sliding the sum one symbol.
            for (int b = 0; b < P; b++)
            {
                Complex s = Complex.Zero;
                int mb = LagLow + b, ma = LagLow;
                for (int i = 0; i < seg; i++)
                {
                    s += Complex.Conjugate(S(n0 + i - ma)) * S(n0 + i - mb);
                }
                r[b] = s;
            }
            for (int a = 1; a < P; a++)
            {
                for (int b = a; b < P; b++)
                {
                    int ma = LagLow + a - 1, mb = LagLow + b - 1;
                    r[(a * P) + b] = r[((a - 1) * P) + b - 1]
                        + (Complex.Conjugate(S(n0 - 1 - ma)) * S(n0 - 1 - mb))
                        - (Complex.Conjugate(S(n0 + seg - 1 - ma)) * S(n0 + seg - 1 - mb));
                }
            }
            for (int a = 0; a < P; a++)
            {
                for (int b = 0; b < a; b++)
                {
                    r[(a * P) + b] = Complex.Conjugate(r[(b * P) + a]);
                }
            }
            // S^H Y, for each of the 4 sample phases; and |Y|^2.
            double yy = 0;
            Array.Clear(rhs);
            for (int i = 0; i < seg; i++)
            {
                int idx = idx0 + (4 * i);
                for (int ph = 0; ph < 4; ph++)
                {
                    var v = y[idx + ph];
                    yy += ChannelMaths.Norm(v);
                    for (int p = 0; p < P; p++)
                    {
                        rhs[(p * 4) + ph] += Complex.Conjugate(S(n0 + i - (LagLow + p))) * v;
                    }
                }
            }
            var rhsKept = (Complex[])rhs.Clone();
            if (!ChannelMaths.CholeskySolve(r, P, rhs, 4))
            {
                break;
            }
            // Explained power G^H S^H Y; the residual is what is left of |Y|^2.
            double ps = 0;
            for (int i = 0; i < P * 4; i++)
            {
                ps += (Complex.Conjugate(rhs[i]) * rhsKept[i]).Real;
            }
            double count = seg * 4.0;
            double pr = Math.Max(yy - ps, 1e-30) / count * seg / (seg - P);
            signal.Add(ps / count);
            residual.Add(pr);
            received.Add(yy / count);
            var c = new Complex[4 * P];
            for (int p = 0; p < P; p++)
            {
                for (int ph = 0; ph < 4; ph++)
                {
                    c[(4 * p) + ph] = rhs[(p * 4) + ph];
                }
            }
            estimates.Add(c);
        }

        // Noise: just before the burst, and just after it if that is in the audio, as analyze.py's load_burst.
        double noise = MeanPower(y, start - (int)(0.9 * ChannelMaths.Rate), start - (int)(0.1 * ChannelMaths.Rate));
        int tail = start + (sym.Length * 4) + 80;
        if (tail + (int)(0.5 * ChannelMaths.Rate) < y.Length)
        {
            noise = 0.5 * (noise + MeanPower(y, tail, tail + (int)(0.5 * ChannelMaths.Rate)));
        }

        // A snapshot is bad where the reference explains far less than the noise would allow:
        // a block the decoder got wrong, so the burst made again is not what was sent.
        var good = new bool[estimates.Count];
        double sumS = 0, sumR = 0;
        for (int j = 0; j < good.Length; j++)
        {
            double snr = signal[j] / residual[j];
            double explained = snr / (1 + snr);
            double expect = Math.Clamp(1 - (noise / received[j]), 0, 1);
            good[j] = !((expect - explained > 0.3) && (received[j] > 3 * noise));
            if (good[j])
            {
                sumS += signal[j];
                sumR += residual[j];
            }
        }
        double snrDb = sumR > 0 ? (10 * Math.Log10(sumS / sumR)) + (10 * Math.Log10(2400.0 / 3000)) : double.NaN;
        return new ChannelSnapshots
        {
            Basis = "bursts",
            FirstLagSeconds = 4.0 * LagLow / ChannelMaths.Rate,
            LagStepSeconds = 1.0 / ChannelMaths.Rate,
            Estimates = [.. estimates],
            Good = good,
            SnapshotSeconds = (double)seg / ChannelMaths.Baud,
            SnrDb = snrDb,
            OffsetHz = offsetHz,
        };
    }

    private static double MeanPower(Complex[] y, int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(y.Length, to);
        if (to <= from)
        {
            return double.NaN;
        }
        double s = 0;
        for (int i = from; i < to; i++)
        {
            s += ChannelMaths.Norm(y[i]);
        }
        return s / (to - from);
    }
}
