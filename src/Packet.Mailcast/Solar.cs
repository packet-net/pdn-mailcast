namespace Packet.Mailcast;

/// <summary>Whether the sun rises and sets on a day, or stays up or down all day.</summary>
public enum SunDay
{
    /// <summary>The sun rises and sets.</summary>
    RisesAndSets,

    /// <summary>The sun stays above the horizon all day (polar day).</summary>
    AlwaysUp,

    /// <summary>The sun stays below the horizon all day (polar night).</summary>
    AlwaysDown,
}

/// <summary>Sunrise and sunset for one day at one place.</summary>
/// <param name="Day">The UTC day whose solar noon these are around.</param>
/// <param name="Kind">Whether the sun rises and sets.</param>
/// <param name="Sunrise">Sunrise, UTC, when <paramref name="Kind"/> is <see cref="SunDay.RisesAndSets"/>. Far east of Greenwich it can fall on the UTC day before.</param>
/// <param name="Sunset">Sunset, UTC, likewise. Far west of Greenwich it can fall on the UTC day after.</param>
/// <param name="SolarNoon">When the sun is highest, UTC.</param>
public sealed record SunTimes(DateOnly Day, SunDay Kind, DateTimeOffset? Sunrise, DateTimeOffset? Sunset, DateTimeOffset SolarNoon);

/// <summary>
/// Where the sun is, worked out from the date alone with NOAA's solar calculator equations (after
/// Meeus, <i>Astronomical Algorithms</i>): the sun's declination, the equation of time and the
/// hour angle. Good to about a minute for sunrise and sunset up to about 60 degrees of latitude,
/// which is all the head end and the receiver need; no network, no tables.
/// </summary>
/// <remarks>
/// Sunrise and sunset are when the sun's centre is <see cref="StandardAltitude"/> degrees from
/// the horizon, which allows for refraction and the sun's radius, as almanacs do. Everything is
/// UTC; local clocks and summer time play no part.
/// </remarks>
public static class Solar
{
    /// <summary>The sun's altitude at sunrise and sunset, in degrees: refraction and the sun's radius.</summary>
    public const double StandardAltitude = -0.833;

    private const double Rad = Math.PI / 180;

    /// <summary>
    /// Sunrise and sunset on <paramref name="day"/> (UTC) at a place. Each is found from the sun's
    /// position at that moment, refined a few times, rather than at noon, which keeps them within a
    /// minute of almanac times at high latitudes too.
    /// </summary>
    /// <param name="day">The day.</param>
    /// <param name="latitude">Degrees north, negative south.</param>
    /// <param name="longitude">Degrees east, negative west.</param>
    public static SunTimes SunriseSunset(DateOnly day, double latitude, double longitude)
    {
        CheckPlace(latitude, longitude);
        double midnight = JulianDay(day);
        // Solar noon in minutes from midnight UTC, refined once with the equation of time at noon.
        double noon = 720 - (4 * longitude);
        for (int i = 0; i < 2; i++)
        {
            noon = 720 - (4 * longitude) - Position(midnight + (noon / 1440)).EquationOfTimeMinutes;
        }
        double? rise = Event(midnight, noon, latitude, longitude, -1, out SunDay kind);
        if (kind != SunDay.RisesAndSets)
        {
            return new SunTimes(day, kind, null, null, At(day, noon));
        }
        double? set = Event(midnight, noon, latitude, longitude, +1, out SunDay setKind);
        if (setKind != SunDay.RisesAndSets || rise is null || set is null)
        {
            // At the edge of polar day or night one of the two can just fail to exist. The
            // sun's height at noon says which side of the edge the day is on.
            return new SunTimes(day, ElevationDegrees(At(day, noon), latitude, longitude) > StandardAltitude ? SunDay.AlwaysUp : SunDay.AlwaysDown, null, null, At(day, noon));
        }
        return new SunTimes(day, SunDay.RisesAndSets, At(day, rise.Value), At(day, set.Value), At(day, noon));
    }

    /// <summary>
    /// The sun's elevation above the horizon at <paramref name="time"/>, in degrees: geometric,
    /// without refraction, so at sunrise and sunset it reads <see cref="StandardAltitude"/>.
    /// </summary>
    public static double ElevationDegrees(DateTimeOffset time, double latitude, double longitude)
    {
        CheckPlace(latitude, longitude);
        double jd = JulianDay(time);
        var sun = Position(jd);
        double minutes = time.UtcDateTime.TimeOfDay.TotalMinutes;
        double trueSolarTime = minutes + sun.EquationOfTimeMinutes + (4 * longitude);
        double hourAngle = (trueSolarTime / 4) - 180;
        double cosZenith = (Math.Sin(latitude * Rad) * Math.Sin(sun.DeclinationRadians))
            + (Math.Cos(latitude * Rad) * Math.Cos(sun.DeclinationRadians) * Math.Cos(hourAngle * Rad));
        return 90 - (Math.Acos(Math.Clamp(cosZenith, -1, 1)) / Rad);
    }

    /// <summary>
    /// Sunrise (<paramref name="sign"/> -1) or sunset (+1) in minutes from midnight UTC, found by
    /// working out the hour angle from the sun's position at the last estimate, three times over.
    /// </summary>
    private static double? Event(double midnight, double noon, double latitude, double longitude, int sign, out SunDay kind)
    {
        double estimate = noon + (sign * 360);
        kind = SunDay.RisesAndSets;
        for (int i = 0; i < 4; i++)
        {
            var sun = Position(midnight + (estimate / 1440));
            double cosH = (Math.Sin(StandardAltitude * Rad) - (Math.Sin(latitude * Rad) * Math.Sin(sun.DeclinationRadians)))
                / (Math.Cos(latitude * Rad) * Math.Cos(sun.DeclinationRadians));
            if (cosH > 1)
            {
                kind = SunDay.AlwaysDown;
                return null;
            }
            if (cosH < -1)
            {
                kind = SunDay.AlwaysUp;
                return null;
            }
            double hourAngle = Math.Acos(cosH) / Rad;
            double eventNoon = 720 - (4 * longitude) - sun.EquationOfTimeMinutes;
            estimate = eventNoon + (sign * 4 * hourAngle);
        }
        return estimate;
    }

    /// <summary>The sun's declination and the equation of time at Julian day <paramref name="jd"/>.</summary>
    private static (double DeclinationRadians, double EquationOfTimeMinutes) Position(double jd)
    {
        double t = (jd - 2451545.0) / 36525.0;
        double l0 = Normalise(280.46646 + (t * (36000.76983 + (t * 0.0003032))));
        double m = 357.52911 + (t * (35999.05029 - (0.0001537 * t)));
        double e = 0.016708634 - (t * (0.000042037 + (0.0000001267 * t)));
        double c = (Math.Sin(m * Rad) * (1.914602 - (t * (0.004817 + (0.000014 * t)))))
            + (Math.Sin(2 * m * Rad) * (0.019993 - (0.000101 * t)))
            + (Math.Sin(3 * m * Rad) * 0.000289);
        double trueLongitude = l0 + c;
        double omega = 125.04 - (1934.136 * t);
        double apparentLongitude = trueLongitude - 0.00569 - (0.00478 * Math.Sin(omega * Rad));
        double meanObliquity = 23 + ((26 + ((21.448 - (t * (46.815 + (t * (0.00059 - (t * 0.001813)))))) / 60)) / 60);
        double obliquity = meanObliquity + (0.00256 * Math.Cos(omega * Rad));
        double declination = Math.Asin(Math.Sin(obliquity * Rad) * Math.Sin(apparentLongitude * Rad));
        double y = Math.Tan(obliquity * Rad / 2);
        y *= y;
        double eot = (y * Math.Sin(2 * l0 * Rad))
            - (2 * e * Math.Sin(m * Rad))
            + (4 * e * y * Math.Sin(m * Rad) * Math.Cos(2 * l0 * Rad))
            - (0.5 * y * y * Math.Sin(4 * l0 * Rad))
            - (1.25 * e * e * Math.Sin(2 * m * Rad));
        return (declination, 4 * eot / Rad);
    }

    private static double Normalise(double degrees)
    {
        double d = degrees % 360;
        return d < 0 ? d + 360 : d;
    }

    private static double JulianDay(DateOnly day) => 2440587.5 + (day.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber);

    private static double JulianDay(DateTimeOffset time) => 2440587.5 + ((time.UtcTicks - DateTime.UnixEpoch.Ticks) / (double)TimeSpan.TicksPerDay);

    private static DateTimeOffset At(DateOnly day, double minutes) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddTicks((long)Math.Round(minutes * TimeSpan.TicksPerMinute));

    private static void CheckPlace(double latitude, double longitude)
    {
        if (!(latitude >= -90 && latitude <= 90) || !(longitude >= -180 && longitude <= 180))
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "A place is a latitude of -90 to 90 and a longitude of -180 to 180 degrees.");
        }
    }
}
