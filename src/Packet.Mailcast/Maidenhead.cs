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
    /// own constructor throws <see cref="ArgumentNullException"/> for null rather than a sensible
    /// <see cref="FormatException"/>; null is refused here before it ever reaches that constructor.)
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
        return MaidenheadLocator.LatLngToLocator(latitude, longitude, 0);
    }
}
