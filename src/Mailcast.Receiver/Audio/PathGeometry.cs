using Packet.Mailcast;

namespace Mailcast.Receiver;

/// <summary>A place on the ground, in degrees north and east.</summary>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
/// <param name="Locator">Its Maidenhead locator, as given.</param>
public sealed record GroundPlace(double Latitude, double Longitude, string Locator)
{
    /// <summary>A 4 or 6 character locator's centre; null for anything that is not one.</summary>
    public static GroundPlace? FromLocator(string? locator) =>
        Maidenhead.TryParse(locator?.Trim(), out double lat, out double lon) ? new GroundPlace(lat, lon, locator!.Trim()) : null;
}

/// <summary>
/// The geometry of the path from GB7RDG: which hops the measured modes are, and how high the
/// reflection is. Mirror-model hops over a round Earth: each hop goes up to a virtual height and
/// back down, so a mode's delay is its group path over the speed of light.
/// </summary>
/// <remarks>
/// Only delays between modes are measured (no absolute timing), so the height comes from a pair:
/// one hop and two hops off the same F layer arrive a time apart that grows with the height
/// (probe-plan-2026-10-06). The E layer is only considered for paths long enough for 40 m to be
/// reflected by it at all: foE is at most about 4 MHz, so 7 MHz needs a slant of more than
/// about 55 degrees at 110 km, more than about 300 km of ground.
/// </remarks>
internal static class PathGeometry
{
    /// <summary>GB7RDG, near Reading.</summary>
    public static readonly GroundPlace Gb7rdg = GroundPlace.FromLocator("IO91lk")!;

    private const double EarthKm = 6371.0;

    private const double LightKmPerMs = 299.792458;

    /// <summary>The E layer's virtual height, km.</summary>
    public const double EHeightKm = 110;

    /// <summary>The F layer heights considered, km.</summary>
    public const double LowestF = 150, HighestF = 500;

    /// <summary>The shortest ground distance at which 7 MHz can come off the E layer, km.</summary>
    public const double ShortestEKm = 300;

    /// <summary>How far a mode may be from where a hop would put it and still be that hop, ms.</summary>
    private const double Tolerance = 0.3;

    /// <summary>Great-circle distance in km.</summary>
    public static double DistanceKm(GroundPlace a, GroundPlace b)
    {
        double p1 = a.Latitude * Math.PI / 180, p2 = b.Latitude * Math.PI / 180;
        double dp = p2 - p1, dl = (b.Longitude - a.Longitude) * Math.PI / 180;
        double h = (Math.Sin(dp / 2) * Math.Sin(dp / 2)) + (Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2));
        return 2 * EarthKm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>The delay of <paramref name="hops"/> hops off a layer at <paramref name="heightKm"/>, over <paramref name="groundKm"/>, in ms.</summary>
    public static double DelayMs(double groundKm, int hops, double heightKm)
    {
        double half = groundKm / EarthKm / (2 * hops);
        double r = EarthKm, rh = EarthKm + heightKm;
        double leg = Math.Sqrt((r * r) + (rh * rh) - (2 * r * rh * Math.Cos(half)));
        return 2 * hops * leg / LightKmPerMs;
    }

    /// <summary>
    /// The height at which two hops arrive <paramref name="gapMs"/> after one, over
    /// <paramref name="groundKm"/>; null if no height from 60 to 1000 km does.
    /// </summary>
    public static double? HeightFromGap(double groundKm, double gapMs)
    {
        double Gap(double h) => DelayMs(groundKm, 2, h) - DelayMs(groundKm, 1, h);
        double lo = 60, hi = 1000;
        if (!(gapMs >= Gap(lo) && gapMs <= Gap(hi)))
        {
            return null;
        }
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            if (Gap(mid) < gapMs)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return (lo + hi) / 2;
    }

    /// <summary>A hop: the layer and how many times off it.</summary>
    private sealed record Hop(char Layer, int Count)
    {
        public string Label => $"{Count}{Layer}";
    }

    private static readonly Hop[] Hops = [new('E', 1), new('F', 1), new('F', 2), new('F', 3), new('F', 4)];

    /// <summary>
    /// Names the modes (delays after the first, in ms, earliest first) as hops: 1F, 2F, 3F, and
    /// 1E where the path is long enough. The F height is scanned and each way of naming them
    /// scored on how near each mode falls to its hop. Hops come in order (no 3F without a 2F),
    /// and of two that fit as well, the one with no E and the lower height wins. Returns a label
    /// per mode (null for one that fits no hop) and the F height, which is only given when a 1F
    /// and 2F pair fixes it.
    /// </summary>
    public static (string?[] Labels, double? HeightKm) Label(double groundKm, IReadOnlyList<double> delaysMs)
    {
        int n = delaysMs.Count;
        if (n == 0)
        {
            return ([], null);
        }
        if (n == 1)
        {
            return (["1F"], null);
        }
        bool eOk = groundKm >= ShortestEKm;
        double bestCost = double.PositiveInfinity;
        var best = new string?[n];
        foreach (var firstHop in Hops.Where(h => h.Count == 1 && (h.Layer == 'F' || eOk)))
        {
            for (double hf = LowestF; hf <= HighestF; hf += 1)
            {
                double Delay(Hop h) => DelayMs(groundKm, h.Count, h.Layer == 'E' ? EHeightKm : hf);
                double t0 = Delay(firstHop);
                var later = Hops.Where(h => (h.Layer == 'F' || eOk) && Delay(h) - t0 > 0.15).ToArray();
                var labels = new string?[n];
                labels[0] = firstHop.Label;
                // A tie goes to no E, then to the lower height.
                double cost = (firstHop.Layer == 'E' ? 0.01 : 0) + (1e-6 * hf);
                for (int k = 1; k < n; k++)
                {
                    Hop? near = null;
                    double miss = double.PositiveInfinity;
                    foreach (var h in later)
                    {
                        double m = Math.Abs(delaysMs[k] - (Delay(h) - t0));
                        if (m < miss)
                        {
                            miss = m;
                            near = h;
                        }
                    }
                    if (near is null || miss > Tolerance || labels.Contains(near.Label))
                    {
                        cost += Tolerance * Tolerance;
                        continue;
                    }
                    labels[k] = near.Label;
                    cost += miss * miss;
                }
                // F hops in order: an nF needs the (n-1)F, and a 1F needs 1E or 1F first.
                bool inOrder = labels.All(l => l is null || l[1] != 'F' || l[0] == '1' || labels.Contains($"{l[0] - '1'}F"));
                if (inOrder && labels.Skip(1).Any(l => l is not null) && cost < bestCost)
                {
                    bestCost = cost;
                    best = labels;
                }
            }
        }
        if (best[0] is null)
        {
            best[0] = "1F";
        }
        // The height only from a 1F and 2F pair, solved exactly rather than to the scan's 1 km.
        int one = Array.IndexOf(best, "1F"), two = Array.IndexOf(best, "2F");
        double? height = one >= 0 && two >= 0 ? HeightFromGap(groundKm, delaysMs[two] - delaysMs[one]) : null;
        return (best, height);
    }
}
