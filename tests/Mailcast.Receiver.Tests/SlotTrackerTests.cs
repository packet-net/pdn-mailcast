using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

public class SlotTrackerTests
{
    private static readonly SlotSchedule Hourly = new(new TimeOnly(0, 0), 60);

    private static DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 10, 5, hour, minute, second, TimeSpan.Zero);

    /// <summary>A directory naming <paramref name="mode"/> as the waveform the slot went out on (null for a head end that does not say).</summary>
    private static BroadcastDirectory Directory(string? mode = null) => new(DateOnly.FromDateTime(DateTime.UtcNow), [], mode: mode);

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
}
