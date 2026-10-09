using System.Numerics;
using System.Text.Json;
using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The Channel tile's measurement: synthetic channels with a known truth, two real bursts from
/// GB7RDG's 16:00 UTC slot on 2026-10-05, the hop geometry, and the watch that runs it all after
/// each slot.
/// </summary>
/// <remarks>
/// The recordings in Channel/ are one WN4 burst each from the UberSDR IQ recordings of that slot
/// (mailcast-test/fulltest4), cut to 2.5 s before the burst and 3 s after it and made into USB
/// audio at 12 kHz with <c>sm-iqcapture convert --dial-hz -1800 --ssb-low 150 --ssb-high 3450
/// --out-rate 12000 --gain 1</c>. The passive analysis in Python (probe-step1/passive) measured
/// the same bursts: WESSEX burst_05 2F at +2.03 ms and -17.1 dB (first path to path); EI4HQ
/// 2F at +1.80 ms, -8.5 dB, and 3F at +4.01 ms, -22.1 dB.
/// </remarks>
public class ChannelTests
{
    private const int Rate = OnAir.SampleRate;

    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly Ms110dLockInfo Wn4 = new(4, Ms110dInterleaverKind.Short, 7, 0);

    /// <summary>A path of a synthetic channel: its delay, power, a steady Doppler shift and a Gaussian Doppler spread (two sigma).</summary>
    private sealed record SynthPath(double DelayMs, double PowerDb, double ShiftHz = 0, double SpreadHz = 0);

    /// <summary>
    /// A WN4 burst of <paramref name="bits"/> random payload bits through the paths given, as
    /// 48 kHz USB audio with white noise at <paramref name="snrDb"/> in 3 kHz: 1 s of noise, the
    /// burst, 1.5 s of noise. Delays are whole 48 kHz samples, so the truth is exact.
    /// </summary>
    private static (float[] Audio, int End, byte[] Bits) Synthesise(int bits, int seed, SynthPath[] paths, double snrDb, double offsetHz = 0)
    {
        var rng = new Random(seed);
        var payload = new byte[bits];
        for (int i = 0; i < bits; i++)
        {
            payload[i] = (byte)rng.Next(2);
        }
        var sym = BurstReference.Symbols(Wn4, 3, payload);
        const int Up = Rate / ChannelMaths.Baud;
        var pulse = new double[(16 * Up) + 1];
        for (int i = 0; i < pulse.Length; i++)
        {
            pulse[i] = FilterDesign.RootRaisedCosine((i - (8.0 * Up)) / Up, 0.35);
        }
        int lead = Rate, tail = 3 * Rate / 2;
        int length = lead + (sym.Length * Up) + pulse.Length + tail;
        var shaped = new Complex[length];
        for (int n = 0; n < sym.Length; n++)
        {
            int at = lead + (n * Up);
            for (int k = 0; k < pulse.Length; k++)
            {
                shaped[at + k] += sym[n] * pulse[k];
            }
        }
        double total = paths.Sum(p => Math.Pow(10, p.PowerDb / 10));
        var sum = new Complex[length];
        foreach (var p in paths)
        {
            int d = (int)Math.Round(p.DelayMs * Rate / 1000);
            double amp = Math.Sqrt(Math.Pow(10, p.PowerDb / 10) / total);
            var fade = Fading(length, p.SpreadHz, rng);
            for (int i = d; i < length; i++)
            {
                double t = (double)i / Rate;
                sum[i] += amp * fade(i) * Complex.FromPolarCoordinates(1, 2 * Math.PI * (p.ShiftHz + offsetHz) * t) * shaped[i - d];
            }
        }
        var audio = new float[length];
        double power = 0;
        for (int i = 0; i < length; i++)
        {
            double v = (sum[i] * Complex.FromPolarCoordinates(1, 2 * Math.PI * OnAir.CentreAudioHz * i / Rate)).Real;
            audio[i] = (float)v;
            if (i >= lead && i < length - tail)
            {
                power += v * v;
            }
        }
        power /= length - lead - tail;
        double sigma = Math.Sqrt(power / Math.Pow(10, snrDb / 10) * (Rate / 2.0 / 3000));
        double scale = 0.1 / Math.Sqrt(power);
        for (int i = 0; i < length; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            audio[i] = (float)((audio[i] + (sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2))) * scale);
        }
        return (audio, length - tail + (Rate / 4), payload);
    }

    /// <summary>A unit-power Rayleigh fading gain with a Gaussian Doppler spectrum, made at 100 Hz and interpolated; 1 for no spread.</summary>
    internal static Func<int, Complex> Fading(int samples, double spread2s, Random rng)
    {
        if (spread2s <= 0)
        {
            return _ => Complex.One;
        }
        const double Fs = 100;
        int n = 1;
        while (n < (samples / (double)Rate * Fs) + 2)
        {
            n <<= 1;
        }
        var x = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble(), r = Math.Sqrt(-Math.Log(u1));
            x[i] = new Complex(r * Math.Cos(2 * Math.PI * u2), r * Math.Sin(2 * Math.PI * u2));
        }
        ChannelMaths.Fft(x, false);
        double sigma = spread2s / 2;
        for (int k = 0; k < n; k++)
        {
            double f = (k < n / 2 ? k : k - n) * Fs / n;
            x[k] *= Math.Exp(-(f * f) / (4 * sigma * sigma));
        }
        ChannelMaths.Fft(x, true);
        double rms = Math.Sqrt(x.Average(ChannelMaths.Norm));
        return i =>
        {
            double at = i / (double)Rate * Fs;
            int k = (int)at;
            double frac = at - k;
            return ((x[k] * (1 - frac)) + (x[k + 1] * frac)) / rms;
        };
    }

    private static PathPicture Picture(float[] audio, int end, byte[] bits, Ms110dLockInfo? locked = null)
    {
        var series = BurstChannel.Measure(audio, end, locked ?? Wn4, bits);
        Assert.NotNull(series);
        var picture = ChannelAnalysis.Analyse(series);
        Assert.NotNull(picture);
        return picture;
    }

    private static readonly GroundPlace Wessex = GroundPlace.FromLocator("IO80qr")!;

    private static readonly GroundPlace Ei4hq = GroundPlace.FromLocator("IO51uu")!;

    [Fact]
    public void BuildSymbols_IsReachable()
    {
        // Pins the one thing read from inside pdn-soundmodem: see BurstReference.
        Assert.True(BurstReference.Available);
        var bits = new byte[3000];
        var settings = new Ms110dTxSettings { WaveformNumber = 4, PreambleSuperframes = 3 };
        // The modulator's own audio is the symbols at 4 samples each, plus the pulse.
        int samples = new Ms110dModulator(settings).Modulate(bits).Length;
        Assert.Equal(samples, (BurstReference.Symbols(Wn4, 3, bits).Length * 4) + ChannelMaths.Pulse.Length);
        Assert.Equal(128, BurstReference.FrameSymbols(4));
        // A preamble put in front of the data is the burst as made with that preamble.
        var one = BurstReference.Symbols(Wn4, 1, bits);
        Assert.Equal(BurstReference.Symbols(Wn4, 5, bits), BurstReference.WithPreamble(Wn4, 5, one));
    }

    [Fact]
    public void TwoPaths_AreFoundWhereTheyWere()
    {
        var paths = new[] { new SynthPath(0, 0), new SynthPath(1.5, -10, ShiftHz: 0.4) };
        var (audio, end, bits) = Synthesise(6000, seed: 1, paths, snrDb: 15, offsetHz: 1.3);
        // The modem's lock is near the offset, not on it.
        var p = Picture(audio, end, bits, Wn4 with { CfoHz = 1.2 });

        Assert.Equal(2, p.Modes.Count);
        Assert.Equal(1.5, p.Modes[1].DelayMs, 0.03);
        Assert.Equal(-10, p.Modes[1].PowerDb, 1.0);
        // RMS delay spread of 1 and 0.1 of the power 1.5 ms apart: 1.5 sqrt(p1 p2).
        Assert.Equal(1.5 * Math.Sqrt(1 / 1.1 * (0.1 / 1.1)), p.DelaySpreadMs, 0.03);
        Assert.Equal(15, p.SnrDb, 1.0);
        // The offset is found though the "lock" said 0, and so is the second path's shift.
        Assert.Equal(1.3, p.OffsetHz!.Value, 0.05);
        Assert.Equal(0.4, p.Modes[1].CentroidHz!.Value - p.Modes[0].CentroidHz!.Value, 0.08);
        Assert.True(p.Modes[0].SpreadHz < 0.1, $"a steady path spread {p.Modes[0].SpreadHz} Hz");

        var report = ChannelReport.Summarise(Noon, Noon, [p], 1, Wessex);
        Assert.True(report.Enough);
        Assert.Equal(["1F", "2F"], report.Modes.Select(m => m.Label));
        Assert.Equal(0.4, report.Modes[1].DopplerShiftHz!.Value, 0.08);
        // 1.5 ms between one hop and two over 136 km: a reflection about 230 km up.
        Assert.InRange(report.VirtualHeightKm!.Value, 215, 245);
        Assert.Equal(136, report.DistanceKm);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FadingPath_SpreadAndCoherenceAreMeasured(int seed)
    {
        // Rayleigh fading with a 0.5 Hz Doppler spread: it keeps half its correlation for about 0.75 s.
        var paths = new[] { new SynthPath(0, 0, SpreadHz: 0.5), new SynthPath(2.5, -10) };
        var (audio, end, bits) = Synthesise(12000, seed, paths, snrDb: 15);
        var p = Picture(audio, end, bits);

        Assert.Equal(2, p.Modes.Count);
        Assert.Equal(2.5, p.Modes[1].DelayMs, 0.05);
        Assert.InRange(p.Modes[0].SpreadHz!.Value, 0.35, 0.7);
        Assert.True(p.Modes[1].SpreadHz < 0.15, $"a steady path spread {p.Modes[1].SpreadHz} Hz");
        Assert.True(p.FadeDb > 2, $"fade {p.FadeDb} dB on a fading path");
        Assert.InRange(p.CoherenceS!.Value, 0.4, 1.6);
    }

    [Fact]
    public void OnePath_IsSteady()
    {
        var (audio, end, bits) = Synthesise(6000, seed: 3, [new SynthPath(0, 0)], snrDb: 10);
        var p = Picture(audio, end, bits);

        Assert.Single(p.Modes);
        Assert.True(p.DelaySpreadMs < 0.05, $"delay spread {p.DelaySpreadMs} ms");
        Assert.True(p.DopplerSpreadHz < 0.1, $"Doppler spread {p.DopplerSpreadHz} Hz");
        Assert.True(p.FadeDb < 1.5, $"fade {p.FadeDb} dB");
        Assert.True(p.CoherenceS >= 10, $"coherence {p.CoherenceS} s");
        var report = ChannelReport.Summarise(Noon, Noon, [p], 1, Wessex);
        Assert.Equal("1 path: 1 hop. Doppler spread under 0.1 Hz: steady (good for 1200 bps).", report.Words);
        Assert.Null(report.VirtualHeightKm);
    }

    [Fact]
    public void WrongReference_IsNotMeasured()
    {
        var (audio, end, _) = Synthesise(6000, seed: 4, [new SynthPath(0, 0)], snrDb: 15);
        var other = new byte[6000];
        new Random(99).NextBytes(other);
        for (int i = 0; i < other.Length; i++)
        {
            other[i] &= 1;
        }
        // The preamble still matches (it is the same for any payload), so the burst is found;
        // but the data is not what was sent, and nearly every snapshot is thrown out.
        var series = BurstChannel.Measure(audio, end, Wn4, other);
        Assert.True(series is null || ChannelAnalysis.Analyse(series) is null || series.Good.Count(g => g) < series.Good.Length / 3);
    }

    /// <summary>A recording in Channel/, through the receiver's own pipeline: decoded, kept and measured.</summary>
    private static async Task<List<CapturedBurst>> HearAsync(string name)
    {
        var (samples, rate) = WavFile.ReadMono(Path.Combine(AppContext.BaseDirectory, "Channel", name));
        var audio = AudioPipeline.ToPipelineRate(samples, rate);
        var pipeline = AudioPipeline.ForInput(new NoInput(), _ => { }, new FakeTimeProvider(Noon), watch: false);
        var heard = new List<CapturedBurst>();
        pipeline.BurstCaptured += heard.Add;
        // The modem is given some quiet before the recording, as it has on the air.
        Feed(pipeline, new float[8 * Rate]);
        Feed(pipeline, audio);
        Feed(pipeline, new float[8 * Rate]);
        await pipeline.DisposeAsync();
        return heard;
    }

    private static void Feed(AudioPipeline pipeline, float[] audio)
    {
        for (int offset = 0; offset < audio.Length; offset += 4800)
        {
            pipeline.Feed(audio.AsSpan(offset, Math.Min(4800, audio.Length - offset)));
        }
    }

    private sealed class NoInput : IAudioInput
    {
        public int SampleRate => Rate;

        public int Read(Span<float> destination) => 0;
    }

    [Fact]
    public async Task WessexRecording_OneHopAndTwo()
    {
        var bursts = await HearAsync("wessex-burst.wav");
        var burst = Assert.Single(bursts);
        var series = BurstChannel.Measure(burst.Samples(), burst.EndSample, burst.Lock, burst.PayloadBits, out var found);
        Assert.Equal(3, found!.Superframes);
        var p = ChannelAnalysis.Analyse(series!)!;

        // Python: SNR 16.7 dB, floor -36.7 dB, RMS 0.282 ms, 2F +2.03 ms from the first path
        // found, -17.1 dB. The C# picture (packet-net/pdn-mailcast#60) instead re-references
        // every mode to the strongest path, which here sits a little later within its own mode
        // than the single earliest tap Python measured from, hence the slightly smaller gap.
        Assert.Equal(16.7, p.SnrDb, 0.5);
        Assert.Equal(0.28, p.DelaySpreadMs, 0.02);
        Assert.Equal(2, p.Modes.Count);
        Assert.Equal(1.93, p.Modes[1].DelayMs, 0.05);
        Assert.Equal(-17.1, p.Modes[1].PowerDb, 1.0);

        var report = ChannelReport.Summarise(Noon, Noon, [p], 1, Wessex);
        Assert.Equal(["1F", "2F"], report.Modes.Select(m => m.Label));
        Assert.InRange(report.VirtualHeightKm!.Value, 280, 305);
        Assert.StartsWith("2 paths: 1 hop, and 2 hops 1.9 ms later, 17 dB weaker. Reflection about 290 km up. Doppler spread 0.2 Hz: steady", report.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ei4hqRecording_ThreeHops()
    {
        var burst = Assert.Single(await HearAsync("ei4hq-burst.wav"));
        var p = Picture(burst.Samples(), burst.EndSample, burst.PayloadBits, burst.Lock);

        Assert.Equal(3, p.Modes.Count);
        Assert.Equal(1.80, p.Modes[1].DelayMs, 0.05);
        Assert.Equal(-8.5, p.Modes[1].PowerDb, 1.0);
        Assert.Equal(4.01, p.Modes[2].DelayMs, 0.05);
        Assert.Equal(-22.1, p.Modes[2].PowerDb, 1.5);

        var report = ChannelReport.Summarise(Noon, Noon, [p], 1, Ei4hq);
        Assert.Equal(["1F", "2F", "3F"], report.Modes.Select(m => m.Label));
        Assert.Equal(502, report.DistanceKm);
        Assert.Contains("3 hops 4.0 ms later, 22 dB weaker", report.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void Geometry_DistancesAndHeights()
    {
        Assert.Equal(136, PathGeometry.DistanceKm(Wessex, PathGeometry.Gb7rdg), 1.0);
        Assert.Equal(502, PathGeometry.DistanceKm(Ei4hq, PathGeometry.Gb7rdg), 1.0);
        // Straight up and down, 300 km: 600 km of path, 2.0 ms.
        Assert.Equal(2.0013, PathGeometry.DelayMs(0, 1, 300), 0.001);
        double gap = PathGeometry.DelayMs(136, 2, 270) - PathGeometry.DelayMs(136, 1, 270);
        Assert.Equal(270, PathGeometry.HeightFromGap(136, gap)!.Value, 0.01);
        Assert.Null(PathGeometry.HeightFromGap(136, 20));
        // A gap small enough to imply a height under 200 km is never reported (packet-net/pdn-mailcast#60):
        // no height is better than an implausible one.
        double tooSmall = PathGeometry.DelayMs(136, 2, 150) - PathGeometry.DelayMs(136, 1, 150);
        Assert.Null(PathGeometry.HeightFromGap(136, tooSmall));
    }

    [Theory]
    [InlineData(502, new[] { 0, 1.80, 4.01 }, new[] { "1F", "2F", "3F" })]
    [InlineData(136, new[] { 0, 1.93 }, new[] { "1F", "2F" })]
    // A mode 2.56 ms after the first and another at 3.51: the second is the 2F (a high
    // reflection, about 390 km), not a 3F with no 2F before it.
    [InlineData(136, new[] { 0, 2.56, 3.51 }, new[] { "1F", "2F", null })]
    [InlineData(220, new[] { 0, 1.74, 2.44 }, new[] { "1F", "2F", null })]
    [InlineData(136, new[] { 0.0 }, new[] { "1F" })]
    // CT 150, 534 km, 2026-10-08 11:00 UTC (packet-net/pdn-mailcast#60): a sidelobe at 0.89 ms,
    // 12 dB too close after 1F to be a real 2F at a plausible F height, is left unlabelled; the
    // true 2F is the one at 2.08 ms (the bug called the sidelobe "2F" at an implausible 188 km).
    [InlineData(534, new[] { 0, 0.89, 2.08 }, new[] { "1F", null, "2F" })]
    // CT 150, 534 km, 2026-10-08 13:00 UTC (packet-net/pdn-mailcast#60): at 534 km a real 2F is
    // never less than about 1 ms after 1F, so +0.53 ms is too soon to be one and is left
    // unlabelled (a sidelobe); +1.08 ms does fall where a 2F at a plausible F height would be, so
    // it is named 2F. The bug paired +0.53 and +1.08 up as "1F" and "2F" instead and from their
    // 0.55 ms gap invented an implausible 137 km reflection, below the F layer's 200 km floor.
    [InlineData(534, new[] { 0, 0.53, 1.08 }, new[] { "1F", null, "2F" })]
    public void Labels_NameTheHops(double km, double[] delays, string?[] expected)
    {
        var (labels, _) = PathGeometry.Label(km, delays);
        Assert.Equal(expected, labels);
    }

    [Fact]
    public void Labels_NeverInventAHeightOutsideThePlausibleRange()
    {
        // The two real CT 150 cases from packet-net/pdn-mailcast#60: both get a height from the
        // confirmed 1F/2F pair, both inside 200 to 450 km, and neither is the bug's wrong answer.
        var (elevenUtc, elevenHeight) = PathGeometry.Label(534, [0, 0.89, 2.08]);
        Assert.Equal(new string?[] { "1F", null, "2F" }, elevenUtc);
        Assert.InRange(elevenHeight!.Value, 200, 450);
        Assert.NotEqual(188, elevenHeight.Value);

        var (thirteenUtc, thirteenHeight) = PathGeometry.Label(534, [0, 0.53, 1.08]);
        Assert.Equal(new string?[] { "1F", null, "2F" }, thirteenUtc);
        Assert.InRange(thirteenHeight!.Value, 200, 450);
        Assert.NotEqual(137, thirteenHeight.Value);

        // Nothing fits any plausible F height at all (the gaps are both far too small for 300
        // km): no hop beyond the first is named, and no height is given, rather than one outside
        // the plausible range.
        var (nothingFits, noHeight) = PathGeometry.Label(300, [0, 0.1, 0.2]);
        Assert.Equal(new string?[] { "1F", null, null }, nothingFits);
        Assert.Null(noHeight);
    }

    [Fact]
    public void OneMeasurement_NamesModesAfterTheStrongestPath_EvenAtANegativeDelay()
    {
        // A weak artefact 0.8 ms ahead of the real (and strongest) first path, which is itself
        // 1.9 ms ahead of a real second hop. Naming modes after whichever arrived first (the
        // bug in packet-net/pdn-mailcast#60) would have put the real path at +0.8 ms and the
        // second hop at +2.7 ms, offsetting both from where the profile's power actually peaks.
        // One measurement cannot yet tell a real, if unusual, early weak path from a processing
        // sidelobe (ChannelReport.Summarise does, from several measurements), so nothing here is
        // thrown away: the artefact keeps its place, at a negative delay from the strongest.
        var paths = new[] { new SynthPath(0, -15), new SynthPath(0.8, 0), new SynthPath(2.7, -16) };
        var (audio, end, bits) = Synthesise(6000, seed: 7, paths, snrDb: 15);
        var p = Picture(audio, end, bits);

        Assert.Equal(3, p.Modes.Count);
        Assert.Equal(-0.8, p.Modes[0].DelayMs, 0.05);
        Assert.Equal(-15, p.Modes[0].PowerDb, 1.0);
        Assert.Equal(0, p.Modes[1].DelayMs, 0.03);
        Assert.Equal(0, p.Modes[1].PowerDb, 0.5);
        Assert.Equal(1.9, p.Modes[2].DelayMs, 0.05);
        Assert.Equal(-16, p.Modes[2].PowerDb, 1.0);
    }

    [Fact]
    public void SidelobeConsistentlyAheadOfTheStrongestPath_IsLeftOutOfTheReport()
    {
        // The same artefact as above, repeated over several measurements: consistently 0.8 ms
        // ahead of the strongest path, it clusters and would otherwise be named a mode of its
        // own (an impossible negative-delay "hop"). ChannelReport.Summarise leaves it out.
        var paths = new[] { new SynthPath(0, -15), new SynthPath(0.8, 0), new SynthPath(2.7, -16) };
        var pictures = Enumerable.Range(0, 4)
            .Select(seed =>
            {
                var (audio, end, bits) = Synthesise(6000, seed, paths, snrDb: 15);
                return Picture(audio, end, bits);
            })
            .ToList();

        var report = ChannelReport.Summarise(Noon, Noon, pictures, pictures.Count, Wessex);
        Assert.True(report.Enough);
        Assert.Equal(2, report.Modes.Count);
        Assert.Equal(0, report.Modes[0].DelayMs);
        Assert.Equal(1.9, report.Modes[1].DelayMs, 0.1);
        Assert.All(report.Modes, m => Assert.True(m.DelayMs >= 0, $"a mode at {m.DelayMs} ms, before the strongest path"));
    }

    [Fact]
    public void Words_SayWhatTheSlotWasLike()
    {
        var report = new ChannelReport
        {
            Enough = true,
            Modes =
            [
                new ChannelMode { Label = "1F", DelayMs = 0, PowerDb = 0 },
                new ChannelMode { Label = "2F", DelayMs = 1.8, PowerDb = -9.5 },
                new ChannelMode { Label = "3F", DelayMs = 4.0, PowerDb = -21.6 },
            ],
            VirtualHeightKm = 311,
            DopplerSpreadHz = 0.7,
        };
        Assert.Equal(
            "3 paths: 1 hop; 2 hops 1.8 ms later, 10 dB weaker; and 3 hops 4.0 ms later, 22 dB weaker. Reflection about 310 km up. "
            + "Doppler spread 0.7 Hz: a little unsteady (1200 bps should still cope).",
            ChannelReport.Describe(report));
        // Unknown place: no hops named.
        Assert.Equal(
            "2 paths: the second 1.9 ms after the first, 17 dB weaker. Doppler spread 1.5 Hz: unsteady (600 bps copes better).",
            ChannelReport.Describe(new ChannelReport
            {
                Enough = true,
                Modes = [new ChannelMode { PowerDb = 0 }, new ChannelMode { DelayMs = 1.93, PowerDb = -16.6 }],
                DopplerSpreadHz = 1.5,
            }));
        Assert.Equal(ChannelReport.TooLittle, ChannelReport.Summarise(Noon, Noon, [], 3, Wessex).Words);
    }

    internal static CapturedBurst Captured(int seed, DateTimeOffset heard)
    {
        var (audio, end, bits) = Synthesise(4000, seed, [new SynthPath(0, 0), new SynthPath(1.9, -16)], snrDb: 15);
        return new CapturedBurst([.. audio.Select(s => (Half)s)], end, Wn4, bits, heard, 0);
    }

    [Fact]
    public async Task Watch_MeasuresOnceTheSlotIsQuiet_AndKeepsTheDay()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Noon.AddMinutes(3));
        string file = Path.Combine(dir.Path, ChannelWatch.FileName);
        var log = new List<string>();
        var measured = new TaskCompletionSource<ChannelReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothKept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var watch = new ChannelWatch(time, line => { lock (log) { log.Add(line); } }, file, () => Wessex) { Rest = false })
        {
            // The timer that counts is the one set once both bursts are kept: one set after the
            // first alone is put off again by the second.
            watch.QuietWaiting += kept =>
            {
                if (kept == 2)
                {
                    bothKept.TrySetResult();
                }
            };
            watch.Measured += r => measured.TrySetResult(r);
            watch.Offer(Captured(1, time.GetUtcNow()), Noon);
            watch.Offer(Captured(2, time.GetUtcNow()), Noon);
            Assert.Equal(2, watch.Waiting);
            Assert.Null(watch.Latest);

            // Nothing is measured while the slot may still be on.
            await bothKept.Task.WaitAsync(TimeSpan.FromSeconds(10));
            time.Advance(ChannelWatch.Quiet - TimeSpan.FromSeconds(1));
            await Task.Delay(50);
            Assert.False(measured.Task.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(1));
            var report = await measured.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(Noon, report.Slot);
            Assert.Equal(2, report.Measurements);
            Assert.Equal(["1F", "2F"], report.Modes.Select(m => m.Label));
            Assert.Equal(1.9, report.Modes[1].DelayMs, 0.05);
            Assert.Equal("bursts", report.Basis);
            await watch.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, watch.Waiting);
            Assert.Same(report, watch.Latest);
            Assert.Contains(log, l => l.StartsWith("channel: the 12:00 UTC slot, from 2 of 2 bursts", StringComparison.Ordinal));
        }

        // Kept across a restart, and dropped once a day old.
        await using (var again = new ChannelWatch(time, _ => { }, file, () => Wessex))
        {
            Assert.Equal(Noon, again.Latest!.Slot);
            Assert.Equal(1.9, again.Latest.Modes[1].DelayMs, 0.05);
            time.Advance(TimeSpan.FromHours(24));
            Assert.Null(again.Latest);
        }
    }

    [Fact]
    public async Task Watch_ANewSlotEndsTheLast_AndTheAudioStoppingEndsTheNew()
    {
        var time = new FakeTimeProvider(Noon.AddMinutes(3));
        var reports = new List<ChannelReport>();
        await using var watch = new ChannelWatch(time, _ => { }, null, () => null) { Rest = false };
        watch.Measured += r => { lock (reports) { reports.Add(r); } };
        watch.Offer(Captured(1, time.GetUtcNow()), Noon);
        watch.Offer(Captured(2, time.GetUtcNow()), Noon.AddHours(1));
        watch.SlotOver();
        await watch.IdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([Noon, Noon.AddHours(1)], reports.Select(r => r.Slot));
        // No place known: the paths are not named, but the height is given, for a typical UK path.
        Assert.All(reports[1].Modes, m => Assert.Null(m.Label));
        Assert.InRange(reports[1].VirtualHeightKm!.Value, 270, 310);
        Assert.StartsWith("2 paths: the second 1.9 ms after the first, 16 dB weaker.", reports[1].Words, StringComparison.Ordinal);
        Assert.Equal(2, watch.History.Count);
    }

    private static ReceiverConfig Config(string dir, string audio = "wav:/nonexistent.wav") => new()
    {
        Audio = audio,
        StateDirectory = dir,
        Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "secret" },
    };

    [Fact]
    public async Task Status_HasTheChannel()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Noon.AddMinutes(5));
        await using var host = new ReceiverHost(Config(dir.Path), time, _ => { });
        host.ChannelWatch.Rest = false;
        var page = new StatusPage(host, null, _ => { });
        var before = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine).GetProperty("channel");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("slot").ValueKind);
        Assert.Equal(ChannelTile.Nothing, before.GetProperty("words").GetString());

        host.ChannelWatch.Offer(Captured(5, time.GetUtcNow()), Noon);
        host.ChannelWatch.SlotOver();
        await host.ChannelWatch.IdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var channel = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine).GetProperty("channel");
        Assert.Equal("bursts", channel.GetProperty("basis").GetString());
        Assert.True(channel.GetProperty("enough").GetBoolean());
        Assert.Equal(Noon, channel.GetProperty("slot").GetDateTimeOffset());
        var modes = channel.GetProperty("modes").EnumerateArray().ToList();
        // A recording: where the receiver is is not known, so the paths are not named, but the
        // height is still worked out, for a typical UK path.
        Assert.All(modes, m => Assert.Equal(JsonValueKind.Null, m.GetProperty("label").ValueKind));
        Assert.Equal(1.9, modes[1].GetProperty("delayMs").GetDouble(), 0.05);
        Assert.Equal(JsonValueKind.Null, channel.GetProperty("distanceKm").ValueKind);
        foreach (string field in (string[])["delaySpreadMs", "dopplerSpreadHz", "fadeDb", "coherenceS", "virtualHeightKm"])
        {
            Assert.Equal(JsonValueKind.Number, channel.GetProperty(field).ValueKind);
        }
        Assert.Contains("Reflection about", channel.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Single(channel.GetProperty("history").EnumerateArray());
        Assert.True(channel.GetProperty("profile").GetProperty("db").GetArrayLength() > 50);
        Assert.Equal(0, channel.GetProperty("tooLong").GetInt32());
    }

    [Fact]
    public void Status_NeverCarriesNanOrInfinity()
    {
        // System.Text.Json refuses NaN and infinity, which would take the whole status down.
        var odd = new PathPicture
        {
            Basis = "bursts",
            Modes = [new PictureMode(0, 0, double.NaN, double.PositiveInfinity), new PictureMode(1.9, double.NegativeInfinity, null, double.NaN)],
            PathsMs = [0, 1.9],
            PathsDb = [0, double.NegativeInfinity],
            DelaySpreadMs = double.NaN,
            DopplerSpreadHz = double.PositiveInfinity,
            OffsetHz = double.NaN,
            FadeDb = double.PositiveInfinity,
            CoherenceS = double.NaN,
            SnrDb = double.NegativeInfinity,
            FloorDb = double.NaN,
            GoodSnapshots = 200,
            Profile = [double.NaN, double.PositiveInfinity, 0, 1, double.NegativeInfinity],
            ProfileStartMs = double.NaN,
            ProfileStepMs = 0.1,
        };
        var report = ChannelReport.Summarise(Noon, Noon, [odd, odd], 2, Wessex);
        string json = JsonSerializer.Serialize(report, ReceiverConfig.JsonLine);
        Assert.DoesNotContain("NaN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", json, StringComparison.Ordinal);
        Assert.Equal(-99, report.Modes[1].PowerDb);
        Assert.Null(report.DelaySpreadMs);
        Assert.Null(report.FadeDb);
    }

    [Fact]
    public void ChannelSlot_OnlyWithinASlot()
    {
        using var dir = new TempDirectory();
        var host = new ReceiverHost(Config(dir.Path, "ubersdr:wessex.zapto.org"), new FakeTimeProvider(Noon), _ => { });
        (DateTimeOffset, DateTimeOffset)? recording = null;
        Assert.Equal(Noon, host.ChannelSlot(Noon.AddMinutes(4), ref recording));
        // A burst between slots is someone else's, or a lock on noise: not filed under any slot.
        Assert.Null(host.ChannelSlot(Noon.AddMinutes(40), ref recording));
        Assert.Equal(Noon.AddHours(1), host.ChannelSlot(Noon.AddHours(1).AddMinutes(1), ref recording));
        // At night there is no slot at all.
        Assert.Null(host.ChannelSlot(Noon.AddHours(10).AddMinutes(2), ref recording));

        // A recording has no slots: each run of bursts is one.
        host.Slots.Schedule = null;
        Assert.Equal(Noon, host.ChannelSlot(Noon, ref recording));
        Assert.Equal(Noon, host.ChannelSlot(Noon.AddMinutes(3), ref recording));
        Assert.Equal(Noon.AddHours(2), host.ChannelSlot(Noon.AddHours(2), ref recording));
    }

    [Fact]
    public async Task Capture_OnlyBurstsAFrameWasReadFrom()
    {
        var pipeline = AudioPipeline.ForInput(new NoInput(), _ => { }, new FakeTimeProvider(Noon), watch: false);
        var kept = new List<CapturedBurst>();
        pipeline.BurstCaptured += kept.Add;
        var tx = new Ms110dTxSettings { WaveformNumber = 4 };

        // A burst that decodes but carries no frame: noise the modem locked to, or another station.
        var random = new byte[3000];
        new Random(5).NextBytes(random);
        for (int i = 0; i < random.Length; i++)
        {
            random[i] &= 1;
        }
        Feed(pipeline, new float[Rate]);
        Feed(pipeline, AudioPipeline.ToPipelineRate(new Ms110dModulator(tx).Modulate(random), Ms110dModulator.NativeRate));
        Feed(pipeline, new float[3 * Rate]);
        Assert.Empty(kept);

        // One with a frame in it is kept.
        var payload = new byte[120];
        new Random(6).NextBytes(payload);
        var modem = new Ms110dModem(Rate, _ => { }, tx);
        Feed(pipeline, modem.Modulate(Ax25UiFrame.Build(Samples.Source, OnAir.Destination, payload), 0));
        Feed(pipeline, new float[3 * Rate]);
        Assert.Single(kept);
        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task Decode_DeliversFirst_AndLeavesTheStateDirectoryAlone()
    {
        using var dir = new TempDirectory();
        var (samples, rate) = WavFile.ReadMono(Path.Combine(AppContext.BaseDirectory, "Channel", "wessex-burst.wav"));
        string wav = Path.Combine(dir.Path, "slot.wav");
        WavFile.WriteMono(wav, [.. new float[8 * rate], .. samples, .. new float[8 * rate]], rate);
        var log = new List<string>();
        await using var host = new ReceiverHost(Config(dir.Path, "wav:" + wav), new FakeTimeProvider(Noon), line => { lock (log) { log.Add(line); } });

        await host.DecodeOnceAsync(wav, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));

        int decoded = log.FindIndex(l => l.StartsWith("decode:", StringComparison.Ordinal));
        int measured = log.FindIndex(l => l.StartsWith("channel: the", StringComparison.Ordinal));
        Assert.True(decoded >= 0 && measured > decoded, string.Join("\n", log));
        Assert.Contains("the second 1.9 ms after the first", log[measured], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir.Path, ChannelWatch.FileName)));
    }

    [Fact]
    public void WebSdrPlace_FromItsDescription()
    {
        var place = AudioPipeline.PlaceInDescription("""{"receiver":{"callsign":"WESSEX","gps":{"lat":50.74013,"lon":-2.630721,"maidenhead":"IO80qr"}}}""");
        Assert.Equal(50.74013, place!.Latitude, 5);
        Assert.Equal("IO80qr", place.Locator);
        Assert.Equal("IO91lk", AudioPipeline.PlaceInDescription("""{"receiver":{"gps":{"lat":0,"lon":0,"maidenhead":"IO91lk"}}}""")!.Locator);
        Assert.Null(AudioPipeline.PlaceInDescription("""{"receiver":{"name":"no gps"}}"""));
        Assert.Null(AudioPipeline.PlaceInDescription("not json"));
    }
}
