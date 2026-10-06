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
    public void WebSdr_EightOfTwentyFour_IsEveryThreeHoursFromTheAnchor()
    {
        Assert.Equal(["00:00", "03:00", "06:00", "09:00", "12:00", "15:00", "18:00", "21:00"], Hhmm(ListeningWindow.WebSdrSlots(Hourly, 8)));
    }

    [Fact]
    public void WebSdr_AnchorLateInTheDay_WrapsPastMidnightAndIsSorted()
    {
        var schedule = new SlotSchedule(new TimeOnly(22, 30), 60);

        Assert.Equal(["01:30", "04:30", "07:30", "10:30", "13:30", "16:30", "19:30", "22:30"], Hhmm(ListeningWindow.WebSdrSlots(schedule, 8)));
    }

    [Fact]
    public void WebSdr_UnevenCount_IsSpreadThroughTheDay()
    {
        Assert.Equal(["00:00", "04:00", "09:00", "14:00", "19:00"], Hhmm(ListeningWindow.WebSdrSlots(Hourly, 5)));
    }

    [Fact]
    public void WebSdr_MoreThanThereAreSlots_ListensToEach()
    {
        var daily = new SlotSchedule(new TimeOnly(12, 0), 1440);

        Assert.Equal(["12:00"], Hhmm(ListeningWindow.WebSdrSlots(daily, 8)));
        Assert.Equal("once a day at 12:00 UTC", daily.Describe());
    }

    [Theory]
    // Before a window, in one, at its very end, and between two.
    [InlineData("2026-10-05T01:00:00Z", "2026-10-05T02:58:00Z", "2026-10-05T03:12:00Z", "2026-10-05T03:00:00Z")]
    [InlineData("2026-10-05T02:58:00Z", "2026-10-05T02:58:00Z", "2026-10-05T03:12:00Z", "2026-10-05T03:00:00Z")]
    [InlineData("2026-10-05T03:11:59Z", "2026-10-05T02:58:00Z", "2026-10-05T03:12:00Z", "2026-10-05T03:00:00Z")]
    [InlineData("2026-10-05T03:12:00Z", "2026-10-05T05:58:00Z", "2026-10-05T06:12:00Z", "2026-10-05T06:00:00Z")]
    // The last slot of the day, then midnight's, which opens the evening before.
    [InlineData("2026-10-05T21:05:00Z", "2026-10-05T20:58:00Z", "2026-10-05T21:12:00Z", "2026-10-05T21:00:00Z")]
    [InlineData("2026-10-05T23:00:00Z", "2026-10-05T23:58:00Z", "2026-10-06T00:12:00Z", "2026-10-06T00:00:00Z")]
    [InlineData("2026-10-05T23:59:00Z", "2026-10-05T23:58:00Z", "2026-10-06T00:12:00Z", "2026-10-06T00:00:00Z")]
    [InlineData("2026-10-06T00:05:00Z", "2026-10-05T23:58:00Z", "2026-10-06T00:12:00Z", "2026-10-06T00:00:00Z")]
    public void Next_WebSdrHourly_IsTheWindowInProgressOrTheNextOne(string now, string opens, string closes, string slot)
    {
        var (o, c, s) = ListeningWindow.Next(T(now), ListeningWindow.WebSdrSlots(Hourly, 8));

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
    public void WebSdr_MostSlotsADay_StaysInsideThreeHours()
    {
        Assert.Equal(12, ReceiverConfig.MostWebSdrSlotsPerDay);
        Assert.True(ReceiverConfig.MostWebSdrSlotsPerDay * (ReceiverConfig.WebSdrBefore + ReceiverConfig.WebSdrAfter) <= TimeSpan.FromHours(3));
        Assert.True((ReceiverConfig.MostWebSdrSlotsPerDay + 1) * (ReceiverConfig.WebSdrBefore + ReceiverConfig.WebSdrAfter) > TimeSpan.FromHours(3));
    }

    private static readonly SlotSchedule Daylight = new(new TimeOnly(0, 0), 60, Packet.Mailcast.DaylightRule.Gb7rdg);

    private static string[] Hhmm(IEnumerable<DateTimeOffset> times) => [.. times.Select(t => t.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture))];

    [Theory]
    // Every daylight slot fits for most of the year: 9 in October, 5 in midwinter.
    [InlineData("2026-10-05", 9, new[] { "09:00", "10:00", "11:00", "12:00", "13:00", "14:00", "15:00", "16:00", "17:00" })]
    [InlineData("2026-12-21", 5, new[] { "11:00", "12:00", "13:00", "14:00", "15:00" })]
    // Midsummer has 14, more than the 12 that fit: the two earliest morning ones are left out.
    [InlineData("2026-06-21", 14, new[] { "08:00", "09:00", "10:00", "11:00", "12:00", "13:00", "14:00", "15:00", "16:00", "17:00", "18:00", "19:00" })]
    public void WebSdr_Default_IsEveryDaylightSlotThatFits_LeavingOutTheEarliest(string date, int daylightSlots, string[] listened)
    {
        var day = DateOnly.Parse(date, CultureInfo.InvariantCulture);

        Assert.Equal(daylightSlots, Daylight.ActiveOn(day).Count);
        Assert.Equal(listened, Hhmm(ListeningWindow.WebSdrSlotsOn(Daylight, null, day)));
        Assert.True(listened.Length * ReceiverConfig.WebSdrMinutesPerSlot <= ReceiverConfig.WebSdrAllowanceMinutes);
    }

    [Fact]
    public void WebSdr_Default_NeverHasMoreThanFitAllYear()
    {
        var first = new DateOnly(2026, 1, 1);
        for (var day = first; day < first.AddYears(1); day = day.AddDays(1))
        {
            var active = Daylight.ActiveOn(day);
            var listened = ListeningWindow.WebSdrSlotsOn(Daylight, null, day);
            Assert.Equal(Math.Min(active.Count, ReceiverConfig.MostWebSdrSlotsPerDay), listened.Count);
            Assert.Equal(active[^1], listened[^1]);
        }
    }

    [Fact]
    public void WebSdr_Explicit_IsSpreadEvenlyAsBefore()
    {
        Assert.Equal(["09:00", "10:00", "11:00", "12:00", "13:00", "14:00", "15:00", "16:00"], Hhmm(ListeningWindow.WebSdrSlotsOn(Daylight, 8, new DateOnly(2026, 10, 5))));
        Assert.Equal(["06:00", "07:00", "09:00", "11:00", "13:00", "14:00", "16:00", "18:00"], Hhmm(ListeningWindow.WebSdrSlotsOn(Daylight, 8, new DateOnly(2026, 6, 21))));
        Assert.Equal(["11:00", "12:00", "13:00", "14:00", "15:00"], Hhmm(ListeningWindow.WebSdrSlotsOn(Daylight, 8, new DateOnly(2026, 12, 21))));
    }

    [Fact]
    public void WebSdr_DefaultWithoutDaylight_IsTwelveSpreadEvenly()
    {
        Assert.Equal(["00:00", "02:00", "04:00", "06:00", "08:00", "10:00", "12:00", "14:00", "16:00", "18:00", "20:00", "22:00"], Hhmm(ListeningWindow.WebSdrSlots(Hourly, null)));
    }

    [Fact]
    public void Describe_SaysWhichSlots()
    {
        string words = ListeningWindow.Describe(Hourly, ListeningWindow.WebSdrSlots(Hourly, 8));

        Assert.Contains("8 of the 24 slots a day, at 00:00, 03:00, 06:00, 09:00, 12:00, 15:00, 18:00 and 21:00 UTC", words, StringComparison.Ordinal);
    }
}
