using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Packet.Mailcast.Propagation;

/// <summary>What a closed verdict at a distance rests on: silence alone never makes one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PskEvidence>))]
public enum PskEvidence : byte
{
    /// <summary>Not closed, or no reason given.</summary>
    [JsonStringEnumMemberName("none")]
    None = 0,

    /// <summary>Plenty of spots on the same band at other distances, and next to none at this one.</summary>
    [JsonStringEnumMemberName("otherDistances")]
    OtherDistances = 1,

    /// <summary>80 m busy at this distance while 40 m has next to nothing there.</summary>
    [JsonStringEnumMemberName("eightyMetres")]
    EightyMetres = 2,
}

/// <summary>One distance on one band over the reading's window: the verdict and the counts behind it.</summary>
/// <param name="Km">The distance it stands for: 100, 500 or 1000 km.</param>
/// <param name="FromKm">The shortest path counted in it.</param>
/// <param name="ToKm">The longest.</param>
/// <param name="Verdict">Open, closed, or no data (too few spots, or too little evidence, to say).</param>
/// <param name="Spots">Spots in it.</param>
/// <param name="Stations">Different callsigns in those spots, at either end.</param>
/// <param name="SnrMedianDb">The median SNR the receivers reported, dB; null under <see cref="PskEvaluator.MedianSpots"/> SNRs.</param>
/// <param name="ClosedBy">For a closed verdict, what it rests on.</param>
public sealed record PskBin(int Km, int FromKm, int ToKm, PathVerdict Verdict, int Spots, int Stations, int? SnrMedianDb, PskEvidence ClosedBy = PskEvidence.None);

/// <summary>One band over the reading's window.</summary>
/// <param name="Band">40 or 80 m.</param>
/// <param name="Bins">100, 500 and 1000 km, in that order.</param>
/// <param name="SkipZoneKm">Where the band starts to be open: 0 when open at 100 km; null when not open, or not known to be closed close in.</param>
public sealed record PskBandReading(PskBand Band, IReadOnlyList<PskBin> Bins, int? SkipZoneKm)
{
    /// <summary>The bin for 100, 500 or 1000 km, if there is one.</summary>
    public PskBin? At(int km) => Bins.FirstOrDefault(b => b.Km == km);

    /// <summary>The verdict at 100, 500 or 1000 km; no data when there is no such bin.</summary>
    public PathVerdict VerdictAt(int km) => At(km)?.Verdict ?? PathVerdict.NoData;

    /// <summary>Spots in all three bins.</summary>
    [JsonIgnore]
    public int Spots => Bins.Sum(b => b.Spots);
}

/// <summary>
/// The PSK Reporter reading: what the last <see cref="PskEvaluator.Window"/> of FT8, FT4 and WSPR
/// spots between stations in the UK and Ireland say about 40 m (and, at the head end, 80 m) at
/// 100, 500 and 1000 km. Observe only: it never changes what is sent. What the head end logs,
/// reports and sends as content type 4, source 3, and what a receiver shows.
/// </summary>
public sealed record PskReading
{
    /// <summary>The verdict as a whole, by the same words as the ionosonde's: GOOD open at 100 and 500 km, MARGINAL open somewhere, POOR closed and open nowhere, UNKNOWN otherwise.</summary>
    public IonoState State { get; init; }

    /// <summary>The end of the window, when the reading was taken; null for no reading at all.</summary>
    public DateTimeOffset? ObservedUtc { get; init; }

    /// <summary>The reading's age in whole minutes, as of when it was last looked at.</summary>
    public int? AgeMinutes { get; init; }

    /// <summary>How far back the spots go, minutes.</summary>
    public int WindowMinutes { get; init; } = (int)PskEvaluator.Window.TotalMinutes;

    /// <summary>The feed was down for too much of the window to judge by: every verdict is no data.</summary>
    public bool FeedDown { get; init; }

    /// <summary>40 m.</summary>
    public PskBandReading? Forty { get; init; }

    /// <summary>80 m, at the head end only: it is not sent on the air.</summary>
    public PskBandReading? Eighty { get; init; }

    /// <summary>No reading at all.</summary>
    public static PskReading None { get; } = new();

    /// <summary>Whether there is a reading, fresh or not.</summary>
    [JsonIgnore]
    public bool HasObservation => ObservedUtc is not null;

    /// <summary>40 m at 100 km.</summary>
    public PathVerdict At100 => Forty?.VerdictAt(100) ?? PathVerdict.NoData;

    /// <summary>40 m at 500 km.</summary>
    public PathVerdict At500 => Forty?.VerdictAt(500) ?? PathVerdict.NoData;

    /// <summary>40 m at 1000 km.</summary>
    public PathVerdict At1000 => Forty?.VerdictAt(1000) ?? PathVerdict.NoData;

    /// <summary>40 m's skip zone, km: see <see cref="PskBandReading.SkipZoneKm"/>.</summary>
    public int? SkipZoneKm => Forty?.SkipZoneKm;

    /// <summary>The same reading as of <paramref name="now"/>: its age counted again, and UNKNOWN once older than <paramref name="staleAfter"/>.</summary>
    public PskReading AsOf(DateTimeOffset now, TimeSpan staleAfter)
    {
        if (ObservedUtc is not { } at)
        {
            return this;
        }
        int age = IonoEvaluator.AgeMinutes(at, now);
        return this with
        {
            AgeMinutes = age,
            State = TimeSpan.FromMinutes(age) > staleAfter ? IonoState.Unknown : State,
        };
    }

    /// <summary>
    /// A few words for the top of the tile, saying whose verdict it is: "FT8 spots: open from
    /// about 260 km", "FT8 spots: open near and far", "FT8 spots: closed", "FT8 spots: too few to
    /// say". FT8 decodes far weaker signals than the mailcast needs, so these never promise that
    /// GB7RDG will be heard.
    /// </summary>
    public string Headline()
    {
        if (!HasObservation)
        {
            return "No PSK Reporter reading yet";
        }
        if (FeedDown)
        {
            return "No verdict: the PSK Reporter feed was down";
        }
        return State switch
        {
            IonoState.Good => "FT8 spots: open near and far",
            IonoState.Poor => "FT8 spots: closed",
            IonoState.Marginal when SkipZoneKm is > 0 and var k => string.Create(CultureInfo.InvariantCulture, $"FT8 spots: open from about {k} km"),
            IonoState.Marginal => "FT8 spots: open at some distances",
            _ => AgeMinutes is { } age && TimeSpan.FromMinutes(age) > PskEvaluator.StaleAfter ? "No fresh PSK Reporter reading" : "FT8 spots: too few to say",
        };
    }

    /// <summary>
    /// The reading in a sentence, plain ASCII, for the page: "40 m FT8/FT4/WSPR spots (PSK
    /// Reporter, last 30 min): open at 500 km (25 spots, 12 stations, median -11 dB) and 1000 km
    /// (16 spots, 9 stations, median -13 dB), nothing under 250 km despite 41 spots further out."
    /// </summary>
    public string Summary(DateTimeOffset now)
    {
        if (!HasObservation)
        {
            return "PSK Reporter: no reading yet.";
        }
        int age = IonoEvaluator.AgeMinutes(ObservedUtc!.Value, now);
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"40 m FT8/FT4/WSPR spots (PSK Reporter, last {WindowMinutes} min"));
        text.Append(age >= 1 ? string.Create(CultureInfo.InvariantCulture, $" to {ObservedUtc.Value.UtcDateTime:HH:mm} UTC, {age} min ago") : "");
        text.Append("): ");
        if (FeedDown)
        {
            return text.Append("the feed was down for too much of that time to judge by.").ToString();
        }
        if (TimeSpan.FromMinutes(age) > PskEvaluator.StaleAfter)
        {
            return text.Append("too old to judge 40 m by.").ToString();
        }
        return text.Append(Band(Forty)).Append('.').ToString();
    }

    /// <summary><see cref="Summary"/>'s words, for a receiver's log.</summary>
    public string Describe(DateTimeOffset now) => Summary(now);

    /// <summary>
    /// One band in words from its three bins: "open at 500 km (25 spots, 12 stations), nothing
    /// under 250 km despite 41 spots further out", "too few spots to judge (3 spots, 2 stations)".
    /// </summary>
    public static string Band(PskBandReading? band)
    {
        if (band is null || band.Bins.Count == 0)
        {
            return "no spots";
        }
        var parts = new List<string>();
        var open = band.Bins.Where(b => b.Verdict is PathVerdict.Open or PathVerdict.Reliable).ToList();
        if (open.Count > 0)
        {
            parts.Add("open at " + JoinAnd(open.Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Km} km ({Counts(b)}{Median(b)})"))));
        }
        foreach (var b in band.Bins.Where(b => b.Verdict == PathVerdict.Closed))
        {
            string what = b.Spots == 0 ? "nothing" : b.Spots == 1 ? "only 1 spot" : string.Create(CultureInfo.InvariantCulture, $"only {b.Spots} spots");
            string why = b.ClosedBy == PskEvidence.EightyMetres
                ? string.Create(CultureInfo.InvariantCulture, $" while 80 m is busy there")
                : string.Create(CultureInfo.InvariantCulture, $" despite {band.Spots - b.Spots} spots {Elsewhere(band, b)}");
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{what} {Range(b)}{why}"));
        }
        var unknown = band.Bins.Where(b => b.Verdict == PathVerdict.NoData).ToList();
        if (unknown.Count == band.Bins.Count)
        {
            int spots = band.Spots;
            parts.Add(spots == 0 ? "no spots to judge by" : string.Create(CultureInfo.InvariantCulture, $"too few spots to judge ({spots} {(spots == 1 ? "spot" : "spots")})"));
        }
        else if (unknown.Count > 0)
        {
            parts.Add("too few spots to judge at " + JoinAnd(unknown.Select(b => b.Km.ToString(CultureInfo.InvariantCulture))) + " km");
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// The three distances in words, counts and medians: "100 km closed (0 spots), 500 km open (25
    /// spots, 12 stations, median -11 dB), ...". With <paramref name="judged"/> false (the feed was
    /// down) each is "not judged", with the counts there were.
    /// </summary>
    public static string Distances(PskBandReading? band, bool judged = true) => band is null
        ? "no spots"
        : string.Join(", ", band.Bins.Select(b =>
            string.Create(CultureInfo.InvariantCulture, $"{b.Km} km {(judged ? Words(b.Verdict) : "not judged")} ({Counts(b)}{Median(b)})")));

    private static string Median(PskBin b) => b.SnrMedianDb is { } snr ? string.Create(CultureInfo.InvariantCulture, $", median {snr} dB") : "";

    /// <summary>One distance's verdict in a word or two.</summary>
    public static string Words(PathVerdict verdict) => verdict switch
    {
        PathVerdict.Closed => "closed",
        PathVerdict.Open or PathVerdict.Reliable => "open",
        _ => "too few spots",
    };

    /// <summary>
    /// One plain ASCII line for the head end's journal, with the numbers: the state, the window,
    /// both bands with each distance's verdict, counts and median SNR, and the skip zones.
    /// </summary>
    public string JournalLine(DateTimeOffset now)
    {
        if (!HasObservation)
        {
            return "pskreporter: UNKNOWN, no reading";
        }
        string state = State switch
        {
            IonoState.Good => "GOOD",
            IonoState.Marginal => "MARGINAL",
            IonoState.Poor => "POOR",
            _ => "UNKNOWN",
        };
        static string Skip(PskBandReading? band) => band?.SkipZoneKm switch
        {
            0 => "no skip zone, open close in",
            { } k => string.Create(CultureInfo.InvariantCulture, $"skip zone about {k} km"),
            null => "no skip zone estimate",
        };
        int age = IonoEvaluator.AgeMinutes(ObservedUtc!.Value, now);
        return string.Create(CultureInfo.InvariantCulture,
            $"pskreporter: {state}, last {WindowMinutes} min to {ObservedUtc.Value.UtcDateTime:HH:mm}Z ({age} min old{(FeedDown ? ", feed down too long to judge by" : "")}), UK and Ireland FT8/FT4/WSPR: 40 m {Distances(Forty, !FeedDown)}; {Skip(Forty)}; 80 m (not sent) {Distances(Eighty, !FeedDown)}; {Skip(Eighty)}");
    }

    private static string Counts(PskBin b) => b.Stations == 0 && b.Spots == 0
        ? "0 spots"
        : string.Create(CultureInfo.InvariantCulture, $"{b.Spots} {(b.Spots == 1 ? "spot" : "spots")}, {b.Stations} {(b.Stations == 1 ? "station" : "stations")}");

    private static string Range(PskBin b) => b.Km switch
    {
        100 => string.Create(CultureInfo.InvariantCulture, $"under {b.ToKm} km"),
        1000 => string.Create(CultureInfo.InvariantCulture, $"beyond {b.FromKm} km"),
        _ => string.Create(CultureInfo.InvariantCulture, $"from {b.FromKm} to {b.ToKm} km"),
    };

    private static string Elsewhere(PskBandReading band, PskBin b) =>
        b == band.Bins[0] ? "further out" : b == band.Bins[^1] ? "closer in" : "at other distances";

    private static string JoinAnd(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }
}
