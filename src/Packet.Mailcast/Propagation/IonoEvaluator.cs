namespace Packet.Mailcast.Propagation;

/// <summary>
/// Turns ionosonde soundings into an <see cref="IonoReading"/>: picks the sounding, works out the
/// MUF at 100, 500 and 1000 km, and judges 40 m at each.
/// </summary>
/// <remarks>
/// <para>The MUF at a distance is the ionosonde's own MUF(D) for it when the source gives one.
/// Otherwise it is the F2 layer's basic MUF by ITU-R P.533 section 3.5.1.1 (the same as ITU-R
/// P.1240 section 3.1) from foF2 and M(3000)F2: see <see cref="EstimateMuf"/>. Without
/// M(3000)F2 only 100 km has a MUF, foF2 itself. D-layer absorption is not modelled.</para>
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
            FoF2 = sounding.FoF2,
            Mufd100 = m100,
            Mufd500 = m500,
            Mufd1000 = m1000,
            SkipZoneKm = method == MufMethod.Estimated
                ? SkipZoneAlong(km => MufAt(sounding, km).Muf, settings.OpenMhz)
                : SkipZone(m100, m500, m1000, settings.OpenMhz),
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
        if (IonoLimits.Frequency(measured) is { } m)
        {
            return (m, true);
        }
        if (sounding.FoF2 is not { } foF2)
        {
            return (null, false);
        }
        double? m3000 = sounding.M3000 ?? IonoLimits.M3000(sounding.Muf3000 / foF2);
        return (EstimateMuf(foF2, m3000, distanceKm), false);
    }

    /// <summary>
    /// The gyrofrequency at 300 km over southern England, MHz, for the basic MUF's x-wave term:
    /// about 43,000 nT there (49,000 nT at the ground, falling off as a dipole's) at 28 Hz a nT.
    /// ITU-R P.533 takes it from a field model at the path's midpoint.
    /// </summary>
    public const double GyroMhz = 1.2;

    /// <summary>
    /// The F2 layer's basic MUF for a ground distance up to dmax, by ITU-R P.533 equations 3 to 6
    /// (section 3.5.1.1; also ITU-R P.1240 section 3.1), as ITU-R Study Group 3's reference code
    /// (ITURHFProp, P533/MUFBasic.c) works it:
    /// <code>
    /// MUF(D) = (1 + (C(D) / C(3000)) x (B - 1)) x foF2 + (fH / 2) x (1 - D / dmax)
    /// B      = M - 0.124 + (M^2 - 4) x (0.0215 + 0.005 sin(7.854 / x - 1.9635))
    /// dmax   = 4780 + (12610 + 2140 / x^2 - 49720 / x^4 + 688900 / x^6) x (1 / B - 0.303), at most 4000 km
    /// C(D)   = 0.74 - 0.591 Z - 0.424 Z^2 - 0.090 Z^3 + 0.088 Z^4 + 0.181 Z^5 + 0.096 Z^6, Z = 1 - 2D / dmax
    /// </code>
    /// with M = M(3000)F2, fH = <see cref="GyroMhz"/> and x = foF2 / foE, taken as 2 (its floor,
    /// and the reference code's value without foE; from 2 to 6 it moves the MUF by about 1%).
    /// Null without a usable M(3000)F2, except at 100 km or less, where it is foF2 itself.
    /// </summary>
    public static double? EstimateMuf(double foF2, double? m3000, int distanceKm)
    {
        if (IonoLimits.Frequency(foF2) is null)
        {
            return null;
        }
        if (IonoLimits.M3000(m3000) is not { } m)
        {
            return distanceKm <= 100 ? foF2 : null;
        }
        const double x = 2;
        double b = m - 0.124 + ((m * m) - 4) * (0.0215 + (0.005 * Math.Sin((7.854 / x) - 1.9635)));
        double dmax = Math.Min(4000, 4780 + ((12610 + (2140 / (x * x)) - (49720 / Math.Pow(x, 4)) + (688900 / Math.Pow(x, 6))) * ((1 / b) - 0.303)));
        double d = Math.Min(distanceKm, dmax);
        double muf = ((1 + (C(d, dmax) / C(3000, dmax) * (b - 1))) * foF2) + (GyroMhz / 2 * (1 - (d / dmax)));
        return IonoLimits.Frequency(muf);
    }

    private static double C(double d, double dmax)
    {
        double z = 1 - (2 * d / dmax);
        return 0.74 - (0.591 * z) - (0.424 * z * z) - (0.090 * Math.Pow(z, 3)) + (0.088 * Math.Pow(z, 4)) + (0.181 * Math.Pow(z, 5)) + (0.096 * Math.Pow(z, 6));
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

    /// <summary>
    /// The skip zone's radius, km to the nearest 10, when the MUF is known at every distance (as
    /// estimated): 0 when open at 100 km, else the first 10 km step out to 1000 km at which it
    /// is open, null if none is.
    /// </summary>
    public static int? SkipZoneAlong(Func<int, double?> mufAt, double openMhz)
    {
        ArgumentNullException.ThrowIfNull(mufAt);
        if (mufAt(100) is not { } near)
        {
            return null;
        }
        if (near >= openMhz)
        {
            return 0;
        }
        for (int km = 110; km <= 1000; km += 10)
        {
            if (mufAt(km) is { } m && m >= openMhz)
            {
                return km;
            }
        }
        return null;
    }

    /// <summary>Whole minutes from <paramref name="at"/> to <paramref name="now"/>, never below 0.</summary>
    public static int AgeMinutes(DateTimeOffset at, DateTimeOffset now) =>
        (int)Math.Clamp(Math.Floor((now - at).TotalMinutes), 0, int.MaxValue);
}
