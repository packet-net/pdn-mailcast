using System.Globalization;

namespace Packet.Mailcast.Tests;

/// <summary>Which slots run with a daylight rule, and the timetable's form in the directory.</summary>
public class TimetableTests
{
    private static readonly SlotTimetable Gb7rdg = new(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);

    private static DateTimeOffset T(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string[] Hhmm(IEnumerable<DateTimeOffset> slots) => [.. slots.Select(s => s.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture))];

    [Theory]
    // Sunrise 06:11 and sunset 17:33 UTC (USNO), so 08:11 to 17:03.
    [InlineData("2026-10-05", "09:00,10:00,11:00,12:00,13:00,14:00,15:00,16:00,17:00")]
    // Sunrise 08:07, sunset 15:57, so 10:07 to 15:27.
    [InlineData("2026-12-21", "11:00,12:00,13:00,14:00,15:00")]
    // Sunrise 03:47, sunset 20:25, so 05:47 to 19:55.
    [InlineData("2026-06-21", "06:00,07:00,08:00,09:00,10:00,11:00,12:00,13:00,14:00,15:00,16:00,17:00,18:00,19:00")]
    public void Gb7rdg_ActiveSlots_AreTheDaylightOnes(string day, string expected)
    {
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(expected.Split(','), Hhmm(Gb7rdg.ActiveSlotsOn(date)));
    }

    [Fact]
    public void Gb7rdg_TheSlotsHeardBy_Nobody_AreDark_AndTheOnesThatDecoded_AreNot()
    {
        // 2026-10-05 at GB7RDG: 07:12 and 18:00 UTC were heard by nobody, 09:12 to 16:00 decoded well.
        Assert.False(DaylightRule.Gb7rdg.Allows(T("2026-10-05T07:12:00Z")));
        Assert.False(DaylightRule.Gb7rdg.Allows(T("2026-10-05T18:00:00Z")));
        Assert.True(DaylightRule.Gb7rdg.Allows(T("2026-10-05T09:12:00Z")));
        Assert.True(DaylightRule.Gb7rdg.Allows(T("2026-10-05T16:00:00Z")));
    }

    [Fact]
    public void Window_IncludesBothEdges_AndNothingOutside()
    {
        var window = DaylightRule.Gb7rdg.WindowOn(new DateOnly(2026, 10, 5));
        var opens = window.Opens!.Value;
        var closes = window.Closes!.Value;
        Assert.Equal(window.Sun.Sunrise!.Value.AddMinutes(120), opens);
        Assert.Equal(window.Sun.Sunset!.Value.AddMinutes(-30), closes);
        Assert.True(DaylightRule.Gb7rdg.Allows(opens));
        Assert.True(DaylightRule.Gb7rdg.Allows(closes));
        Assert.False(DaylightRule.Gb7rdg.Allows(opens.AddTicks(-1)));
        Assert.False(DaylightRule.Gb7rdg.Allows(closes.AddTicks(1)));

        // Offsets that open the window just before 09:00 run it; a minute more and the first is 10:00.
        int after = (int)Math.Floor((T("2026-10-05T09:00:00Z") - window.Sun.Sunrise.Value).TotalMinutes);
        var rule = new DaylightRule("IO91lk", after, 30);
        var timetable = new SlotTimetable(TimeOnly.MinValue, 60, rule);
        Assert.Equal("09:00", Hhmm(timetable.ActiveSlotsOn(new DateOnly(2026, 10, 5)))[0]);
        var later = new SlotTimetable(TimeOnly.MinValue, 60, new DaylightRule("IO91lk", after + 1, 30));
        Assert.Equal("10:00", Hhmm(later.ActiveSlotsOn(new DateOnly(2026, 10, 5)))[0]);
    }

    [Fact]
    public void SummerTime_PlaysNoPart_EverythingIsUtc()
    {
        // British Summer Time starts on 29 March 2026 and ends on 25 October. Sunrise in UTC moves
        // by about two minutes a day across both, never by an hour, and so do the slots.
        foreach (var change in new[] { new DateOnly(2026, 3, 29), new DateOnly(2026, 10, 25) })
        {
            for (int d = -2; d < 2; d++)
            {
                var a = DaylightRule.Gb7rdg.WindowOn(change.AddDays(d)).Sun.Sunrise!.Value;
                var b = DaylightRule.Gb7rdg.WindowOn(change.AddDays(d + 1)).Sun.Sunrise!.Value;
                Assert.InRange(Math.Abs((b - a - TimeSpan.FromDays(1)).TotalMinutes), 0, 3);
            }
            var before = Gb7rdg.ActiveSlotsOn(change.AddDays(-1));
            var after = Gb7rdg.ActiveSlotsOn(change.AddDays(1));
            Assert.InRange(after.Count - before.Count, -1, 1);
        }

        // The same instant written with a UTC+1 offset is the same slot, in or out.
        foreach (var slot in Gb7rdg.SlotsOn(new DateOnly(2026, 10, 5)))
        {
            Assert.Equal(Gb7rdg.IsActive(slot), Gb7rdg.IsActive(slot.ToOffset(TimeSpan.FromHours(1))));
        }
        Assert.Equal(Gb7rdg.NextActiveAfter(T("2026-10-05T17:30:00Z")), Gb7rdg.NextActiveAfter(T("2026-10-05T18:30:00+01:00")));
    }

    [Fact]
    public void PolarDay_RunsEverySlot_AndPolarNight_None_UntilTheSunComesBack()
    {
        // JP99 is near Tromso, 69.5 N.
        var arctic = new SlotTimetable(TimeOnly.MinValue, 60, new DaylightRule("JP99", 120, 30));
        Assert.Equal(24, arctic.ActiveSlotsOn(new DateOnly(2026, 6, 21)).Count);
        Assert.True(arctic.Daylight!.WindowOn(new DateOnly(2026, 6, 21)).AllDay);
        Assert.Empty(arctic.ActiveSlotsOn(new DateOnly(2026, 12, 21)));
        Assert.True(arctic.Daylight.WindowOn(new DateOnly(2026, 12, 21)).None);

        // The next slot after midwinter is when the days have grown past the offsets, in late January.
        var next = arctic.NextActiveAfter(T("2026-12-21T12:00:00Z"));
        Assert.NotNull(next);
        Assert.InRange(next.Value, T("2027-01-15T00:00:00Z"), T("2027-02-15T00:00:00Z"));
        Assert.True(arctic.ActiveAtOrBefore(T("2026-12-21T12:00:00Z")) < T("2026-12-01T00:00:00Z"));
        Assert.True(arctic.HasActiveSlotInAYear());
    }

    [Fact]
    public void OffsetsNoDayIsLongEnoughFor_AreRefused()
    {
        var settings = new DaylightSettings { Locator = "IO91lk", AfterSunriseMinutes = 600, BeforeSunsetMinutes = 600 };
        Assert.Contains("no slot in a whole year", settings.Problem(60, TimeOnly.MinValue), StringComparison.Ordinal);
        Assert.Null(new DaylightSettings().Problem(60, TimeOnly.MinValue));
        Assert.Contains("locator", new DaylightSettings { Locator = "XX99" }.Problem(60, TimeOnly.MinValue), StringComparison.Ordinal);
        Assert.Contains("-240 to 720", new DaylightSettings { AfterSunriseMinutes = 721 }.Problem(60, TimeOnly.MinValue), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new DaylightRule("IO91lk", -241, 0));
    }

    [Fact]
    public void WithoutARule_EverySlotRuns()
    {
        var hourly = new SlotTimetable(TimeOnly.MinValue, 60);
        Assert.Equal(24, hourly.ActiveSlotsOn(new DateOnly(2026, 12, 21)).Count);
        Assert.Equal(T("2026-12-21T03:00:00Z"), hourly.NextActiveAfter(T("2026-12-21T02:00:00Z")));
        Assert.Equal(T("2026-12-21T02:00:00Z"), hourly.ActiveAtOrBefore(T("2026-12-21T02:59:59Z")));
        var daily = new SlotTimetable(new TimeOnly(12, 0), 1440);
        Assert.Equal(["12:00"], Hhmm(daily.ActiveSlotsOn(new DateOnly(2026, 12, 21))));
        var offset = new SlotTimetable(new TimeOnly(22, 30), 60);
        Assert.Equal("00:30", Hhmm(offset.SlotsOn(new DateOnly(2026, 12, 21)))[0]);
    }

    [Fact]
    public void NextActive_FromTheDark_IsTheMorningsFirst()
    {
        Assert.Equal(T("2026-10-06T09:00:00Z"), Gb7rdg.NextActiveAfter(T("2026-10-05T17:00:00Z")));
        Assert.Equal(T("2026-10-05T17:00:00Z"), Gb7rdg.ActiveAtOrBefore(T("2026-10-06T08:59:00Z")));
        Assert.Equal(T("2026-10-05T09:00:00Z"), Gb7rdg.NextActiveAtOrAfter(T("2026-10-05T09:00:00Z")));
    }

    [Fact]
    public void Fields_RoundTrip_AndOddOnesAreIgnored()
    {
        Assert.Equal(["slots=00:00/60", "daylight=IO91lk/120/30"], Gb7rdg.ToFields());
        Assert.Equal(Gb7rdg, SlotTimetable.FromFields(["type=1", "slots=00:00/60", "future=thing", "daylight=IO91lk/120/30"]));
        Assert.Equal(new SlotTimetable(new TimeOnly(12, 0), 1440), SlotTimetable.FromFields(["slots=12:00/1440"]));
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60, new DaylightRule("IO91", -30, 0)), SlotTimetable.FromFields(["slots=00:00/60", "daylight=io91/-30/0"]));
        Assert.Null(SlotTimetable.FromFields(["daylight=IO91lk/120/30"]));
        Assert.Null(SlotTimetable.FromFields(["slots=00:00/7"]));
        Assert.Null(SlotTimetable.FromFields(["slots=25:00/60"]));
        Assert.Null(SlotTimetable.FromFields(["slots=00:00/60", "daylight=XX00/120/30"]));
        Assert.Null(SlotTimetable.FromFields(["slots=00:00/60", "daylight=IO91lk/120"]));
        Assert.Null(SlotTimetable.FromFields([]));
    }

    [Fact]
    public void Describe_SaysTheRuleInWords()
    {
        Assert.Equal("from 120 minutes after sunrise to 30 minutes before sunset at IO91lk", DaylightRule.Gb7rdg.Describe());
        Assert.Equal("from 30 minutes before sunrise to sunset at IO91", new DaylightRule("io91", -30, 0).Describe());
        Assert.Equal("09:00, 10:00 and 11:00 UTC", SlotTimetable.Times([T("2026-10-05T09:00:00Z"), T("2026-10-05T10:00:00Z"), T("2026-10-05T11:00:00Z")]));
        Assert.Equal("none", SlotTimetable.Times([]));
    }
}
