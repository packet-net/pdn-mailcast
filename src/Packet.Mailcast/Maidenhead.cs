namespace Packet.Mailcast;

/// <summary>Maidenhead locators (IO91, IO91lk) as latitude and longitude.</summary>
public static class Maidenhead
{
    /// <summary>
    /// The centre of a 4 or 6 character locator, in degrees (north and east positive). Letters may
    /// be either case. Returns false for anything that is not a locator.
    /// </summary>
    public static bool TryParse(string? locator, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (locator is null || (locator.Length != 4 && locator.Length != 6))
        {
            return false;
        }
        string l = locator.ToUpperInvariant();
        if (l[0] is < 'A' or > 'R' || l[1] is < 'A' or > 'R' || l[2] is < '0' or > '9' || l[3] is < '0' or > '9')
        {
            return false;
        }
        double lon = -180 + ((l[0] - 'A') * 20) + ((l[2] - '0') * 2);
        double lat = -90 + ((l[1] - 'A') * 10) + (l[3] - '0');
        if (l.Length == 6)
        {
            if (l[4] is < 'A' or > 'X' || l[5] is < 'A' or > 'X')
            {
                return false;
            }
            // Subsquares are 5 minutes of longitude by 2.5 of latitude; the centre is half of each in.
            lon += ((l[4] - 'A') + 0.5) * 5 / 60.0;
            lat += ((l[5] - 'A') + 0.5) * 2.5 / 60.0;
        }
        else
        {
            lon += 1;
            lat += 0.5;
        }
        latitude = lat;
        longitude = lon;
        return true;
    }

    /// <summary>The centre of a locator. Throws <see cref="FormatException"/> for one that is not.</summary>
    public static (double Latitude, double Longitude) Parse(string locator) =>
        TryParse(locator, out double lat, out double lon)
            ? (lat, lon)
            : throw new FormatException($"'{locator}' is not a 4 or 6 character Maidenhead locator such as IO91 or IO91lk.");
}
