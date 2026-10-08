using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// Issue #49's check, <see cref="ReceiverHost.EarlyEndReason"/>: every condition it looks at, on
/// a fake clock, without a real audio pipeline (frames go straight into <see cref="Intake"/>).
/// </summary>
public class EarlyEndReasonTests
{
    private static readonly DateTimeOffset SlotStart = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixture : IAsyncDisposable
    {
        public TempDirectory Dir { get; } = new();

        public FakeTimeProvider Time { get; }

        public ReceiverHost Host { get; }

        public Fixture(DateTimeOffset start)
        {
            Time = new FakeTimeProvider(start);
            var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = Dir.Path, Daylight = null };
            Host = new ReceiverHost(config, Time, _ => { });
        }

        /// <summary>Hears a frame (through <see cref="Intake"/>, as the modem would) and waits for it to settle.</summary>
        public async Task HearAsync(byte[] frame)
        {
            Host.Intake.Offer(frame);
            await Host.Intake.DrainAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task NothingHeardYet_IsNull()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(5));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task DirectoryNotYetHeard_IsNull_EvenIfOtherwiseQuiet()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(5));
        f.Host.Slots.OnFrame(); // a frame heard, but no directory yet
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task DirectoryHeard_ButABulletinInItIsNotComplete_IsNull()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(5));
        var frames = Samples.Frames([Samples.Bulletin(1, bodyLines: 200)]);
        Assert.True(frames.Count >= 8);
        await f.HearAsync(frames[0]); // the directory
        await f.HearAsync(frames[1]); // one piece of a bulletin that needs many
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task EverythingComplete_ButProbeNotCapturedAndWindowNotPassed_IsNull()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(2));
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            await f.HearAsync(frame);
        }
        // A few seconds on: still well inside the probe's capture window, and it was never captured.
        f.Time.Advance(TimeSpan.FromSeconds(5));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task EverythingComplete_ProbeNeverCaptured_ButItsWindowHasPassed_IsReady()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(2));
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            await f.HearAsync(frame);
        }
        f.Time.Advance(ReceiverHost.ProbeCaptureWindow + ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.NotNull(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task EverythingComplete_ProbeCaptured_ButNotQuietLongEnough_IsNull()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(2));
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            await f.HearAsync(frame);
        }
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd - TimeSpan.FromSeconds(1));

        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task EverythingComplete_ProbeCaptured_AndQuietLongEnough_IsReady()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(2));
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            await f.HearAsync(frame);
        }
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        var why = f.Host.EarlyEndReason(SlotStart);
        Assert.NotNull(why);
        Assert.Contains("quiet", why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObjectNotInTheDirectory_Arriving_ResetsTheQuietClock_ConservativelyStayingOpen()
    {
        // Today's rotation: one bulletin, heard and complete.
        await using var f = new Fixture(SlotStart.AddSeconds(2));
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            await f.HearAsync(frame);
        }
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));
        Assert.NotNull(f.Host.EarlyEndReason(SlotStart)); // ready, before the stray frame

        // A frame for some other object entirely (not in this directory) arrives: the head end
        // may still be sending something new, so this is conservative and keeps the window open,
        // even though the stray object is never tracked as part of this slot's rotation.
        var stray = Samples.Frames([Samples.Bulletin(99, from: "M0XYZ")], seed: 7)[1];
        await f.HearAsync(stray);

        Assert.Null(f.Host.EarlyEndReason(SlotStart));

        // Quiet again for long enough since that last frame: ready once more.
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));
        Assert.NotNull(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task WrongSlot_IsNull()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(2));
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            await f.HearAsync(frame);
        }
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.Null(f.Host.EarlyEndReason(SlotStart.AddHours(1)));
    }
}
