using System.Globalization;
using System.Numerics;
using System.Text;
using Xunit.Abstractions;
using P = Mailcast.Receiver.Tests.ProbeSlot.Path;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The channel probe after the tone: synthetic probes through channels with a known truth, as in
/// the probe's step 1 study (mailcast-test/probe-step1/synth.py), measured by the receiver.
/// </summary>
public class ProbeTests(ITestOutputHelper output)
{
    private const int Rate = OnAir.SampleRate;

    /// <summary>The channels of step 1: delays in ms, powers in dB, steady shifts and two-sigma spreads in Hz.</summary>
    internal static readonly Dictionary<string, P[]> Channels = new()
    {
        ["static1"] = [new P(0, 0)],
        ["static1+0.5Hz"] = [new P(0, 0, ShiftHz: 0.5)],
        ["mid1.2ms"] = [new P(0, 0, ShiftHz: 0.1, SpreadHz: 0.2), new P(1.2, -3, ShiftHz: -0.3, SpreadHz: 0.5)],
        ["moderate1ms"] = [new P(0, 0, SpreadHz: 0.5), new P(1.0, 0, SpreadHz: 0.5)],
        ["poor2ms"] = [new P(0, 0, SpreadHz: 1.0), new P(2.0, 0, SpreadHz: 1.0)],
        ["close0.6ms"] = [new P(0, 0, ShiftHz: 0.2, SpreadHz: 0.5), new P(0.6, -3, ShiftHz: -0.2, SpreadHz: 0.5)],
    };

    /// <summary>One trial's result against the truth.</summary>
    internal sealed record Trial(string Channel, double Snr, int Seed, int Modes, int TrueModes, double FirstErrorUs, double SeparationErrorUs, double ShiftErrorHz, double[] SpreadHz, double SnrDb, double FloorDb);

    /// <summary>
    /// One trial as synth.py ran them: the paths after an unknown delay of 0.25 to 2.25 ms, the
    /// signal up to 3 Hz off, the tone's end where the detector would put it (up to 1 s off) and
    /// its frequency measured to about 0.05 Hz.
    /// </summary>
    internal static Trial? Run(string channel, double snr, int seed)
    {
        var rng = new Random((Channels.Keys.ToList().IndexOf(channel) * 1000) + ((int)snr * 10) + seed + 50_000);
        var truth = Channels[channel];
        double baseMs = 0.25 + (rng.NextDouble() * 2.0);
        double offset = (rng.NextDouble() * 6) - 3;
        var paths = truth.Select(p => p with { DelayMs = p.DelayMs + baseMs }).ToArray();
        var made = ProbeSlot.Make(paths, snr, seed: rng.Next(), offsetHz: offset);
        double endError = (rng.NextDouble() * 2) - 1;
        var (audio, toneEnd) = ProbeSlot.Kept(made, endError);
        double toneError = (rng.NextDouble() - 0.5) * 0.1;
        double mixHz = OnAir.CentreAudioHz + offset + toneError;
        if (ProbeChannel.Analyse(audio, toneEnd, mixHz) is not var (picture, found))
        {
            return null;
        }
        // Where the first path arrived against the truth, both modulo a period.
        double period = ProbeChannel.Period / (double)ChannelMaths.Rate;
        double captureStart = made.ToneEndSeconds + endError - CapturedProbe.BeforeSeconds;
        double trueArrival = made.ProbeStartSeconds + ProbeChannel.Probe.RampSeconds + (paths[0].DelayMs / 1000) - captureStart;
        double measured = found.LagZeroSeconds + picture.FirstPathSeconds;
        double firstError = measured - trueArrival;
        firstError -= Math.Round(firstError / period) * period;
        // Rounded to the nearest 48 kHz sample when the audio was kept.
        firstError += (Math.Round(captureStart * Rate) / Rate) - captureStart;
        var modes = picture.Modes;
        double separation = truth.Length > 1 && modes.Count > 1 ? ((modes[1].DelayMs - modes[0].DelayMs) - (truth[1].DelayMs - truth[0].DelayMs)) * 1000 : double.NaN;
        double shiftError = modes[0].CentroidHz is double c ? c - (truth[0].ShiftHz + offset - (mixHz - OnAir.CentreAudioHz)) : double.NaN;
        return new Trial(channel, snr, seed, modes.Count, truth.Length, firstError * 1e6, separation, shiftError,
            [.. modes.Select(m => m.SpreadHz ?? double.NaN)], picture.SnrDb, picture.FloorDb);
    }

    private static double Quantile(IEnumerable<double> values, double q)
    {
        var v = values.Where(double.IsFinite).Order().ToArray();
        return v.Length == 0 ? double.NaN : v[Math.Min(v.Length - 1, (int)Math.Floor(q * v.Length))];
    }

    [Theory]
    [InlineData("mid1.2ms")]
    [InlineData("moderate1ms")]
    [InlineData("close0.6ms")]
    public void TwoPaths_MatchStepOne_FromMinus3dBUp(string channel)
    {
        // Step 1 (synth-summary.txt): from -3 dB up both modes were found every time, and each
        // mode's delay was within about 6 us at the 90th percentile.
        var trials = new List<Trial>();
        foreach (double snr in (double[])[-3, 5, 20])
        {
            for (int seed = 0; seed < 3; seed++)
            {
                var t = Run(channel, snr, seed);
                Assert.NotNull(t);
                trials.Add(t);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{channel} {snr,4} dB seed {seed}: {t.Modes} modes, first {t.FirstErrorUs:F1} us, separation {t.SeparationErrorUs:F1} us, shift {t.ShiftErrorHz:F3} Hz, SNR {t.SnrDb:F1} dB"));
            }
        }
        Assert.All(trials, t => Assert.Equal(2, t.Modes));
        Assert.True(Quantile(trials.Select(t => Math.Abs(t.FirstErrorUs)), 0.5) < 6);
        Assert.True(Quantile(trials.Select(t => Math.Abs(t.SeparationErrorUs)), 0.5) < 6);
        // Every window of a fading probe is measured, the deepest fades too, and at -3 dB their
        // noise can take a trial out to about 20 us.
        Assert.All(trials, t => Assert.InRange(Math.Abs(t.SeparationErrorUs), 0, 20));
        Assert.All(trials, t => Assert.InRange(Math.Abs(t.ShiftErrorHz), 0, 0.3));
    }

    [Theory]
    [InlineData(-15)]
    [InlineData(-9)]
    public void WeakPaths_AreStillFound(double snr)
    {
        // Far below what MS110D decodes (WN4 wants about +3 dB): the probe still shows the path.
        for (int seed = 0; seed < 3; seed++)
        {
            var t = Run("static1", snr, seed);
            Assert.NotNull(t);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"static1 {snr} dB seed {seed}: {t.Modes} modes, first {t.FirstErrorUs:F1} us, SNR {t.SnrDb:F1} dB"));
            Assert.Equal(1, t.Modes);
            Assert.InRange(Math.Abs(t.FirstErrorUs), 0, 25);
            Assert.InRange(t.SnrDb, snr - 2, snr + 2);
        }
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(20)]
    public void SignalToNoise_IsMeasuredIn3kHz(double snr)
    {
        var t = Run("static1", snr, 1);
        Assert.NotNull(t);
        Assert.InRange(t.SnrDb, snr - 1.5, snr + 1.5);
    }

    [Fact]
    public void ModesWithDifferentDoppler_KeepTheirTrueSpacing()
    {
        // Two steady paths 1.5 ms apart, one 1 Hz high and one 1 Hz low: each is displaced and
        // smeared into shoulders when mixed on the other's frequency. Measured again on its own
        // frequency, each comes out clean.
        var made = ProbeSlot.Make([new P(0.8, 0, ShiftHz: 1.0), new P(2.3, -2, ShiftHz: -1.0)], 20, seed: 4);
        var (audio, end) = ProbeSlot.Kept(made);
        var (picture, _) = ProbeChannel.Analyse(audio, end, OnAir.CentreAudioHz)!.Value;
        var single = ChannelAnalysis.Analyse(ProbeChannel.Measure(audio, end, OnAir.CentreAudioHz)!.Snapshots)!;
        string Show(PathPicture p) => string.Join(" | ", p.Modes.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.DelayMs:F4} ms {m.PowerDb:F1} dB {m.CentroidHz:F2} Hz")));
        output.WriteLine($"one pass: {Show(single)}");
        output.WriteLine($"a pass a mode: {Show(picture)}");
        Assert.Equal(2, picture.Modes.Count);
        Assert.InRange(Math.Abs(((picture.Modes[1].DelayMs - picture.Modes[0].DelayMs) - 1.5) * 1000), 0, 4);
        Assert.Equal(-2, picture.Modes[1].PowerDb, 0.5);
        Assert.Equal(1.0, picture.Modes[0].CentroidHz!.Value, 0.05);
        Assert.Equal(-1.0, picture.Modes[1].CentroidHz!.Value, 0.05);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(-0.25)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    public void ASmallDopplerDifference_IsTakenOut(double difference)
    {
        var errors = new List<double>();
        for (int seed = 0; seed < 3; seed++)
        {
            var made = ProbeSlot.Make([new P(1.0, 0, ShiftHz: 0.3), new P(2.2, -3, ShiftHz: 0.3 + difference)], 15, seed: 200 + seed, offsetHz: 0.7);
            var (audio, end) = ProbeSlot.Kept(made);
            var (picture, _) = ProbeChannel.Analyse(audio, end, OnAir.CentreAudioHz + 0.7)!.Value;
            Assert.Equal(2, picture.Modes.Count);
            errors.Add(((picture.Modes[1].DelayMs - picture.Modes[0].DelayMs) - 1.2) * 1000);
        }
        output.WriteLine(string.Join(", ", errors.Select(e => e.ToString("F1", CultureInfo.InvariantCulture))));
        Assert.All(errors, e => Assert.InRange(Math.Abs(e), 0, 6));
    }

    [Theory]
    [InlineData(-1.2)]
    [InlineData(-0.5)]
    [InlineData(0.6)]
    [InlineData(1.2)]
    public void Found_WhereverInItsBlockTheToneDetectorPutTheEnd(double error)
    {
        var made = ProbeSlot.Make(Channels["mid1.2ms"], 10, seed: 9);
        var (audio, end) = ProbeSlot.Kept(made, error);
        var found = ProbeChannel.Measure(audio, end, OnAir.CentreAudioHz);
        Assert.NotNull(found);
        // The run begins after the probe's first (cyclic prefix) period and its first whole window.
        double probeAt = made.ProbeStartSeconds - (made.ToneEndSeconds + error - CapturedProbe.BeforeSeconds);
        // The first window used may overlap the probe's start by a little, never by more than its ramp and part of a period.
        Assert.InRange(found.StartSeconds - probeAt, -0.05, 0.25);
        var (picture, _) = ProbeChannel.Analyse(audio, end, OnAir.CentreAudioHz)!.Value;
        Assert.Equal(2, picture.Modes.Count);
        Assert.Equal(1.2, picture.Modes[1].DelayMs, 0.01);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(0)]
    [InlineData(-15)]
    public void NoProbe_IsNotFound(double snr)
    {
        // An older head end: the tone, then silence and its first burst 8 s after the tone.
        var modem = new Packet.SoundModem.Ms110d.Ms110dModem(Rate, _ => { }, new Packet.SoundModem.Ms110d.Ms110dTxSettings { WaveformNumber = 4 });
        var burst = modem.Modulate(new byte[200], 0);
        for (int i = 0; i < burst.Length; i++)
        {
            burst[i] *= 0.5f;
        }
        var made = ProbeSlot.Make(Channels["mid1.2ms"], snr, seed: 3, probe: false, extra: burst);
        var (audio, end) = ProbeSlot.Kept(made);
        Assert.Null(ProbeChannel.Measure(audio, end, OnAir.CentreAudioHz));
        // And noise alone.
        var rng = new Random(8);
        var noise = new float[audio.Length];
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)(0.1 * ((rng.NextDouble() * 2) - 1));
        }
        Assert.Null(ProbeChannel.Measure(noise, 1.0, OnAir.CentreAudioHz));
    }

    [Fact]
    public void ShortAudio_IsNotMeasured()
    {
        Assert.Null(ProbeChannel.Measure(new float[3 * Rate], 1.0, OnAir.CentreAudioHz));
        Assert.Null(ProbeChannel.Measure([], 0, OnAir.CentreAudioHz));
    }

    private static readonly P[] Fading2F = [new P(1.0, 0, SpreadHz: 0.2), new P(2.9, -6, SpreadHz: 0.2)];

    private static readonly P[] Steady2F = [new P(1.0, 0), new P(2.9, -6)];

    /// <summary>The whole probe through <paramref name="paths"/> at 5 dB, and the same channel and noise with the probe cut as given.</summary>
    private static (PathPicture Whole, PathPicture? Cut) WholeAndCut(P[] paths, int seed, double periodsSent = 61, (int, int)? silent = null)
    {
        PathPicture? Picture(ProbeSlot.Made made)
        {
            var (audio, end) = ProbeSlot.Kept(made, 0.3);
            return ProbeChannel.Analyse(audio, end, OnAir.CentreAudioHz)?.Picture;
        }
        var whole = Picture(ProbeSlot.Make(paths, 5, seed));
        var cut = Picture(ProbeSlot.Make(paths, 5, seed, periodsSent: periodsSent, silentPeriods: silent));
        return (whole!, cut);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(35)]
    [InlineData(40.5)]
    public void AProbeCutShort_IsMeasuredOnWhatWasSent_NotTheSilenceAfter(double periods)
    {
        // A Stop at the station while the probe is on the air: the rest of the run is silence,
        // which must not count as the channel fading away.
        for (int seed = 0; seed < 3; seed++)
        {
            var (whole, cut) = WholeAndCut(Fading2F, 10 + seed, periodsSent: periods);
            Assert.NotNull(cut);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{periods} periods, seed {seed}: {cut.GoodSnapshots} good, fade {cut.FadeDb:F1} dB (whole {whole.FadeDb:F1}), spread {cut.DopplerSpreadHz?.ToString("F2", CultureInfo.InvariantCulture) ?? "-"} Hz (whole {whole.DopplerSpreadHz:F2})"));
            // Never more than was sent; a few fewer where a fade at the cut took the last windows with it.
            Assert.InRange(cut.GoodSnapshots, (int)Math.Floor(periods) - 8, (int)Math.Floor(periods));
            Assert.Equal(2, cut.Modes.Count);
            Assert.InRange(cut.FadeDb, 0, 5);
            // A spread only from a long enough run; never one the silence made.
            if (cut.GoodSnapshots >= ProbeChannel.FewestDopplerPeriods)
            {
                Assert.InRange(cut.DopplerSpreadHz!.Value, 0, 0.45);
            }
            else
            {
                Assert.Null(cut.DopplerSpreadHz);
            }
            Assert.Equal(1.9, cut.Modes[1].DelayMs, 0.02);

            // On a steady channel the truth is exact: the 2F path 6 dB down, no fades, no spread.
            var (_, steady) = WholeAndCut(Steady2F, 10 + seed, periodsSent: periods);
            Assert.NotNull(steady);
            Assert.InRange(steady.Modes[1].PowerDb, -6 - 1, -6 + 1);
            Assert.InRange(steady.FadeDb, 0, 1.5);
            Assert.InRange(steady.DopplerSpreadHz ?? 0, 0, 0.15);
            Assert.Equal(steady.GoodSnapshots >= ProbeChannel.FewestDopplerPeriods, steady.DopplerSpreadHz is not null);
        }
    }

    [Fact]
    public void AProbeCutTooShort_IsNotMeasured()
    {
        // 30 periods or fewer leaves fewer windows than the analysis needs (30): given up, not
        // measured on silence.
        foreach (double periods in (double[])[30, 25])
        {
            for (int seed = 0; seed < 3; seed++)
            {
                Assert.Null(WholeAndCut(Fading2F, 20 + seed, periodsSent: periods).Cut);
                Assert.Null(WholeAndCut(Steady2F, 20 + seed, periodsSent: periods).Cut);
            }
        }
    }

    [Fact]
    public void ALostStretchInTheMiddle_IsLeftOut()
    {
        // Ten periods of a web SDR's audio lost: those windows are left out, the rest measured.
        for (int seed = 0; seed < 3; seed++)
        {
            var (_, cut) = WholeAndCut(Steady2F, 30 + seed, silent: (25, 35));
            Assert.NotNull(cut);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"seed {seed}: {cut.GoodSnapshots} good, fade {cut.FadeDb:F1} dB, spread {cut.DopplerSpreadHz:F2} Hz, 2F {cut.Modes[^1].PowerDb:F1} dB, {cut.Modes.Count} modes"));
            Assert.InRange(cut.GoodSnapshots, 44, 49);
            Assert.InRange(cut.FadeDb, 0, 1.5);
            Assert.Equal(2, cut.Modes.Count);
            Assert.InRange(cut.Modes[1].PowerDb, -6 - 1, -6 + 1);
            // Neither side of the gap is long enough to tell a spread of 0.1 Hz from none: no
            // spread is given, rather than the gap's own smear of 0.7 Hz.
            Assert.Null(cut.DopplerSpreadHz);
            Assert.All(cut.Modes, m => Assert.Null(m.SpreadHz));
            Assert.All(cut.Modes, m => Assert.InRange(m.CentroidHz!.Value, -0.1, 0.1));

            // A gap near the end leaves a run long enough, and the steady path reads as steady.
            var (_, late) = WholeAndCut(Steady2F, 30 + seed, silent: (48, 53));
            Assert.NotNull(late);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  late gap: {late.GoodSnapshots} good, spread {late.DopplerSpreadHz:F2} Hz"));
            Assert.InRange(late.DopplerSpreadHz!.Value, 0, 0.15);
        }
    }

    [Theory]
    [InlineData(2900)]
    [InlineData(1850)]
    [InlineData(1500)]
    [InlineData(2300)]
    public void ACarrierInTheBand_IsCutOut(double hz)
    {
        // A birdie as strong as the probe, in the flat middle or the roll-off near the band's edge.
        var made = ProbeSlot.Make([new P(1.0, 0)], 10, seed: 41);
        var audio = made.Audio;
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] += (float)(0.1 * Math.Sqrt(2) * Math.Sin(2 * Math.PI * hz * i / Rate));
        }
        var (kept, end) = ProbeSlot.Kept(made with { Audio = audio });
        var (picture, _) = ProbeChannel.Analyse(kept, end, OnAir.CentreAudioHz)!.Value;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{hz} Hz: {picture.Modes.Count} modes ({string.Join(", ", picture.Modes.Select(m => $"{m.DelayMs:F2} ms {m.PowerDb:F1} dB"))}), spread {picture.DopplerSpreadHz:F2} Hz, SNR {picture.SnrDb:F1} dB"));
        // Without the cut it reads as a spread of about 1.2 Hz and strong false paths; with it,
        // and the pulse fitted with the notch the cut left, there is one steady path and nothing else.
        Assert.InRange(picture.DopplerSpreadHz!.Value, 0, 0.15);
        Assert.Single(picture.Modes);
        Assert.Single(picture.PathsMs);
    }

    [Fact]
    public void ACarrierOnATwoPathChannel_MakesNoPathOfItsOwn()
    {
        // As above, on a path with a weaker second hop: both found, nothing more.
        foreach (double hz in (double[])[1850, 2900])
        {
            var made = ProbeSlot.Make(Steady2F, 10, seed: 42);
            var audio = made.Audio;
            for (int i = 0; i < audio.Length; i++)
            {
                audio[i] += (float)(0.1 * Math.Sqrt(2) * Math.Sin(2 * Math.PI * hz * i / Rate));
            }
            var (kept, end) = ProbeSlot.Kept(made with { Audio = audio });
            var (picture, _) = ProbeChannel.Analyse(kept, end, OnAir.CentreAudioHz)!.Value;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{hz} Hz: {string.Join(", ", picture.Modes.Select(m => $"{m.DelayMs:F2} ms {m.PowerDb:F1} dB"))}"));
            Assert.Equal(2, picture.Modes.Count);
            Assert.Equal(1.9, picture.Modes[1].DelayMs, 0.01);
            Assert.InRange(picture.Modes[1].PowerDb, -7, -5);
        }
    }

    [Fact]
    public void Present_KeepsFades_ButNotSilence()
    {
        // Noise alone puts 1.0 in the band, the probe 4.0. The gap before the probe, two windows
        // of probe before the run, the run, and the time after a Stop. A fade, down at the noise
        // for a window but still showing in the correlation, stays; the silence after the Stop
        // does not, nor does the window straddling the cut.
        double[] power = [1.0, 1.0, 1.0, 4.0, 4.0, .. Enumerable.Repeat(4.0, 27), 2.5, 1.6, 1.2, 1.5, 2.2, .. Enumerable.Repeat(4.0, 9), 2.5, 1.0, 1.0, 0.9, 1.0, 1.0];
        double[] score = [0, 2, -1, 400, 400, .. Enumerable.Repeat(400.0, 27), 150, 60, 45, 50, 120, .. Enumerable.Repeat(400.0, 9), 250, 5, 2, -3, 1, 0];
        var present = ProbeChannel.Present(score, 10, power, 1.0, 5, 47);
        Assert.All(present.Take(41), Assert.True);
        Assert.All(present.Skip(41), Assert.False);

        // However weak against the rest, a fade at the very end of the run stays.
        double[] fadePower = [1.0, 1.0, 4.0, 4.0, .. Enumerable.Repeat(4.0, 40), 2.0, 1.6, 1.7, 2.5, 4.0, 1.0, 1.0];
        double[] fadeScore = [0, 1, 400, 400, .. Enumerable.Repeat(400.0, 40), 60, 45, 50, 90, 300, 2, -1];
        Assert.All(ProbeChannel.Present(fadeScore, 10, fadePower, 1.0, 4, 44), Assert.True);

        // A stretch lost in the middle, the probe at full strength either side, goes, with the
        // windows either side of it, which straddle its edges.
        double[] gapPower = [.. Enumerable.Repeat(4.0, 20), 1.0, 1.0, 1.0, .. Enumerable.Repeat(4.0, 20)];
        double[] gapScore = [.. Enumerable.Repeat(2000.0, 20), 1, -2, 3, .. Enumerable.Repeat(2000.0, 20)];
        var gap = ProbeChannel.Present(gapScore, 10, gapPower, 1.0, 0, 43);
        Assert.Equal(Enumerable.Range(0, 43).Select(j => j < 19 || j > 23), gap);

        // One window into the noise and straight out again is a fast fade, not a gap.
        double[] nullScore = [.. Enumerable.Repeat(2000.0, 20), 2, .. Enumerable.Repeat(2000.0, 20)];
        double[] nullPower = [.. Enumerable.Repeat(4.0, 20), 1.1, .. Enumerable.Repeat(4.0, 20)];
        Assert.All(ProbeChannel.Present(nullScore, 10, nullPower, 1.0, 0, 41), Assert.True);

        // A probe too weak to stand clear of the noise: a fade and a gap look alike, so a stretch
        // inside the run with no correlation in it is kept as a fade, unless it is silence.
        double[] weakPower = [.. Enumerable.Repeat(1.05, 43)];
        double[] weakScore = [.. Enumerable.Repeat(30.0, 20), 1, -2, 3, .. Enumerable.Repeat(30.0, 20)];
        Assert.All(ProbeChannel.Present(weakScore, 2, weakPower, 1.0, 0, 43), Assert.True);
        double[] silentPower = [.. Enumerable.Repeat(1.05, 20), 0.0, 0.0, 0.0, .. Enumerable.Repeat(1.05, 20)];
        Assert.Equal(Enumerable.Range(0, 43).Select(j => j < 19 || j > 23), ProbeChannel.Present(weakScore, 2, silentPower, 1.0, 0, 43));
    }

    [Theory]
    [InlineData(5, 0.2)]
    [InlineData(5, 1.0)]
    [InlineData(-12, 0.5)]
    public void AFadingFullProbe_KeepsItsWindows_ItsFadesAndItsSpread(double snr, double spread)
    {
        // A whole probe on one fading path: every window is the probe, faded or not. The same
        // channel at 40 dB (the same fading; only the noise differs) is the truth for the fades.
        for (int seed = 0; seed < 4; seed++)
        {
            PathPicture Picture(double at)
            {
                var made = ProbeSlot.Make([new P(1.0, 0, SpreadHz: spread)], at, seed: 60 + seed);
                var (audio, end) = ProbeSlot.Kept(made, 0.3);
                return ProbeChannel.Analyse(audio, end, OnAir.CentreAudioHz)!.Value.Picture;
            }
            var picture = Picture(snr);
            var clean = Picture(40);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{snr} dB, {spread} Hz, seed {seed}: {picture.GoodSnapshots} good of {picture.Seconds / 0.10625:F0}, fade {picture.FadeDb:F1} dB (clean {clean.FadeDb:F1}, {clean.GoodSnapshots} good), spread {picture.DopplerSpreadHz?.ToString("F2", CultureInfo.InvariantCulture) ?? "-"} Hz (clean {clean.DopplerSpreadHz:F2})"));
            // Every window, faded or not. Only where the run, chosen on the strongest stretch of a
            // fading probe, overhangs its end by a window does that window go, with the one
            // straddling the end; the same happens with no noise at all.
            Assert.InRange(picture.GoodSnapshots, ProbeChannel.Windows - 2, ProbeChannel.Windows);
            if (snr > 0)
            {
                // The fades as deep as with no noise, less what the noise fills in.
                Assert.InRange(picture.FadeDb, 0.7 * clean.FadeDb, clean.FadeDb + 1);
            }
            Assert.NotNull(picture.DopplerSpreadHz);
            Assert.InRange(picture.DopplerSpreadHz.Value, spread / 2.5, spread * 2);
        }
    }

    [Fact]
    public void AShortDropout_IsNotBridged()
    {
        // Three periods lost: never carried across, which would read the gap as spread. Near the
        // end the longer side still gives a spread, and it reads as the whole probe's; in the
        // middle neither side is long enough for one.
        for (int seed = 0; seed < 3; seed++)
        {
            var (whole, late) = WholeAndCut(Fading2F, 70 + seed, silent: (45, 48));
            Assert.NotNull(late);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"fading, 45-48, seed {seed}: {late.GoodSnapshots} good, spread {late.DopplerSpreadHz?.ToString("F2", CultureInfo.InvariantCulture) ?? "-"} Hz (whole {whole.DopplerSpreadHz:F2}), fade {late.FadeDb:F1} dB (whole {whole.FadeDb:F1})"));
            Assert.InRange(late.GoodSnapshots, 52, 55);
            Assert.NotNull(late.DopplerSpreadHz);
            Assert.InRange(late.DopplerSpreadHz.Value, whole.DopplerSpreadHz!.Value - 0.1, whole.DopplerSpreadHz.Value + 0.1);

            var (_, steady) = WholeAndCut(Steady2F, 70 + seed, silent: (45, 48));
            Assert.NotNull(steady);
            Assert.InRange(steady.DopplerSpreadHz!.Value, 0, 0.15);
            Assert.InRange(steady.FadeDb, 0, 1.5);

            var (_, middle) = WholeAndCut(Steady2F, 70 + seed, silent: (28, 31));
            Assert.NotNull(middle);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"steady, 28-31, seed {seed}: {middle.GoodSnapshots} good, spread {middle.DopplerSpreadHz?.ToString("F2", CultureInfo.InvariantCulture) ?? "-"} Hz"));
            Assert.InRange(middle.GoodSnapshots, 52, 55);
            Assert.Null(middle.DopplerSpreadHz);
            Assert.InRange(middle.FadeDb, 0, 1.5);
        }
    }

    [Theory]
    [InlineData(1020)]
    [InlineData(17)]
    public void Bluestein_IsTheDft(int n)
    {
        var rng = new Random(n);
        var x = Enumerable.Range(0, n).Select(_ => new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)).ToArray();
        var dft = new ProbeChannel.Bluestein(n);
        var y = dft.Forward(x);
        for (int k = 0; k < n; k += Math.Max(1, n / 13))
        {
            Complex sum = Complex.Zero;
            for (int t = 0; t < n; t++)
            {
                sum += x[t] * Complex.FromPolarCoordinates(1, -2 * Math.PI * ((long)t * k % n) / n);
            }
            Assert.True((y[k] - sum).Magnitude < 1e-9, $"bin {k}");
        }
        var back = dft.Inverse(y);
        Assert.All(Enumerable.Range(0, n), i => Assert.True((back[i] - x[i]).Magnitude < 1e-9));
    }

    /// <summary>
    /// The full step 1 grid: 6 channels, 10 SNRs, 8 seeds, as synth-summary.txt. Slow, so only when
    /// MAILCAST_PROBE_SWEEP is set; it writes the table to the test output and to that path.
    /// </summary>
    [Fact]
    public void Sweep_StepOneGrid()
    {
        string? path = Environment.GetEnvironmentVariable("MAILCAST_PROBE_SWEEP");
        if (string.IsNullOrEmpty(path))
        {
            output.WriteLine("set MAILCAST_PROBE_SWEEP to a file to run the full grid");
            return;
        }
        double[] snrs = [-15, -12, -9, -6, -3, 0, 5, 10, 15, 20];
        var jobs = Channels.Keys.SelectMany(c => snrs.SelectMany(s => Enumerable.Range(0, 8).Select(k => (c, s, k)))).ToList();
        var results = new Trial?[jobs.Count];
        Parallel.For(0, jobs.Count, i => results[i] = Run(jobs[i].c, jobs[i].s, jobs[i].k));
        var text = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        text.AppendLine("Receiver probe accuracy, synthetic (as step 1's synth.py). Per cell: modes right / trials; |first path delay error| (us, median/90%),");
        text.AppendLine("|mode separation error| (us, median/90%), |Doppler shift error| (Hz, median/90%), Doppler spread of each mode (Hz, median) against the truth, SNR measured (dB, median).");
        foreach (var channel in Channels.Keys)
        {
            var truth = Channels[channel];
            text.AppendLine().AppendLine($"== {channel}");
            text.AppendLine(" SNR  modes      d0 us      sep us     shift Hz     spread0      spread1   SNR meas");
            foreach (double snr in snrs)
            {
                var cell = results.Where((r, i) => jobs[i].c == channel && jobs[i].s == snr).ToList();
                var ok = cell.Where(r => r is not null && r.Modes == truth.Length).Select(r => r!).ToList();
                var all = cell.Where(r => r is not null).Select(r => r!).ToList();
                string Q(IEnumerable<double> v, string f) => $"{Quantile(v, 0.5).ToString(f, c)}/{Quantile(v, 0.9).ToString(f, c)}";
                text.AppendLine(string.Create(c,
                    $" {snr,3}  {ok.Count,2}/{cell.Count,-2}  {Q(all.Select(r => Math.Abs(r.FirstErrorUs)), "F1"),10}  {Q(ok.Select(r => Math.Abs(r.SeparationErrorUs)), "F1"),10}  {Q(all.Select(r => Math.Abs(r.ShiftErrorHz)), "F2"),10}" +
                    $"  {Quantile(all.Select(r => r.SpreadHz[0]), 0.5),5:F2} ({truth[0].SpreadHz:F1})  {(truth.Length > 1 ? Quantile(ok.Select(r => r.SpreadHz.Length > 1 ? r.SpreadHz[1] : double.NaN), 0.5).ToString("F2", c) + $" ({truth[1].SpreadHz:F1})" : "   -      "),11}  {Quantile(all.Select(r => r.SnrDb), 0.5),6:F1}"));
            }
        }
        output.WriteLine(text.ToString());
        File.WriteAllText(path, text.ToString());
    }
}
