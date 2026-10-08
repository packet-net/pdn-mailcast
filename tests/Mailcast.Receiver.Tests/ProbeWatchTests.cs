using System.Text.Json;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Waterfall;
using Mailcast.Receiver.Feedback;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;
using P = Mailcast.Receiver.Tests.ProbeSlot.Path;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The probe through the receiver: kept from the audio thread after the tone, measured on the
/// channel watch's worker with the slot's bursts, and shown on the tile, the API and in the
/// daily report. And with no probe, everything as it was.
/// </summary>
public class ProbeWatchTests
{
    private const int Rate = OnAir.SampleRate;

    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly GroundPlace Wessex = GroundPlace.FromLocator("IO80qr")!;

    private sealed class NoInput : IAudioInput
    {
        public int SampleRate => Rate;

        public int Read(Span<float> destination) => 0;
    }

    private static void Feed(AudioPipeline pipeline, float[] audio)
    {
        for (int offset = 0; offset < audio.Length; offset += 4800)
        {
            pipeline.Feed(audio.AsSpan(offset, Math.Min(4800, audio.Length - offset)));
        }
    }

    /// <summary>A probe as the pipeline keeps it, from a synthetic slot opening.</summary>
    private static CapturedProbe Kept(P[] paths, double snrDb, int seed, DateTimeOffset heard, bool probe = true)
    {
        var made = ProbeSlot.Make(paths, snrDb, seed, probe: probe);
        var (audio, end) = ProbeSlot.Kept(made, 0.4);
        var tone = new ToneReport(OnAir.CentreAudioHz, 0, 30, TimeSpan.FromSeconds(10));
        return new CapturedProbe([.. audio.Select(s => (Half)s)], tone, end, heard, 0);
    }

    [Fact]
    public async Task Pipeline_KeepsTheAudioAfterEachTone_OnTheAudioThread_AndNoMore()
    {
        var made = ProbeSlot.Make([new P(1.0, 0), new P(2.9, -6)], 10, seed: 1);
        var pipeline = AudioPipeline.ForInput(new NoInput(), _ => { }, new FakeTimeProvider(Noon), watch: false);
        var kept = new List<CapturedProbe>();
        var threads = new List<int>();
        pipeline.ProbeCaptured += p =>
        {
            kept.Add(p);
            threads.Add(Environment.CurrentManagedThreadId);
        };
        Feed(pipeline, made.Audio);
        Feed(pipeline, new float[12 * Rate]);
        await pipeline.DisposeAsync();

        var probe = Assert.Single(kept);
        Assert.Equal(Environment.CurrentManagedThreadId, threads[0]);
        Assert.Equal(CapturedProbe.BeforeSeconds + CapturedProbe.AfterSeconds, probe.Audio.Length / (double)Rate, 0.01);
        // About 1.25 MB a probe, as half-precision floats.
        Assert.InRange(probe.Audio.Length * 2, 1_200_000, 1_300_000);
        // Where the tone detector put the end, within its block of the truth.
        double end = (probe.FirstSample / (double)Rate) + probe.ToneEndSeconds;
        Assert.InRange(end - made.ToneEndSeconds, -1.1, 1.1);
        Assert.Equal(OnAir.CentreAudioHz, probe.Tone.FrequencyHz, 0.2);

        // What was kept is where the probe is.
        var (picture, _) = ProbeChannel.Analyse(probe.Samples(), probe.ToneEndSeconds, probe.Tone.FrequencyHz)!.Value;
        Assert.Equal(2, picture.Modes.Count);
        Assert.Equal(1.9, picture.Modes[1].DelayMs, 0.01);
    }

    [Fact]
    public async Task Watch_APathTooWeakToDecode_IsMeasuredFromTheProbe()
    {
        // -12 dB in 3 kHz: no MS110D burst decodes, so only the probe is kept.
        var time = new FakeTimeProvider(Noon.AddMinutes(3));
        var log = new List<string>();
        await using var watch = new ChannelWatch(time, line => { lock (log) { log.Add(line); } }, null, () => Wessex) { Rest = false };
        watch.OfferProbe(Kept([new P(1.0, 0), new P(2.9, -3)], -12, 2, time.GetUtcNow()), Noon);
        watch.SlotOver();
        await watch.IdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var report = watch.Latest!;
        Assert.Equal("probe", report.Basis);
        Assert.True(report.Enough);
        Assert.Null(report.Other);
        Assert.Equal(1, report.Measurements);
        Assert.Equal(["1F", "2F"], report.Modes.Select(m => m.Label));
        Assert.Equal(1.9, report.Modes[1].DelayMs, 0.02);
        Assert.InRange(report.SnrDb!.Value, -14, -10);
        Assert.Contains(log, l => l.StartsWith("channel: the 12:00 UTC slot, from the probe, measured in", StringComparison.Ordinal));
        Assert.Equal('p', ChannelSummary.From(report)!.Basis);
    }

    [Fact]
    public async Task Watch_BothWays_GivesTheBetter_AndKeepsTheOtherBeside_ItOnTheTileAndInTheApi()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Noon.AddMinutes(5));
        var config = new ReceiverConfig
        {
            Audio = "wav:/nonexistent.wav",
            StateDirectory = dir.Path,
            Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "secret" },
        };
        await using var host = new ReceiverHost(config, time, _ => { });
        host.ChannelWatch.Rest = false;
        host.ChannelWatch.OfferProbe(Kept([new P(1.0, 0), new P(2.9, -16)], 15, 3, time.GetUtcNow()), Noon);
        host.ChannelWatch.Offer(ChannelTests.Captured(5, time.GetUtcNow()), Noon);
        host.ChannelWatch.Offer(ChannelTests.Captured(6, time.GetUtcNow()), Noon);
        host.ChannelWatch.SlotOver();
        await host.ChannelWatch.IdleAsync().WaitAsync(TimeSpan.FromSeconds(120));

        var report = host.ChannelWatch.Latest!;
        Assert.NotNull(report.Other);
        Assert.NotEqual(report.Basis, report.Other.Basis);
        Assert.True(report.Enough && report.Other.Enough);
        // The better reaches deeper.
        Assert.True(report.FloorDb <= report.Other.FloorDb);
        var probe = report.Basis == "probe" ? report : report.Other;
        var bursts = report.Basis == "probe" ? report.Other : report;
        Assert.Equal(1.9, probe.Modes[1].DelayMs, 0.02);
        Assert.Equal(1.9, bursts.Modes[1].DelayMs, 0.05);
        Assert.Equal(2, bursts.Measurements);

        var page = new StatusPage(host, null, _ => { });
        var channel = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine).GetProperty("channel");
        Assert.Equal(report.Basis, channel.GetProperty("basis").GetString());
        var other = channel.GetProperty("other");
        Assert.Equal(report.Other.Basis, other.GetProperty("basis").GetString());
        Assert.True(other.GetProperty("enough").GetBoolean());
        Assert.Equal(2, other.GetProperty("modes").GetArrayLength());
        Assert.Equal(JsonValueKind.Number, channel.GetProperty("floorDb").ValueKind);
        Assert.Equal(report.Basis, channel.GetProperty("history")[0].GetProperty("basis").GetString());
        Assert.Equal(report.Basis == "probe" ? 'p' : 'b', ChannelSummary.From(report)!.Basis);

        // Kept across a restart, the other measurement too.
        await using var again = new ChannelWatch(time, _ => { }, Path.Combine(dir.Path, ChannelWatch.FileName), () => null);
        Assert.Equal(report.Other.Basis, again.Latest!.Other!.Basis);
    }

    [Fact]
    public async Task Watch_WithNoProbeAfterTheTone_IsExactlyAsBefore()
    {
        // An older head end: the tone is heard and its audio kept, but there is no probe in it.
        // The report is the bursts' alone, as it was before the probe, to the last field.
        var time = new FakeTimeProvider(Noon.AddMinutes(3));
        var withKept = new List<string>();
        var without = new List<string>();
        ChannelReport a, b;
        await using (var watch = new ChannelWatch(time, line => { lock (withKept) { withKept.Add(line); } }, null, () => Wessex) { Rest = false })
        {
            watch.OfferProbe(Kept([new P(1.0, 0)], 10, 4, time.GetUtcNow(), probe: false), Noon);
            watch.Offer(ChannelTests.Captured(7, time.GetUtcNow()), Noon);
            watch.Offer(ChannelTests.Captured(8, time.GetUtcNow()), Noon);
            watch.SlotOver();
            await watch.IdleAsync().WaitAsync(TimeSpan.FromSeconds(60));
            a = watch.Latest!;
        }
        await using (var watch = new ChannelWatch(time, line => { lock (without) { without.Add(line); } }, null, () => Wessex) { Rest = false })
        {
            watch.Offer(ChannelTests.Captured(7, time.GetUtcNow()), Noon);
            watch.Offer(ChannelTests.Captured(8, time.GetUtcNow()), Noon);
            watch.SlotOver();
            await watch.IdleAsync().WaitAsync(TimeSpan.FromSeconds(60));
            b = watch.Latest!;
        }
        Assert.Equal("bursts", a.Basis);
        Assert.Null(a.Other);
        Assert.Equal(
            JsonSerializer.Serialize(b with { ComputeSeconds = 0 }, ReceiverConfig.Json),
            JsonSerializer.Serialize(a with { ComputeSeconds = 0 }, ReceiverConfig.Json));
        string Line(List<string> log) => System.Text.RegularExpressions.Regex.Replace(log.Single(l => l.StartsWith("channel: the", StringComparison.Ordinal)), @"measured in [0-9.]+ s", "");
        Assert.Equal(Line(without), Line(withKept));
        Assert.DoesNotContain("probe", Line(withKept), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Decode_ASlotWithTheProbe_MeasuresItWithTheBursts(bool probe)
    {
        using var dir = new TempDirectory();
        string wav = Path.Combine(dir.Path, "slot.wav");
        var frames = Enumerable.Range(0, 3).Select(i =>
        {
            var payload = new byte[180];
            new Random(i).NextBytes(payload);
            return Ax25UiFrame.Build(Samples.Source, OnAir.Destination, payload);
        }).ToList();
        SlotRecording.Write(wav, frames, new HashSet<int>(), snrDb: 15, toneOffsetHz: 1.5, seed: 3, probe: probe);
        var log = new List<string>();
        var config = new ReceiverConfig
        {
            Audio = "wav:" + wav,
            StateDirectory = dir.Path,
            Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "secret" },
        };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(Noon), line => { lock (log) { log.Add(line); } });
        await host.DecodeOnceAsync(wav, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(180));

        string line = log.Single(l => l.StartsWith("channel: the", StringComparison.Ordinal));
        if (probe)
        {
            Assert.Contains("from the probe and ", line, StringComparison.Ordinal);
            var report = host.ChannelWatch.Latest!;
            Assert.True(report.Basis == "probe" || report.Other?.Basis == "probe", line);
            var measured = report.Basis == "probe" ? report : report.Other!;
            Assert.Single(measured.Modes);
            Assert.Equal(1.5, measured.OffsetHz!.Value, 0.1);
        }
        else
        {
            Assert.DoesNotContain("probe", line, StringComparison.Ordinal);
            Assert.Null(host.ChannelWatch.Latest!.Other);
        }
    }

    [Fact]
    public async Task Decode_ARecordingThatEndsJustAfterTheProbe_StillMeasuresIt()
    {
        using var dir = new TempDirectory();
        string wav = Path.Combine(dir.Path, "probe.wav");
        var made = ProbeSlot.Make([new P(1.0, 0), new P(2.9, -6)], 5, seed: 6, tailSeconds: 0.5);
        WavFile.WriteMono(wav, made.Audio, Rate);
        var log = new List<string>();
        var config = new ReceiverConfig
        {
            Audio = "wav:" + wav,
            StateDirectory = dir.Path,
            Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "secret" },
        };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(Noon), line => { lock (log) { log.Add(line); } });
        await host.DecodeOnceAsync(wav, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));
        string line = log.Single(l => l.StartsWith("channel: the", StringComparison.Ordinal));
        Assert.Contains("from the probe, measured in", line, StringComparison.Ordinal);
        Assert.Contains("2 paths", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_AToneWithNoProbeAndNoBursts_SaysNothing_AsBefore()
    {
        var time = new FakeTimeProvider(Noon.AddMinutes(3));
        var log = new List<string>();
        await using var watch = new ChannelWatch(time, line => { lock (log) { log.Add(line); } }, null, () => Wessex) { Rest = false };
        watch.OfferProbe(Kept([new P(1.0, 0)], 10, 4, time.GetUtcNow(), probe: false), Noon);
        watch.SlotOver();
        await watch.IdleAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Null(watch.Latest);
        Assert.Empty(watch.History);
        Assert.DoesNotContain(log, l => l.StartsWith("channel:", StringComparison.Ordinal));
        Assert.Equal(0, watch.Waiting);
    }

    [Fact]
    public void Best_IsTheProbeWhenTheBurstsSayTooLittle_OrItReachesDeeper()
    {
        var bursts = new ChannelReport { Basis = "bursts", Enough = true, Measurements = 4, Kept = 5, FloorDb = -30 };
        var probe = new ChannelReport { Basis = "probe", Enough = true, Measurements = 1, Kept = 1, FloorDb = -36 };
        Assert.Equal("probe", ChannelReport.Best(bursts, probe).Basis);
        Assert.Equal("bursts", ChannelReport.Best(bursts, probe with { FloorDb = -25 }).Basis);
        Assert.Equal("probe", ChannelReport.Best(bursts with { Enough = false, Words = ChannelReport.TooLittle }, probe with { FloorDb = -20 }).Basis);
        // A probe not found leaves the bursts' report as it was.
        Assert.Same(bursts, ChannelReport.Best(bursts, probe with { Measurements = 0, Enough = false }));
        Assert.Same(bursts, ChannelReport.Best(bursts, null));
        // The probe's report keeps a bursts' one beside it only if any bursts were kept.
        Assert.Null(ChannelReport.Best(bursts with { Enough = false, Kept = 0, Measurements = 0 }, probe).Other);
    }
}
