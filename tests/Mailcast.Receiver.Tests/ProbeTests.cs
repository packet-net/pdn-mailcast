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
        Assert.All(trials, t => Assert.InRange(Math.Abs(t.SeparationErrorUs), 0, 15));
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
        Assert.InRange(found.StartSeconds - probeAt, 0, 0.25);
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
