using System.Globalization;

namespace Mailcast.Receiver.Tests;

public class ListeningWindowTests
{
    private static readonly SlotSchedule Hourly = new(new TimeOnly(0, 0), 60);

    private static DateTimeOffset T(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

    private static string[] Hhmm(IEnumerable<TimeOnly> times) => [.. times.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture))];

    [Fact]
    public void Hourly_HasASlotEveryHourOnTheHour()
    {
        Assert.Equal(24, Hourly.SlotsPerDay);
        Assert.Equal(Enumerable.Range(0, 24).Select(h => $"{h:00}:00"), Hhmm(Hourly.FromAnchor));
        Assert.Equal("every hour on the hour", Hourly.Describe());
    }

    [Theory]
    [InlineData("2026-10-05T13:00:00Z", "2026-10-05T13:00:00Z")]
    [InlineData("2026-10-05T13:00:01Z", "2026-10-05T14:00:00Z")]
    [InlineData("2026-10-05T23:30:00Z", "2026-10-06T00:00:00Z")]
    public void NextStart_IsTheNextHour(string now, string next)
    {
        Assert.Equal(T(next), Hourly.NextStart(T(now)));
    }

    [Fact]
    public void OnceADay_DescribesItself()
    {
        var daily = new SlotSchedule(new TimeOnly(12, 0), 1440);

        Assert.Equal("once a day at 12:00 UTC", daily.Describe());
    }

    private static readonly FollowedSlots TwoHourly = FollowedSlots.One(new SlotSchedule(new TimeOnly(0, 0), 120), "M0LTE");

    [Theory]
    // Before a window, in one, at its very end, and between two.
    [InlineData("2026-10-05T01:00:00Z", "2026-10-05T01:58:00Z", "2026-10-05T02:12:00Z", "2026-10-05T02:00:00Z")]
    [InlineData("2026-10-05T01:58:00Z", "2026-10-05T01:58:00Z", "2026-10-05T02:12:00Z", "2026-10-05T02:00:00Z")]
    [InlineData("2026-10-05T02:11:59Z", "2026-10-05T01:58:00Z", "2026-10-05T02:12:00Z", "2026-10-05T02:00:00Z")]
    [InlineData("2026-10-05T02:12:00Z", "2026-10-05T03:58:00Z", "2026-10-05T04:12:00Z", "2026-10-05T04:00:00Z")]
    // The last slot of the day, then midnight's, which opens the evening before.
    [InlineData("2026-10-05T22:05:00Z", "2026-10-05T21:58:00Z", "2026-10-05T22:12:00Z", "2026-10-05T22:00:00Z")]
    [InlineData("2026-10-05T23:00:00Z", "2026-10-05T23:58:00Z", "2026-10-06T00:12:00Z", "2026-10-06T00:00:00Z")]
    [InlineData("2026-10-05T23:59:00Z", "2026-10-05T23:58:00Z", "2026-10-06T00:12:00Z", "2026-10-06T00:00:00Z")]
    [InlineData("2026-10-06T00:05:00Z", "2026-10-05T23:58:00Z", "2026-10-06T00:12:00Z", "2026-10-06T00:00:00Z")]
    public void Next_WebSdr_IsTheWindowInProgressOrTheNextOne(string now, string opens, string closes, string slot)
    {
        var (o, c, s) = ListeningWindow.Next(T(now), TwoHourly);

        Assert.Equal(T(opens), o);
        Assert.Equal(T(closes), c);
        Assert.Equal(T(slot), s);
    }

    [Fact]
    public void Next_EverySlot_FollowsOnHourByHour()
    {
        var (opens, closes, slot) = ListeningWindow.Next(T("2026-10-05T13:20:00Z"), Hourly.FromAnchor);

        Assert.Equal(T("2026-10-05T13:58:00Z"), opens);
        Assert.Equal(T("2026-10-05T14:12:00Z"), closes);
        Assert.Equal(T("2026-10-05T14:00:00Z"), slot);
    }

    [Fact]
    public void Next_WebSdrWithoutDaylight_IsEverySlot()
    {
        var followed = FollowedSlots.One(Hourly, "M0LTE");
        var at = T("2026-10-05T00:00:00Z");
        for (int h = 0; h < 24; h++)
        {
            var (_, closes, slot) = ListeningWindow.Next(at, followed);
            Assert.Equal(T("2026-10-05T00:00:00Z").AddHours(h), slot);
            at = closes;
        }
    }

    private static readonly SlotSchedule Daylight = new(new TimeOnly(0, 0), 60, StationFixture.Daylight);

    [Fact]
    public void WebSdr_ListensToEveryDaylightSlot_AllYear()
    {
        var followed = FollowedSlots.One(Daylight, "M0LTE");
        var first = new DateOnly(2026, 1, 1);
        for (var day = first; day < first.AddYears(1); day = day.AddDays(1))
        {
            var active = Daylight.ActiveOn(day);
            var opened = new List<DateTimeOffset>();
            var at = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            while (true)
            {
                var (_, closes, slot) = ListeningWindow.Next(at, followed);
                if (DateOnly.FromDateTime(slot.UtcDateTime) != day)
                {
                    break;
                }
                opened.Add(slot);
                at = closes;
            }
            Assert.Equal(active, opened);
        }
    }

    [Fact]
    public void Describe_InDaylight_SaysEverySlot()
    {
        var followed = FollowedSlots.One(Daylight, "M0LTE");
        Assert.Contains("listens to all 9 daylight slots, at 09:00, 10:00, 11:00, 12:00, 13:00, 14:00, 15:00, 16:00 and 17:00 UTC, from 2 minutes before each to 12 after",
            ListeningWindow.Describe(followed, new DateOnly(2026, 10, 5)), StringComparison.Ordinal);
        // Midsummer's 14, the earliest morning ones too.
        Assert.Contains("listens to all 14 daylight slots, at 06:00, 07:00, 08:00,",
            ListeningWindow.Describe(followed, new DateOnly(2026, 6, 21)), StringComparison.Ordinal);
    }
}
