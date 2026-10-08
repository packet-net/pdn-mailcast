using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.Mailcast.Propagation;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// Issue #49's check, <see cref="ReceiverHost.EarlyEndReason"/>: every condition it looks at, on
/// a fake clock, without a real audio pipeline (frames go straight into <see cref="Intake"/>).
/// </summary>
public class EarlyEndReasonTests
{
    private static readonly DateTimeOffset SlotStart = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Ax25(MailcastFrame frame) => Ax25UiFrame.Build(Samples.Source, OnAir.Destination, frame.ToBytes());

    /// <summary>An ionosonde reading sounded at <paramref name="sounded"/>, as its own object (two frames).</summary>
    private static IonoReading IonoAt(DateTimeOffset sounded) => IonoEvaluator.Evaluate(
        [new IonoSounding("RL052", sounded, IonoSource.Giro, 6.05, M3000: 3.3)], new IonoSettings(), sounded.AddMinutes(2));

    /// <summary>A PSK Reporter reading observed at <paramref name="observed"/>, as its own object (two frames).</summary>
    private static PskReading PskAt(DateTimeOffset observed) => PskEvaluator.Evaluate(
        [new PskSpot(0, observed.AddMinutes(-4), PskBand.Forty, 520, -11, "S0", "S1")], observed, PskEvaluator.Window);

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

        /// <summary>Hears a whole object's frames and waits for them to settle.</summary>
        public async Task HearAsync(TransferObject obj, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                await HearAsync(Ax25(obj.Frame((uint)i)));
            }
        }

        /// <summary>Hears an ionosonde reading sounded at <paramref name="sounded"/>, as its own fresh object.</summary>
        public Task HearIonosphereAsync(DateTimeOffset sounded) => HearAsync(IonoRecord.ToTransferObject(IonoAt(sounded)), 2);

        /// <summary>Hears a PSK Reporter reading observed at <paramref name="observed"/>, as its own fresh object.</summary>
        public Task HearPskReporterAsync(DateTimeOffset observed) => HearAsync(PskRecord.ToTransferObject(PskAt(observed)), 2);

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

    [Fact]
    public async Task DirectoryAlreadyKnownFromAnEarlierSlot_StillCountsAsHeard_ViaARepeatFrame()
    {
        // The first slot hears and completes everything: the usual case, as a sanity check.
        await using var f = new Fixture(SlotStart.AddSeconds(2));
        var frames = Samples.Frames([Samples.Bulletin(1)]);
        foreach (var frame in frames)
        {
            await f.HearAsync(frame);
        }
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));
        Assert.NotNull(f.Host.EarlyEndReason(SlotStart));

        // An hour on, a new slot: the head end resends the same directory (WN3/WN4 taking turns,
        // or just the rotation coming round again), which this receiver already has, so it comes
        // back AlreadyComplete rather than CompletedDirectory. It must still count as heard.
        var slot2 = SlotStart.AddHours(1);
        f.Time.SetUtcNow(slot2.AddSeconds(2));
        await f.HearAsync(frames[0]); // a directory frame; AlreadyComplete this time
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.NotNull(f.Host.EarlyEndReason(slot2));
    }

    [Fact]
    public async Task NeitherPropagationReadingEverHeard_TreatedAsThisBroadcastSendsNeither_DoesNotBlock()
    {
        await using var f = new Fixture(SlotStart.AddSeconds(2));
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            await f.HearAsync(frame);
        }
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.NotNull(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task IonosphereKnownFromAnEarlierSlot_ButNotHeardThisSlot_BlocksEarlyEnd()
    {
        // Heard a whole slot earlier, so Intake.Ionosphere is not null by the time this slot
        // starts, but (hearing anything at all starts tracking a slot) that earlier slot's own
        // IonosphereHeard flag never carries over to this one's.
        await using var f = new Fixture(SlotStart.AddHours(-1).AddSeconds(2));
        await f.HearIonosphereAsync(SlotStart.AddHours(-1));
        Assert.NotNull(f.Host.Intake.Ionosphere);

        f.Time.SetUtcNow(SlotStart.AddSeconds(2));
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            await f.HearAsync(frame);
        }
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.False(f.Host.Slots.Last!.IonosphereHeard);
        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task PskReporterKnownFromAnEarlierSlot_ButNotHeardThisSlot_BlocksEarlyEnd()
    {
        await using var f = new Fixture(SlotStart.AddHours(-1).AddSeconds(2));
        await f.HearPskReporterAsync(SlotStart.AddHours(-1));
        Assert.NotNull(f.Host.Intake.PskReporter);

        f.Time.SetUtcNow(SlotStart.AddSeconds(2));
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            await f.HearAsync(frame);
        }
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.False(f.Host.Slots.Last!.PskReporterHeard);
        Assert.Null(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task BothPropagationReadingsHeardThisSlot_AllowsEarlyEnd()
    {
        // Known from an earlier slot (so each gate is live, not "never heard at all"), then
        // this slot's own, fresh readings (new objects, so distinctly heard this slot).
        await using var f = new Fixture(SlotStart.AddHours(-1).AddSeconds(2));
        await f.HearIonosphereAsync(SlotStart.AddHours(-1));
        await f.HearPskReporterAsync(SlotStart.AddHours(-1));

        f.Time.SetUtcNow(SlotStart.AddSeconds(2));
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            await f.HearAsync(frame);
        }
        await f.HearIonosphereAsync(SlotStart);
        await f.HearPskReporterAsync(SlotStart);
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.True(f.Host.Slots.Last!.IonosphereHeard);
        Assert.True(f.Host.Slots.Last!.PskReporterHeard);
        Assert.NotNull(f.Host.EarlyEndReason(SlotStart));
    }

    [Fact]
    public async Task AlternatingWn4AndWn3Directories_AcrossThreeSlots_TheRepeatOnTheThirdStillCountsAsHeard()
    {
        // GB7RDG takes turns on WN4 and WN3: the same rotation, resent on the other waveform, is
        // a different object (the mode it went out on is part of what is hashed), so remembering
        // only the newest would forget the one before as soon as the other took its place.
        await using var f = new Fixture(SlotStart.AddSeconds(2));
        var bulletin = Samples.Bulletin(1);

        // 12:00, WN4.
        foreach (var frame in Samples.Frames([bulletin], ScheduleOptions.Hourly, SlotStart, "ms110d-wn4"))
        {
            await f.HearAsync(frame);
        }
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));
        Assert.NotNull(f.Host.EarlyEndReason(SlotStart));

        // 13:00, WN3: a fresh object, so a fresh "CompletedDirectory", not a repeat.
        var slot2 = SlotStart.AddHours(1);
        f.Time.SetUtcNow(slot2.AddSeconds(2));
        foreach (var frame in Samples.Frames([bulletin], ScheduleOptions.Hourly, slot2, "ms110d-wn3"))
        {
            await f.HearAsync(frame);
        }
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));
        Assert.NotNull(f.Host.EarlyEndReason(slot2));

        // 14:00: the head end resends the exact WN4-encoded directory from 12:00 (same object,
        // same ID, byte for byte) again. Only remembering the newest (WN3's, from 13:00) would
        // mean this is not recognised, and the bug: EarlyEndReason never fires for this slot.
        var slot3 = SlotStart.AddHours(2);
        f.Time.SetUtcNow(slot3.AddSeconds(2));
        var wn4Repeat = Samples.Frames([bulletin], ScheduleOptions.Hourly, SlotStart, "ms110d-wn4");
        await f.HearAsync(wn4Repeat[0]); // the directory; AlreadyComplete this time
        f.Host.Slots.OnProbeCaptured();
        f.Time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.NotNull(f.Host.EarlyEndReason(slot3));
    }

    [Fact]
    public async Task DirectoryKnownFromBeforeARestart_AResendOnTheOtherWaveform_StillCountsAsHeard()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(SlotStart.AddSeconds(2));
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Daylight = null };
        var bulletin = Samples.Bulletin(1);
        var wn4 = Samples.Frames([bulletin], ScheduleOptions.Hourly, SlotStart, "ms110d-wn4");

        await using (var first = new ReceiverHost(config, time, _ => { }))
        {
            foreach (var frame in wn4)
            {
                first.Intake.Offer(frame);
            }
            await first.Intake.DrainAsync(CancellationToken.None);
        }

        // A new receiver (a restart), a slot later: the head end sends the rotation on WN3, a
        // fresh object this receiver has never seen. Remembering the WN4 directory only in
        // memory (lost across the restart) would still let this complete normally; the point of
        // this test is the next slot's WN4 repeat, below.
        var slot2 = SlotStart.AddHours(1);
        time.SetUtcNow(slot2.AddSeconds(2));
        var wn3 = Samples.Frames([bulletin], ScheduleOptions.Hourly, slot2, "ms110d-wn3");
        await using var second = new ReceiverHost(config, time, _ => { });
        foreach (var frame in wn3)
        {
            second.Intake.Offer(frame);
        }
        await second.Intake.DrainAsync(CancellationToken.None);
        second.Slots.OnProbeCaptured();
        time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));
        Assert.NotNull(second.EarlyEndReason(slot2));

        // 14:00: GB7RDG resends the WN4 directory from before the restart. Without persisting
        // known directories across restarts (not just the single newest in memory), this would
        // never be recognised, since this process never saw it complete.
        var slot3 = SlotStart.AddHours(2);
        time.SetUtcNow(slot3.AddSeconds(2));
        await using var third = new ReceiverHost(config, time, _ => { });
        third.Intake.Offer(wn4[0]); // the directory from before the restart; AlreadyComplete
        await third.Intake.DrainAsync(CancellationToken.None);
        third.Slots.OnProbeCaptured();
        time.Advance(ReceiverHost.QuietBeforeEarlyEnd + TimeSpan.FromSeconds(1));

        Assert.NotNull(third.EarlyEndReason(slot3));
    }
}
