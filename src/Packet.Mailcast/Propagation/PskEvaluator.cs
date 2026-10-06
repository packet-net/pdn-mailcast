namespace Packet.Mailcast.Propagation;

/// <summary>
/// Turns the last half hour of PSK Reporter spots between stations in the UK and Ireland into a
/// <see cref="PskReading"/>: for 40 and 80 m, whether each of 100, 500 and 1000 km is open,
/// closed or not known, with the counts behind it, and where the skip zone ends.
/// </summary>
/// <remarks>
/// <para>Spots are put in three bins by the great-circle distance between the two locators: 30 to
/// 250 km for 100 km, 250 to 700 for 500, 700 to 1200 for 1000. Paths under 30 km are left out,
/// as ground wave or two stations in one locator square; paths over 1200 km are left out too.</para>
/// <para>A distance is open with at least <see cref="OpenSpots"/> spots from at least
/// <see cref="OpenStations"/> different callsigns. Silence is not proof of closure: a distance is
/// closed only when it has at most <see cref="ClosedMostSpots"/> spots and there is evidence that
/// people are on and the band works elsewhere: at least <see cref="ElsewhereSpots"/> spots from
/// <see cref="ElsewhereStations"/> stations on the same band at the other distances, or, for
/// 40 m, 80 m busy at the same distance (<see cref="EightySpots"/> spots, <see cref="EightyStations"/>
/// stations). Anything else is no data, and so is everything when the feed was down for more
/// than a third of the window.</para>
/// <para>The skip zone is 0 when 100 km is open. When 100 km is closed and a further distance is
/// open, it is the 5th percentile of the band's spot distances, and at least the third shortest,
/// to the nearest 10 km: where spots start to be common.</para>
/// </remarks>
public static class PskEvaluator
{
    /// <summary>How far back the spots go.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>A reading older than this says nothing about now (a receiver's view).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(45);

    /// <summary>The feed must have been up for at least this much of the window.</summary>
    public static readonly TimeSpan LeastCoverage = TimeSpan.FromMinutes(20);

    /// <summary>A spot further than this in the future is not believed.</summary>
    public static readonly TimeSpan MostAhead = TimeSpan.FromMinutes(5);

    /// <summary>Paths shorter than this, km, are not counted: ground wave, or one locator square.</summary>
    public const int ShortestKm = 30;

    /// <summary>The three bins: the distance each stands for and the paths it counts, km, the last one inclusive.</summary>
    public static IReadOnlyList<(int Km, int FromKm, int ToKm)> Bins { get; } = [(100, ShortestKm, 250), (500, 250, 700), (1000, 700, 1200)];

    /// <summary>Spots a distance needs to be open.</summary>
    public const int OpenSpots = 5;

    /// <summary>Different callsigns a distance needs to be open.</summary>
    public const int OpenStations = 4;

    /// <summary>The most spots a distance may have and still be closed: a stray or two is not an opening.</summary>
    public const int ClosedMostSpots = 2;

    /// <summary>Spots the same band needs at the other distances for a quiet one to count as closed.</summary>
    public const int ElsewhereSpots = 40;

    /// <summary>Different callsigns in those.</summary>
    public const int ElsewhereStations = 12;

    /// <summary>Spots 80 m needs at a distance for a quiet 40 m there to count as closed.</summary>
    public const int EightySpots = 10;

    /// <summary>Different callsigns in those.</summary>
    public const int EightyStations = 6;

    /// <summary>SNRs a median needs: a median of one or two is noise.</summary>
    public const int MedianSpots = 5;

    /// <summary>
    /// The reading for <paramref name="now"/> from the spots at hand. <paramref name="feedUp"/> is
    /// how long the feed was up during the window; under <see cref="LeastCoverage"/>, or when
    /// <paramref name="connected"/> is false, every verdict is no data (the counts are still given).
    /// </summary>
    public static PskReading Evaluate(IEnumerable<PskSpot> spots, DateTimeOffset now, TimeSpan feedUp, bool connected = true)
    {
        ArgumentNullException.ThrowIfNull(spots);
        var recent = spots
            .Where(s => s.Time > now - Window && s.Time <= now + MostAhead && s.DistanceKm >= ShortestKm && s.DistanceKm <= Bins[^1].ToKm)
            .DistinctBy(s => s.Sequence)
            .ToList();
        bool down = !connected || feedUp < LeastCoverage;
        var eightyCounts = Count(recent, PskBand.Eighty);
        var fortyCounts = Count(recent, PskBand.Forty);
        var eighty = Judge(PskBand.Eighty, eightyCounts, null, recent, down);
        var forty = Judge(PskBand.Forty, fortyCounts, eightyCounts, recent, down);
        return new PskReading
        {
            ObservedUtc = now,
            AgeMinutes = 0,
            WindowMinutes = (int)Window.TotalMinutes,
            FeedDown = down,
            Forty = forty,
            Eighty = eighty,
            State = State(forty),
        };
    }

    /// <summary>
    /// The whole reading from 40 m's three verdicts: GOOD open at 100 and 500 km; MARGINAL open
    /// somewhere else; POOR closed somewhere and open nowhere; UNKNOWN when nothing is known.
    /// </summary>
    public static IonoState State(PskBandReading? forty)
    {
        if (forty is null)
        {
            return IonoState.Unknown;
        }
        bool Open(int km) => forty.VerdictAt(km) is PathVerdict.Open or PathVerdict.Reliable;
        if (Open(100) && Open(500))
        {
            return IonoState.Good;
        }
        if (forty.Bins.Any(b => b.Verdict is PathVerdict.Open or PathVerdict.Reliable))
        {
            return IonoState.Marginal;
        }
        return forty.Bins.Any(b => b.Verdict == PathVerdict.Closed) ? IonoState.Poor : IonoState.Unknown;
    }

    /// <summary>The bin a path of <paramref name="km"/> falls in, or -1 for none.</summary>
    public static int BinOf(int km)
    {
        for (int i = 0; i < Bins.Count; i++)
        {
            var (_, from, to) = Bins[i];
            if (km >= from && (km < to || (i == Bins.Count - 1 && km <= to)))
            {
                return i;
            }
        }
        return -1;
    }

    private sealed class Counts
    {
        public int Spots { get; set; }

        public HashSet<string> Stations { get; } = new(StringComparer.Ordinal);

        public List<int> Snrs { get; } = [];
    }

    private static Counts[] Count(List<PskSpot> recent, PskBand band)
    {
        var counts = Bins.Select(_ => new Counts()).ToArray();
        foreach (var s in recent.Where(s => s.Band == band))
        {
            int i = BinOf(s.DistanceKm);
            if (i < 0)
            {
                continue;
            }
            counts[i].Spots++;
            counts[i].Stations.Add(s.Sender);
            counts[i].Stations.Add(s.Receiver);
            if (s.SnrDb is { } snr)
            {
                counts[i].Snrs.Add(snr);
            }
        }
        return counts;
    }

    private static PskBandReading Judge(PskBand band, Counts[] counts, Counts[]? eighty, List<PskSpot> recent, bool down)
    {
        var bins = new List<PskBin>(Bins.Count);
        for (int i = 0; i < Bins.Count; i++)
        {
            var (km, from, to) = Bins[i];
            var c = counts[i];
            var verdict = PathVerdict.NoData;
            var why = PskEvidence.None;
            if (!down)
            {
                if (c.Spots >= OpenSpots && c.Stations.Count >= OpenStations)
                {
                    verdict = PathVerdict.Open;
                }
                else if (c.Spots <= ClosedMostSpots)
                {
                    int elsewhereSpots = counts.Where((_, j) => j != i).Sum(o => o.Spots);
                    int elsewhereStations = counts.Where((_, j) => j != i).SelectMany(o => o.Stations).Distinct(StringComparer.Ordinal).Count();
                    if (elsewhereSpots >= ElsewhereSpots && elsewhereStations >= ElsewhereStations)
                    {
                        verdict = PathVerdict.Closed;
                        why = PskEvidence.OtherDistances;
                    }
                    else if (eighty is not null && eighty[i].Spots >= EightySpots && eighty[i].Stations.Count >= EightyStations)
                    {
                        verdict = PathVerdict.Closed;
                        why = PskEvidence.EightyMetres;
                    }
                }
            }
            bins.Add(new PskBin(km, from, to, verdict, c.Spots, c.Stations.Count, Median(c.Snrs), why));
        }
        return new PskBandReading(band, bins, SkipZone(bins, recent.Where(s => s.Band == band).Select(s => s.DistanceKm)));
    }

    private static int? SkipZone(List<PskBin> bins, IEnumerable<int> distances)
    {
        if (bins[0].Verdict is PathVerdict.Open or PathVerdict.Reliable)
        {
            return 0;
        }
        if (bins[0].Verdict != PathVerdict.Closed || !bins.Skip(1).Any(b => b.Verdict is PathVerdict.Open or PathVerdict.Reliable))
        {
            return null;
        }
        var sorted = distances.Order().ToList();
        if (sorted.Count < 3)
        {
            return null;
        }
        int index = Math.Max(2, (int)Math.Floor(sorted.Count * 0.05));
        return (int)(Math.Round(sorted[index] / 10.0, MidpointRounding.AwayFromZero) * 10);
    }

    /// <summary>The median, the mean of the middle two for an even count, rounded to whole dB; null under <see cref="MedianSpots"/> values.</summary>
    public static int? Median(IReadOnlyCollection<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < MedianSpots)
        {
            return null;
        }
        var sorted = values.Order().ToList();
        int n = sorted.Count;
        double median = n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
        return (int)Math.Round(median, MidpointRounding.AwayFromZero);
    }
}
