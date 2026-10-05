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

        public Rig(DateTimeOffset start)
        {
            Clock = new SteppableClock(start);
            var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", SlotUtc = "12:00", StateDirectory = Dir.Path };
            Host = new ReceiverHost(config, Clock, _ => { });
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
    public async Task ClockCorrectedPastTheOpening_OpensWithinOneCheck()
    {
        // Booted at 09:00 by the stale clock: the window opens at 11:45, far off.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
        await rig.Waits.Reader.ReadAsync();
        Assert.Contains("closed until 11:45", rig.Host.AudioState, StringComparison.Ordinal);

        // NTP puts the clock right: it is really 11:50. Nothing opens until the next check...
        rig.Clock.Step = TimeSpan.FromHours(2.5) + TimeSpan.FromMinutes(20);
        Assert.False(rig.Inputs.Reader.TryPeek(out _));

        // ...which is at most a minute away.
        rig.Clock.Advance(ReceiverHost.ClockCheck);
        await rig.Inputs.Reader.ReadAsync();
    }

    [Fact]
    public async Task ClockCorrectedPastTheClosing_ClosesWithinOneCheck()
    {
        // Open at 12:30, in the window, which closes at 13:30.
        await using var rig = new Rig(new DateTimeOffset(2026, 10, 5, 12, 30, 0, TimeSpan.Zero));
        var input = await rig.Inputs.Reader.ReadAsync();
        await rig.Waits.Reader.ReadAsync();

        // The clock was slow: it is really 14:00. Still open until the next check...
        rig.Clock.Step = TimeSpan.FromMinutes(90);
        Assert.False(input.Disposed);

        // ...when the web SDR is closed, and stays closed until tomorrow's window.
        rig.Clock.Advance(ReceiverHost.ClockCheck);
        await rig.Waits.Reader.ReadAsync();
        Assert.True(input.Disposed);
        Assert.Contains("closed until 11:45", rig.Host.AudioState, StringComparison.Ordinal);
    }
}
