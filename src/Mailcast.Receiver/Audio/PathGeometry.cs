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
///
/// Labelling (packet-net/pdn-mailcast#60): a later mode is only called an F hop when its delay
/// falls where a real F layer, 200 to 450 km up, would put it for this ground distance. The
/// first F (or E) hop fit at all pins the height exactly (by bisection); every later mode is then
/// checked against that one height, not re-fit from scratch, so a processing sidelobe next to the
/// main path (close in delay, but not where a longer real hop would be) cannot masquerade as a
/// hop just because some other, lower height would have put a hop near it. A mode that fits
/// neither the broad 200 to 450 km band (before anything is pinned) nor the pinned height (once
/// something is) is left unlabelled: a sidelobe, not a path. The height is only reported from a
/// confirmed 1F and 2F pair, never invented from a mode that did not fit.
/// </remarks>
internal static class PathGeometry
{
    /// <summary>GB7RDG, near Reading.</summary>
    public static readonly GroundPlace Gb7rdg = GroundPlace.FromLocator("IO91lk")!;

    private const double EarthKm = 6371.0;

    private const double LightKmPerMs = 299.792458;

    /// <summary>The E layer's virtual height, km.</summary>
    public const double EHeightKm = 110;

    /// <summary>The F layer heights considered plausible, km (packet-net/pdn-mailcast#60).</summary>
    public const double LowestF = 200, HighestF = 450;

    /// <summary>The shortest ground distance at which 7 MHz can come off the E layer, km.</summary>
    public const double ShortestEKm = 300;

    /// <summary>How far a mode may be from the delay its pinned height predicts and still be that hop, ms.</summary>
    private const double PinnedTolerance = 0.3;

    /// <summary>How far outside the 200 to 450 km band's edge a mode may still fall and be considered for that hop, ms.</summary>
    private const double BandSlop = 0.05;

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
    /// The delay of <paramref name="hops"/> hops off an F layer at <paramref name="heightKm"/>,
    /// after the reference hop (one hop off the E layer if <paramref name="afterE"/>, otherwise
    /// one hop off the same F layer), over <paramref name="groundKm"/>, in ms.
    /// </summary>
    private static double GapMs(double groundKm, int hops, double heightKm, bool afterE)
    {
        double reference = afterE ? DelayMs(groundKm, 1, EHeightKm) : DelayMs(groundKm, 1, heightKm);
        return DelayMs(groundKm, hops, heightKm) - reference;
    }

    /// <summary>
    /// The height from 60 to 450 km at which <see cref="GapMs"/> (for <paramref name="hops"/>
    /// hops, not after the E layer) equals <paramref name="target"/> ms; null if no height from
    /// <see cref="LowestF"/> to <see cref="HighestF"/> does.
    /// </summary>
    private static double? SolveHeight(double groundKm, int hops, bool afterE, double target)
    {
        double lo = LowestF, hi = HighestF;
        double glo = GapMs(groundKm, hops, lo, afterE), ghi = GapMs(groundKm, hops, hi, afterE);
        if (!(target >= Math.Min(glo, ghi) && target <= Math.Max(glo, ghi)))
        {
            return null;
        }
        bool increasing = ghi >= glo;
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            bool below = GapMs(groundKm, hops, mid, afterE) < target;
            if (below == increasing)
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

    /// <summary>
    /// The height at which two hops arrive <paramref name="gapMs"/> after one, over
    /// <paramref name="groundKm"/>; null if no height from <see cref="LowestF"/> to
    /// <see cref="HighestF"/> does (packet-net/pdn-mailcast#60: a height outside the plausible F
    /// range is never reported).
    /// </summary>
    public static double? HeightFromGap(double groundKm, double gapMs) => SolveHeight(groundKm, 2, afterE: false, gapMs);

    /// <summary>
    /// Names the modes (delays after the strongest, in ms, earliest first) as hops: 1F, 2F, 3F
    /// and onward, with 1E first where the path is long enough for it. Tried with the first mode
    /// as 1F and, where the ground distance allows it, as 1E; the one that explains more of the
    /// later modes wins, a tie going to no E. Within each try, the first later mode that fits the
    /// plausible F band (200 to 450 km) pins the height exactly; every later mode is then checked
    /// against that one pinned height, not matched afresh, so it cannot be explained away at some
    /// other, implausible height. A mode that fits neither pins nor matches what is already
    /// pinned is left unlabelled: a sidelobe. Returns a label per mode (null for one that fits no
    /// hop) and the F height, which is only given when a 1F and 2F pair is confirmed.
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
        string?[] bestLabels = [];
        int bestExplained = -1;
        double? bestHeight = null;
        bool[] tries = eOk ? [false, true] : [false];
        foreach (bool afterE in tries)
        {
            var labels = new string?[n];
            labels[0] = afterE ? "1E" : "1F";
            double? pinned = null;
            int nextHops = afterE ? 1 : 2;
            int explained = 0;
            for (int k = 1; k < n; k++)
            {
                double d = delaysMs[k];
                if (pinned is { } h)
                {
                    if (Math.Abs(d - GapMs(groundKm, nextHops, h, afterE)) <= PinnedTolerance)
                    {
                        labels[k] = $"{nextHops}F";
                        nextHops++;
                        explained++;
                    }
                    continue;
                }
                double lo = GapMs(groundKm, nextHops, LowestF, afterE), hi = GapMs(groundKm, nextHops, HighestF, afterE);
                if (d < Math.Min(lo, hi) - BandSlop || d > Math.Max(lo, hi) + BandSlop)
                {
                    continue;
                }
                double? fit = SolveHeight(groundKm, nextHops, afterE, d);
                if (fit is not { } height)
                {
                    continue;
                }
                pinned = height;
                labels[k] = $"{nextHops}F";
                nextHops++;
                explained++;
            }
            if (explained > bestExplained)
            {
                bestExplained = explained;
                bestLabels = labels;
                bestHeight = Array.IndexOf(labels, "1F") >= 0 && Array.IndexOf(labels, "2F") >= 0 ? pinned : null;
            }
        }
        double? heightKm = bestHeight is { } km && double.IsFinite(km) ? Math.Round(km) : null;
        return (bestLabels, heightKm);
    }
}
