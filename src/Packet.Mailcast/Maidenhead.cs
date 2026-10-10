using MaidenheadLib;

namespace Packet.Mailcast;

/// <summary>
/// Maidenhead locators (IO91, IO91lk) as latitude and longitude, over MaidenheadLib (issue #88;
/// this package no longer carries its own copy of the conversion).
/// </summary>
public static class Maidenhead
{
    /// <summary>
    /// The centre of a 4 or 6 character locator, in degrees (north and east positive). Letters may
    /// be either case. Returns false for anything that is not a locator, including null. (MaidenheadLib's
    /// own constructor throws <see cref="NullReferenceException"/> rather than a sensible exception for
    /// null; null is refused here before it ever reaches that constructor.)
    /// </summary>
    public static bool TryParse(string? locator, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (locator is null)
        {
            return false;
        }
        try
        {
            (latitude, longitude) = new Locator(locator).Centre;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The centre of a locator. Throws <see cref="FormatException"/> for one that is not.</summary>
    public static (double Latitude, double Longitude) Parse(string locator) =>
        TryParse(locator, out double lat, out double lon)
            ? (lat, lon)
            : throw new FormatException($"'{locator}' is not a 4 or 6 character Maidenhead locator such as IO91 or IO91lk.");

    /// <summary>
    /// The 6 character locator (IO91lk) of a place in degrees, north and east positive; null for
    /// a latitude or longitude that is not finite or not on the Earth.
    /// </summary>
    public static string? Format(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return null;
        }
        // The north pole, and the antimeridian itself, belong to the last square, same as our own
        // formula used to give them: MaidenheadLib does not clamp either (any longitude at
        // latitude 90, not just the corner, and likewise any latitude at longitude 180), and
        // without this it would hand back a locator whose field letter is one past 'R', outside a
        // real locator's range.
        double lat = Math.Min(latitude, 89.999999);
        double lon = Math.Min(longitude, 179.999999);
        // Precision 0 normally gives exactly the 6 characters we want (field, square, subsquare),
        // but MaidenheadLib has a bug: when the subsquare comes out "mm" it returns only the first
        // 4 characters instead (decompiled, it special-cases that one string literally), silently
        // dropping the subsquare for about 1 in 600 points, e.g. (-85.46, -174.98) gives "AA24" at
        // precision 0 rather than "AA24mm". Precision 1 does not have that bug (it always gives 8:
        // field, square, subsquare, then an extra digit pair we do not want), so the 6 characters
        // we want are the first 6 of the 8 that precision 1 gives, every time.
        return MaidenheadLocator.LatLngToLocator(lat, lon, 1)[..6];
    }
}
