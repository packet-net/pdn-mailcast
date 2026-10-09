using System.Numerics;

namespace Mailcast.Receiver;

/// <summary>
/// A radio's receive filter, measured from band noise: its edges, how wide it is, and the USB
/// dial that puts GB7RDG's fixed signal (<see cref="OnAir.TransmitHz"/>) in the middle of it.
/// </summary>
/// <param name="LowHz">The filter's lower edge, Hz.</param>
/// <param name="HighHz">Its upper edge, Hz.</param>
/// <param name="DialKHz">
/// The USB dial, in kHz, that puts the signal's centre in the middle of the filter: <see
/// cref="OnAir.TransmitHz"/> minus the filter's own middle, rounded to the nearest 10 Hz (most
/// rigs tune in 10 Hz steps).
/// </param>
/// <param name="Narrow">
/// Whether the filter is narrower than <see cref="FilterScan.NarrowWidthHz"/>: some frames will
/// still be lost on a weak, fading path even on <see cref="DialKHz"/>, and a wider filter (often
/// a DATA or PKT mode) would do better.
/// </param>
public sealed record RadioFilter(double LowHz, double HighHz, double DialKHz, bool Narrow)
{
    /// <summary>The filter's width, Hz.</summary>
    public double WidthHz => HighHz - LowHz;
}

/// <summary>
/// Finds a radio's receive filter from band noise, and the dial that centres GB7RDG's signal in
/// it: for the settings page's "Measure my filter", and for typing the edges by hand (<see
/// cref="DialFor"/>), which gives the same answer.
/// </summary>
/// <remarks>
/// <para><b>Method.</b> About 10 s of audio (with no signal tuned in: band noise, or a dead
/// band) is cut into non-overlapping 4096-sample (85 ms) blocks, Hann windowed, and FFT'd; the
/// power spectrum is averaged over every block (Welch's method, no overlap). The loudest bin
/// from 100 to 4000 Hz is taken as inside the filter's passband, and the median power in a
/// window around it is the passband's level ("the flat middle"). Scanning away from that bin
/// each way, the first run of several bins that stays <see cref="EdgeDropDb"/> below the
/// passband level is the edge; a scan that never drops means the filter is wider than this can
/// measure (or there is none). The edges found are checked for giving a dial this receiver can
/// use (<see cref="ReceiverConfig.LowestAudioCentreHz"/> to <see
/// cref="ReceiverConfig.HighestAudioCentreHz"/>) and for the noise not being too uneven to trust
/// (<see cref="MostUnevenDb"/>).</para>
/// <para>Checked on synthetic band-limited noise (a known passband filtered from white noise) in
/// FilterScanTests; a real radio's own noise is rougher, which is why the edge must hold for
/// several bins running and why too-uneven noise is refused rather than guessed at.</para>
/// </remarks>
public static class FilterScan
{
    /// <summary>Samples an FFT block: 4096, about 85 ms at 48 kHz, 11.7 Hz a bin.</summary>
    public const int FftSize = 4096;

    /// <summary>The lowest frequency a filter's edge is looked for, Hz: below this is where DC leakage and rumble live.</summary>
    public const double LowestSearchHz = 100;

    /// <summary>The highest frequency, Hz: above this is above where GB7RDG's signal, or any SSB voice passband, would ever need to reach.</summary>
    public const double HighestSearchHz = 4000;

    /// <summary>How far below the passband's own level counts as the edge, dB.</summary>
    public const double EdgeDropDb = 6;

    /// <summary>How many bins running below the edge threshold it takes to call it the edge, not a dip inside the passband.</summary>
    public const int EdgeRunBins = 4;

    /// <summary>A filter narrower than this still loses frames on a weak, fading path even on the right dial.</summary>
    public const double NarrowWidthHz = 2400;

    /// <summary>How uneven (in dB, standard deviation inside the passband found) the noise can be before it is not trusted.</summary>
    public const double MostUnevenDb = 5;

    private const double BinHz = (double)OnAir.SampleRate / FftSize;

    /// <summary>The dial that centres GB7RDG's signal in a filter from <paramref name="lowHz"/> to <paramref name="highHz"/>, or a problem if it cannot be used.</summary>
    public static (RadioFilter? Filter, string? Problem) DialFor(double lowHz, double highHz)
    {
        if (!(highHz > lowHz) || lowHz < 0)
        {
            return (null, "The low edge must be less than the high edge, and neither can be negative.");
        }
        double middle = (lowHz + highHz) / 2;
        double dialHz = Math.Round((OnAir.TransmitHz - middle) / 10) * 10;
        double centreHz = OnAir.TransmitHz - dialHz;
        if (centreHz < ReceiverConfig.LowestAudioCentreHz || centreHz > ReceiverConfig.HighestAudioCentreHz)
        {
            return (null, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"A filter from {lowHz:F0} to {highHz:F0} Hz needs a dial this receiver cannot use (its centre would be {centreHz:F0} Hz in your audio, outside {ReceiverConfig.LowestAudioCentreHz:F0} to {ReceiverConfig.HighestAudioCentreHz:F0}). Type the edges again, or check the radio."));
        }
        return (new RadioFilter(lowHz, highHz, dialHz / 1000, highHz - lowHz < NarrowWidthHz), null);
    }

    /// <summary>Measures the filter in about 10 s of band noise at 48 kHz. A problem, not a filter, if it could not be measured.</summary>
    public static (RadioFilter? Filter, string? Problem) Analyse(ReadOnlySpan<float> audio)
    {
        if (audio.Length < FftSize * 4)
        {
            return (null, "Not enough audio was captured to measure the filter.");
        }
        double[] db = AveragedSpectrumDb(audio);
        int lowBin = Bin(LowestSearchHz), highBin = Math.Min(Bin(HighestSearchHz), db.Length - 1);
        int peak = lowBin;
        for (int k = lowBin + 1; k <= highBin; k++)
        {
            if (db[k] > db[peak])
            {
                peak = k;
            }
        }
        double plateau = MedianAround(db, peak, lowBin, highBin, spanBins: 10);

        int? lowEdge = ScanForEdge(db, peak, lowBin, plateau, down: true);
        int? highEdge = ScanForEdge(db, peak, highBin, plateau, down: false);
        if (lowEdge is null || highEdge is null)
        {
            return (null, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The audio looks flat from about {LowestSearchHz:F0} to {HighestSearchHz:F0} Hz: either the filter is wider than this can measure, or there is none in the signal path. Type the edges instead, or check the radio is receiving noise with nothing tuned in."));
        }

        double unevenness = StandardDeviation(db, lowEdge.Value, highEdge.Value, plateau);
        if (unevenness > MostUnevenDb)
        {
            return (null, "The noise looks too uneven to measure reliably (a tone or interference, rather than a clean hiss). Type the edges instead, or try again.");
        }

        return DialFor(lowEdge.Value * BinHz, highEdge.Value * BinHz);
    }

    /// <summary>The averaged power spectrum in dB, one bin for each of <see cref="FftSize"/>/2 up to the Nyquist edge, from non-overlapping Hann-windowed blocks.</summary>
    private static double[] AveragedSpectrumDb(ReadOnlySpan<float> audio)
    {
        int bins = FftSize / 2;
        var sum = new double[bins];
        var window = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / FftSize));
        }
        int blocks = audio.Length / FftSize;
        var x = new Complex[FftSize];
        for (int b = 0; b < blocks; b++)
        {
            int start = b * FftSize;
            for (int i = 0; i < FftSize; i++)
            {
                x[i] = audio[start + i] * window[i];
            }
            ChannelMaths.Fft(x, inverse: false);
            for (int k = 0; k < bins; k++)
            {
                sum[k] += ChannelMaths.Norm(x[k]);
            }
        }
        var db = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            db[k] = 10 * Math.Log10(Math.Max(sum[k] / blocks, double.Epsilon));
        }
        return db;
    }

    /// <summary>The median of the bins within <paramref name="spanBins"/> of <paramref name="centre"/>, clamped to <paramref name="lowBin"/>..<paramref name="highBin"/>.</summary>
    private static double MedianAround(double[] db, int centre, int lowBin, int highBin, int spanBins)
    {
        int from = Math.Max(lowBin, centre - spanBins), to = Math.Min(highBin, centre + spanBins);
        var window = new double[to - from + 1];
        for (int k = from; k <= to; k++)
        {
            window[k - from] = db[k];
        }
        Array.Sort(window);
        return window[window.Length / 2];
    }

    /// <summary>
    /// Scans from <paramref name="from"/> towards <paramref name="limit"/> (down or up) for the
    /// first run of <see cref="EdgeRunBins"/> bins all at least <see cref="EdgeDropDb"/> below
    /// <paramref name="plateau"/>: the filter's edge there. Null if the scan reaches
    /// <paramref name="limit"/> without ever finding one (the filter, if there is one, is wider
    /// than this range).
    /// </summary>
    private static int? ScanForEdge(double[] db, int from, int limit, double plateau, bool down)
    {
        double threshold = plateau - EdgeDropDb;
        int step = down ? -1 : 1;
        int run = 0;
        for (int k = from; down ? k >= limit : k <= limit; k += step)
        {
            if (db[k] < threshold)
            {
                run++;
                if (run >= EdgeRunBins)
                {
                    return k + (step * (run - 1));
                }
            }
            else
            {
                run = 0;
            }
        }
        return null;
    }

    /// <summary>The standard deviation of the bins from <paramref name="from"/> to <paramref name="to"/> (inclusive, either order) about <paramref name="plateau"/>, dB.</summary>
    private static double StandardDeviation(double[] db, int from, int to, double plateau)
    {
        int lo = Math.Min(from, to), hi = Math.Max(from, to);
        double sum = 0;
        int n = hi - lo + 1;
        for (int k = lo; k <= hi; k++)
        {
            double d = db[k] - plateau;
            sum += d * d;
        }
        return Math.Sqrt(sum / Math.Max(n, 1));
    }

    private static int Bin(double hz) => (int)Math.Round(hz / BinHz);
}
