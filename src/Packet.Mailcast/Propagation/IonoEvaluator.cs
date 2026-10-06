namespace Packet.Mailcast.Propagation;

/// <summary>
/// Turns ionosonde soundings into an <see cref="IonoReading"/>: picks the sounding, works out the
/// MUF at 100, 500 and 1000 km, and judges 40 m at each.
/// </summary>
/// <remarks>
/// <para>The MUF at a distance is the ionosonde's own MUF(D) for it when the source gives one.
/// Otherwise it is estimated: foF2 itself at 100 km, where the path is nearly vertical, and beyond
/// that the flat-earth secant law, with the layer's height chosen so the same law gives the
/// measured MUF(3000) at 3000 km: MUF(d) = foF2 x sqrt(1 + (M(3000)F2^2 - 1) x (d / 3000)^2).
/// That ties the estimate to what the ionosonde saw at both ends; it is a rough guide in between,
/// and it ignores the earth's curvature and D-layer absorption.</para>
/// <para>A distance is open when its MUF is at least <see cref="IonoSettings.OpenMhz"/>, and
/// reliable when <see cref="IonoSettings.ReliableFactor"/> times it still is. GOOD is reliable at
/// 100 and 500 km; POOR is closed at all three distances; MARGINAL is anything open in between;
/// UNKNOWN is no data, or a sounding older than <see cref="IonoSettings.StaleAfter"/>.</para>
/// </remarks>
public static class IonoEvaluator
{
    /// <summary>A sounding further than this in the future is not believed.</summary>
    public static readonly TimeSpan MostAhead = TimeSpan.FromMinutes(10);

    /// <summary>The three distances judged, km.</summary>
    public static IReadOnlyList<int> DistancesKm { get; } = [100, 500, 1000];

    /// <summary>
    /// The reading for <paramref name="now"/> from whatever soundings are at hand: the newest of
    /// the first station in <see cref="IonoSettings.Stations"/> that has one no older than
    /// <see cref="IonoSettings.StaleAfter"/>; failing that, the newest of any listed station, as
    /// UNKNOWN with its values and age; failing that, <see cref="IonoReading.None"/>.
    /// </summary>
    public static IonoReading Evaluate(IEnumerable<IonoSounding> soundings, IonoSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(soundings);
        ArgumentNullException.ThrowIfNull(settings);
        var usable = soundings
            .Where(s => s.Usable && s.Time <= now + MostAhead)
            .ToList();
        IonoSounding? newestAny = null;
        foreach (string station in settings.Stations)
        {
            // The newest of this station's, GIRO first on a tie (it has a confidence score).
            var newest = usable
                .Where(s => string.Equals(s.Station, station, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Time)
                .ThenBy(s => s.Source)
                .FirstOrDefault();
            if (newest is null)
            {
                continue;
            }
            if (TimeSpan.FromMinutes(AgeMinutes(newest.Time, now)) <= settings.StaleAfter)
            {
                return Judge(newest, settings, now);
            }
            if (newestAny is null || newest.Time > newestAny.Time)
            {
                newestAny = newest;
            }
        }
        return newestAny is null ? IonoReading.None : Judge(newestAny, settings, now) with { State = IonoState.Unknown };
    }

    /// <summary>One sounding judged as of <paramref name="now"/>, stale or not.</summary>
    public static IonoReading Judge(IonoSounding sounding, IonoSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sounding);
        ArgumentNullException.ThrowIfNull(settings);
        var (m100, measured100) = MufAt(sounding, 100);
        var (m500, measured500) = MufAt(sounding, 500);
        var (m1000, measured1000) = MufAt(sounding, 1000);
        var v100 = Verdict(m100, settings);
        var v500 = Verdict(m500, settings);
        var v1000 = Verdict(m1000, settings);
        int age = AgeMinutes(sounding.Time, now);
        var found = new List<bool>(3);
        if (m100 is not null)
        {
            found.Add(measured100);
        }
        if (m500 is not null)
        {
            found.Add(measured500);
        }
        if (m1000 is not null)
        {
            found.Add(measured1000);
        }
        MufMethod method = found.Count == 0 ? MufMethod.None
            : found.All(m => m) ? MufMethod.Measured
            : found.All(m => !m) ? MufMethod.Estimated
            : MufMethod.Mixed;
        var state = TimeSpan.FromMinutes(age) > settings.StaleAfter ? IonoState.Unknown : State(v100, v500, v1000);
        return new IonoReading
        {
            State = state,
            FoF2 = sounding.FoF2 > 0 ? sounding.FoF2 : null,
            Mufd100 = m100,
            Mufd500 = m500,
            Mufd1000 = m1000,
            SkipZoneKm = SkipZone(m100, m500, m1000, settings.OpenMhz),
            Station = sounding.Station.ToUpperInvariant(),
            SoundingTimeUtc = sounding.Time.ToUniversalTime(),
            AgeMinutes = age,
            Source = sounding.Source,
            Method = method,
            At100 = v100,
            At500 = v500,
            At1000 = v1000,
        };
    }

    /// <summary>The whole verdict from the three distances' (see the remarks).</summary>
    public static IonoState State(PathVerdict at100, PathVerdict at500, PathVerdict at1000)
    {
        PathVerdict[] all = [at100, at500, at1000];
        if (at100 == PathVerdict.Reliable && at500 == PathVerdict.Reliable)
        {
            return IonoState.Good;
        }
        if (all.Any(v => v is PathVerdict.Open or PathVerdict.Reliable))
        {
            return IonoState.Marginal;
        }
        return all.All(v => v == PathVerdict.Closed) ? IonoState.Poor : IonoState.Unknown;
    }

    /// <summary>One distance's verdict from its MUF.</summary>
    public static PathVerdict Verdict(double? muf, IonoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return muf is not { } m ? PathVerdict.NoData
            : settings.ReliableFactor * m >= settings.OpenMhz ? PathVerdict.Reliable
            : m >= settings.OpenMhz ? PathVerdict.Open
            : PathVerdict.Closed;
    }

    /// <summary>
    /// The MUF at <paramref name="distanceKm"/> (100, 500 or 1000), and whether it was measured:
    /// the ionosonde's own MUF(D) if given, else <see cref="EstimateMuf"/>. Null when neither can be had.
    /// </summary>
    public static (double? Muf, bool Measured) MufAt(IonoSounding sounding, int distanceKm)
    {
        ArgumentNullException.ThrowIfNull(sounding);
        double? measured = distanceKm switch
        {
            100 => sounding.Mufd100,
            500 => sounding.Mufd500,
            1000 => sounding.Mufd1000,
            _ => null,
        };
        if (measured is > 0)
        {
            return (measured, true);
        }
        if (sounding.FoF2 is not (> 0 and var foF2))
        {
            return (null, false);
        }
        double? m3000 = sounding.M3000 is > 1 ? sounding.M3000 : sounding.Muf3000 / foF2;
        return (EstimateMuf(foF2, m3000, distanceKm), false);
    }

    /// <summary>
    /// The flat-earth secant estimate of the MUF at <paramref name="distanceKm"/>: foF2 at 100 km
    /// or less; beyond, foF2 x sqrt(1 + (M^2 - 1) x (d / 3000)^2) with M = M(3000)F2, which is
    /// the secant law for a mirror at the height that makes it give MUF(3000) at 3000 km. Null
    /// beyond 100 km without a usable M(3000)F2.
    /// </summary>
    public static double? EstimateMuf(double foF2, double? m3000, int distanceKm)
    {
        if (distanceKm <= 100)
        {
            return foF2;
        }
        if (m3000 is not (> 1 and var m))
        {
            return null;
        }
        double ratio = distanceKm / 3000.0;
        return foF2 * Math.Sqrt(1 + ((m * m) - 1) * ratio * ratio);
    }

    /// <summary>
    /// The skip zone's radius, km to the nearest 10: 0 when open at 100 km; otherwise the point
    /// where the MUF, taken as a straight line between 100, 500 and 1000 km, reaches
    /// <paramref name="openMhz"/>. Null when it does not within 1000 km, or a MUF it needs is missing.
    /// </summary>
    public static int? SkipZone(double? mufd100, double? mufd500, double? mufd1000, double openMhz)
    {
        if (mufd100 is not { } a)
        {
            return null;
        }
        if (a >= openMhz)
        {
            return 0;
        }
        (int Km, double? Muf)[] points = [(100, a), (500, mufd500), (1000, mufd1000)];
        for (int i = 1; i < points.Length; i++)
        {
            if (points[i].Muf is not { } here || points[i - 1].Muf is not { } before)
            {
                return null;
            }
            if (here >= openMhz)
            {
                double km = points[i - 1].Km + ((openMhz - before) / (here - before) * (points[i].Km - points[i - 1].Km));
                return (int)(Math.Round(km / 10, MidpointRounding.AwayFromZero) * 10);
            }
        }
        return null;
    }

    /// <summary>Whole minutes from <paramref name="at"/> to <paramref name="now"/>, never below 0.</summary>
    public static int AgeMinutes(DateTimeOffset at, DateTimeOffset now) =>
        (int)Math.Clamp(Math.Floor((now - at).TotalMinutes), 0, int.MaxValue);
}
