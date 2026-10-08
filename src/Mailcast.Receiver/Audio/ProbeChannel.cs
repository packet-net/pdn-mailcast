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

    /// <summary>
    /// The shortest unbroken run of periods a Doppler spread is given for: 40, 4.25 s, whose own
    /// spectral width (about 0.14 Hz, which is taken out) leaves a spread of 0.1 Hz still to be
    /// told from none. A fade does not break the run, however deep; a stretch where the probe was
    /// not there does (<see cref="Present"/>), and when no run is left long enough the spread is
    /// not given, only the shift.
    /// </summary>
    public const int FewestDopplerPeriods = 40;

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
    public static ProbeMeasurement? Measure(ReadOnlySpan<float> audio, double toneEndSeconds, double toneHz) =>
        Measure(ChannelMaths.ToBaseband(audio), toneEndSeconds, toneHz);

    /// <summary>As <see cref="Measure(ReadOnlySpan{float}, double, double)"/>, from the audio already at baseband (<see cref="ChannelMaths.ToBaseband"/>), which is left as it is.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ProbeMeasurement? Measure(Complex[] baseband, double toneEndSeconds, double toneHz)
    {
        var reference = Ref.Value;
        int L = Period;
        int count = baseband.Length / L;
        double expected = (toneEndSeconds + GapSeconds) * Rate;
        // Runs whose first whole window could be the probe's: windows from first + 1 to first + Windows.
        int lowest = Math.Max(0, (int)Math.Floor((expected - (SearchEarlierSeconds * Rate)) / L));
        int highest = Math.Min(count - Windows - 2, (int)Math.Ceiling((expected + (SearchLaterSeconds * Rate)) / L));
        if (highest < lowest)
        {
            return null;
        }
        int from = lowest + 1, to = highest + Windows;
        int windows = to - from + 1;
        // The arrays are the thread's own, kept from one measurement to the next so a Pi's
        // collector is not kept busy; nothing returned refers to them.
        var ws = Workspace.For(baseband.Length, windows, L, reference.Dft);
        var bb = ws.Mixed;
        // On the tone's own frequency: the probe goes out on it.
        double w = -2 * Math.PI * (toneHz - OnAir.CentreAudioHz) / Rate;
        for (int n = 0; n < baseband.Length; n++)
        {
            bb[n] = baseband[n] * Complex.FromPolarCoordinates(1, w * n);
        }
        // Each window's correlation, and its power against the window's own floor, so a run is
        // chosen by where the probe is and not by where the noise happens to be quieter.
        var c = ws.Correlation;
        var pn = ws.Normalised;
        var floors = new double[windows];
        var bandPower = new double[windows];
        var cuts = new int[windows][];
        var removed = new List<int>();
        var x = ws.Bins;
        var scratchBins = new double[reference.InBand.Length];
        var scratch = new double[L];
        for (int k = from; k <= to; k++)
        {
            reference.Dft.Forward(bb.AsSpan(k * L, L), x, ws.Work);
            removed.Clear();
            Excise(x, reference.InBand, reference.InBandWeight, scratchBins, removed);
            cuts[k - from] = removed.Count == 0 ? [] : [.. removed];
            // What was received in the probe's band, carriers taken out: a probe gone leaves the
            // noise alone there, or silence, where one faded leaves it over the noise.
            double received = 0;
            foreach (int bin in reference.InBand)
            {
                received += ChannelMaths.Norm(x[bin]);
            }
            bandPower[k - from] = received;
            for (int i = 0; i < L; i++)
            {
                x[i] *= reference.Conjugate[i];
            }
            var corr = c[k - from];
            reference.Dft.Inverse(x, corr, ws.Work);
            var norm = pn[k - from];
            for (int i = 0; i < L; i++)
            {
                corr[i] /= reference.Energy;
                norm[i] = ChannelMaths.Norm(corr[i]);
            }
            double floorHere = Math.Max(MedianOf(norm, scratch), double.Epsilon);
            floors[k - from] = floorHere;
            for (int i = 0; i < L; i++)
            {
                norm[i] /= floorHere;
            }
        }
        // The run whose summed profile, each window against its own floor, stands highest over its median.
        var sum = new double[L];
        for (int k = lowest + 1; k <= lowest + Windows; k++)
        {
            Add(sum, pn[k - from], 1);
        }
        double bestRatio = 0;
        int bestRun = -1, bestLag = 0;
        for (int run = lowest; run <= highest; run++)
        {
            if (run > lowest)
            {
                Add(sum, pn[run - from], -1);
                Add(sum, pn[run + Windows - from], 1);
            }
            int peak = ArgMax(sum);
            double median = MedianOf(sum, scratch);
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
        // The run's windows among all of them, and the rest, outside it.
        int runStart = bestRun + 1 - from;
        bool Outside(int i) => i < runStart || i >= runStart + Windows;
        // Whether the probe is in each window, from its own correlation: the excess over that
        // window's floor where the paths are, in units of the floor.
        // Weighted by the run's own profile there, so what counts is power where the paths are,
        // not interference spread over every delay (a carrier's leftovers, the tone's tail).
        int regionBefore = (int)Math.Round((SearchBefore + 0.1e-3) * Rate), regionAfter = (int)Math.Round(6.5e-3 * Rate);
        int region = regionBefore + regionAfter + 1;
        // Each window's power over its floor as a mean (noise's power is exponential, its median ln 2 of its mean), less one.
        double Excess(int index, int i) => (pn[index][Wrap(bestLag + i, L)] * Math.Log(2)) - 1;
        var shape = new double[region];
        for (int i = 0; i < region; i++)
        {
            double mean = 0;
            for (int j = 0; j < Windows; j++)
            {
                mean += Excess(runStart + j, i - regionBefore);
            }
            shape[i] = Math.Max(mean / Windows, 0);
        }
        double shapeSum = shape.Sum();
        if (!(shapeSum > 0))
        {
            return null;
        }
        for (int i = 0; i < region; i++)
        {
            shape[i] /= shapeSum;
        }
        // Scored over every window, not just the run's, so a stretch without the probe is seen
        // to run on past the run's end (a Stop) or not.
        var score = new double[windows];
        for (int j = 0; j < windows; j++)
        {
            for (int i = 0; i < region; i++)
            {
                score[j] += shape[i] * Excess(j, i - regionBefore);
            }
        }
        // The noise in the probe's band, from the windows outside the run (the gap before the
        // probe, the time after it), the quieter of them: those after it may hold the bursts.
        var outside = Enumerable.Range(0, windows).Where(Outside).Select(i => bandPower[i]).Order().ToArray();
        double noise = outside.Length >= 4 ? outside[outside.Length / 4] : double.NaN;
        // How much noise alone moves the score: neighbouring lags are not independent, so it is
        // more than the weights alone say. Measured on the windows outside the run with only
        // noise in them, and never taken as less than the weights say.
        double sigma = Math.Sqrt(shape.Sum(v => v * v));
        double[] quietScores = [.. Enumerable.Range(0, windows).Where(i => Outside(i) && bandPower[i] <= QuietRatio * noise).Select(i => score[i])];
        if (quietScores.Length >= 8)
        {
            double middle = ChannelMaths.Median(quietScores);
            sigma = Math.Max(sigma, 1.4826 * ChannelMaths.Median([.. quietScores.Select(v => Math.Abs(v - middle))]));
        }
        var present = Present(score, sigma, bandPower, noise, runStart, Windows);

        // A burst of interference (the CW ident's keying) shows in a few windows only.
        int showing = 0;
        for (int j = runStart; j < runStart + Windows; j++)
        {
            // Against the window's own floor, which pn is already over.
            var window = pn[j];
            double near = 0;
            for (int i = -3; i <= 3; i++)
            {
                near = Math.Max(near, window[Wrap(bestLag + i, L)]);
            }
            if (near > ShowingRatio)
            {
                showing++;
            }
        }
        double share = showing / (double)Windows;
        int presentCount = present.Count(x => x);
        if (peakDb < FoundDb || share < FoundShare || presentCount < ChannelAnalysis.FewestSnapshots)
        {
            // Not there, or too little of it: a probe cut short (a Stop at the station) gives up
            // here rather than counting its silence as a channel that faded away.
            return null;
        }

        // The averaged profile of the windows with the probe in them, for the signal to noise.
        var pdp = new double[L];
        for (int j = 0; j < Windows; j++)
        {
            if (present[j])
            {
                Add(pdp, pn[runStart + j], floors[runStart + j] / presentCount);
            }
        }
        double floor = MedianOf(pdp, scratch);
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
        // Only the span the probe was in: a cut-short probe's silence after it is no part of the
        // record, so the Doppler spectrum and the fades are worked out over what was sent.
        int firstIn = Array.IndexOf(present, true), lastIn = Array.LastIndexOf(present, true);
        int span = lastIn - firstIn + 1;
        var estimates = new Complex[span][];
        var windowPower = new double[span];
        for (int j = 0; j < span; j++)
        {
            int k = bestRun + 1 + firstIn + j;
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
        double typical = ChannelMaths.Median([.. windowPower.Where((_, j) => present[firstIn + j])]);
        var good = windowPower.Select((e, j) => present[firstIn + j] && e <= 6 * typical).ToArray();
        // The probe gone, as against a window spoilt by a crash: a gap the Doppler run is never carried across.
        var missing = Enumerable.Range(0, span).Select(j => !present[firstIn + j]).ToArray();
        if (good.Count(g => g) < ChannelAnalysis.FewestSnapshots)
        {
            return null;
        }
        // The pulse as these windows measured it: with any bins the carrier cut took out, in the
        // share of windows it took them from, so the notch is not fitted as extra paths.
        var keep = new double[L];
        Array.Fill(keep, 1.0);
        int goodCount = good.Count(g => g);
        bool notched = false;
        for (int j = 0; j < span; j++)
        {
            if (good[j])
            {
                foreach (int bin in cuts[bestRun + 1 + firstIn + j - from])
                {
                    keep[bin] -= 1.0 / goodCount;
                    notched = true;
                }
            }
        }
        var pulse = notched ? PulseOf(reference.Transform, keep) : reference.Pulse;
        return new ProbeMeasurement(
            new ChannelSnapshots
            {
                Basis = Basis,
                FirstLagSeconds = -before / (double)Rate,
                LagStepSeconds = 1.0 / Rate,
                Estimates = estimates,
                Good = good,
                Missing = missing,
                SnapshotSeconds = L / (double)Rate,
                SymbolRate = Probe.ChipRate,
                RollOff = Probe.RollOff,
                SnrDb = snr,
                OffsetHz = toneHz - OnAir.CentreAudioHz,
                DelayPerHzSeconds = DelayPerHz,
                Pulse = pulse,
                ContiguousDoppler = true,
                FewestDopplerSnapshots = FewestDopplerPeriods,
                FinerDelays = true,
                SearchBeforeSeconds = SearchBefore,
                FloorMarginDb = FloorMargin,
            },
            peakDb,
            share,
            (bestRun + 1 + firstIn) * L / (double)Rate,
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
        // At baseband once, and mixed again on each mode's own frequency from there.
        var baseband = ChannelMaths.ToBaseband(audio);
        if (Measure(baseband, toneEndSeconds, toneHz) is not { } first || ChannelAnalysis.Analyse(first.Snapshots) is not { } rough)
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
                pass = Measure(baseband, toneEndSeconds, toneHz + shift) is { } again && ChannelAnalysis.Analyse(again.Snapshots) is { } picture ? (again, picture) : null;
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
        var modeOf = found.SelectMany((x, m) => x.Members.Select(i => (Path: i, Mode: m))).OrderBy(x => x.Path).ToList();
        paths = [.. modeOf.Select(x => paths[x.Path])];
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
            PathModes = [.. modeOf.Select(x => x.Mode)],
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
    /// Which windows of the run (<paramref name="runLength"/> of them from <paramref name="runStart"/>
    /// among all the windows given) the probe is in. A fade is the channel and stays in, however
    /// deep; only a probe that is not there at all is left out: one cut short by a Stop at the
    /// station, a stretch of audio lost, or the noise beyond the probe where the run overhangs it.
    /// </summary>
    /// <remarks>
    /// <para>A window is empty when what it received in the probe's band (<paramref name="power"/>)
    /// is down at the <paramref name="noise"/> measured outside the probe, or below it, and its
    /// correlation <paramref name="score"/> (the excess over its floor where the paths are,
    /// weighted by the run's profile, noise's standard deviation <paramref name="sigma"/>) is no
    /// more than noise alone gives. One loud with something else is not empty: the probe may be
    /// under it. A window is never left out for being weaker than the rest.</para>
    /// <para>A stretch of empty windows is the probe gone when it runs on past either end of the
    /// run (the probe stopped, or had not begun); when it is silent, well under the noise (lost
    /// audio, which a fade, leaving the noise, cannot be); or when it is two windows or more and
    /// the probe stands at full strength (<see cref="FullSigmas"/>) within two windows of it on
    /// both sides: so sudden a drop is a gap, where a fade comes and goes over a few windows. A
    /// probe too weak to stand that clear can fade into the noise just as it would drop out, and
    /// a stretch inside the run is then kept as a fade.</para>
    /// <para>The window either side of a gap straddles its edge and goes too, after any next to it
    /// with only a trace of the probe (<see cref="FaintShare"/>: a later path carries it a little
    /// way past the edge). Without a measure of the noise (too few windows outside the run), only
    /// the correlation says.</para>
    /// </remarks>
    internal static bool[] Present(double[] score, double sigma, double[] power, double noise, int runStart, int runLength)
    {
        int n = score.Length;
        bool known = double.IsFinite(noise) && noise >= 0;
        bool Quiet(int j) => !known || power[j] <= QuietRatio * noise;
        var empty = new bool[n];
        for (int j = 0; j < n; j++)
        {
            empty[j] = score[j] < AbsentSigmas * sigma && Quiet(j);
        }
        // The run's typical window where the probe is in it.
        double typical = ChannelMaths.Median(Enumerable.Range(runStart, runLength).Where(j => !empty[j]).Select(j => score[j]));
        // Noise now and then scores a little over the test in one window of many: a lone window
        // between two empty ones with no more than twice it, and well under the probe's typical
        // window (so never a weak probe's own), is empty with them.
        double lonely = double.IsFinite(typical) ? Math.Min(2 * AbsentSigmas * sigma, LoneShare * typical) : 0;
        var lone = new bool[n];
        for (int j = 1; j < n - 1; j++)
        {
            lone[j] = !empty[j] && empty[j - 1] && empty[j + 1] && score[j] < lonely && Quiet(j);
        }
        bool Empty(int j) => empty[j] || lone[j];
        // A trace of the probe: 20 dB or more under the run's typical window.
        double faint = double.IsFinite(typical) ? Math.Max(AbsentSigmas * sigma, FaintShare * typical) : AbsentSigmas * sigma;
        bool Faint(int j) => score[j] < faint && Quiet(j);
        bool Full(int j) => j >= 0 && j < n && score[j] >= FullSigmas * sigma;
        var absent = new bool[n];
        for (int j = 0; j < n;)
        {
            if (!Empty(j))
            {
                j++;
                continue;
            }
            int start = j;
            double received = 0;
            for (; j < n && Empty(j); j++)
            {
                received += power[j];
            }
            bool past = start == 0 || start < runStart || j == n || j > runStart + runLength;
            bool silent = known && received / (j - start) <= SilentRatio * noise;
            bool sudden = j - start >= 2
                && (Full(start - 1) || Full(start - 2))
                && (Full(j) || Full(j + 1));
            if (!(past || silent || sudden))
            {
                continue;
            }
            int lo = start, hi = j;
            while (lo > 0 && Faint(lo - 1))
            {
                lo--;
            }
            while (hi < n && Faint(hi))
            {
                hi++;
            }
            for (int i = Math.Max(0, lo - 1); i <= Math.Min(n - 1, hi); i++)
            {
                absent[i] = true;
            }
            j = Math.Max(j, hi);
        }
        var present = new bool[runLength];
        for (int j = 0; j < runLength; j++)
        {
            present[j] = !absent[runStart + j];
        }
        return present;
    }

    /// <summary>
    /// A window whose power in the probe's band is no more than this times the noise measured
    /// outside the probe is down at the noise: +1.8 dB, room for the noise's own unevenness from
    /// one window to the next and for a receiver's AGC.
    /// </summary>
    private const double QuietRatio = 1.5;

    /// <summary>A window next to a gap with this share of the run's typical correlation or less (-20 dB) holds only a trace of the probe, and goes with the gap.</summary>
    private const double FaintShare = 0.01;

    /// <summary>A lone window scoring a little over the empty test is noise only under this share of the run's typical window (-10 dB).</summary>
    private const double LoneShare = 0.1;

    /// <summary>A stretch this far under the noise (-3 dB) is silence: lost audio, which a fade, leaving the noise, cannot be.</summary>
    private const double SilentRatio = 0.5;

    /// <summary>
    /// The probe at full strength beside a gap: this many of noise's standard deviations, 14 dB
    /// over what empties a window. A probe heard at about -4 dB or more stands so clear; under
    /// that its fades and its gaps look alike.
    /// </summary>
    private const double FullSigmas = 100;

    /// <summary>A window's score must pass this many of noise's standard deviations for the probe to count as in it.</summary>
    private const double AbsentSigmas = 4;

    /// <summary>
    /// Takes out a window's narrowband interference before it is correlated: every bin of the
    /// probe's band, its power weighted by the probe's own spectrum there, holding more than
    /// <see cref="ExciseRatio"/> times the band's median is zeroed. The probe fills its band to
    /// its spectrum's shape, so it loses nothing; a carrier (the tone's tail, the CW ident, a
    /// birdie, in the flat middle or the roll-off) would otherwise correlate with the Zadoff-Chu
    /// sequence, a chirp, as a sharp path at the delay where the chirp passes its frequency.
    /// </summary>
    private static void Excise(Complex[] x, int[] inBand, double[] weight, double[] power, List<int> removed)
    {
        for (int i = 0; i < inBand.Length; i++)
        {
            power[i] = ChannelMaths.Norm(x[inBand[i]]) / weight[i];
        }
        double median = ChannelMaths.Median(power);
        double limit = median * ExciseRatio, spill = median * SpillRatio;
        var cut = new bool[inBand.Length];
        for (int i = 0; i < inBand.Length; i++)
        {
            if (power[i] > limit)
            {
                // A carrier between bins spills into its neighbours: those standing clear of the
                // probe go with it, and no more, so the notch stays as narrow as it can.
                cut[i] = true;
                for (int d = 1; d <= SpillBins; d++)
                {
                    if (i - d >= 0 && power[i - d] > spill)
                    {
                        cut[i - d] = true;
                    }
                    if (i + d < inBand.Length && power[i + d] > spill)
                    {
                        cut[i + d] = true;
                    }
                }
            }
        }
        for (int i = 0; i < inBand.Length; i++)
        {
            if (cut[i])
            {
                x[inBand[i]] = Complex.Zero;
                removed.Add(inBand[i]);
            }
        }
    }

    /// <summary>How far either side of an interfering bin its spill is looked for.</summary>
    private const int SpillBins = 3;

    /// <summary>A neighbour of an interfering bin goes with it if it holds this many times the band's median.</summary>
    private const double SpillRatio = 2;

    /// <summary>A bin this many times the band's median power is interference: 10 dB, well clear of a fading probe's own unevenness.</summary>
    private const double ExciseRatio = 10;

    private static int Wrap(int i, int n) => ((i % n) + n) % n;

    /// <summary>The median of <paramref name="values"/>, as <see cref="ChannelMaths.Median"/> has it for finite ones, sorted in <paramref name="scratch"/> rather than in new arrays.</summary>
    private static double MedianOf(double[] values, double[] scratch)
    {
        var v = scratch.AsSpan(0, values.Length);
        values.CopyTo(v);
        v.Sort();
        int n = v.Length;
        return n % 2 == 1 ? v[n / 2] : (v[(n / 2) - 1] + v[n / 2]) / 2;
    }

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
    private sealed record Reference(Complex[] Transform, Bluestein Dft, Complex[] Conjugate, double Energy, double PulseEnergy, double GainDb, Func<double, double> Pulse, int[] InBand, double[] InBandWeight);

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
        var pulseAt = PulseOf(transform, null);
        // A unit path's response summed in power over its lags, to turn the profile's area into path power.
        double pulse = 0;
        for (int m = -L / 2; m < L / 2; m++)
        {
            double v = pulseAt(m / (double)Rate);
            pulse += v * v;
        }
        // The correlation's gain over the noise in 3 kHz: a period of samples at 9600 Hz against 3 kHz.
        double gain = 10 * Math.Log10(L * 3000.0 / Rate);
        // The bins the probe's band covers, with its spectrum's shape there against its flat
        // middle (floored at a tenth, so the edges' noise is not taken for a carrier).
        double binHz = Rate / (double)L;
        double flat = transform.Max(ChannelMaths.Norm);
        int Signed(int k) => k <= L / 2 ? k : k - L;
        int[] inBand = [.. Enumerable.Range(0, L).Where(k => Math.Abs(Signed(k) * binHz) <= probe.HalfBandwidthHz).OrderBy(Signed)];
        double[] weight = [.. inBand.Select(k => Math.Max(ChannelMaths.Norm(transform[k]) / flat, 0.1))];
        return new Reference(transform, dft, conjugate, energy, pulse, gain, pulseAt, inBand, weight);
    }

    /// <summary>
    /// What one path looks like after the correlation, against its delay in seconds: the period's
    /// circular autocorrelation, from its power spectrum <paramref name="transform"/> with each
    /// bin weighted by <paramref name="keep"/> (the share of windows that kept it after the
    /// carrier cut; all of it when null), worked out finely (about 0.8 us apart). It is the raised
    /// cosine but for the pulse's truncation and any notch the cut made, which the fit would
    /// otherwise take for weak paths beside a strong one. Peak 1 with nothing cut.
    /// </summary>
    private static Func<double, double> PulseOf(Complex[] transform, double[]? keep)
    {
        int L = transform.Length;
        const int Fine = 1 << 17;
        var spectrum = new Complex[Fine];
        double whole = 0;
        for (int k = 0; k < L; k++)
        {
            int signed = k <= L / 2 ? k : k - L;
            double power = ChannelMaths.Norm(transform[k]);
            whole += power;
            spectrum[((signed % Fine) + Fine) % Fine] = power * (keep?[k] ?? 1);
        }
        ChannelMaths.Fft(spectrum, true);
        var table = new double[Fine];
        // Against the uncut pulse's peak: the inverse FFT scales by 1/Fine.
        double zero = whole / Fine;
        for (int i = 0; i < Fine; i++)
        {
            table[i] = spectrum[i].Real / zero;
        }
        double perSecond = Fine / (L / (double)Rate);
        return seconds =>
        {
            double at = seconds * perSecond;
            double floorAt = Math.Floor(at);
            int i = (int)(((long)floorAt % Fine + Fine) % Fine);
            double frac = at - floorAt;
            return (table[i] * (1 - frac)) + (table[(i + 1) % Fine] * frac);
        };
    }

    /// <summary>
    /// A thread's working arrays for <see cref="Measure(Complex[], double, double)"/>, kept from
    /// one call to the next: the audio mixed on the frequency measured at, and each window's
    /// correlation and its power over its floor, some 5 MB in all, made once rather than for
    /// every pass. Nothing a measurement returns refers to them.
    /// </summary>
    private sealed class Workspace
    {
        [ThreadStatic]
        private static Workspace? _mine;

        public Complex[] Mixed { get; private set; } = [];

        public Complex[][] Correlation { get; private set; } = [];

        public double[][] Normalised { get; private set; } = [];

        public Complex[] Bins { get; private set; } = [];

        public Complex[] Work { get; private set; } = [];

        public static Workspace For(int samples, int windows, int length, Bluestein dft)
        {
            var ws = _mine ??= new Workspace();
            if (ws.Mixed.Length < samples)
            {
                ws.Mixed = new Complex[samples];
            }
            if (ws.Bins.Length != length)
            {
                ws.Bins = new Complex[length];
                ws.Work = dft.Work();
                ws.Correlation = [];
                ws.Normalised = [];
            }
            if (ws.Correlation.Length < windows)
            {
                ws.Correlation = [.. Enumerable.Range(0, windows).Select(_ => new Complex[length])];
                ws.Normalised = [.. Enumerable.Range(0, windows).Select(_ => new double[length])];
            }
            return ws;
        }
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

        /// <summary>A working buffer for <see cref="Forward(ReadOnlySpan{Complex}, Span{Complex}, Complex[])"/>; one per thread.</summary>
        public Complex[] Work() => new Complex[_m];

        /// <summary>The forward transform, X[k] = sum x[n] exp(-j 2 pi n k / N), as a new array.</summary>
        public Complex[] Forward(Complex[] x)
        {
            var y = new Complex[_n];
            Forward(x, y, Work());
            return y;
        }

        /// <summary>The forward transform into <paramref name="y"/>, with <paramref name="work"/> from <see cref="Work"/>. <paramref name="x"/> and <paramref name="y"/> may be the same.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Forward(ReadOnlySpan<Complex> x, Span<Complex> y, Complex[] work)
        {
            var a = work;
            for (int k = 0; k < _n; k++)
            {
                a[k] = x[k] * _chirp[k];
            }
            Array.Clear(a, _n, _m - _n);
            ChannelMaths.Fft(a, false);
            for (int i = 0; i < _m; i++)
            {
                a[i] *= _filter[i];
            }
            ChannelMaths.Fft(a, true);
            for (int k = 0; k < _n; k++)
            {
                y[k] = a[k] * _chirp[k];
            }
        }

        /// <summary>The inverse, scaled by 1/N, as a new array.</summary>
        public Complex[] Inverse(Complex[] x)
        {
            var y = new Complex[_n];
            Inverse(x, y, Work());
            return y;
        }

        /// <summary>The inverse, scaled by 1/N, into <paramref name="y"/>; <paramref name="x"/> is left conjugated.</summary>
        public void Inverse(Complex[] x, Span<Complex> y, Complex[] work)
        {
            for (int k = 0; k < _n; k++)
            {
                x[k] = Complex.Conjugate(x[k]);
            }
            Forward(x, y, work);
            for (int k = 0; k < _n; k++)
            {
                y[k] = Complex.Conjugate(y[k]) / _n;
            }
        }
    }
}
