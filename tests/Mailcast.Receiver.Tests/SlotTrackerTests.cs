using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

public class SlotTrackerTests
{
    [Fact]
    public void HourlySlots_WithNoToneHeard_AreStillTwoSlots()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 30, TimeSpan.Zero));
        var slots = new SlotTracker(time, _ => { });

        // Three minutes of frames in the 12:00 slot...
        for (int i = 0; i < 10; i++)
        {
            slots.OnFrame();
            time.Advance(TimeSpan.FromSeconds(18));
        }
        Assert.Equal(10, slots.Last!.FramesHeard);

        // ...then the 13:00 slot's first frame, its tone missed.
        time.SetUtcNow(new DateTimeOffset(2026, 10, 5, 13, 0, 15, TimeSpan.Zero));
        slots.OnFrame();

        Assert.Equal(1, slots.Last!.FramesHeard);
        Assert.Equal(time.GetUtcNow(), slots.Last.Started);
    }

    [Fact]
    public void TenSecondTone_StartsTheSlotWhenTheToneBegan()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 13, 0, 10, TimeSpan.Zero));
        var slots = new SlotTracker(time, _ => { });

        slots.OnTone(new ToneReport(1801, 1, 12, TimeSpan.FromSeconds(10)));
        slots.OnFrame();

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero), slots.Last!.Started);
        Assert.Equal(1, slots.Last.FramesHeard);
    }
}
