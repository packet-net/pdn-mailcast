using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The web SDR's listening window when the wall clock is corrected under it, as NTP does to a Pi
/// that booted on fake-hwclock's stale time: timers run on, the time of day jumps.
/// </summary>
public class WebSdrWindowTests
{
    /// <summary>A fake clock whose time of day can jump without moving its timers, as a real one's does.</summary>
    private sealed class SteppableClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public TimeSpan Step { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Step;
    }

    /// <summary>Silence until the pipeline stops; the starvation watch is off for these tests.</summary>
    private sealed class QuietInput : IAudioInput, IDisposable
    {
        private readonly ManualResetEventSlim _stopped = new();

        public int SampleRate => OnAir.SampleRate;

        public bool Disposed { get; private set; }

        public int Read(Span<float> destination)
        {
            _stopped.Wait(10);
            return 0;
        }

        public void Dispose()
        {
            Disposed = true;
            _stopped.Set();
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        public Rig(DateTimeOffset start, Func<ReceiverConfig, ReceiverConfig>? configure = null)
        {
            Clock = new SteppableClock(start);
            // Every slot, as before daylight hours, and 8 of them a day, every third hour, as
            // the clock tests expect; the daylight tests below set their own.
            var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = Dir.Path, Daylight = null, WebSdrSlotsPerDay = 8 };
            config = configure?.Invoke(config) ?? config;
            Host = new ReceiverHost(config, Clock, line => Log.Enqueue(line));
            Host.ClockWaiting += () => Waits.Writer.TryWrite(true);
            Host.AudioChanged += condition => Conditions.Writer.TryWrite(condition);
            Host.PipelineFactory = source =>
            {
                var input = new QuietInput();
                Inputs.Writer.TryWrite(input);
                return AudioPipeline.ForInput(input, _ => { }, Clock, source, watch: false, webSdrDescription: About);
            };
            _run = Host.RunAsync(_stop.Token);
        }

        public TempDirectory Dir { get; } = new();

        /// <summary>What the web SDR says about itself once opened.</summary>
        public string? About { get; set; } = "M0ABC, Wessex SDR, Salisbury";

        public SteppableClock Clock { get; }

        public ReceiverHost Host { get; }

        public System.Collections.Concurrent.ConcurrentQueue<string> Log { get; } = new();

        public System.Threading.Channels.Channel<bool> Waits { get; } = System.Threading.Channels.Channel.CreateUnbounded<bool>();

        public System.Threading.Channels.Channel<AudioCondition> Conditions { get; } = System.Threading.Channels.Channel.CreateUnbounded<AudioCondition>();

        /// <summary>Waits until the audio has reached <paramref name="phase"/>.</summary>
        public async Task<AudioCondition> AudioAsync(AudioPhase phase)
        {
            while (true)
            {
                var condition = await Conditions.Reader.ReadAsync();
                if (condition.Phase == phase)
                {
                    return condition;
                }
            }
        }

        /// <summary>The audio part of the status page's /api/status, as the page reads it.</summary>
        public System.Text.Json.JsonElement StatusAudio()
        {
            var page = new Mailcast.Receiver.Web.StatusPage(Host, null, _ => { }); // never started, so it holds no port
            return System.Text.Json.JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine).GetProperty("audio");
        }

        public System.Threading.Channels.Channel<QuietInput> Inputs { get; } = System.Threading.Channels.Channel.CreateUnbounded<QuietInput>();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _run;
            await Host.DisposeAsync();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task Hourly_ListensToEveryThirdSlotAndSaysWhich()
    {
        // 09:20: the 09:00 slot's window closed at 09:12, so the next is 12:00's, from 11:58.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 9, 20, 0, TimeSpan.Zero));
        await rig.Waits.Reader.ReadAsync();

        Assert.Contains("closed until 11:58 UTC, ready for the 12:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
        Assert.Contains(rig.Log, l => l.Contains("8 of the 24 slots a day, at 00:00, 03:00, 06:00, 09:00, 12:00, 15:00, 18:00 and 21:00 UTC", StringComparison.Ordinal));
        Assert.Contains(rig.Log, l => l.Contains("every hour on the hour", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Log, l => l.Contains("everyMinutes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhileClosed_SaysSoOnceNotAtEveryLook()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 9, 20, 0, TimeSpan.Zero));
        for (int i = 0; i < 5; i++)
        {
            await rig.Waits.Reader.ReadAsync();
            rig.Clock.Advance(ReceiverHost.ClockCheck);
        }
        await rig.Waits.Reader.ReadAsync();

        Assert.Single(rig.Log, l => l.Contains("closed until", StringComparison.Ordinal));
        Assert.False(rig.Inputs.Reader.TryPeek(out _));
    }

    [Fact]
    public async Task ClockCorrectedPastTheOpening_OpensWithinOneCheck()
    {
        // Booted at 09:20 by the stale clock: the window opens at 11:58, far off.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 9, 20, 0, TimeSpan.Zero));
        await rig.Waits.Reader.ReadAsync();
        Assert.Contains("closed until 11:58", rig.Host.AudioState, StringComparison.Ordinal);

        // NTP puts the clock right: it is really 11:59. Nothing opens until the next check...
        rig.Clock.Step = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(39);
        Assert.False(rig.Inputs.Reader.TryPeek(out _));

        // ...which is at most a minute away.
        rig.Clock.Advance(ReceiverHost.ClockCheck);
        await rig.Inputs.Reader.ReadAsync();
    }

    [Fact]
    public async Task ClockCorrectedPastTheClosing_ClosesWithinOneCheck()
    {
        // Open at 12:05, in the window, which closes at 12:12.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 12, 5, 0, TimeSpan.Zero));
        var input = await rig.Inputs.Reader.ReadAsync();
        await rig.Waits.Reader.ReadAsync();

        // The clock was slow: it is really 12:15. Still open until the next check...
        rig.Clock.Step = TimeSpan.FromMinutes(10);
        Assert.False(input.Disposed);

        // ...when the web SDR is closed, and stays closed until the next slot it listens to.
        rig.Clock.Advance(ReceiverHost.ClockCheck);
        await rig.Waits.Reader.ReadAsync();
        Assert.True(input.Disposed);
        Assert.Contains("closed until 14:58 UTC, ready for the 15:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MidnightSlot_OpensTheEveningBeforeAndClosesAfterMidnight()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 22, 0, 0, TimeSpan.Zero));
        await rig.Waits.Reader.ReadAsync();
        Assert.Contains("closed until 23:58 UTC, ready for the 00:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);

        // The clock is put right to 23:57; the next look, at 23:58, opens it.
        rig.Clock.Step = TimeSpan.FromMinutes(117);
        rig.Clock.Advance(ReceiverHost.ClockCheck);
        var input = await rig.Inputs.Reader.ReadAsync();
        await rig.Waits.Reader.ReadAsync();

        // It stays open past midnight, to 00:12 on the 6th.
        for (int i = 0; i < 13; i++)
        {
            rig.Clock.Advance(ReceiverHost.ClockCheck);
            Assert.False(input.Disposed);
            await rig.Waits.Reader.ReadAsync();
        }
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 11, 0, TimeSpan.Zero), rig.Clock.GetUtcNow());

        rig.Clock.Advance(ReceiverHost.ClockCheck);
        await rig.Waits.Reader.ReadAsync();
        Assert.True(input.Disposed);
        Assert.Contains("closed until 02:58 UTC, ready for the 03:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OldConfig_IsHourlyFromItsSlotAndSaysSoInOneLine()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 13, 20, 0, TimeSpan.Zero),
            c => c with { SlotUtc = "12:00", SlotUtcWithoutEveryMinutes = true });
        await rig.Waits.Reader.ReadAsync();

        Assert.Contains("closed until 14:58 UTC, ready for the 15:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
        Assert.Single(rig.Log, l => l.Contains("read as every 60 minutes from 12:00 UTC", StringComparison.Ordinal)
            && l.Contains("nothing needs changing", StringComparison.Ordinal));
        Assert.Contains(rig.Log, l => l.Contains("8 of the 24 slots a day, at 00:00, 03:00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClockPutBackBeforeTheOpening_ClosesWithinOneCheck()
    {
        // Open at 12:05, in the 12:00 slot's window, which opened at 11:58.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 12, 5, 0, TimeSpan.Zero));
        var input = await rig.Inputs.Reader.ReadAsync();
        await rig.Waits.Reader.ReadAsync();

        // The clock was fast: it is really 10:05. Still open until the next check...
        rig.Clock.Step = -TimeSpan.FromHours(2);
        Assert.False(input.Disposed);

        // ...when the web SDR is closed until the window that really is next.
        rig.Clock.Advance(ReceiverHost.ClockCheck);
        await rig.Waits.Reader.ReadAsync();
        Assert.True(input.Disposed);
        Assert.Contains("closed until 11:58 UTC, ready for the 12:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SoundCard_IsNotHeldToTheWebSdrSlots()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 9, 20, 0, TimeSpan.Zero), c => c with { Audio = "plughw:CARD=Device,DEV=0" });

        await rig.Inputs.Reader.ReadAsync();
        Assert.DoesNotContain(rig.Log, l => l.Contains("closed until", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Daylight_Explicit8_ListensToEightOfTheDaysDaylightSlots_AsBefore_AndWaitsForTheMorningsFirst()
    {
        // 06:00 on 5 October: the first daylight slot is 09:00, so the window opens at 08:58.
        // The Rig sets webSdrSlotsPerDay to 8.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero), c => c with { Daylight = new Packet.Mailcast.DaylightSettings() });
        await rig.Waits.Reader.ReadAsync();

        Assert.Contains("closed until 08:58 UTC, ready for the 09:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
        Assert.Contains(rig.Log, l => l.Contains("on 2026-10-05 the web SDR listens to 8 of the 9 daylight slots, at 09:00, 10:00, 11:00, 12:00, 13:00, 14:00, 15:00 and 16:00 UTC", StringComparison.Ordinal));
        Assert.Contains(rig.Log, l => l.Contains("every hour on the hour, in daylight: from 120 minutes after sunrise to 30 minutes before sunset at IO91lk", StringComparison.Ordinal));
    }

    [Theory]
    // 5 October: all 9 daylight slots, the 17:00 one too.
    [InlineData("2026-10-05T06:00:00Z", "closed until 08:58 UTC, ready for the 09:00 UTC slot",
        "on 2026-10-05 the web SDR listens to all 9 daylight slots, at 09:00, 10:00, 11:00, 12:00, 13:00, 14:00, 15:00, 16:00 and 17:00 UTC")]
    [InlineData("2026-10-05T16:30:00Z", "closed until 16:58 UTC, ready for the 17:00 UTC slot",
        "on 2026-10-05 the web SDR listens to all 9 daylight slots")]
    // Midwinter: all 5.
    [InlineData("2026-12-21T06:00:00Z", "closed until 10:58 UTC, ready for the 11:00 UTC slot",
        "on 2026-12-21 the web SDR listens to all 5 daylight slots, at 11:00, 12:00, 13:00, 14:00 and 15:00 UTC")]
    // Midsummer: 12 of the 14, leaving out 06:00 and 07:00, so the morning starts at 08:00.
    [InlineData("2026-06-21T05:00:00Z", "closed until 07:58 UTC, ready for the 08:00 UTC slot",
        "on 2026-06-21 the web SDR listens to 12 of the 14 daylight slots, at 08:00, 09:00, 10:00, 11:00, 12:00, 13:00, 14:00, 15:00, 16:00, 17:00, 18:00 and 19:00 UTC")]
    [InlineData("2026-06-21T18:30:00Z", "closed until 18:58 UTC, ready for the 19:00 UTC slot",
        "on 2026-06-21 the web SDR listens to 12 of the 14 daylight slots")]
    public async Task Daylight_Default_ListensToEveryDaylightSlotThatFits(string now, string state, string logged)
    {
        await using var rig = new Rig(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture),
            c => c with { Daylight = new Packet.Mailcast.DaylightSettings(), WebSdrSlotsPerDay = null });
        await rig.Waits.Reader.ReadAsync();

        Assert.Contains(state, rig.Host.AudioState, StringComparison.Ordinal);
        Assert.Contains(rig.Log, l => l.Contains(logged, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Daylight_AfterTheDaysLastSlot_WaitsForTomorrowsFirst_AndSaysTomorrowsSlots()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 12, 21, 16, 0, 0, TimeSpan.Zero), c => c with { Daylight = new Packet.Mailcast.DaylightSettings() });
        await rig.Waits.Reader.ReadAsync();

        Assert.Contains("closed until 10:58 UTC, ready for the 11:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
        Assert.Contains(rig.Log, l => l.Contains("on 2026-12-22 the web SDR listens to all 5 daylight slots, at 11:00, 12:00, 13:00, 14:00 and 15:00 UTC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_BetweenSlots_SaysWhenTheSpectrogramComesBack_AndThenThatItIsLive()
    {
        // 08:30: the web SDR next opens at 08:58, for the 09:00 slot.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 8, 30, 0, TimeSpan.Zero));
        await rig.AudioAsync(AudioPhase.Closed);
        await rig.Waits.Reader.ReadAsync();

        var closed = rig.StatusAudio();
        Assert.False(closed.GetProperty("live").GetBoolean());
        Assert.Equal("closed", closed.GetProperty("phase").GetString());
        Assert.Equal("webSdr", closed.GetProperty("kind").GetString());
        var reopens = closed.GetProperty("reopens").GetDateTimeOffset();
        var forSlot = closed.GetProperty("forSlot").GetDateTimeOffset();
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 8, 58, 0, TimeSpan.Zero), reopens);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero), forSlot);
        Assert.Equal(JsonValueKindNull, closed.GetProperty("problem").ValueKind);
        Assert.Contains("the web SDR wessex.zapto.org is closed until 08:58 UTC", closed.GetProperty("state").GetString(), StringComparison.Ordinal);
        var sdr = closed.GetProperty("webSdr");
        Assert.Equal("wessex.zapto.org", sdr.GetProperty("host").GetString());
        Assert.Equal("https://wessex.zapto.org/", sdr.GetProperty("url").GetString());
        Assert.Equal(JsonValueKindNull, sdr.GetProperty("about").ValueKind); // not opened yet

        // The clock reaches the opening: the audio is live and the page may ask for the spectrogram.
        rig.Clock.Step = reopens - rig.Clock.GetUtcNow();
        rig.Clock.Advance(ReceiverHost.ClockCheck);
        await rig.AudioAsync(AudioPhase.Listening);
        var open = rig.StatusAudio();
        Assert.True(open.GetProperty("live").GetBoolean());
        Assert.Equal("listening", open.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKindNull, open.GetProperty("reopens").ValueKind);
        Assert.Equal("M0ABC, Wessex SDR, Salisbury", open.GetProperty("webSdr").GetProperty("about").GetString());

        // Closed again after the slot, it still says which web SDR it is.
        rig.Clock.Step += TimeSpan.FromMinutes(20);
        rig.Clock.Advance(ReceiverHost.ClockCheck);
        await rig.AudioAsync(AudioPhase.Closed);
        var after = rig.StatusAudio();
        Assert.Equal("wessex.zapto.org", after.GetProperty("webSdr").GetProperty("host").GetString());
        Assert.Equal("M0ABC, Wessex SDR, Salisbury", after.GetProperty("webSdr").GetProperty("about").GetString());
    }

    [Fact]
    public async Task Status_WebSdrOnAnotherPort_SaysItsAddressAsWritten_AndASoundCardHasNone()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 8, 30, 0, TimeSpan.Zero), c => c with { Audio = "ubersdr:sdr.example.org:8073" });
        await rig.AudioAsync(AudioPhase.Closed);
        Assert.Equal("sdr.example.org:8073", rig.StatusAudio().GetProperty("webSdr").GetProperty("host").GetString());
        Assert.Contains("the web SDR sdr.example.org:8073 is closed", rig.Host.AudioState, StringComparison.Ordinal);

        Assert.Null(Mailcast.Receiver.Web.StatusPage.WebSdrView("plughw:CARD=Device,DEV=0", null));
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Status_SoundCardThatCannotBeOpened_SaysWhyAndWhenItIsTriedAgain()
    {
        using var dir = new TempDirectory();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var config = new ReceiverConfig { Audio = "plughw:CARD=NoSuchCard,DEV=0", StateDirectory = dir.Path };
        await using var host = new ReceiverHost(config, clock, _ => { });
        var failed = new TaskCompletionSource<AudioCondition>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AudioChanged += c =>
        {
            if (c.Phase == AudioPhase.Failed)
            {
                failed.TrySetResult(c);
            }
        };
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);

        var condition = await failed.Task;
        var page = new Mailcast.Receiver.Web.StatusPage(host, null, _ => { });
        var audio = System.Text.Json.JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine).GetProperty("audio");

        Assert.False(audio.GetProperty("live").GetBoolean());
        Assert.Equal("failed", audio.GetProperty("phase").GetString());
        Assert.Equal("soundCard", audio.GetProperty("kind").GetString());
        Assert.Contains("NoSuchCard", audio.GetProperty("problem").GetString(), StringComparison.Ordinal);
        Assert.Equal(clock.GetUtcNow() + ReceiverHost.AudioRetry, audio.GetProperty("retryAt").GetDateTimeOffset());
        Assert.Equal(condition.RetryAt, audio.GetProperty("retryAt").GetDateTimeOffset());

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public void Page_AsksForTheSpectrogramOnlyWhileTheAudioIsLive_AndExplainsOtherwise()
    {
        // The page is a file, not a program this suite can run: these check that its logic is the one described.
        using var stream = typeof(ReceiverHost).Assembly.GetManifestResourceStream("Mailcast.Receiver.Web.index.html")!;
        string html = new StreamReader(stream).ReadToEnd();

        Assert.DoesNotContain("setTimeout(connect", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\nconnect();", html, StringComparison.Ordinal);
        Assert.Contains("if (s.audio.live && !ws) connect();", html, StringComparison.Ordinal);
        Assert.Contains("The web SDR ${sdr(s)} is closed between slots. The spectrogram comes back at", html, StringComparison.Ordinal);
        Assert.Contains("The web SDR ${esc(sdr(s))} is tuned for it", html, StringComparison.Ordinal);
        Assert.Contains("${w.about ? ` (${esc(w.about)})` : \"\"}", html, StringComparison.Ordinal);
        Assert.Contains("No audio from the sound card", html, StringComparison.Ordinal);
        Assert.Contains("id=\"specNotice\"", html, StringComparison.Ordinal);
    }
}
