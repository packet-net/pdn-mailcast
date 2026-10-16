using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

public class SlotTrackerTests
{
    private static readonly SlotSchedule Hourly = new(new TimeOnly(0, 0), 60);

    private static DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 10, 5, hour, minute, second, TimeSpan.Zero);

    /// <summary>A directory naming <paramref name="mode"/> as the waveform the slot went out on (null for a head end that does not say).</summary>
    private static BroadcastDirectory Directory(string? mode = null) => new(DateOnly.FromDateTime(DateTime.UtcNow), [], mode: mode);

    /// <summary>A directory with one entry, on <paramref name="date"/>, so two can be compared as the same or a different rotation.</summary>
    private static BroadcastDirectory DirectoryWithEntry(DateOnly date, ulong objectId = 1) =>
        new(date, [new DirectoryEntry(objectId, 0, 100, "1_GB7RDG", "Club news")]);

    private static ToneReport Tone(double seconds = 10) => new(1801, 1, 12, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void HourlySlots_WithNoToneHeard_AreStillTwoSlots()
    {
        var time = new FakeTimeProvider(At(12, 0, 30));
        var slots = new SlotTracker(time, _ => { }, Hourly);

        for (int i = 0; i < 10; i++)
        {
            slots.OnFrame();
            time.Advance(TimeSpan.FromSeconds(18));
        }
        Assert.Equal(10, slots.Last!.FramesHeard);
        Assert.Equal(At(12, 0), slots.Last.Scheduled);

        time.SetUtcNow(At(13, 0, 15));
        slots.OnFrame();

        Assert.Equal(1, slots.Last!.FramesHeard);
        Assert.Equal(At(13, 0), slots.Last.Scheduled);
    }

    [Fact]
    public void LongSlot_WithAQuietSpell_IsNotSplit()
    {
        var time = new FakeTimeProvider(At(12, 0, 30));
        var slots = new SlotTracker(time, _ => { }, Hourly);

        slots.OnFrame();
        time.SetUtcNow(At(12, 25));
        slots.OnFrame();
        time.SetUtcNow(At(12, 58, 30));
        slots.OnFrame();

        Assert.Equal(3, slots.Last!.FramesHeard);
        Assert.Equal(At(12, 0), slots.Last.Scheduled);
    }

    [Fact]
    public void LateSlot_IsNotMergedWithTheOneBefore()
    {
        var time = new FakeTimeProvider(At(12, 50));
        var slots = new SlotTracker(time, _ => { }, Hourly);
        slots.OnFrame();

        // The 13:00 slot started late; its frames are only minutes after the 12:00 slot's last.
        time.SetUtcNow(At(13, 3));
        slots.OnFrame();

        Assert.Equal(1, slots.Last!.FramesHeard);
        Assert.Equal(At(13, 0), slots.Last.Scheduled);
    }

    [Fact]
    public void FrameJustBeforeTheSlotByThisClock_CountsForIt()
    {
        var time = new FakeTimeProvider(At(12, 59, 30));
        var slots = new SlotTracker(time, _ => { }, Hourly);

        slots.OnFrame();

        Assert.Equal(At(13, 0), slots.Last!.Scheduled);
    }

    [Theory]
    // Began at the slot, a little early by this clock, late, and 5 minutes late.
    [InlineData(13, 0, 10, 13, 0)]
    [InlineData(12, 59, 50, 13, 0)]
    [InlineData(13, 2, 0, 13, 0)]
    [InlineData(13, 5, 10, 13, 0)]
    [InlineData(23, 58, 10, 0, 0)]
    public void ToneNearASlotStart_StartsThatSlot(int hour, int minute, int second, int slotHour, int slotMinute)
    {
        // The tone is reported when it ends, 10 s after it began.
        var time = new FakeTimeProvider(At(hour, minute, second));
        var slots = new SlotTracker(time, _ => { }, Hourly);

        slots.OnTone(Tone());

        Assert.NotNull(slots.Last!.Tone);
        Assert.Equal(time.GetUtcNow() - TimeSpan.FromSeconds(10), slots.Last.Started);
        var expected = new DateTimeOffset(2026, 10, slotHour == 0 && hour == 23 ? 6 : 5, slotHour, slotMinute, 0, TimeSpan.Zero);
        Assert.Equal(expected, slots.Last.Scheduled);
    }

    [Theory]
    [InlineData(13, 6, 0)]
    [InlineData(13, 30, 0)]
    [InlineData(12, 54, 0)]
    public void ToneFarFromAnySlotStart_IsIgnored(int hour, int minute, int second)
    {
        var time = new FakeTimeProvider(At(hour, minute, second));
        var log = new List<string>();
        var slots = new SlotTracker(time, log.Add, Hourly);

        slots.OnTone(Tone());

        Assert.Null(slots.Last);
        Assert.Contains(log, l => l.Contains("not GB7RDG's: ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void ToneFarFromAnySlot_DoesNotDisturbTheSlotInProgress()
    {
        var time = new FakeTimeProvider(At(13, 1));
        var slots = new SlotTracker(time, _ => { }, Hourly);
        slots.OnTone(Tone());
        slots.OnFrame();

        time.SetUtcNow(At(13, 20));
        slots.OnTone(Tone(12));
        slots.OnFrame();

        Assert.Equal(2, slots.Last!.FramesHeard);
        Assert.Equal(At(13, 0, 50), slots.Last.Started);
    }

    [Fact]
    public void FramesThenTheTone_AreOneSlot()
    {
        var time = new FakeTimeProvider(At(13, 0, 5));
        var slots = new SlotTracker(time, _ => { }, Hourly);
        slots.OnFrame();

        time.SetUtcNow(At(13, 0, 12));
        slots.OnTone(Tone());

        Assert.Equal(1, slots.Last!.FramesHeard);
        Assert.NotNull(slots.Last.Tone);
        Assert.Equal(At(13, 0, 2), slots.Last.Started);
    }

    [Fact]
    public void Recording_WithNoSchedule_TakesAnyToneAndGroupsByQuiet()
    {
        var time = new FakeTimeProvider(At(13, 30));
        var slots = new SlotTracker(time, _ => { });

        slots.OnTone(Tone());
        slots.OnFrame();
        time.Advance(SlotTracker.Gap + TimeSpan.FromSeconds(1));
        slots.OnFrame();

        Assert.Equal(1, slots.Last!.FramesHeard);
        Assert.Null(slots.Last.Tone);
        Assert.Null(slots.Last.Scheduled);
    }

    [Fact]
    public void OnDirectory_SetsHeardDirectory_EvenWithoutAWaveform()
    {
        var time = new FakeTimeProvider(At(13, 0, 5));
        var slots = new SlotTracker(time, _ => { }, Hourly);
        slots.OnFrame();
        var directory = Directory();

        slots.OnDirectory(directory);

        Assert.Same(directory, slots.Last!.HeardDirectory);
        Assert.Null(slots.Last.ListedWaveform);
    }

    [Fact]
    public void OnDirectory_WithAWaveform_SetsBothAndKeepsTheWaveformOnANextCallWithout()
    {
        var time = new FakeTimeProvider(At(13, 0, 5));
        var slots = new SlotTracker(time, _ => { }, Hourly);
        slots.OnFrame();
        var second = Directory();

        slots.OnDirectory(Directory("ms110d-wn4"));
        slots.OnDirectory(second);

        Assert.Same(second, slots.Last!.HeardDirectory);
        Assert.Equal("ms110d-wn4", slots.Last.ListedWaveform);
    }

    [Fact]
    public void OnDirectory_WithNoSlotTrackedYet_DoesNothing()
    {
        var time = new FakeTimeProvider(At(13, 0, 5));
        var slots = new SlotTracker(time, _ => { }, Hourly);

        slots.OnDirectory(Directory("ms110d-wn4"));

        Assert.Null(slots.Last);
    }

    [Fact]
    public void OnDirectory_StrayOlderDirectory_DoesNotReplaceTheOneTracked()
    {
        // Issue #72: a stray directory from an earlier day (a decode of an old directory's
        // frame, say) arriving mid-slot must not take the place of the one already tracked for
        // this slot, or the early-end check would judge completeness against the wrong rotation.
        var time = new FakeTimeProvider(At(13, 0, 5));
        var slots = new SlotTracker(time, _ => { }, Hourly);
        slots.OnFrame();
        var todayDate = DateOnly.FromDateTime(At(13, 0).UtcDateTime);
        var today = DirectoryWithEntry(todayDate);
        slots.OnDirectory(today);

        var strayOlder = DirectoryWithEntry(todayDate.AddDays(-1), objectId: 99);
        slots.OnDirectory(strayOlder);

        Assert.Same(today, slots.Last!.HeardDirectory);
    }

    [Fact]
    public void OnDirectory_StrayDirectorySameDateButADifferentRotation_DoesNotReplaceTheOneTracked()
    {
        // Issue #72: same day, but a different rotation (a second head end, or a test head end
        // on the air at the same time): still not this slot's.
        var time = new FakeTimeProvider(At(13, 0, 5));
        var slots = new SlotTracker(time, _ => { }, Hourly);
        slots.OnFrame();
        var today = DateOnly.FromDateTime(At(13, 0).UtcDateTime);
        var tracked = DirectoryWithEntry(today, objectId: 1);
        slots.OnDirectory(tracked);

        var strayOtherRotation = DirectoryWithEntry(today, objectId: 99);
        slots.OnDirectory(strayOtherRotation);

        Assert.Same(tracked, slots.Last!.HeardDirectory);
    }

    [Fact]
    public void OnDirectory_SameDateAndRotation_DifferentObject_StillReplaces()
    {
        // The two waveforms taking turns resend the same rotation as a different object (the
        // mode is part of what is hashed): still this slot's, so it may replace what is tracked.
        var time = new FakeTimeProvider(At(13, 0, 5));
        var slots = new SlotTracker(time, _ => { }, Hourly);
        slots.OnFrame();
        var today = DateOnly.FromDateTime(At(13, 0).UtcDateTime);
        slots.OnDirectory(DirectoryWithEntry(today, objectId: 1));

        var resend = DirectoryWithEntry(today, objectId: 1);
        slots.OnDirectory(resend);

        Assert.Same(resend, slots.Last!.HeardDirectory);
    }

    [Fact]
    public void OnDirectory_NewerDate_Replaces()
    {
        // Issue #72: the day rolling over mid-slot is this slot's too, even with an entirely
        // different rotation.
        var time = new FakeTimeProvider(At(13, 0, 5));
        var slots = new SlotTracker(time, _ => { }, Hourly);
        slots.OnFrame();
        var today = DateOnly.FromDateTime(At(13, 0).UtcDateTime);
        slots.OnDirectory(DirectoryWithEntry(today, objectId: 1));

        var tomorrow = DirectoryWithEntry(today.AddDays(1), objectId: 99);
        slots.OnDirectory(tomorrow);

        Assert.Same(tomorrow, slots.Last!.HeardDirectory);
    }

    [Fact]
    public void OnProbeCaptured_SetsTheFlagOnTheSlotInProgress()
    {
        var time = new FakeTimeProvider(At(13, 0, 5));
        var slots = new SlotTracker(time, _ => { }, Hourly);
        slots.OnFrame();
        Assert.False(slots.Last!.ProbeCaptured);

        slots.OnProbeCaptured();

        Assert.True(slots.Last!.ProbeCaptured);
    }

    [Fact]
    public void Schedule_LatestAndNearestStart()
    {
        Assert.Equal(At(13, 0), Hourly.LatestStart(At(13, 0)));
        Assert.Equal(At(13, 0), Hourly.LatestStart(At(13, 59, 59)));
        Assert.Equal(At(13, 0), Hourly.NearestStart(At(13, 29)));
        Assert.Equal(At(14, 0), Hourly.NearestStart(At(13, 31)));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), Hourly.LatestStart(At(0, 0, 30)));
    }

    private const string Wn4 = "ms110d-wn4";
    private const string Wn3 = "ms110d-wn3";

    /// <summary>Issue #133: one line summing the slot up once its window has closed, and only once.</summary>
    [Fact]
    public void WindowClosing_WritesTheSlotsSummaryLineOnce()
    {
        var lines = new List<string>();
        var time = new FakeTimeProvider(At(11, 0, 12));
        var slots = new SlotTracker(time, lines.Add, Hourly);
        slots.OnTone(new ToneReport(1801.2, 1.2, 14.2, TimeSpan.FromSeconds(10)));
        for (int i = 0; i < 120; i++)
        {
            slots.OnFrame(Wn4, 240);
        }
        for (int i = 0; i < 5; i++)
        {
            slots.OnFrame(Wn3, 120);
        }
        var signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        slots.OnDirectory(new BroadcastDirectory(DateOnly.FromDateTime(DateTime.UtcNow), [new DirectoryEntry(1, 0, 100, "1_GB7RDG", "Club news")], signer: signer));
        slots.OnBulletinCompleted();
        slots.OnBulletinCompleted();
        slots.OnBulletinCompleted();
        slots.OnDelivered(accepted: 1, alreadyHad: 2);
        slots.OnGivenUp(1);

        time.SetUtcNow(At(11, 11, 59));
        slots.EndIfOver(null);
        Assert.DoesNotContain(lines, l => l.Contains("ended", StringComparison.Ordinal));

        time.SetUtcNow(At(11, 12));
        slots.EndIfOver(null);
        slots.EndIfOver(null);

        var line = Assert.Single(lines, l => l.Contains("ended", StringComparison.Ordinal));
        Assert.Equal("slot: the 11:00 UTC slot ended: 125 frames, 120 on 1200 bps (WN4) and 5 on 600 bps (WN3); 29.4 KB decoded from them; "
            + "tone SNR 14 dB, +1.2 Hz off; bulletins 3 completed, 1 delivered, 2 already had, 1 given up; directory heard, signed", line);
        Assert.True(line.All(ch => ch is >= ' ' and <= '~'));
        Assert.True(slots.Last!.Ended);
        Assert.Equal(29_400, slots.Last.BytesDecoded);
    }

    /// <summary>Issue #133: a window ended early gets its line then, saying so, not at its usual close.</summary>
    [Fact]
    public void WindowEndedEarly_WritesTheLineAtOnce_AndSaysSo()
    {
        var lines = new List<string>();
        var time = new FakeTimeProvider(At(11, 3));
        var slots = new SlotTracker(time, lines.Add, Hourly);
        slots.OnFrame(Wn4, 200);

        slots.EndIfOver(At(10, 0));
        Assert.DoesNotContain(lines, l => l.Contains("ended", StringComparison.Ordinal));

        slots.EndIfOver(At(11, 0));

        Assert.Equal("slot: the 11:00 UTC slot ended: 1 frame on 1200 bps (WN4); 200 bytes decoded from them; no tone; bulletins 0 completed, 0 delivered; no directory; window ended early, nothing left to wait for",
            Assert.Single(lines, l => l.Contains("ended", StringComparison.Ordinal)));
    }

    /// <summary>Issue #133: a slot that never got its line (the check came too late) gets it as the next slot begins, before that slot's own lines.</summary>
    [Fact]
    public void NextSlotBeginning_WritesAMissedLineFirst()
    {
        var lines = new List<string>();
        var time = new FakeTimeProvider(At(11, 3));
        var slots = new SlotTracker(time, lines.Add, Hourly);
        slots.OnFrame(null, 50);

        time.SetUtcNow(At(12, 0, 30));
        slots.OnFrame(Wn4, 50);

        Assert.Equal("slot: the 11:00 UTC slot ended: 1 frame; 50 bytes decoded from them; no tone; bulletins 0 completed, 0 delivered; no directory", lines[^2]);
        Assert.Equal("slot: 1 frame heard on 1200 bps (WN4)", lines[^1]);
    }

    /// <summary>Issue #133: without a schedule (a recording) the line comes from <see cref="SlotTracker.Finish"/>, once.</summary>
    [Fact]
    public void Recording_GetsItsLineFromFinish()
    {
        var lines = new List<string>();
        var time = new FakeTimeProvider(At(9, 15, 7));
        var slots = new SlotTracker(time, lines.Add);
        slots.OnFrame(Wn4, 10);
        slots.OnFrame(Wn4, 10);

        time.SetUtcNow(At(23, 0));
        slots.EndIfOver(null);
        Assert.DoesNotContain(lines, l => l.Contains("ended", StringComparison.Ordinal));

        slots.Finish();
        slots.Finish();

        Assert.Equal("slot: the slot that began at 09:15:07 UTC ended: 2 frames on 1200 bps (WN4); 20 bytes decoded from them; no tone; bulletins 0 completed, 0 delivered; no directory",
            Assert.Single(lines, l => l.Contains("ended", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(999, "999 bytes")]
    [InlineData(1000, "1.0 KB")]
    [InlineData(29_449, "29.4 KB")]
    public void Bytes_ReadsPlainly(long bytes, string words) => Assert.Equal(words, SlotTracker.Bytes(bytes));
}
