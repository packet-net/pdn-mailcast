using System.Globalization;
using System.IO.Compression;
using System.Reflection;

namespace Mailcast.Receiver;

/// <summary>
/// Names where a ground bounce falls (packet-net/pdn-mailcast#89): the nearest town on land, or
/// at sea the sea's name and, where it is inside one, the shipping forecast area too. Offline,
/// from three small embedded files built by tools/ground-bounce-data/build.py: towns (GeoNames,
/// CC BY 4.0), sea areas (Flanders Marine Institute's IHO Sea Areas, CC BY 4.0, clipped and
/// heavily simplified) and shipping forecast areas (Met Office Factsheet 8, Crown copyright,
/// Open Government Licence v3.0). Sources and licences are credited in full in docs/receiver.md.
/// </summary>
internal static class GroundBounceGazetteer
{
    /// <summary>
    /// How far a bounce point's nearest town may be and still be called "near" it, km. Falling
    /// outside every sea polygon is not the same as being on land: the simplified sea polygons
    /// have real gaps (a strait too narrow to survive simplification, a point just past the edge
    /// of the gazetteer's own region), and a point in one of those gaps is still more likely to
    /// be open water than the nearest named place on the coast. Review of issue #89 found this
    /// the hard way: a point a few km off the French Atlantic coast, outside every sea polygon at
    /// the time, was called "near Lesparre-Medoc" (a real town, about 25 km inland) rather than
    /// left unlabelled or correctly put in the Bay of Biscay. 30 km keeps "near" meaning a town
    /// the point is actually close to, in line with a bounce point itself only being good to a
    /// few tens of km; the gazetteer's sea coverage was also widened to close the gap this came
    /// from (tools/ground-bounce-data/build.py), but the cap stays regardless, since a gap is
    /// always possible wherever the simplification happens to land.
    /// </summary>
    private const double MaxTownKm = 30;

    private const double EarthKm = 6371.0;

    private readonly record struct Town(string Name, double Latitude, double Longitude);

    private readonly record struct Area(string Name, IReadOnlyList<IReadOnlyList<(double Lon, double Lat)>> Parts);

    private static readonly Lazy<IReadOnlyList<Town>> LazyTowns = new(LoadTowns);

    private static readonly Lazy<IReadOnlyList<Area>> LazySeas = new(() => LoadAreas("sea-areas.txt.gz"));

    private static readonly Lazy<IReadOnlyList<Area>> LazyForecastAreas = new(() => LoadAreas("shipping-forecast-areas.txt.gz"));

    /// <summary>
    /// Where a point falls: "near Aberystwyth" on land, or at sea the sea's name, with the
    /// shipping forecast area in brackets when the point is inside one and its name is not
    /// already effectively the sea's own (so "Irish Sea" is said once, not "Irish Sea (Irish
    /// Sea)"). Null when nothing in the gazetteer is close enough to say anything useful.
    /// </summary>
    public static string? Label(double latitude, double longitude)
    {
        string? sea = FindContaining(LazySeas.Value, longitude, latitude);
        if (sea is null)
        {
            return NearestTown(latitude, longitude);
        }
        string? area = FindContaining(LazyForecastAreas.Value, longitude, latitude);
        return area is null || NamesMatch(sea, area) ? sea : $"{sea} ({area})";
    }

    private static bool NamesMatch(string sea, string area) =>
        sea.Contains(area, StringComparison.OrdinalIgnoreCase) || area.Contains(sea, StringComparison.OrdinalIgnoreCase);

    private static string? FindContaining(IReadOnlyList<Area> areas, double lon, double lat)
    {
        foreach (var area in areas)
        {
            foreach (var part in area.Parts)
            {
                if (PointInRing(lon, lat, part))
                {
                    return area.Name;
                }
            }
        }
        return null;
    }

    /// <summary>Even-odd ray casting; the ring need not be explicitly closed (last point need not repeat the first).</summary>
    private static bool PointInRing(double x, double y, IReadOnlyList<(double Lon, double Lat)> ring)
    {
        bool inside = false;
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            (double xi, double yi) = ring[i];
            (double xj, double yj) = ring[j];
            if (((yi > y) != (yj > y)) && (x < (((xj - xi) * (y - yi) / (yj - yi)) + xi)))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static string? NearestTown(double latitude, double longitude)
    {
        string? best = null;
        double bestKm = double.MaxValue;
        foreach (var town in LazyTowns.Value)
        {
            double km = HaversineKm(latitude, longitude, town.Latitude, town.Longitude);
            if (km < bestKm)
            {
                bestKm = km;
                best = town.Name;
            }
        }
        return best is not null && bestKm <= MaxTownKm ? $"near {best}" : null;
    }

    /// <summary>Great-circle distance in km, the same formula and Earth radius as <see cref="PathGeometry.DistanceKm"/>.</summary>
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
        double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
        double h = (Math.Sin(dp / 2) * Math.Sin(dp / 2)) + (Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2));
        return 2 * EarthKm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static List<Town> LoadTowns()
    {
        var towns = new List<Town>();
        foreach (string line in ReadLines("towns.txt.gz"))
        {
            string[] f = line.Split('\t');
            towns.Add(new Town(f[0], double.Parse(f[1], CultureInfo.InvariantCulture), double.Parse(f[2], CultureInfo.InvariantCulture)));
        }
        return towns;
    }

    private static List<Area> LoadAreas(string resourceName)
    {
        var areas = new List<Area>();
        foreach (string line in ReadLines(resourceName))
        {
            int bar = line.IndexOf('|');
            string name = line[..bar];
            var parts = line[(bar + 1)..].Split('#', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => (IReadOnlyList<(double Lon, double Lat)>)[.. part.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ParsePoint)])
                .ToList();
            areas.Add(new Area(name, parts));
        }
        return areas;
    }

    private static (double Lon, double Lat) ParsePoint(string token)
    {
        int comma = token.IndexOf(',');
        return (double.Parse(token[..comma], CultureInfo.InvariantCulture), double.Parse(token[(comma + 1)..], CultureInfo.InvariantCulture));
    }

    private static IEnumerable<string> ReadLines(string resourceName)
    {
        string full = $"Mailcast.Receiver.Data.{resourceName}";
        using Stream? raw = Assembly.GetExecutingAssembly().GetManifestResourceStream(full);
        if (raw is null)
        {
            throw new InvalidOperationException($"embedded resource '{full}' is missing.");
        }
        using var gz = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, System.Text.Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length > 0 && line[0] != '#')
            {
                yield return line;
            }
        }
    }
}
