using System.Runtime.Versioning;
using M0LTE.Radio.Audio;
using Mailcast.Receiver.Hooks;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

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
            // Every slot, as before daylight hours, so the web SDR listens to 12 of the 24, every
            // other hour; the daylight tests below set their own.
            var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = Dir.Path, Daylight = null };
            config = configure?.Invoke(config) ?? config;
            Host = new ReceiverHost(config, Clock, line => Log.Enqueue(line));
            Host.ClockWaiting += () => Waits.Writer.TryWrite(true);
            Host.Hooks.Waiting += d => HookWaits.Writer.TryWrite(d);
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

        /// <summary>Each delay the hooks loop has set a timer on the clock for, as it sets it.</summary>
        public System.Threading.Channels.Channel<TimeSpan> HookWaits { get; } = System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();

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
    public async Task Hourly_ListensToEveryOtherSlotAndSaysWhich()
    {
        // 09:20: 09:00 is not one it listens to, so the next is 10:00's, from 09:58.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 9, 20, 0, TimeSpan.Zero));
        await rig.Waits.Reader.ReadAsync();

        Assert.Contains("closed until 09:58 UTC, ready for the 10:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
        Assert.Contains(rig.Log, l => l.Contains("12 of the 24 slots a day, at 00:00, 02:00, 04:00, 06:00, 08:00, 10:00, 12:00, 14:00, 16:00, 18:00, 20:00 and 22:00 UTC, and skips the other 12", StringComparison.Ordinal));
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
        // Booted at 08:20 by the stale clock: the window opens at 09:58, far off.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 8, 20, 0, TimeSpan.Zero));
        await rig.Waits.Reader.ReadAsync();
        Assert.Contains("closed until 09:58", rig.Host.AudioState, StringComparison.Ordinal);

        // NTP puts the clock right: it is really 09:59. Nothing opens until the next check...
        rig.Clock.Step = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(39);
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
        Assert.Contains("closed until 13:58 UTC, ready for the 14:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MidnightSlot_OpensTheEveningBeforeAndClosesAfterMidnight()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 22, 20, 0, TimeSpan.Zero));
        await rig.Waits.Reader.ReadAsync();
        Assert.Contains("closed until 23:58 UTC, ready for the 00:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);

        // The clock is put right to 23:57; the next look, at 23:58, opens it.
        rig.Clock.Step = TimeSpan.FromMinutes(97);
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
        Assert.Contains("closed until 01:58 UTC, ready for the 02:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OldConfig_IsHourlyFromItsSlotAndSaysSoInOneLine()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 13, 20, 0, TimeSpan.Zero),
            c => c with { SlotUtc = "12:00", SlotUtcWithoutEveryMinutes = true });
        await rig.Waits.Reader.ReadAsync();

        Assert.Contains("closed until 13:58 UTC, ready for the 14:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
        Assert.Single(rig.Log, l => l.Contains("read as every 60 minutes from 12:00 UTC", StringComparison.Ordinal)
            && l.Contains("nothing needs changing", StringComparison.Ordinal));
        Assert.Contains(rig.Log, l => l.Contains("12 of the 24 slots a day, at 00:00, 02:00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClockPutBackBeforeTheOpening_ClosesWithinOneCheck()
    {
        // Open at 12:05, in the 12:00 slot's window, which opened at 11:58.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 12, 5, 0, TimeSpan.Zero));
        var input = await rig.Inputs.Reader.ReadAsync();
        await rig.Waits.Reader.ReadAsync();

        // The clock was fast: it is really 09:05. Still open until the next check...
        rig.Clock.Step = -TimeSpan.FromHours(3);
        Assert.False(input.Disposed);

        // ...when the web SDR is closed until the window that really is next.
        rig.Clock.Advance(ReceiverHost.ClockCheck);
        await rig.Waits.Reader.ReadAsync();
        Assert.True(input.Disposed);
        Assert.Contains("closed until 09:58 UTC, ready for the 10:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SoundCard_IsNotHeldToTheWebSdrSlots()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 9, 20, 0, TimeSpan.Zero), c => c with { Audio = "plughw:CARD=Device,DEV=0" });

        await rig.Inputs.Reader.ReadAsync();
        Assert.DoesNotContain(rig.Log, l => l.Contains("closed until", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OldWebSdrSlotsPerDay_IsIgnored_AndSaidOnceAtStartUp()
    {
        // A config from 0.5.1 and before, with "webSdrSlotsPerDay": 8: all 9 slots on 5 October all the same.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero),
            c => c with { Daylight = new Packet.Mailcast.DaylightSettings(), WebSdrSlotsPerDayIgnored = true });
        await rig.Waits.Reader.ReadAsync();
        rig.Clock.Advance(ReceiverHost.ClockCheck);
        await rig.Waits.Reader.ReadAsync();

        Assert.Single(rig.Log, l => l == "config: webSdrSlotsPerDay is no longer used and is ignored; a web SDR listens to every daylight slot that fits in its allowance");
        Assert.All(rig.Log, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), l));
        Assert.Contains(rig.Log, l => l.Contains("on 2026-10-05 the web SDR listens to all 9 daylight slots", StringComparison.Ordinal));
        Assert.Contains("closed until 08:58 UTC, ready for the 09:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoWebSdrSlotsPerDay_SaysNothingAboutIt()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero), c => c with { Daylight = new Packet.Mailcast.DaylightSettings() });
        await rig.Waits.Reader.ReadAsync();

        Assert.DoesNotContain(rig.Log, l => l.Contains("webSdrSlotsPerDay", StringComparison.Ordinal));
    }

    [Theory]
    // 5 October: all 9 daylight slots, the 17:00 one too.
    [InlineData("2026-10-05T06:00:00Z", "closed until 08:58 UTC, ready for the 09:00 UTC slot",
        "on 2026-10-05 the web SDR listens to all 9 daylight slots, at 09:00, 10:00, 11:00, 12:00, 13:00, 14:00, 15:00, 16:00 and 17:00 UTC, from 2 minutes")]
    [InlineData("2026-10-05T16:30:00Z", "closed until 16:58 UTC, ready for the 17:00 UTC slot",
        "on 2026-10-05 the web SDR listens to all 9 daylight slots")]
    // Midwinter: all 5.
    [InlineData("2026-12-21T06:00:00Z", "closed until 10:58 UTC, ready for the 11:00 UTC slot",
        "on 2026-12-21 the web SDR listens to all 5 daylight slots, at 11:00, 12:00, 13:00, 14:00 and 15:00 UTC")]
    // Midsummer: 12 of the 14, leaving out 06:00 and 07:00, so the morning starts at 08:00.
    [InlineData("2026-06-21T05:00:00Z", "closed until 07:58 UTC, ready for the 08:00 UTC slot",
        "on 2026-06-21 the web SDR listens to 12 of the 14 daylight slots, at 08:00, 09:00, 10:00, 11:00, 12:00, 13:00, 14:00, 15:00, 16:00, 17:00, 18:00 and 19:00 UTC, and skips 06:00 and 07:00 UTC")]
    [InlineData("2026-06-21T18:30:00Z", "closed until 18:58 UTC, ready for the 19:00 UTC slot",
        "on 2026-06-21 the web SDR listens to 12 of the 14 daylight slots")]
    public async Task Daylight_ListensToEveryDaylightSlotThatFits(string now, string state, string logged)
    {
        await using var rig = new Rig(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture),
            c => c with { Daylight = new Packet.Mailcast.DaylightSettings() });
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
        // 08:30: the web SDR next opens at 09:58, for the 10:00 slot.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 8, 30, 0, TimeSpan.Zero));
        await rig.AudioAsync(AudioPhase.Closed);
        await rig.Waits.Reader.ReadAsync();

        var closed = rig.StatusAudio();
        Assert.False(closed.GetProperty("live").GetBoolean());
        Assert.Equal("closed", closed.GetProperty("phase").GetString());
        Assert.Equal("webSdr", closed.GetProperty("kind").GetString());
        var reopens = closed.GetProperty("reopens").GetDateTimeOffset();
        var forSlot = closed.GetProperty("forSlot").GetDateTimeOffset();
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 9, 58, 0, TimeSpan.Zero), reopens);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero), forSlot);
        Assert.Equal(JsonValueKindNull, closed.GetProperty("problem").ValueKind);
        Assert.Contains("the web SDR wessex.zapto.org is closed until 09:58 UTC", closed.GetProperty("state").GetString(), StringComparison.Ordinal);
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
    public async Task ListenNow_BetweenWindows_OpensAtOnceAndClosesAfterDuration()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 9, 20, 0, TimeSpan.Zero));
        await rig.AudioAsync(AudioPhase.Closed);

        var result = rig.Host.RequestListenNow();
        Assert.True(result.Ok);

        var input = await rig.Inputs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await rig.AudioAsync(AudioPhase.Listening);
        Assert.False(input.Disposed);
        Assert.Contains(rig.Log, l => l.Contains("\"Listen now\"", StringComparison.Ordinal));

        rig.Clock.Advance(ListenNowService.Duration);
        await rig.AudioAsync(AudioPhase.Closed);
        Assert.True(input.Disposed);
        // Back to the real schedule afterwards: the next real window, not stuck or skipped.
        Assert.Contains("closed until 09:58 UTC, ready for the 10:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListenNow_RefusedWithinFiveMinutesOfTheRealWindow()
    {
        // 09:56: the 10:00 slot's window opens at 09:58, under 5 minutes away.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 9, 56, 0, TimeSpan.Zero));
        await rig.AudioAsync(AudioPhase.Closed);

        var result = rig.Host.RequestListenNow();

        Assert.False(result.Ok);
        Assert.Contains("wait for that instead", result.Reason, StringComparison.Ordinal);
        Assert.False(rig.Inputs.Reader.TryPeek(out _));
    }

    [Fact]
    public async Task ListenNow_CutShortAtOnce_IfTheScheduleMovesAWindowCloserMidSession()
    {
        // At 09:20 the next hourly slot is 09:58's, far enough away to grant a session that
        // would otherwise run to about 09:23.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 9, 20, 0, TimeSpan.Zero));
        await rig.AudioAsync(AudioPhase.Closed);
        var result = rig.Host.RequestListenNow();
        Assert.True(result.Ok);
        var input = await rig.Inputs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await rig.AudioAsync(AudioPhase.Listening);
        Assert.False(input.Disposed);

        // GB7RDG's directory, heard mid-session, moves the slots to :21 past the hour: the
        // 09:21 slot's window (opens 09:19) is already due.
        var heard = new SlotTimetable(new TimeOnly(9, 21), 60, null);
        var options = ScheduleOptions.Hourly with { Timetable = heard };
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)], options, new DateTimeOffset(2026, 10, 5, 9, 21, 0, TimeSpan.Zero)))
        {
            rig.Host.Intake.Offer(frame);
        }
        await rig.Host.Intake.DrainAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Cut short at once, well before the ~3 minutes the session was granted for.
        for (int i = 0; i < 100 && !input.Disposed; i++)
        {
            await Task.Delay(20);
        }
        Assert.True(input.Disposed);
    }

    [Fact]
    public async Task EarlyEnd_WebSdr_ClosesBeforeTheUsualEnd_OnceEverythingIsHeardAndQuiet()
    {
        // Open at 12:01, in the 12:00 slot's window (opens 11:58, closes 12:12).
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 12, 1, 0, TimeSpan.Zero));
        var input = await rig.Inputs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // The directory and its one bulletin are heard, and the probe is captured.
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            rig.Host.Intake.Offer(frame);
        }
        await rig.Host.Intake.DrainAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        rig.Host.Slots.OnProbeCaptured();
        Assert.False(input.Disposed);
        Assert.NotNull(rig.Host.Intake.Progress().Directory);
        Assert.All(rig.Host.Intake.Progress().Progress, p => Assert.True(p.Complete));

        // Once it has been quiet for a minute, the window closes well before its usual 12:12.
        for (int i = 0; i < 6 && !input.Disposed; i++)
        {
            rig.Clock.Advance(ReceiverHost.EarlyEndPoll);
            await rig.Waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(input.Disposed);
        Assert.Contains(rig.Log, l => l.Contains("ending the 12:00 UTC slot's window early", StringComparison.Ordinal));
        // The early-end line says why; the "outside the listening window" one would be
        // misleading here, since by the clock the window was still open.
        Assert.DoesNotContain(rig.Log, l => l.Contains("the clock is outside the slot's listening window", StringComparison.Ordinal));
        // Closed well ahead of the usual 12:12 end, ready for the next slot it listens to.
        Assert.Contains("closed until 13:58 UTC, ready for the 14:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);

        // The status page says why too.
        var page = new Mailcast.Receiver.Web.StatusPage(rig.Host, null, _ => { });
        var json = System.Text.Json.JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine);
        Assert.Contains("the directory and everything in rotation are heard", json.GetProperty("slot").GetProperty("endedEarly").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EarlyEnd_WebSdr_NeverFiresWhileABulletinInTheDirectoryIsStillIncomplete()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 12, 1, 0, TimeSpan.Zero));
        var input = await rig.Inputs.Reader.ReadAsync();
        await rig.Waits.Reader.ReadAsync();

        // Only the directory frame (always first) and one piece of a bulletin that needs many:
        // the directory lists a bulletin that is not complete here.
        var frames = Samples.Frames([Samples.Bulletin(1, bodyLines: 200)]);
        Assert.True(frames.Count >= 8);
        rig.Host.Intake.Offer(frames[0]);
        rig.Host.Intake.Offer(frames[1]);
        await rig.Host.Intake.DrainAsync(CancellationToken.None);
        rig.Host.Slots.OnProbeCaptured();

        rig.Clock.Advance(ReceiverHost.QuietBeforeEarlyEnd + ReceiverHost.EarlyEndPoll);
        await rig.Waits.Reader.ReadAsync();

        // Still the usual window: closed at 12:12, not sooner.
        Assert.False(input.Disposed);
        Assert.DoesNotContain(rig.Log, l => l.Contains("ending the 12:00 UTC slot's window early", StringComparison.Ordinal));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task EarlyEnd_WebSdrWithHooks_RunsAfterSoonerThanTheUsualClose()
    {
        using var scripts = new TempDirectory();
        string record = Path.Combine(scripts.Path, "runs");
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 12, 1, 0, TimeSpan.Zero), c => c with
        {
            Hooks = new HooksSettings
            {
                Before = HookScript.Write(scripts.Path, "before.sh", record),
                After = HookScript.Write(scripts.Path, "after.sh", record),
            },
        });
        var input = await rig.Inputs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        // "before" started well ahead of 11:58, so it is already done.
        await rig.HookWaits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(HookScript.Runs(record), l => l.StartsWith("before ", StringComparison.Ordinal));

        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            rig.Host.Intake.Offer(frame);
        }
        await rig.Host.Intake.DrainAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        rig.Host.Slots.OnProbeCaptured();

        for (int i = 0; i < 6 && !input.Disposed; i++)
        {
            rig.Clock.Advance(ReceiverHost.EarlyEndPoll);
            await rig.Waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(input.Disposed);
        Assert.DoesNotContain(HookScript.Runs(record), l => l.StartsWith("after ", StringComparison.Ordinal));

        // The hooks loop polls at most every SlotHooks.Check: well under the ~11 minutes still
        // left to the slot's usual 12:12 close, it notices the window ended early and runs
        // "after" then, not at the usual close.
        for (int i = 0; i < 15 && !HookScript.Runs(record).Any(l => l.StartsWith("after ", StringComparison.Ordinal)); i++)
        {
            rig.Clock.Advance(SlotHooks.Check);
            await rig.HookWaits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Contains(HookScript.Runs(record), l => l.StartsWith("after ", StringComparison.Ordinal));
        Assert.True(rig.Clock.GetUtcNow() < new DateTimeOffset(2026, 10, 5, 12, 12, 0, TimeSpan.Zero),
            $"\"after\" should have run well before the usual 12:12 close; it is now {rig.Clock.GetUtcNow():HH:mm:ss}");
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
