using Packet.Mailcast;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Slot;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// A head end whose slots start off the hour (issue #176): <c>slot.timeUtc</c> is what every slot
/// is counted from, so <c>"00:30"</c> with <c>"everyMinutes": 60</c> is a slot at half past each
/// hour, and everything that works out a slot's time follows it.
/// </summary>
public class SlotStartTests
{
    private static HeadEndConfig Parse(string slot, string schedule = "{}") =>
        HeadEndConfig.Parse($$"""{"station": {"apiKey": "k"}, "slot": {{slot}}, "schedule": {{schedule}}}""");

    private const string HalfPastInDaylight = """{"timeUtc": "00:30", "everyMinutes": 60, "daylight": {"locator": "IO91lk", "afterSunriseMinutes": 120, "beforeSunsetMinutes": 30}}""";

    private static DateTimeOffset At(int hour, int minute = 0, int day = 5) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void HalfPast_ReachesTheScheduleTheSchedulerAndTheDirectory()
    {
        var config = Parse(HalfPastInDaylight);
        var expected = new SlotTimetable(new TimeOnly(0, 30), 60, DaylightRule.Gb7rdg);
        Assert.Equal(expected, config.ToSlotTimetable());
        Assert.Equal(expected, config.ToSlotSchedule().Timetable);
        Assert.Equal(expected, config.ToScheduleOptions().Timetable);
        Assert.Equal(["slots=00:30/60", "daylight=IO91lk/120/30"], expected.ToFields());
        Assert.Equal(expected, SlotTimetable.FromFields(expected.ToFields()));
    }

    [Fact]
    public void HalfPast_EverySlotOfTheDayIsAtHalfPast_AndDaylightPicksFromThem()
    {
        var schedule = Parse(HalfPastInDaylight).ToSlotSchedule();
        var day = new DateOnly(2026, 10, 5);
        Assert.All(schedule.Timetable.SlotsOn(day), t => Assert.Equal(30, t.Minute));
        Assert.Equal(24, schedule.Timetable.SlotsOn(day).Count);

        // Sunrise 06:11Z and sunset 17:33Z: from 08:11 to 17:03, so 08:30 to 16:30.
        Assert.Equal(
            "daylight 2026-10-05 at IO91lk: sunrise 06:11Z, sunset 17:33Z; 9 slots, 08:30, 09:30, 10:30, 11:30, 12:30, 13:30, 14:30, 15:30 and 16:30 UTC",
            schedule.DescribeDay(day));
        Assert.Equal(At(12, 30), schedule.SlotAtOrBefore(At(13, 10)));
        Assert.Equal(At(13, 30), schedule.SlotAfter(At(12, 30)));
        Assert.Equal(At(8, 30, day: 6), schedule.NextActiveAfter(At(16, 30)));

        // "12:30" is the same hourly timetable as "00:30".
        var noonish = Parse("""{"timeUtc": "12:30", "everyMinutes": 60}""").ToSlotSchedule();
        Assert.Equal(Parse("""{"timeUtc": "00:30", "everyMinutes": 60}""").ToSlotSchedule().Timetable.SlotsOn(day), noonish.Timetable.SlotsOn(day));
    }

    [Fact]
    public void HalfPast_CatchUpAndRetriesCountFromTheHalfHour()
    {
        var schedule = Parse(HalfPastInDaylight).ToSlotSchedule();
        var catchUp = TimeSpan.FromMinutes(5);
        Assert.Equal(At(12, 30), HeadEndService.NextSlot(At(12, 10), schedule, catchUp, null));
        Assert.Equal(At(12, 33), HeadEndService.NextSlot(At(12, 33), schedule, catchUp, null));
        Assert.Equal(At(13, 30), HeadEndService.NextSlot(At(12, 36), schedule, catchUp, null));
        Assert.Equal(At(8, 30), HeadEndService.NextSlot(At(3), schedule, catchUp, null));

        var ran = new SlotReport { Slot = At(12, 30), Day = new DateOnly(2026, 10, 5), Outcome = SlotOutcome.Completed, End = At(12, 37) };
        Assert.Equal(At(13, 30), HeadEndService.NextSlot(At(12, 38), schedule, catchUp, ran));
        var skipped = ran with { Outcome = SlotOutcome.Skipped, Retryable = true, End = At(12, 30) };
        Assert.Equal(At(12, 35), HeadEndService.NextSlot(At(12, 30), schedule, TimeSpan.FromMinutes(10), skipped, TimeSpan.FromMinutes(5)));

        // A report from before slots had times was that day's slot at the anchor.
        Assert.Equal(At(0, 30), schedule.SlotOf(new SlotReport { Day = new DateOnly(2026, 10, 5), Outcome = SlotOutcome.Completed }));
    }

    [Fact]
    public void HalfPast_SlotOffsetsCountFromTheBulletinsFirstSlot_AndLandOnHalfPastSlotsInDaylight()
    {
        var options = Parse(HalfPastInDaylight, """{"slotShares": [1.2, 0.4], "slotOffsets": [0, 5]}""").ToScheduleOptions();
        Assert.Equal([At(10, 30), At(15, 30)], BroadcastScheduler.CarryingSlots(At(10, 30), options));

        // Five slots after 15:30 is 20:30, in the dark: the carrying moves to the next morning's first.
        Assert.Equal([At(15, 30), At(8, 30, day: 6)], BroadcastScheduler.CarryingSlots(At(15, 30), options));
    }

    [Fact]
    public void HalfPast_SlotsHaveTheirOwnSeed()
    {
        Assert.NotEqual(StoreSlotPlanner.Seed(At(12)), StoreSlotPlanner.Seed(At(12, 30)));
        Assert.NotEqual(StoreSlotPlanner.Seed(At(13)), StoreSlotPlanner.Seed(At(12, 30)));
    }

    [Theory]
    [InlineData("""{"timeUtc": "00:30", "everyMinutes": 60}""", "schedule: a slot every 60 min, counted from 00:30Z, so at :30 each hour")]
    [InlineData("""{"timeUtc": "00:00", "everyMinutes": 60}""", "schedule: a slot every 60 min, counted from 00:00Z, so at :00 each hour")]
    [InlineData("""{"timeUtc": "07:10", "everyMinutes": 30}""", "schedule: a slot every 30 min, counted from 07:10Z, so at :10 and :40 each hour")]
    [InlineData("""{"timeUtc": "00:00", "everyMinutes": 15}""", "schedule: a slot every 15 min, counted from 00:00Z, so at :00, :15, :30 and :45 each hour")]
    [InlineData("""{"timeUtc": "01:30", "everyMinutes": 120}""", "schedule: a slot every 120 min, counted from 01:30Z")]
    [InlineData("""{"timeUtc": "12:00"}""", "schedule: a slot every day at 12:00Z")]
    public void TheStartUpLine_SaysWhereInTheHourSlotsStart(string slot, string expected)
    {
        Assert.Equal(expected, Program.DescribeSchedule(Parse(slot)));
    }
}
