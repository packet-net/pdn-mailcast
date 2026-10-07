using System.Numerics;
using System.Runtime.CompilerServices;
using Packet.SoundModem.Audio;

namespace Mailcast.Receiver;

/// <summary>The probe found after a slot's tone, measured: the channel estimates and how clearly it was heard.</summary>
/// <param name="Snapshots">One estimate a period, for <see cref="ChannelAnalysis"/>, with <c>basis: "probe"</c>.</param>
/// <param name="PeakDb">The averaged delay profile's peak over its floor, dB.</param>
/// <param name="Share">The share of its windows in which the strongest arrival stands clear of that window's own floor.</param>
/// <param name="StartSeconds">Where the periods used begin, seconds into the audio handed in.</param>
/// <param name="LagZeroSeconds">
/// When a period of the probe arriving at the estimates' zero delay began, seconds into the audio
/// handed in, give or take whole periods (106.25 ms): add a path's delay in the estimates for
/// when it arrived. Only as good as the audio's own timing.
/// </param>
internal sealed record ProbeMeasurement(ChannelSnapshots Snapshots, double PeakDb, double Share, double StartSeconds, double LagZeroSeconds);

/// <summary>
/// The radio path measured from the channel-sounding probe GB7RDG sends after each slot's tone:
/// pdn-soundmodem's <c>zc255</c>, a Zadoff-Chu sequence of 255 chips at 2400 chips/s repeated 61
/// times, 1.5 s after the tone, on the tone's frequency.
/// </summary>
/// <remarks>
/// <para><b>How.</b> The audio after the tone is brought to complex baseband at 9600 Hz on the
/// tone's own measured frequency. It is cut into windows one period long (1020 samples); in each,
/// any narrowband interference is cut out of the spectrum and the window is circularly correlated
/// with one period of the probe, built by pdn-soundmodem's own <see cref="ProbeSignal"/>. A
/// Zadoff-Chu sequence's periodic autocorrelation is zero away from lag 0, so inside the probe
/// each window gives the path's impulse response directly, the same at every window but for the
/// fading. Windows outside the probe give noise. The run of 58 windows where the profile, each
/// window against its own floor, stands highest is the probe, which needs no timing beyond the
/// tone's end: the probe's first period is its cyclic prefix, and one window each side is left as
/// a margin.</para>
/// <para><b>Then as the bursts.</b> The estimates go to <see cref="ChannelAnalysis"/> with the
/// probe's exact pulse (about a raised cosine at 2400 chips/s, roll-off 0.15) and one snapshot a
/// period (106.25 ms, so Doppler to 4.7 Hz either way). Zadoff-Chu couples delay and Doppler, so
/// each mode is then measured again mixed on its own frequency (<see cref="Analyse"/>), and what
/// little shift is left is taken out at <see cref="DelayPerHz"/>.</para>
/// <para><b>Cost.</b> About 2 s of one core on a Raspberry Pi 4 for a two-mode path, on the
/// channel watch's worker thread.</para>
/// </remarks>
internal static class ProbeChannel
{
    /// <summary>The probe this receiver knows.</summary>
    public static ProbeDescriptor Probe => ProbeSignal.Zc255;

    /// <summary>The basis its estimates carry.</summary>
    public const string Basis = "probe";

    /// <summary>The gap between the tone and the probe the head end asks for, seconds.</summary>
    public const double GapSeconds = 1.5;

    /// <summary>
    /// How far a path's fitted delay moves for each hertz of its Doppler shift against the
    /// frequency mixed at: 24.25 us earlier, measured for root 1 (probe-step1/coupling.txt) and
    /// again here for small shifts. Added back as delay + this x shift. Beyond about half a hertz
    /// the shift also smears the path into shoulders, which is why each mode gets a pass of its own.
    /// </summary>
    public const double DelayPerHz = 24.25e-6;

    /// <summary>
    /// Paths closer than this are one mode: 0.4 ms, under the bursts' 0.6 ms, since the probe
    /// resolves paths a chip (0.42 ms) apart cleanly and modes on short paths can be 0.6 ms apart.
    /// </summary>
    public const double ModeGap = 0.4e-3;

    /// <summary>Windows used: the 60 whole ones inside 61 periods, less one each side.</summary>
    public const int Windows = 58;

    /// <summary>
    /// The averaged profile's peak (each window against its own floor) must stand this far over
    /// its floor. Noise alone reaches under 3 dB in GB7RDG's recordings, and the probe at -15 dB
    /// in 3 kHz about 12 dB. The CW ident's keying with no noise at all can reach 10 dB, but in
    /// only a few windows, which <see cref="FoundShare"/> catches.
    /// </summary>
    public const double FoundDb = 6;

    /// <summary>...and it must stand clear (6 dB over that window's median) in this share of the windows.</summary>
    public const double FoundShare = 0.5;

    private const double ShowingRatio = 4;

    /// <summary>The estimates' delays, from this long before the strongest arrival...</summary>
    private const double BeforeSeconds = 5e-3;

    /// <summary>...to this long after it.</summary>
    private const double AfterSeconds = 11e-3;

    /// <summary>
    /// Paths are looked for this far before the strongest: a fading first hop can be weaker than
    /// the second for the whole probe, and the bursts' 1.5 ms would miss it 2 ms early.
    /// </summary>
    private const double SearchBefore = 3.5e-3;

    /// <summary>A path is kept 4 dB over the floor: averaged over 58 periods the floor is steady, and noise alone reaches under 3 dB.</summary>
    private const double FloorMargin = 4;

    /// <summary>
    /// How much earlier than where the tone's end puts it the probe is looked for, seconds: the
    /// tone detector's blocks are just over a second, so the tone may have gone on into the next.
    /// </summary>
    public const double SearchEarlierSeconds = 1.5;

    /// <summary>
    /// How much later, seconds: a weak tone's last blocks can fail the detector, which then puts
    /// its end a block or two early.
    /// </summary>
    public const double SearchLaterSeconds = 3;

    private const int Rate = ChannelMaths.Rate;

    private const int Decimation = ChannelMaths.AudioRate / Rate;

    /// <summary>Samples a period at <see cref="Rate"/>: 1020.</summary>
    public static readonly int Period = (int)Math.Round(Probe.SequenceLength * Rate / Probe.ChipRate);

    private static readonly Lazy<Reference> Ref = new(BuildReference);

    /// <summary>
    /// Measures the probe in <paramref name="audio"/>, 48 kHz audio from somewhere before the
    /// tone ended to somewhere after the probe. <paramref name="toneEndSeconds"/> is where the
    /// tone detector put the tone's end, seconds into the audio, and <paramref name="toneHz"/>
    /// its measured frequency. Null when no probe was found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ProbeMeasurement? Measure(ReadOnlySpan<float> audio, double toneEndSeconds, double toneHz)
    {
        var reference = Ref.Value;
        int L = Period;
        var bb = ChannelMaths.ToBaseband(audio);
        // On the tone's own frequency: the probe goes out on it.
        double w = -2 * Math.PI * (toneHz - OnAir.CentreAudioHz) / Rate;
        for (int n = 0; n < bb.Length; n++)
        {
            bb[n] *= Complex.FromPolarCoordinates(1, w * n);
        }
        int count = bb.Length / L;
        double expected = (toneEndSeconds + GapSeconds) * Rate;
        // Runs whose first whole window could be the probe's: windows from first + 1 to first + Windows.
        int lowest = Math.Max(0, (int)Math.Floor((expected - (SearchEarlierSeconds * Rate)) / L));
        int highest = Math.Min(count - Windows - 2, (int)Math.Ceiling((expected + (SearchLaterSeconds * Rate)) / L));
        if (highest < lowest)
        {
            return null;
        }
        int from = lowest + 1, to = highest + Windows;
        // Each window's correlation, as power, and the correlations kept for the run chosen.
        var c = new Complex[to - from + 1][];
        var p = new double[c.Length][];
        var pn = new double[c.Length][];
        var buffer = new Complex[L];
        var scratchBins = new double[reference.InBand.Length];
        for (int k = from; k <= to; k++)
        {
            Array.Copy(bb, k * L, buffer, 0, L);
            var x = reference.Dft.Forward(buffer);
            Excise(x, reference.InBand, scratchBins);
            for (int i = 0; i < L; i++)
            {
                x[i] *= reference.Conjugate[i];
            }
            var corr = reference.Dft.Inverse(x);
            var power = new double[L];
            for (int i = 0; i < L; i++)
            {
                corr[i] /= reference.Energy;
                power[i] = ChannelMaths.Norm(corr[i]);
            }
            c[k - from] = corr;
            p[k - from] = power;
            // Each window against its own floor, so a run is chosen by where the probe is and not
            // by where the noise happens to be quieter.
            double floorHere = Math.Max(ChannelMaths.Median(power), double.Epsilon);
            pn[k - from] = [.. power.Select(v => v / floorHere)];
        }
        // The run whose summed profile, each window against its own floor, stands highest over its median.
        var sum = new double[L];
        for (int k = lowest + 1; k <= lowest + Windows; k++)
        {
            Add(sum, pn[k - from], 1);
        }
        double bestRatio = 0;
        int bestRun = -1, bestLag = 0;
        var scratch = new double[L];
        for (int run = lowest; run <= highest; run++)
        {
            if (run > lowest)
            {
                Add(sum, pn[run - from], -1);
                Add(sum, pn[run + Windows - from], 1);
            }
            int peak = ArgMax(sum);
            Array.Copy(sum, scratch, L);
            double median = ChannelMaths.Median(scratch);
            double ratio = median > 0 ? sum[peak] / median : 0;
            if (ratio > bestRatio)
            {
                (bestRatio, bestRun, bestLag) = (ratio, run, peak);
            }
        }
        double peakDb = 10 * Math.Log10(Math.Max(bestRatio, 1e-30));
        if (bestRun < 0)
        {
            return null;
        }
        // The probe is in every window; a burst of interference (the CW ident's keying) is in a few.
        int showing = 0;
        for (int k = bestRun + 1; k <= bestRun + Windows; k++)
        {
            var window = p[k - from];
            double near = 0;
            for (int i = -3; i <= 3; i++)
            {
                near = Math.Max(near, window[Wrap(bestLag + i, L)]);
            }
            if (near > ShowingRatio * ChannelMaths.Median(window))
            {
                showing++;
            }
        }
        double share = showing / (double)Windows;
        if (peakDb < FoundDb || share < FoundShare)
        {
            return null;
        }

        // The averaged profile of the run, for the signal to noise.
        var pdp = new double[L];
        for (int k = bestRun + 1; k <= bestRun + Windows; k++)
        {
            Add(pdp, p[k - from], 1.0 / Windows);
        }
        Array.Copy(pdp, scratch, L);
        double floor = ChannelMaths.Median(scratch);
        int before = (int)Math.Round(BeforeSeconds * Rate), after = (int)Math.Round(AfterSeconds * Rate);
        double paths = 0;
        for (int i = -(int)Math.Round((SearchBefore + 0.1e-3) * Rate); i <= (int)Math.Round(6.5e-3 * Rate); i++)
        {
            paths += pdp[Wrap(bestLag + i, L)] - floor;
        }
        paths /= reference.PulseEnergy;
        double snr = floor > 0 && paths > 0 ? (10 * Math.Log10(paths / floor)) - reference.GainDb : double.NaN;

        // The estimates around the strongest arrival, one per window, and a window spoilt by a
        // crash (far more power than the rest) left out.
        var estimates = new Complex[Windows][];
        var windowPower = new double[Windows];
        for (int j = 0; j < Windows; j++)
        {
            int k = bestRun + 1 + j;
            var row = new Complex[before + after + 1];
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = c[k - from][Wrap(bestLag - before + i, L)];
            }
            estimates[j] = row;
            double e = 0;
            for (int n = 0; n < L; n++)
            {
                e += ChannelMaths.Norm(bb[(k * L) + n]);
            }
            windowPower[j] = e;
        }
        double typical = ChannelMaths.Median([.. windowPower]);
        var good = windowPower.Select(e => e <= 6 * typical).ToArray();
        return new ProbeMeasurement(
            new ChannelSnapshots
            {
                Basis = Basis,
                FirstLagSeconds = -before / (double)Rate,
                LagStepSeconds = 1.0 / Rate,
                Estimates = estimates,
                Good = good,
                SnapshotSeconds = L / (double)Rate,
                SymbolRate = Probe.ChipRate,
                RollOff = Probe.RollOff,
                SnrDb = snr,
                OffsetHz = toneHz - OnAir.CentreAudioHz,
                DelayPerHzSeconds = DelayPerHz,
                Pulse = reference.Pulse,
                FinerDelays = true,
                SearchBeforeSeconds = SearchBefore,
                FloorMarginDb = FloorMargin,
            },
            peakDb,
            share,
            (bestRun + 1) * L / (double)Rate,
            (((bestRun + 1) * L) + bestLag) / (double)Rate);
    }

    /// <summary>
    /// Measures the probe and analyses it, a pass for each mode. A Zadoff-Chu probe couples delay
    /// and Doppler: a mode heard off the frequency mixed at comes out displaced and smeared into
    /// weaker shoulders about a chip either side, which the fit takes for extra paths. So the
    /// first pass, mixed on the tone, finds the modes and their Doppler shifts; then each mode is
    /// measured again mixed on its own frequency, where it is clean, and its delay taken from
    /// there. A mode not found again on its own frequency was another mode's shoulder and is
    /// dropped. What little shift is left (the second pass's own) is taken out with
    /// <see cref="DelayPerHz"/>. Null when no probe was found.
    /// </summary>
    public static (PathPicture Picture, ProbeMeasurement Measurement)? Analyse(ReadOnlySpan<float> audio, double toneEndSeconds, double toneHz)
    {
        if (Measure(audio, toneEndSeconds, toneHz) is not { } first || ChannelAnalysis.Analyse(first.Snapshots) is not { } rough)
        {
            return null;
        }
        double period = Period / (double)Rate;
        double At(ProbeMeasurement m, PathPicture p, double ms) => m.LagZeroSeconds + p.FirstPathSeconds + (ms / 1000);
        double Near(double t, double to) => to + ((t - to) - (Math.Round((t - to) / period) * period));

        // The first pass groups paths as the bursts' analysis does (0.6 ms), which keeps a mode's
        // shoulders with it. Each mode is then measured on its own frequency, and its paths taken
        // from there: those within its extent (and a little either side) and within 10 dB of the
        // strongest of them. The shoulders are gone on the mode's own frequency, so they are left
        // behind; two modes closer than 0.6 ms are told apart again below.
        var groups = Enumerable.Range(0, rough.Modes.Count)
            .Select(m => Enumerable.Range(0, rough.PathsMs.Count).Where(i => rough.PathModes[i] == m).OrderBy(i => rough.PathsMs[i]).ToList())
            .ToList();
        double origin = At(first, rough, 0);
        var passes = new Dictionary<double, (ProbeMeasurement, PathPicture)?> { [0] = (first, rough) };
        var own = new (ProbeMeasurement Measurement, PathPicture Picture)[rough.Modes.Count];
        var window = new (double Lo, double Hi)[rough.Modes.Count];
        for (int m = 0; m < rough.Modes.Count; m++)
        {
            var mode = rough.Modes[m];
            window[m] = groups[m].Count == 0 ? (double.NaN, double.NaN)
                : (At(first, rough, rough.PathsMs[groups[m][0]]) - Margin, At(first, rough, rough.PathsMs[groups[m][^1]]) + Margin);
            double shift = mode.CentroidHz is double c && Math.Abs(c) <= 4 && Math.Abs(c) >= 0.01 ? Math.Round(c, 3) : 0;
            if (!passes.TryGetValue(shift, out var pass))
            {
                pass = Measure(audio, toneEndSeconds, toneHz + shift) is { } again && ChannelAnalysis.Analyse(again.Snapshots) is { } picture ? (again, picture) : null;
                passes[shift] = pass;
            }
            own[m] = pass ?? (first, rough);
        }
        List<(double At, double Db)> Inside(int pass, int m) =>
            [.. Enumerable.Range(0, own[pass].Picture.PathsMs.Count)
                .Select(i => (At: Near(At(own[pass].Measurement, own[pass].Picture, own[pass].Picture.PathsMs[i]), origin), Db: own[pass].Picture.PathsDb[i]))
                .Where(x => x.At >= window[m].Lo && x.At <= window[m].Hi)];

        var paths = new List<(double At, double Db, PictureMode From)>();
        for (int m = 0; m < rough.Modes.Count; m++)
        {
            var mode = rough.Modes[m];
            var inside = groups[m].Count == 0 ? [] : Inside(m, m);
            if (inside.Count == 0)
            {
                // Nothing there on its own frequency: it was another mode's shoulder.
                continue;
            }
            double top = inside.Max(x => x.Db);
            paths.AddRange(inside.Where(x => x.Db >= top - KeptDb).Select(x => (x.At, x.Db - top + mode.PowerDb, mode)));
        }
        if (paths.Count == 0)
        {
            return (rough, first);
        }

        // The paths into modes again, with the probe's finer gap, each mode's Doppler from the
        // first pass's mode its strongest path came from.
        paths = [.. paths.OrderBy(x => x.At)];
        double earliest = paths[0].At;
        var found = new List<(List<int> Members, double Delay, double Power, PictureMode From)>();
        foreach (var g in Groups([.. paths.Select(x => (x.At - earliest) * 1000)], ModeGap * 1000))
        {
            var power = g.Select(i => Math.Pow(10, paths[i].Db / 10)).ToArray();
            double sum = power.Sum();
            double delay = g.Select((i, k) => paths[i].At * power[k]).Sum() / sum;
            found.Add((g, delay, 10 * Math.Log10(sum), paths[g[Array.IndexOf(power, power.Max())]].From));
        }
        // Too faint to tell from a stronger mode's echo: left out, with its paths.
        double strongest = found.Max(x => x.Power);
        found = [.. found.Where(x => x.Power - strongest >= WeakestModeDb)];
        paths = [.. found.SelectMany(x => x.Members).Order().Select(i => paths[i])];
        earliest = paths[0].At;
        var modes = found.Select(x => x.From with { DelayMs = (x.Delay - earliest) * 1000, PowerDb = x.Power - strongest }).ToList();
        double topPath = paths.Max(x => x.Db);
        var weights = paths.Select(x => Math.Pow(10, x.Db / 10)).ToArray();
        double total = weights.Sum();
        double mean = paths.Select((x, i) => x.At * weights[i]).Sum() / total;
        double rms = Math.Sqrt(paths.Select((x, i) => weights[i] * (x.At - mean) * (x.At - mean)).Sum() / total);
        var result = rough with
        {
            Modes = modes,
            PathsMs = [.. paths.Select(x => (x.At - earliest) * 1000)],
            PathsDb = [.. paths.Select(x => x.Db - topPath)],
            DelaySpreadMs = rms * 1000,
            FirstPathSeconds = earliest - first.LagZeroSeconds,
        };
        return (result, first);
    }

    /// <summary>How far either side of a mode's paths in the first pass its own pass is searched, seconds.</summary>
    private const double Margin = 0.15e-3;

    /// <summary>
    /// The weakest mode reported against the strongest, dB. A fading mode leaves faint echoes of
    /// itself about half a millisecond either side, some 25 dB down, that no pass removes; a
    /// real third hop has been seen at -22 dB (EI4HQ, 2026-10-05).
    /// </summary>
    public const double WeakestModeDb = -25;

    /// <summary>A mode's own-pass paths are kept if within this of its strongest, dB: the smear a fast-fading mode leaves either side of itself is weaker.</summary>
    private const double KeptDb = 10;

    /// <summary>Delays in ms, sorted, into runs with gaps under <paramref name="gapMs"/>: the analysis's own grouping.</summary>
    private static List<List<int>> Groups(IReadOnlyList<double> sortedMs, double gapMs)
    {
        var groups = new List<List<int>>();
        for (int i = 0; i < sortedMs.Count; i++)
        {
            if (groups.Count > 0 && sortedMs[i] - sortedMs[groups[^1][^1]] < gapMs)
            {
                groups[^1].Add(i);
            }
            else
            {
                groups.Add([i]);
            }
        }
        return groups;
    }

    /// <summary>
    /// Takes out a window's narrowband interference before it is correlated: every bin of the
    /// probe's band holding more than <see cref="ExciseRatio"/> times the band's median power is
    /// zeroed. The probe fills its band evenly, so it loses nothing; a carrier (the tone's tail,
    /// the CW ident, a birdie) would otherwise correlate with the Zadoff-Chu sequence, a chirp, as
    /// a sharp path at the delay where the chirp passes its frequency.
    /// </summary>
    private static void Excise(Complex[] x, int[] inBand, double[] power)
    {
        for (int i = 0; i < inBand.Length; i++)
        {
            power[i] = ChannelMaths.Norm(x[inBand[i]]);
        }
        double median = ChannelMaths.Median(power);
        double limit = median * ExciseRatio;
        foreach (int k in inBand)
        {
            if (ChannelMaths.Norm(x[k]) > limit)
            {
                x[k] = Complex.Zero;
            }
        }
    }

    /// <summary>A bin this many times the band's median power is interference: 10 dB, well clear of a fading probe's own unevenness.</summary>
    private const double ExciseRatio = 10;

    private static int Wrap(int i, int n) => ((i % n) + n) % n;

    private static void Add(double[] into, double[] what, double scale)
    {
        for (int i = 0; i < into.Length; i++)
        {
            into[i] += scale * what[i];
        }
    }

    private static int ArgMax(double[] x)
    {
        int best = 0;
        for (int i = 1; i < x.Length; i++)
        {
            if (x[i] > x[best])
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>One period of the probe at 9600 Hz, its transform, and the figures the measurement needs.</summary>
    private sealed record Reference(Bluestein Dft, Complex[] Conjugate, double Energy, double PulseEnergy, double GainDb, Func<double, double> Pulse, int[] InBand);

    private static Reference BuildReference()
    {
        var probe = Probe;
        // pdn-soundmodem's own envelope at 48 kHz, one period from its first, every fifth sample:
        // the envelope holds nothing above 1380 Hz, so it needs no filtering first.
        var envelope = ProbeSignal.Envelope(probe, ChannelMaths.AudioRate);
        int first = probe.FirstPeriodSample(ChannelMaths.AudioRate);
        int L = Period;
        var period = new Complex[L];
        double energy = 0;
        for (int n = 0; n < L; n++)
        {
            period[n] = envelope[first + (n * Decimation)];
            energy += ChannelMaths.Norm(period[n]);
        }
        var dft = new Bluestein(L);
        var transform = dft.Forward(period);
        var conjugate = transform.Select(Complex.Conjugate).ToArray();
        // What one path looks like after the correlation: the period's own circular
        // autocorrelation, worked out finely (about 0.8 us apart) from its power spectrum. It is
        // the raised cosine but for the pulse's truncation, which the fit would otherwise take for
        // weak paths beside a strong one.
        const int Fine = 1 << 17;
        var spectrum = new Complex[Fine];
        for (int k = 0; k < L; k++)
        {
            int signed = k <= L / 2 ? k : k - L;
            spectrum[((signed % Fine) + Fine) % Fine] = ChannelMaths.Norm(transform[k]);
        }
        ChannelMaths.Fft(spectrum, true);
        var table = new double[Fine];
        double zero = spectrum[0].Real;
        for (int i = 0; i < Fine; i++)
        {
            table[i] = spectrum[i].Real / zero;
        }
        double perSecond = Fine / (L / (double)Rate);
        double Pulse(double seconds)
        {
            double at = seconds * perSecond;
            double floorAt = Math.Floor(at);
            int i = (int)(((long)floorAt % Fine + Fine) % Fine);
            double frac = at - floorAt;
            return (table[i] * (1 - frac)) + (table[(i + 1) % Fine] * frac);
        }
        // A unit path's response summed in power over its lags, to turn the profile's area into path power.
        double pulse = 0;
        for (int m = -L / 2; m < L / 2; m++)
        {
            double v = Pulse(m / (double)Rate);
            pulse += v * v;
        }
        // The correlation's gain over the noise in 3 kHz: a period of samples at 9600 Hz against 3 kHz.
        double gain = 10 * Math.Log10(L * 3000.0 / Rate);
        // The bins the probe's band covers (its spectrum is flat there, falling away past 1200 Hz).
        double binHz = Rate / (double)L;
        int[] inBand = [.. Enumerable.Range(0, L).Where(k => Math.Abs((k <= L / 2 ? k : k - L) * binHz) <= probe.ChipRate * (1 - probe.RollOff) / 2)];
        return new Reference(dft, conjugate, energy, pulse, gain, Pulse, inBand);
    }

    /// <summary>A DFT of any length, by Bluestein's chirp z-transform over the power-of-two FFT.</summary>
    internal sealed class Bluestein
    {
        private readonly int _n;
        private readonly int _m;
        private readonly Complex[] _chirp;
        private readonly Complex[] _filter;

        public Bluestein(int n)
        {
            _n = n;
            _m = 1;
            while (_m < (2 * n) - 1)
            {
                _m <<= 1;
            }
            _chirp = new Complex[n];
            for (int k = 0; k < n; k++)
            {
                // k^2 reduced mod 2n before it becomes an angle, so the phase stays exact.
                long q = ((long)k * k) % (2L * n);
                _chirp[k] = Complex.FromPolarCoordinates(1, -Math.PI * q / n);
            }
            _filter = new Complex[_m];
            _filter[0] = Complex.Conjugate(_chirp[0]);
            for (int k = 1; k < n; k++)
            {
                _filter[k] = _filter[_m - k] = Complex.Conjugate(_chirp[k]);
            }
            ChannelMaths.Fft(_filter, false);
        }

        /// <summary>The forward transform, X[k] = sum x[n] exp(-j 2 pi n k / N), as a new array.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public Complex[] Forward(Complex[] x)
        {
            var a = new Complex[_m];
            for (int k = 0; k < _n; k++)
            {
                a[k] = x[k] * _chirp[k];
            }
            ChannelMaths.Fft(a, false);
            for (int i = 0; i < _m; i++)
            {
                a[i] *= _filter[i];
            }
            ChannelMaths.Fft(a, true);
            var y = new Complex[_n];
            for (int k = 0; k < _n; k++)
            {
                y[k] = a[k] * _chirp[k];
            }
            return y;
        }

        /// <summary>The inverse, scaled by 1/N, as a new array.</summary>
        public Complex[] Inverse(Complex[] x)
        {
            var conj = new Complex[_n];
            for (int k = 0; k < _n; k++)
            {
                conj[k] = Complex.Conjugate(x[k]);
            }
            var y = Forward(conj);
            for (int k = 0; k < _n; k++)
            {
                y[k] = Complex.Conjugate(y[k]) / _n;
            }
            return y;
        }
    }
}
