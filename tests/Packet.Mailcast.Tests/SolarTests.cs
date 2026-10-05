using System.Globalization;

namespace Packet.Mailcast.Tests;

/// <summary>
/// The solar calculator against published times. The reference sunrises and sunsets are the US
/// Naval Observatory's (its "Sun and Moon data for one day" service, aa.usno.navy.mil, asked on
/// 2026-10-05 for each place and date below, in UTC, to the minute). The USNO rounds to the
/// minute, so a difference of up to 2 minutes is allowed.
/// </summary>
public class SolarTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    private static DateTimeOffset T(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void Near(DateTimeOffset expected, DateTimeOffset? actual)
    {
        Assert.NotNull(actual);
        Assert.True((actual.Value - expected).Duration() <= Tolerance, $"expected {expected:u}, got {actual.Value:u}");
    }

    [Theory]
    // Reading, 51.4543 N 0.9781 W: the equinoxes, the solstices, and the day of GB7RDG's first hourly slots.
    [InlineData(51.4543, -0.9781, "2026-03-20", "06:07", "18:17")]
    [InlineData(51.4543, -0.9781, "2026-06-21", "03:47", "20:25")]
    [InlineData(51.4543, -0.9781, "2026-09-23", "05:52", "18:00")]
    [InlineData(51.4543, -0.9781, "2026-12-21", "08:07", "15:57")]
    [InlineData(51.4543, -0.9781, "2026-10-05", "06:11", "17:33")]
    // IO91lk's centre, 51.4375 N 1.0417 W.
    [InlineData(51.4375, -1.0417, "2026-06-21", "03:47", "20:25")]
    [InlineData(51.4375, -1.0417, "2026-12-21", "08:07", "15:57")]
    [InlineData(51.4375, -1.0417, "2026-10-05", "06:11", "17:33")]
    // Lerwick, 60.155 N 1.145 W: as far north as the calculator is meant for.
    [InlineData(60.155, -1.145, "2026-06-21", "02:39", "21:34")]
    [InlineData(60.155, -1.145, "2026-12-21", "09:08", "14:57")]
    // Quito, on the equator, at the March equinox.
    [InlineData(-0.18, -78.47, "2026-03-20", "11:18", "23:25")]
    public void SunriseSunset_MatchesTheUsno(double lat, double lon, string day, string rise, string set)
    {
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sun = Solar.SunriseSunset(date, lat, lon);
        Assert.Equal(SunDay.RisesAndSets, sun.Kind);
        Near(T($"{day}T{rise}:00Z"), sun.Sunrise);
        Near(T($"{day}T{set}:00Z"), sun.Sunset);
    }

    [Fact]
    public void SunriseSunset_FarEastOfGreenwich_RisesOnTheUtcDayBefore()
    {
        // Sydney, 33.87 S 151.21 E, 21 June: the USNO gives sunset 06:54 UTC that day, and the
        // sunrise of the same local day at 21:00 UTC on the 20th.
        var sun = Solar.SunriseSunset(new DateOnly(2026, 6, 21), -33.87, 151.21);
        Near(T("2026-06-20T21:00:00Z"), sun.Sunrise);
        Near(T("2026-06-21T06:54:00Z"), sun.Sunset);
    }

    [Fact]
    public void SunriseSunset_InsideTheArcticCircle_IsPolarDayInJuneAndPolarNightInDecember()
    {
        // Tromso, 69.65 N 18.96 E: the USNO lists no sunrise or sunset on either day.
        Assert.Equal(SunDay.AlwaysUp, Solar.SunriseSunset(new DateOnly(2026, 6, 21), 69.65, 18.96).Kind);
        Assert.Equal(SunDay.AlwaysDown, Solar.SunriseSunset(new DateOnly(2026, 12, 21), 69.65, 18.96).Kind);
        Assert.Equal(SunDay.AlwaysUp, Solar.SunriseSunset(new DateOnly(2026, 12, 21), -89.9, 0).Kind);
        Assert.Equal(SunDay.AlwaysDown, Solar.SunriseSunset(new DateOnly(2026, 6, 21), -89.9, 0).Kind);
    }

    [Fact]
    public void Elevation_IsTheStandardAltitudeAtSunriseAndSunset_AndHighestAtNoon()
    {
        var sun = Solar.SunriseSunset(new DateOnly(2026, 10, 5), 51.4375, -1.0417);
        Assert.InRange(Solar.ElevationDegrees(sun.Sunrise!.Value, 51.4375, -1.0417), Solar.StandardAltitude - 0.05, Solar.StandardAltitude + 0.05);
        Assert.InRange(Solar.ElevationDegrees(sun.Sunset!.Value, 51.4375, -1.0417), Solar.StandardAltitude - 0.05, Solar.StandardAltitude + 0.05);
        // At noon on 5 October the sun is about 90 - 51.44 - 4.8 (its declination south) = 33.8 degrees up.
        double noon = Solar.ElevationDegrees(sun.SolarNoon, 51.4375, -1.0417);
        Assert.InRange(noon, 33.3, 34.3);
        Assert.True(noon > Solar.ElevationDegrees(sun.SolarNoon.AddMinutes(-30), 51.4375, -1.0417));
        Assert.True(noon > Solar.ElevationDegrees(sun.SolarNoon.AddMinutes(30), 51.4375, -1.0417));
        Assert.True(Solar.ElevationDegrees(T("2026-10-05T00:00:00Z"), 51.4375, -1.0417) < -30);
    }

    [Fact]
    public void Elevation_AtTheSummerSolsticeNoonOnTheTropic_IsOverhead()
    {
        var sun = Solar.SunriseSunset(new DateOnly(2026, 6, 21), 23.44, 0);
        Assert.InRange(Solar.ElevationDegrees(sun.SolarNoon, 23.44, 0), 89.5, 90);
    }

    [Theory]
    [InlineData("IO91lk", 51.4375, -1.0417)]
    [InlineData("io91LK", 51.4375, -1.0417)]
    [InlineData("IO91", 51.5, -1.0)]
    [InlineData("JJ00aa", 0.0208, 0.0417)]
    [InlineData("AA00aa", -89.9792, -179.9583)]
    [InlineData("RR99xx", 89.9792, 179.9583)]
    [InlineData("FN31pr", 41.7292, -72.7083)]
    public void Maidenhead_GivesTheCentreOfTheSquare(string locator, double lat, double lon)
    {
        Assert.True(Maidenhead.TryParse(locator, out double gotLat, out double gotLon));
        Assert.Equal(lat, gotLat, 3);
        Assert.Equal(lon, gotLon, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("IO9")]
    [InlineData("IO91l")]
    [InlineData("IO91lkk")]
    [InlineData("SO91")]
    [InlineData("IZ91")]
    [InlineData("IOA1")]
    [InlineData("IO91yk")]
    [InlineData("IO91l1")]
    [InlineData(null)]
    public void Maidenhead_RefusesWhatIsNotALocator(string? locator)
    {
        Assert.False(Maidenhead.TryParse(locator, out _, out _));
        Assert.Throws<FormatException>(() => Maidenhead.Parse(locator!));
    }
}
