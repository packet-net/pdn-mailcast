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
            // Every slot, as before daylight hours; the daylight tests below set their own.
            var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = Dir.Path, Daylight = null };
            config = configure?.Invoke(config) ?? config;
            Host = new ReceiverHost(config, Clock, line => Log.Enqueue(line));
            Host.ClockWaiting += () => Waits.Writer.TryWrite(true);
            Host.PipelineFactory = source =>
            {
                var input = new QuietInput();
                Inputs.Writer.TryWrite(input);
                return AudioPipeline.ForInput(input, _ => { }, Clock, source, watch: false);
            };
            _run = Host.RunAsync(_stop.Token);
        }

        public TempDirectory Dir { get; } = new();

        public SteppableClock Clock { get; }

        public ReceiverHost Host { get; }

        public System.Collections.Concurrent.ConcurrentQueue<string> Log { get; } = new();

        public System.Threading.Channels.Channel<bool> Waits { get; } = System.Threading.Channels.Channel.CreateUnbounded<bool>();

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
    public async Task Daylight_ListensToEightOfTheDaysDaylightSlots_AndWaitsForTheMorningsFirst()
    {
        // 06:00 on 5 October: the first daylight slot is 09:00, so the window opens at 08:58.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero), c => c with { Daylight = new Mailcast.Core.DaylightSettings() });
        await rig.Waits.Reader.ReadAsync();

        Assert.Contains("closed until 08:58 UTC, ready for the 09:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
        Assert.Contains(rig.Log, l => l.Contains("on 2026-10-05 the web SDR listens to 8 of the 9 daylight slots, at 09:00, 10:00, 11:00, 12:00, 13:00, 14:00, 15:00 and 16:00 UTC", StringComparison.Ordinal));
        Assert.Contains(rig.Log, l => l.Contains("every hour on the hour, in daylight: from 120 minutes after sunrise to 30 minutes before sunset at IO91lk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Daylight_AfterTheDaysLastSlot_WaitsForTomorrowsFirst_AndSaysTomorrowsSlots()
    {
        await using var rig = new Rig(new DateTimeOffset(2026, 12, 21, 16, 0, 0, TimeSpan.Zero), c => c with { Daylight = new Mailcast.Core.DaylightSettings() });
        await rig.Waits.Reader.ReadAsync();

        Assert.Contains("closed until 10:58 UTC, ready for the 11:00 UTC slot", rig.Host.AudioState, StringComparison.Ordinal);
        Assert.Contains(rig.Log, l => l.Contains("on 2026-12-22 the web SDR listens to all 5 daylight slots, at 11:00, 12:00, 13:00, 14:00 and 15:00 UTC", StringComparison.Ordinal));
    }
}
