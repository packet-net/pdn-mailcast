using System.Numerics;
using System.Runtime.CompilerServices;
using System.Reflection;
using M0LTE.Ofdm;
using Packet.SoundModem.Ms110d;

namespace Mailcast.Receiver;

/// <summary>
/// A decoded MS110D burst made again, symbol for symbol, with pdn-soundmodem's own modulator:
/// once a burst has decoded, every symbol it carried is known, so the whole burst is a reference
/// the received audio can be measured against.
/// </summary>
/// <remarks>
/// pdn-soundmodem (0.86.0) builds the symbol stream in <c>Ms110dModulator.BuildSymbols</c>, which
/// is internal: its public <c>Modulate</c> gives only the audio after the mixer. The method is
/// called here by reflection, as burstdump in the passive analysis does, and a test pins it, so
/// a pdn-soundmodem that renames it fails that test rather than quietly losing the measurement.
/// </remarks>
internal static class BurstReference
{
    private delegate Cf[] BuildFn(ReadOnlySpan<byte> bits);

    private static readonly MethodInfo? BuildSymbols =
        typeof(Ms110dModulator).GetMethod("BuildSymbols", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(ReadOnlySpan<byte>)]);

    /// <summary>Whether this pdn-soundmodem lets the burst be made again.</summary>
    public static bool Available => BuildSymbols is not null;

    /// <summary>Preamble length in symbols for <paramref name="superframes"/> preamble super-frames (no TLC).</summary>
    public static int PreambleSymbols(int superframes) => superframes == 1 ? 320 : 576 * superframes;

    /// <summary>The most preamble super-frames tried.</summary>
    public const int MostSuperframes = 6;

    /// <summary>
    /// The burst's symbols, preamble and all, as the head end sent them: the waveform the modem
    /// locked to, <paramref name="superframes"/> preamble super-frames and no TLC section.
    /// </summary>
    public static Complex[] Symbols(Ms110dLockInfo locked, int superframes, byte[] payloadBits)
    {
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = locked.WaveformNumber,
            Interleaver = locked.Interleaver,
            ConstraintLength = locked.ConstraintLength,
            PreambleSuperframes = superframes,
            TlcBlocks = 0,
        };
        var modulator = new Ms110dModulator(settings);
        var build = (BuildSymbols ?? throw new InvalidOperationException("this pdn-soundmodem has no Ms110dModulator.BuildSymbols")).CreateDelegate<BuildFn>(modulator);
        Cf[] syms = build(payloadBits);
        var result = new Complex[syms.Length];
        for (int i = 0; i < syms.Length; i++)
        {
            result[i] = new Complex(syms[i].Re, syms[i].Im);
        }
        return result;
    }

    /// <summary>One data frame, U + K symbols, of waveform <paramref name="wn"/>: one channel snapshot.</summary>
    public static int FrameSymbols(int wn)
    {
        var mode = Ms110dMode.Mode3k(wn);
        return mode.U + mode.K;
    }

    /// <summary>
    /// Finds the burst in <paramref name="bb"/> (baseband at 9600 Hz, the carrier offset already
    /// taken out) as align.py does: the end of the preamble and the first data by correlation,
    /// near where the modem said the burst ended, then the preamble length that fits.
    /// </summary>
    /// <param name="bb">Baseband around the burst.</param>
    /// <param name="endIndex">Where in <paramref name="bb"/> the modem said the burst was over.</param>
    /// <param name="locked">The waveform the modem locked to.</param>
    /// <param name="payloadBits">What the burst decoded to.</param>
    /// <returns>Where the shaped burst starts and its symbols, or null if it is not there.</returns>
    public static Aligned? Align(Complex[] bb, int endIndex, Ms110dLockInfo locked, byte[] payloadBits)
    {
        // Only the preamble differs with its length, so the data is made once and each
        // preamble put in front of it.
        var sym1 = Symbols(locked, 1, payloadBits);
        var sym3 = WithPreamble(locked, 3, sym1);
        int pre3 = PreambleSymbols(3);
        int superframe = 576;
        // The last preamble super-frame (the same for every count from 2 up) and the first 1200
        // data symbols, searched only where this burst can start: its length before the end the
        // modem reported, less up to 4.5 s of decoder delay, or up to 1 s more.
        int headData = Math.Min(1200, sym3.Length - pre3);
        var head = ChannelMaths.Shape(sym3.AsSpan(pre3 - superframe, superframe + headData));
        int duration = (sym1.Length - PreambleSymbols(1) + pre3) * ChannelMaths.Sps;
        int lo = Math.Max(0, endIndex - duration - (int)(4.5 * ChannelMaths.Rate));
        int hi = Math.Min(bb.Length, endIndex - duration + ChannelMaths.Rate + head.Length + ChannelMaths.Rate);
        if (hi - lo < head.Length + 1)
        {
            return null;
        }
        var c = ChannelMaths.CorrelationMagnitude(bb.AsSpan(lo, hi - lo), head);
        double median = Math.Max(ChannelMaths.Median(c), 1e-30);
        // The data's mini-probes repeat every frame, so the correlation has side peaks whole
        // frames either side of the true one, and one can win where the true head faded. So the
        // best few peaks, and every whole number of frames either side of the best, are each
        // tried against the whole of the data, 0.2 s at a time, and the one the data matches
        // best is taken. (align.py takes the highest peak.)
        var data = ChannelMaths.Shape(sym1.AsSpan(PreambleSymbols(1)));
        var candidates = Peaks(c, 4, 64 * ChannelMaths.Sps);
        int frame = FrameSymbols(locked.WaveformNumber) * ChannelMaths.Sps;
        for (int f = candidates[0] % frame; f < c.Length; f += frame)
        {
            if (!candidates.Contains(f))
            {
                candidates.Add(f);
            }
        }
        // Screened on 8 pieces spread over the burst, then the best three on all of it.
        int k = -1;
        double bestMatch = -1;
        var screened = candidates
            .Select(candidate => (candidate, match: DataMatch(bb, lo + candidate + (superframe * ChannelMaths.Sps), data, 8)))
            .OrderByDescending(x => x.match)
            .Take(3);
        foreach (var (candidate, _) in screened)
        {
            double match = DataMatch(bb, lo + candidate + (superframe * ChannelMaths.Sps), data, int.MaxValue);
            if (match > bestMatch)
            {
                bestMatch = match;
                k = candidate;
            }
        }
        if (k < 0)
        {
            return null;
        }
        double peakRatio = c[k] / median;
        int dataStart = lo + k + (superframe * ChannelMaths.Sps);

        // The preamble length: the most super-frames whose every one still correlates. align.py
        // asks for half the best correlation; a quarter, here, keeps a burst whose preamble
        // faded in one super-frame, and is still well clear of a wrong one (under 0.1).
        var bySuperframes = new Dictionary<int, (Complex[] Symbols, double[] Corr)>();
        for (int m = 1; m <= MostSuperframes; m++)
        {
            var symm = m == 1 ? sym1 : m == 3 ? sym3 : WithPreamble(locked, m, sym1);
            int pl = PreambleSymbols(m);
            int start = dataStart - (pl * ChannelMaths.Sps);
            if (start < 0)
            {
                continue;
            }
            int step = m == 1 ? 320 : superframe;
            var corr = new List<double>();
            for (int s = 0; s < pl; s += step)
            {
                var reference = ChannelMaths.Shape(symm.AsSpan(s, step)).AsSpan(0, step * ChannelMaths.Sps);
                int at = start + (s * ChannelMaths.Sps);
                if (at + reference.Length > bb.Length)
                {
                    break;
                }
                corr.Add(ChannelMaths.NormalisedCorrelation(bb.AsSpan(at, reference.Length), reference));
            }
            bySuperframes[m] = (symm, [.. corr]);
        }
        if (!bySuperframes.TryGetValue(1, out var one) || one.Corr.Length == 0)
        {
            return null;
        }
        double firstBest = one.Corr.Max();
        int best = 1;
        foreach (var (m, entry) in bySuperframes.OrderBy(e => e.Key))
        {
            if (entry.Corr.Length > 0 && entry.Corr.Min() > 0.25 * firstBest)
            {
                best = m;
            }
        }
        var symbols = bySuperframes[best].Symbols;
        int burstStart = dataStart - (PreambleSymbols(best) * ChannelMaths.Sps);
        return new Aligned(burstStart, best, symbols, peakRatio, firstBest);
    }

    /// <summary>
    /// The burst <paramref name="withOne"/> (made with a one super-frame preamble) with a
    /// preamble of <paramref name="superframes"/> instead. The preamble depends only on the
    /// waveform and its length, so it is taken from a burst of one bit.
    /// </summary>
    internal static Complex[] WithPreamble(Ms110dLockInfo locked, int superframes, Complex[] withOne)
    {
        int pre = PreambleSymbols(superframes), one = PreambleSymbols(1);
        var preamble = Symbols(locked, superframes, [0]).AsSpan(0, pre);
        var result = new Complex[pre + withOne.Length - one];
        preamble.CopyTo(result);
        withOne.AsSpan(one).CopyTo(result.AsSpan(pre));
        return result;
    }

    /// <summary>The <paramref name="count"/> highest peaks of <paramref name="c"/> at least <paramref name="apart"/> apart, highest first.</summary>
    private static List<int> Peaks(double[] c, int count, int apart)
    {
        var peaks = new List<int>();
        while (peaks.Count < count)
        {
            int best = -1;
            for (int i = 0; i < c.Length; i++)
            {
                if ((best < 0 || c[i] > c[best]) && peaks.TrueForAll(p => Math.Abs(p - i) >= apart))
                {
                    best = i;
                }
            }
            if (best < 0)
            {
                break;
            }
            peaks.Add(best);
        }
        return peaks;
    }

    /// <summary>
    /// How well the data, starting at <paramref name="dataStart"/>, matches its reference: the
    /// median normalised correlation over 0.2 s pieces, at most <paramref name="pieces"/> of them
    /// spread evenly over the burst.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double DataMatch(Complex[] bb, int dataStart, Complex[] data, int pieces)
    {
        const int Piece = ChannelMaths.Rate / 5;
        int all = data.Length / Piece;
        int step = Piece * Math.Max(1, (int)Math.Ceiling(all / (double)Math.Max(1, Math.Min(pieces, all))));
        var matches = new List<double>();
        for (int s = 0; s + Piece <= data.Length && dataStart + s + Piece <= bb.Length; s += step)
        {
            if (dataStart + s >= 0)
            {
                matches.Add(ChannelMaths.NormalisedCorrelation(bb.AsSpan(dataStart + s, Piece), data.AsSpan(s, Piece)));
            }
        }
        return matches.Count == 0 ? 0 : ChannelMaths.Median(matches);
    }

    /// <summary>A burst found in its audio.</summary>
    /// <param name="Start">Where in the baseband the shaped burst begins (its symbol 0 peaks <see cref="ChannelMaths.PulsePeak"/> later).</param>
    /// <param name="Superframes">The preamble super-frames it was sent with.</param>
    /// <param name="Symbols">Every symbol of it.</param>
    /// <param name="PeakRatio">How far the correlation peak stood above its median: a few for noise, tens to hundreds for the burst.</param>
    /// <param name="PreambleMatch">The preamble's normalised correlation with the reference, 0 to 1.</param>
    public sealed record Aligned(int Start, int Superframes, Complex[] Symbols, double PeakRatio, double PreambleMatch);
}
