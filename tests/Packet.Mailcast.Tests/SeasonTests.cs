using System.Globalization;

namespace Packet.Mailcast.Tests;

/// <summary>
/// The seasonal window (issue #273): slots follow a measured monthly table of when 40 m opens and
/// closes, interpolated between the 15ths, each slot ending by the close; the directory carries it
/// in a field receivers from before ignore.
/// </summary>
public class SeasonTests
{
    private static readonly SlotTimetable Hourly = new(TimeOnly.MinValue, 60, StationFixture.SeasonalDaylight);

    private static string[] Hhmm(IEnumerable<DateTimeOffset> slots) => [.. slots.Select(s => s.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture))];

    private static DateOnly D(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Span(int first, int last) => string.Join(',', Enumerable.Range(first, last - first + 1).Select(h => $"{h:00}:00"));

    [Theory]
    // December 08:24 to 17:01: 09:00 to 16:00, where the offsets gave 11:00 to 15:00.
    [InlineData("2026-12-15", 9, 16)]
    // October 08:19 to 17:17: 17:00 ends at 17:10, in time.
    [InlineData("2026-10-15", 9, 17)]
    // June 07:36 to midnight: 08:00 to 23:00 (23:00 ends at 23:10), where the offsets stopped at 19:00.
    [InlineData("2026-06-15", 8, 23)]
    // April 09:25 to 20:53: no 08:00 or 09:00, which the offsets sent into a weak band.
    [InlineData("2026-04-15", 10, 20)]
    public void OnThe15th_TheSlotsAreTheMonthsWindow(string day, int first, int last)
    {
        Assert.Equal(Span(first, last).Split(','), Hhmm(Hourly.ActiveSlotsOn(D(day))));
    }

    [Fact]
    public void ASlotMustEndByTheClose_NotJustStartBeforeIt()
    {
        // October closes 17:17: a 10 minute 17:00 slot fits, a 20 minute one does not.
        var months = Enumerable.Repeat("08:00-17:17", 12).ToList();
        var ten = new SlotTimetable(TimeOnly.MinValue, 60, new DaylightRule("IO91lk", 120, 30, SeasonalWindow.FromSettings(months, 10, out _)));
        var twenty = new SlotTimetable(TimeOnly.MinValue, 60, new DaylightRule("IO91lk", 120, 30, SeasonalWindow.FromSettings(months, 20, out _)));
        Assert.Equal("17:00", Hhmm(ten.ActiveSlotsOn(D("2026-10-15")))[^1]);
        Assert.Equal("16:00", Hhmm(twenty.ActiveSlotsOn(D("2026-10-15")))[^1]);
        // Opening is the earliest start: 08:00 itself runs.
        Assert.Equal("08:00", Hhmm(ten.ActiveSlotsOn(D("2026-10-15")))[0]);
    }

    [Fact]
    public void BetweenThe15ths_TheWindowMovesInAStraightLine()
    {
        var season = StationFixture.Season;
        // 15 May 07:42 to 22:23, 15 June 07:36 to 24:00: 31 days apart.
        Assert.Equal((462, 1343 - 10), season.MinutesOn(D("2026-05-15")));
        Assert.Equal((456, 1440 - 10), season.MinutesOn(D("2026-06-15")));
        // Day 16 of 31: 462 - 6 * 16 / 31 = 458.9 -> 459; 1343 + 97 * 16 / 31 = 1393.06 -> 1393.
        Assert.Equal((459, 1393 - 10), season.MinutesOn(D("2026-05-31")));
        // Across the new year, 15 December to 15 January, without a jump on 1 January.
        var dec31 = season.MinutesOn(D("2026-12-31"))!.Value;
        var jan1 = season.MinutesOn(D("2027-01-01"))!.Value;
        Assert.InRange(Math.Abs(jan1.Opens - dec31.Opens), 0, 1);
        Assert.InRange(Math.Abs(jan1.LastStart - dec31.LastStart), 0, 1);
        // Every day of a year moves by a few minutes at most (the fastest, August to September, closes 173 minutes earlier over 31 days).
        var day = D("2026-01-01");
        var previous = season.MinutesOn(day)!.Value;
        for (int i = 1; i < 366; i++)
        {
            var today = season.MinutesOn(day.AddDays(i))!.Value;
            Assert.InRange(Math.Abs(today.Opens - previous.Opens), 0, 6);
            Assert.InRange(Math.Abs(today.LastStart - previous.LastStart), 0, 6);
            previous = today;
        }
    }

    [Fact]
    public void ACloseAfterMidnight_RunsSlotsIntoTheNextUtcDay()
    {
        var months = Enumerable.Repeat("20:00-00:30", 12).ToList();
        var late = new SlotTimetable(TimeOnly.MinValue, 60, new DaylightRule("IO91lk", 120, 30, SeasonalWindow.FromSettings(months, 10, out _)));
        // The window of the 15th runs 20:00 to 00:20 for a start; the 16th's 00:00 belongs to it.
        Assert.Equal(["00:00", "20:00", "21:00", "22:00", "23:00"], Hhmm(late.ActiveSlotsOn(D("2026-06-16"))));
        Assert.Equal(new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero), late.NextActiveAfter(new DateTimeOffset(2026, 6, 15, 23, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TheDirectory_CarriesTheSeason_AndReceiversFromBeforeFollowTheOffsets()
    {
        var fields = Hourly.ToFields();
        Assert.Equal(
            ["slots=00:00/60", "daylight=IO91lk/120/30",
             "season=10/0830-1718/0802-1839/0759-2006/0925-2053/0742-2223/0736-0000/0656-2342/0704-2213/0841-1920/0819-1717/0749-1643/0824-1701"],
            fields);
        Assert.Equal(Hourly, SlotTimetable.FromFields(fields));
        // With evening slots, the season goes before evening=.
        var evening = Hourly with { Evening = new EveningRule(new TimeOnly(22, 0)) };
        Assert.Equal("evening=22:00", evening.ToFields()[^1]);
        Assert.Equal(evening, SlotTimetable.FromFields(evening.ToFields()));

        // A reader that only knows slots= and daylight= gets the offsets, as before.
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60, StationFixture.Daylight), SlotTimetable.FromFields(fields.Take(2)));
        // A season field this reader cannot read is left out; the offsets stand.
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60, StationFixture.Daylight), SlotTimetable.FromFields([.. fields.Take(2), "season=10/0830-1718"]));
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60, StationFixture.Daylight), SlotTimetable.FromFields([.. fields.Take(2), "season=x"]));
        // Without a daylight rule, a season means nothing.
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60), SlotTimetable.FromFields(["slots=00:00/60", fields[2]]));
    }

    [Fact]
    public void InSummer_TheSeasonCoversTheEvening_SoTheDaylightSlotsTakePrecedence()
    {
        var withEvening = Hourly with { Evening = new EveningRule(new TimeOnly(22, 0)) };
        // June: daylight runs to 23:00, so no slot before 22:00 is left for the evening.
        Assert.Empty(withEvening.EveningSlotsOn(D("2026-06-15")));
        // December: daylight to 16:00, the evening from 17:00 (sunset is about 15:55).
        Assert.Equal(["17:00", "18:00", "19:00", "20:00", "21:00"], Hhmm(withEvening.EveningSlotsOn(D("2026-12-15"))));
    }

    [Theory]
    [InlineData(new[] { "08:00-17:00" }, "twelve months")]
    [InlineData(new[] { "08:00-17:00", "08:00-17:00", "08:00-17:00", "08:00-17:00", "08:00-17:00", "8am-5pm", "08:00-17:00", "08:00-17:00", "08:00-17:00", "08:00-17:00", "08:00-17:00", "08:00-17:00" }, "Jun '8am-5pm'")]
    public void BadSettings_AreRefusedInASentence(string[] months, string mentions)
    {
        var settings = new DaylightSettings { Locator = "IO91lk", Season = months };
        Assert.Contains(mentions, settings.Problem(60, TimeOnly.MinValue), StringComparison.Ordinal);
        // A window too short for any slot is refused too.
        var tooShort = new DaylightSettings { Locator = "IO91lk", Season = [.. Enumerable.Repeat("12:05-12:10", 12)] };
        Assert.Contains("no slot in a whole year", tooShort.Problem(60, TimeOnly.MinValue), StringComparison.Ordinal);
        Assert.Null(new DaylightSettings { Locator = "IO91lk", Season = StationFixture.SeasonMonths }.Problem(60, TimeOnly.MinValue));
    }

    [Fact]
    public void TheRule_SaysWhatItFollows()
    {
        Assert.StartsWith("by month, UTC, each slot ending by the close (10 minutes): Jan 08:30-17:18, Feb 08:02-18:39", StationFixture.SeasonalDaylight.Describe(), StringComparison.Ordinal);
        Assert.NotEqual(StationFixture.Daylight, StationFixture.SeasonalDaylight);
        Assert.Equal(StationFixture.SeasonalDaylight, new DaylightRule("IO91lk", 120, 30, SeasonalWindow.FromText(StationFixture.Season.ToText())));
    }
}
