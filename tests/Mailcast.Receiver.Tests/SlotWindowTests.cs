using System.Collections.Concurrent;
using M0LTE.Radio.Audio;
using Mailcast.Receiver.Hooks;
using Microsoft.Extensions.Time.Testing;
using M0LTE.Ms110d;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// A source heard all the time: a slot heard in full ends its window early, so "after" runs
/// then, and a sound card rests between slots, decoding only while the waterfall is watched.
/// </summary>
public class SlotWindowTests
{
    private const int Rate = OnAir.SampleRate;

    private static readonly DateTimeOffset SlotStart = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixture : IAsyncDisposable
    {
        public TempDirectory Dir { get; } = new();

        public FakeTimeProvider Time { get; }

        public ReceiverHost Host { get; }

        public ConcurrentQueue<string> Log { get; } = new();

        public Fixture(DateTimeOffset start, string audio)
        {
            Time = new FakeTimeProvider(start);
            var config = new ReceiverConfig { Audio = audio, StateDirectory = Dir.Path, Daylight = null };
            Host = new ReceiverHost(config, Time, Log.Enqueue);
        }

        /// <summary>Hears one frame, as the modem would, and waits for it to settle.</summary>
        public async Task HearAsync(byte[] frame)
        {
            Host.Intake.Offer(frame);
            await Host.Intake.DrainAsync(CancellationToken.None);
        }

        /// <summary>Hears the directory and the one bulletin in it, and the probe, then a quiet minute: the slot is heard in full.</summary>
        public async Task HearTheWholeSlotAsync()
        {
            foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
            {
                Host.Intake.Offer(frame);
                await Host.Intake.DrainAsync(CancellationToken.None);
            }
            Host.Slots.OnProbeCaptured();
            Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task SoundCard_ASlotHeardInFull_ClosesItsHookWindowThen()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(2), Samples.SoundCard);
        await f.HearTheWholeSlotAsync();
        var now = f.Time.GetUtcNow();

        f.Host.CheckSlotWindow();

        Assert.Equal(new HookWindow(SlotStart.AddMinutes(-2), now, SlotStart), f.Host.HookWindowAt(now));
        Assert.Contains(f.Log, line => line.StartsWith("audio: the 12:00 UTC slot is over early: ", StringComparison.Ordinal));
        // The rest of its window is between slots, until the next one's opens.
        Assert.Equal((SlotStart.AddMinutes(58), SlotStart.AddHours(1)), f.Host.BetweenSlots);
    }

    [Fact]
    public async Task SoundModem_ASlotHeardInFull_ClosesItsHookWindow_ButNeverRests()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(2), "soundmodem");
        await f.HearTheWholeSlotAsync();
        var now = f.Time.GetUtcNow();

        f.Host.CheckSlotWindow();

        Assert.Equal(now, f.Host.HookWindowAt(now)!.Value.Closes);
        Assert.Null(f.Host.BetweenSlots);
    }

    [Fact]
    public async Task SoundCard_ASlotNotYetHeardInFull_KeepsItsWindow()
    {
        await using var f = new Fixture(SlotStart.AddMinutes(3), Samples.SoundCard);

        f.Host.CheckSlotWindow();

        Assert.Equal(SlotStart.AddMinutes(12), f.Host.HookWindowAt(f.Time.GetUtcNow())!.Value.Closes);
        Assert.Null(f.Host.BetweenSlots);
    }

    [Fact]
    public async Task SoundCard_RestsBetweenSlots_AndDecodesFromTwoMinutesBefore()
    {
        await using var f = new Fixture(SlotStart.AddMinutes(5), Samples.SoundCard);
        await f.HearAsync(Samples.Frames([Samples.Bulletin(1)])[0]);
        f.Time.Advance(TimeSpan.FromMinutes(25));

        f.Host.CheckSlotWindow();
        Assert.Equal((SlotStart.AddMinutes(58), SlotStart.AddHours(1)), f.Host.BetweenSlots);
        Assert.Contains(f.Log, line => line.StartsWith("audio: between slots until 12:58 UTC, ready for the 13:00 UTC slot", StringComparison.Ordinal));

        f.Time.Advance(TimeSpan.FromMinutes(28));
        f.Host.CheckSlotWindow();
        Assert.Null(f.Host.BetweenSlots);
        Assert.Contains("audio: decoding for the 13:00 UTC slot", f.Log);
    }

    [Fact]
    public async Task WebSdrAndRecording_AreLeftAlone()
    {
        await using var webSdr = new Fixture(SlotStart.AddMinutes(30), "ubersdr:wessex.zapto.org");
        webSdr.Host.CheckSlotWindow();
        Assert.Null(webSdr.Host.BetweenSlots);

        await using var recording = new Fixture(SlotStart.AddMinutes(30), "wav:/tmp/slot.wav");
        recording.Host.CheckSlotWindow();
        Assert.Null(recording.Host.BetweenSlots);
    }

    // ---- the pipeline between slots ----

    private sealed class NoInput : IAudioInput
    {
        public int SampleRate => Rate;

        public int Read(Span<float> destination) => 0;
    }

    /// <summary>How many frames a pipeline decodes from one burst, fed as the audio thread would.</summary>
    private static async Task<int> FramesHeardAsync(bool betweenSlots, bool watched)
    {
        var pipeline = AudioPipeline.ForInput(new NoInput(), _ => { }, new FakeTimeProvider(SlotStart), watch: false);
        pipeline.BetweenSlots = betweenSlots;
        pipeline.Watched = () => watched;
        int frames = 0;
        pipeline.Channel.FrameReceived += (_, _) => frames++;
        var payload = new byte[120];
        new Random(6).NextBytes(payload);
        var modem = new Ms110dModem(Rate, _ => { }, new Ms110dTxSettings { WaveformNumber = 4 });
        float[] audio = [.. new float[Rate], .. modem.Modulate(Ax25UiFrame.Build(Samples.Source, OnAir.Destination, payload), 0), .. new float[3 * Rate]];
        for (int offset = 0; offset < audio.Length; offset += 4800)
        {
            pipeline.Feed(audio.AsSpan(offset, Math.Min(4800, audio.Length - offset)));
        }
        await pipeline.DisposeAsync();
        return frames;
    }

    [Fact]
    public async Task Pipeline_BetweenSlots_DecodesNothing_UnlessTheWaterfallIsWatched()
    {
        Assert.Equal(1, await FramesHeardAsync(betweenSlots: false, watched: false));
        Assert.Equal(0, await FramesHeardAsync(betweenSlots: true, watched: false));
        Assert.Equal(1, await FramesHeardAsync(betweenSlots: true, watched: true));
    }

    [Fact]
    public async Task Pipeline_BetweenSlots_StillFeedsSourceBlock_ForMeasureMyFilter()
    {
        var pipeline = AudioPipeline.ForInput(new NoInput(), _ => { }, new FakeTimeProvider(SlotStart), watch: false);
        pipeline.BetweenSlots = true;
        int samples = 0;
        pipeline.SourceBlock += block => samples += block.Length;

        pipeline.Feed(new float[4800]);

        Assert.Equal(4800, samples);
        await pipeline.DisposeAsync();
    }
}
