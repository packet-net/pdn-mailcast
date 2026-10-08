using System.Text.Json;
using M0LTE.Radio.Audio;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The page's speed tile: which MS110D waveform the frames come on, by the modem's autobaud,
/// burst by burst and counted over each slot. GB7RDG takes turns on WN4 (1200 bps) and WN3 (600 bps).
/// </summary>
public class SpeedTests
{
    private const int Rate = OnAir.SampleRate;
    private const int Block = Rate / 10;
    private const string Wn4 = "ms110d-wn4";
    private const string Wn3 = "ms110d-wn3";
    private static readonly SlotSchedule Hourly = new(new TimeOnly(0, 0), 60);

    /// <summary>A directory naming <paramref name="mode"/> as the waveform the slot went out on (null for a head end that does not say).</summary>
    private static BroadcastDirectory Directory(string? mode) => new(DateOnly.FromDateTime(DateTime.UtcNow), [], mode: mode);

    private static DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 10, 6, hour, minute, second, TimeSpan.Zero);

    private sealed class NoInput : IAudioInput
    {
        public int SampleRate => Rate;

        public int Read(Span<float> destination) => 0;
    }

    [Theory]
    [InlineData(4, "1200 bps (WN4)")]
    [InlineData(3, "600 bps (WN3)")]
    [InlineData(13, "2400 bps (WN13)")]
    public void Waveform_InWords(int wn, string words)
    {
        Assert.Equal(words, Waveform.Words(Waveform.Name(wn)));
        Assert.Equal(wn, Waveform.Number(Waveform.Name(wn)));
    }

    [Fact]
    public void Waveform_NotInTheTable_IsGivenByName()
    {
        Assert.Null(Waveform.Bps("ms110d-wn11"));
        Assert.Equal("ms110d-wn11", Waveform.Words("ms110d-wn11"));
        Assert.Null(Waveform.Number("qpsk2400"));
    }

    [Fact]
    public void Slot_CountsFramesByWaveform_AndTheNextSlotStartsAfresh()
    {
        var time = new FakeTimeProvider(At(12, 0, 30));
        var log = new List<string>();
        var slots = new SlotTracker(time, log.Add, Hourly);

        for (int i = 0; i < 5; i++)
        {
            slots.OnFrame(Wn4);
            time.Advance(TimeSpan.FromSeconds(20));
        }
        slots.OnFrame(Wn3);
        slots.OnFrame(null); // a frame the modem could not say the waveform of

        var slot = slots.Last!;
        Assert.Equal(7, slot.FramesHeard);
        Assert.Equal(5, slot.FrameCounts[Wn4]);
        Assert.Equal(1, slot.FrameCounts[Wn3]);
        Assert.Equal(Wn4, slot.Waveform);
        Assert.Contains("slot: 1 frame heard on 1200 bps (WN4)", log);
        Assert.Contains(log, l => l.Contains("600 bps (WN3), a different waveform", StringComparison.Ordinal));
        Assert.All(log, l => Assert.DoesNotMatch("[^\\x20-\\x7E]", l));

        // The next slot is on the other waveform, and is counted on its own.
        time.SetUtcNow(At(13, 0, 20));
        slots.OnFrame(Wn3);
        slots.OnFrame(Wn3);

        slot = slots.Last!;
        Assert.Equal(At(13, 0), slot.Scheduled);
        Assert.Equal(2, slot.FramesHeard);
        Assert.Equal(Wn3, slot.Waveform);
        Assert.False(slot.FrameCounts.ContainsKey(Wn4));
    }

    [Fact]
    public void Slot_WithNoOneWaveformAhead_IsMixed()
    {
        var time = new FakeTimeProvider(At(12, 1));
        var slots = new SlotTracker(time, _ => { }, Hourly);

        slots.OnFrame(Wn4);
        time.Advance(TimeSpan.FromSeconds(30));
        slots.OnFrame(Wn3);

        Assert.Equal(SlotSummary.Mixed, slots.Last!.Waveform);
    }

    [Fact]
    public void Slot_WithNoWaveformKnown_SaysNone()
    {
        var slots = new SlotTracker(new FakeTimeProvider(At(12, 1)), _ => { }, Hourly);

        slots.OnFrame();

        Assert.Null(slots.Last!.Waveform);
        Assert.Empty(slots.Last.FrameCounts);
    }

    [Fact]
    public void Slot_KeepsTheDirectorysWaveform_AndATonesStartKeepsTheCounts()
    {
        var time = new FakeTimeProvider(At(12, 0, 10));
        var slots = new SlotTracker(time, _ => { }, Hourly);

        slots.OnDirectory(Directory(Wn4)); // nothing heard yet: nothing to put it on
        Assert.Null(slots.Last);

        slots.OnFrame(Wn4);
        slots.OnDirectory(Directory(null)); // a head end that does not say
        Assert.Null(slots.Last!.ListedWaveform);
        slots.OnDirectory(Directory(Wn4));
        time.Advance(TimeSpan.FromSeconds(15));
        slots.OnTone(new ToneReport(1800, 0, 12, TimeSpan.FromSeconds(10)));

        Assert.Equal(Wn4, slots.Last!.ListedWaveform);
        Assert.Equal(1, slots.Last.FrameCounts[Wn4]);
    }

    [Fact]
    public void BurstWatch_ShowsTheBurstNow_ThenTheLastOneFramesCameFrom()
    {
        var time = new FakeTimeProvider(At(12, 2));
        var watch = new BurstWatch(time);
        Assert.Null(watch.Shown);

        // Locked to WN4: shown at once, before any frame.
        watch.AfterBlock(4);
        Assert.Equal(new HeardBurst(Wn4, At(12, 2), 0, null), watch.Shown);
        watch.OnFrame(4);
        watch.OnFrame(4);
        time.Advance(TimeSpan.FromSeconds(3));
        watch.AfterBlock(null);

        Assert.Equal(new HeardBurst(Wn4, At(12, 2), 2, At(12, 2, 3)), watch.Shown);
        Assert.False(watch.Shown!.Live);

        // A preamble heard through noise, with nothing read from it: shown while it lasts, then
        // the last burst frames came from is shown again.
        watch.AfterBlock(-1); // reading the preamble: no waveform yet
        Assert.Equal(Wn4, watch.Shown!.Waveform);
        watch.AfterBlock(3);
        Assert.True(watch.Shown!.Live);
        Assert.Equal(Wn3, watch.Shown.Waveform);
        watch.AfterBlock(null);
        Assert.Equal(Wn4, watch.Shown!.Waveform);

        // One burst straight after another on a different waveform.
        watch.AfterBlock(4);
        watch.OnFrame(3);
        Assert.Equal(new HeardBurst(Wn3, At(12, 2, 3), 1, null), watch.Shown);
    }

    [Fact]
    public void ModemReceiver_IsReachable()
    {
        // Pins the one thing read from inside pdn-soundmodem: see BurstWatch.ReceiverOf.
        Assert.NotNull(BurstWatch.ReceiverOf(new Ms110dModem(Rate, _ => { })));
    }

    [Fact]
    public async Task Pipeline_SaysWhichWaveformEachFrameCameOn()
    {
        var log = new List<string>();
        var time = new FakeTimeProvider(At(12, 3));
        var pipeline = AudioPipeline.ForInput(new NoInput(), log.Add, time, watch: false);
        var heard = new List<string?>();
        pipeline.Channel.FrameReceived += (_, _) => heard.Add(pipeline.FrameWaveform);

        Feed(pipeline, new float[Rate]);
        Feed(pipeline, Burst(4, seed: 1));
        Feed(pipeline, new float[Rate]);
        Assert.Equal(new HeardBurst(Wn4, pipeline.Burst.Shown!.Started, 1, pipeline.Burst.Shown.Ended), pipeline.Burst.Shown);
        Assert.False(pipeline.Burst.Shown.Live);

        Feed(pipeline, Burst(3, seed: 2));
        Feed(pipeline, new float[Rate]);

        Assert.Equal([Wn4, Wn3], heard);
        Assert.Equal(Wn3, pipeline.Burst.Shown!.Waveform);
        Assert.Equal(1, pipeline.Burst.Shown.Frames);
        Assert.Null(pipeline.FrameWaveform);
        Assert.DoesNotContain(log, l => l.Contains("cannot show the speed", StringComparison.Ordinal));
        await pipeline.DisposeAsync();
    }

    private static float[] Burst(int wn, int seed)
    {
        var payload = new byte[120];
        new Random(seed).NextBytes(payload);
        byte[] frame = Ax25UiFrame.Build(Samples.Source, OnAir.Destination, payload);
        return new Ms110dModem(Rate, _ => { }, new Ms110dTxSettings { WaveformNumber = wn }).Modulate(frame, 0);
    }

    private static void Feed(AudioPipeline pipeline, float[] audio)
    {
        for (int offset = 0; offset < audio.Length; offset += Block)
        {
            pipeline.Feed(audio.AsSpan(offset, Math.Min(Block, audio.Length - offset)));
        }
    }

    [Fact]
    public async Task Status_GivesTheSlotsWaveforms_AndTheBurst()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(At(12, 0, 40));
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path };
        await using var host = new ReceiverHost(config, time, _ => { });
        var page = new StatusPage(host, null, _ => { }); // never started, so it holds no port

        var before = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("burst").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("slot").ValueKind);

        host.Slots.OnFrame(Wn4);
        host.Slots.OnFrame(Wn4);
        host.Slots.OnFrame(Wn3);
        host.Slots.OnDirectory(Directory(Wn4));

        var slot = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine).GetProperty("slot");
        Assert.Equal(Wn4, slot.GetProperty("waveform").GetString());
        Assert.Equal(2, slot.GetProperty("frameCounts").GetProperty(Wn4).GetInt32());
        Assert.Equal(1, slot.GetProperty("frameCounts").GetProperty(Wn3).GetInt32());
        Assert.Equal("1200 bps (WN4)", slot.GetProperty("heard")[0].GetProperty("words").GetString());
        Assert.Equal(1, slot.GetProperty("heard")[1].GetProperty("frames").GetInt32());
        Assert.Equal(Wn4, slot.GetProperty("listedWaveform").GetString());

        var burst = JsonSerializer.SerializeToElement(StatusPage.BurstView(new HeardBurst(Wn3, At(12, 1), 4, null)), ReceiverConfig.JsonLine);
        Assert.Equal(Wn3, burst.GetProperty("waveform").GetString());
        Assert.Equal(600, burst.GetProperty("bps").GetInt32());
        Assert.Equal("600 bps (WN3)", burst.GetProperty("words").GetString());
        Assert.True(burst.GetProperty("live").GetBoolean());
        Assert.Equal(4, burst.GetProperty("frames").GetInt32());
    }

    [Theory]
    [InlineData("plughw:CARD=Device,DEV=0", "target")]
    [InlineData("ubersdr:wessex.zapto.org", "clippingOnly")]
    [InlineData("wav:/tmp/slot.wav", "clippingOnly")]
    public void LevelAdvice_DependsOnWhereTheAudioComesFrom(string audio, string advice)
    {
        Assert.Equal(advice, StatusPage.LevelAdvice(audio));
    }

    [Fact]
    public async Task Status_GivesTheLevelAdvice_AndThePageHonoursIt()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(At(12, 5)), _ => { });
        var page = new StatusPage(host, null, _ => { });

        var level = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine).GetProperty("level");
        Assert.Equal("clippingOnly", level.GetProperty("advice").GetString());

        string html = await new StreamReader(typeof(StatusPage).Assembly.GetManifestResourceStream("Mailcast.Receiver.Web.index.html")!).ReadToEndAsync();
        Assert.Contains("status.level.advice === \"target\"", html, StringComparison.Ordinal);
        Assert.Contains("Not heard yet.", html, StringComparison.Ordinal);
    }
}
