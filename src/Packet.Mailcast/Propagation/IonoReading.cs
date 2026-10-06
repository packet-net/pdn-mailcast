using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Packet.Mailcast.Propagation;

/// <summary>What 40 m looks like from the head end, by the nearest ionosonde. Observe only: it never changes what is sent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IonoState>))]
public enum IonoState : byte
{
    /// <summary>No reading, or none in the last <see cref="IonoSettings.StaleAfter"/>.</summary>
    [JsonStringEnumMemberName("UNKNOWN")]
    Unknown = 0,

    /// <summary>Closed at 100, 500 and 1000 km.</summary>
    [JsonStringEnumMemberName("POOR")]
    Poor = 1,

    /// <summary>Open at some of the three distances, but not reliably at both 100 and 500 km.</summary>
    [JsonStringEnumMemberName("MARGINAL")]
    Marginal = 2,

    /// <summary>Reliable at 100 and 500 km.</summary>
    [JsonStringEnumMemberName("GOOD")]
    Good = 3,
}

/// <summary>Where a sounding came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IonoSource>))]
public enum IonoSource : byte
{
    /// <summary>No sounding.</summary>
    [JsonStringEnumMemberName("none")]
    None = 0,

    /// <summary>The Lowell GIRO Data Center's DIDBase.</summary>
    [JsonStringEnumMemberName("giro")]
    Giro = 1,

    /// <summary>PROPquest.</summary>
    [JsonStringEnumMemberName("propquest")]
    PropQuest = 2,

    /// <summary>Not an ionosonde: live PSK Reporter spots, the source of a <see cref="PskReading"/>.</summary>
    [JsonStringEnumMemberName("pskreporter")]
    PskReporter = 3,
}

/// <summary>How the MUFs at 100, 500 and 1000 km were found.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MufMethod>))]
public enum MufMethod : byte
{
    /// <summary>No MUF at all.</summary>
    [JsonStringEnumMemberName("none")]
    None = 0,

    /// <summary>All three are the ionosonde's own MUF(D) values.</summary>
    [JsonStringEnumMemberName("measured")]
    Measured = 1,

    /// <summary>All three are estimated from foF2 and M(3000)F2 (see <see cref="IonoEvaluator.EstimateMuf"/>).</summary>
    [JsonStringEnumMemberName("estimated")]
    Estimated = 2,

    /// <summary>Some measured, some estimated.</summary>
    [JsonStringEnumMemberName("mixed")]
    Mixed = 3,
}

/// <summary>40 m at one distance.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PathVerdict>))]
public enum PathVerdict : byte
{
    /// <summary>No MUF for this distance.</summary>
    [JsonStringEnumMemberName("noData")]
    NoData = 0,

    /// <summary>The MUF is below 40 m.</summary>
    [JsonStringEnumMemberName("closed")]
    Closed = 1,

    /// <summary>The MUF reaches 40 m, but with little to spare.</summary>
    [JsonStringEnumMemberName("open")]
    Open = 2,

    /// <summary>The MUF reaches 40 m with room to spare.</summary>
    [JsonStringEnumMemberName("reliable")]
    Reliable = 3,
}

/// <summary>
/// One ionosonde sounding as a source gives it. Values in MHz; null where the source has none.
/// </summary>
/// <param name="Station">The URSI code, such as RL052.</param>
/// <param name="Time">When the sounding was made, UTC.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="FoF2">The F2 layer's critical frequency.</param>
/// <param name="Muf3000">MUF(3000)F2.</param>
/// <param name="M3000">M(3000)F2, the MUF(3000) factor; worked out from <paramref name="Muf3000"/> and <paramref name="FoF2"/> when left out.</param>
/// <param name="Mufd100">The ionosonde's own MUF for 100 km.</param>
/// <param name="Mufd500">The ionosonde's own MUF for 500 km.</param>
/// <param name="Mufd1000">The ionosonde's own MUF for 1000 km.</param>
public sealed record IonoSounding(
    string Station,
    DateTimeOffset Time,
    IonoSource Source,
    double? FoF2,
    double? Muf3000 = null,
    double? M3000 = null,
    double? Mufd100 = null,
    double? Mufd500 = null,
    double? Mufd1000 = null)
{
    // Every value is checked on the way in: one that is not finite and plausible is no value.
    private readonly double? _foF2 = IonoLimits.Frequency(FoF2);
    private readonly double? _muf3000 = IonoLimits.Frequency(Muf3000);
    private readonly double? _m3000 = IonoLimits.M3000(M3000);
    private readonly double? _mufd100 = IonoLimits.Frequency(Mufd100);
    private readonly double? _mufd500 = IonoLimits.Frequency(Mufd500);
    private readonly double? _mufd1000 = IonoLimits.Frequency(Mufd1000);

    /// <summary>The F2 layer's critical frequency, MHz, if plausible.</summary>
    public double? FoF2 { get => _foF2; init => _foF2 = IonoLimits.Frequency(value); }

    /// <summary>MUF(3000)F2, MHz, if plausible.</summary>
    public double? Muf3000 { get => _muf3000; init => _muf3000 = IonoLimits.Frequency(value); }

    /// <summary>M(3000)F2, if plausible.</summary>
    public double? M3000 { get => _m3000; init => _m3000 = IonoLimits.M3000(value); }

    /// <summary>The ionosonde's own MUF for 100 km, MHz, if plausible.</summary>
    public double? Mufd100 { get => _mufd100; init => _mufd100 = IonoLimits.Frequency(value); }

    /// <summary>The ionosonde's own MUF for 500 km, MHz, if plausible.</summary>
    public double? Mufd500 { get => _mufd500; init => _mufd500 = IonoLimits.Frequency(value); }

    /// <summary>The ionosonde's own MUF for 1000 km, MHz, if plausible.</summary>
    public double? Mufd1000 { get => _mufd1000; init => _mufd1000 = IonoLimits.Frequency(value); }

    /// <summary>Whether it says anything about 40 m: a foF2 or a MUF.</summary>
    public bool Usable => FoF2 is not null || Mufd100 is not null || Mufd500 is not null || Mufd1000 is not null;
}

/// <summary>
/// How a reading is judged, with the defaults the head end uses. These are the only settings:
/// <c>"ionosphere"</c> in the head end's configuration.
/// </summary>
public sealed record IonoSettings
{
    /// <summary>A distance is open when its MUF is at least this, MHz: the top of the mailcast signal is at 7.0554.</summary>
    public double OpenMhz { get; init; } = 7.1;

    /// <summary>A distance is reliable when this times its MUF is still at least <see cref="OpenMhz"/>.</summary>
    public double ReliableFactor { get; init; } = 0.85;

    /// <summary>The default for <see cref="StaleAfter"/>, which a receiver uses too: 45 minutes.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(45);

    /// <summary>A sounding older than this says nothing about now: the reading is UNKNOWN, with the last values and their age.</summary>
    public TimeSpan StaleAfter { get; init; } = DefaultStaleAfter;

    /// <summary>
    /// The ionosondes, best first: the first with a fresh sounding is used. Chilton (RL052) is
    /// near Reading, Fairford (FF051) about 85 km away, Dourbes (DB049) in Belgium. Empty turns the
    /// reading off.
    /// </summary>
    public IReadOnlyList<string> Stations { get; init; } = ["RL052", "FF051", "DB049"];
}

/// <summary>
/// The ionosonde reading: is 40 m open from Reading right now, and to how far. What the head end
/// logs, reports and sends as content type 4, and what a receiver shows.
/// </summary>
public sealed record IonoReading
{
    private readonly double? _foF2;
    private readonly double? _mufd100;
    private readonly double? _mufd500;
    private readonly double? _mufd1000;

    /// <summary>The verdict as a whole.</summary>
    public IonoState State { get; init; }

    /// <summary>foF2, MHz.</summary>
    public double? FoF2 { get => _foF2; init => _foF2 = IonoLimits.Frequency(value); }

    /// <summary>The MUF for 100 km, MHz.</summary>
    public double? Mufd100 { get => _mufd100; init => _mufd100 = IonoLimits.Frequency(value); }

    /// <summary>The MUF for 500 km, MHz.</summary>
    public double? Mufd500 { get => _mufd500; init => _mufd500 = IonoLimits.Frequency(value); }

    /// <summary>The MUF for 1000 km, MHz.</summary>
    public double? Mufd1000 { get => _mufd1000; init => _mufd1000 = IonoLimits.Frequency(value); }

    /// <summary>
    /// The skip zone's radius, km: the shortest distance at which 40 m is open, interpolated
    /// between 100, 500 and 1000 km. 0 when it is open at 100 km; null when it is not open
    /// within 1000 km, or the MUFs to say are missing.
    /// </summary>
    public int? SkipZoneKm { get; init; }

    /// <summary>The ionosonde's URSI code, such as RL052.</summary>
    public string? Station { get; init; }

    /// <summary>When the sounding was made.</summary>
    public DateTimeOffset? SoundingTimeUtc { get; init; }

    /// <summary>The sounding's age in whole minutes, as of when the reading was taken.</summary>
    public int? AgeMinutes { get; init; }

    /// <summary>Where the sounding came from.</summary>
    public IonoSource Source { get; init; }

    /// <summary>How the MUFs were found.</summary>
    public MufMethod Method { get; init; }

    /// <summary>40 m at 100 km.</summary>
    public PathVerdict At100 { get; init; }

    /// <summary>40 m at 500 km.</summary>
    public PathVerdict At500 { get; init; }

    /// <summary>40 m at 1000 km.</summary>
    public PathVerdict At1000 { get; init; }

    /// <summary>No reading at all.</summary>
    public static IonoReading None { get; } = new();

    /// <summary>Whether there is a sounding behind it, fresh or not.</summary>
    [JsonIgnore]
    public bool HasSounding => SoundingTimeUtc is not null;

    /// <summary>
    /// The same reading as of <paramref name="now"/>: its age counted again, and UNKNOWN once the
    /// sounding is older than <paramref name="staleAfter"/>. Never anything better than it was:
    /// nothing is extrapolated.
    /// </summary>
    public IonoReading AsOf(DateTimeOffset now, TimeSpan staleAfter)
    {
        if (SoundingTimeUtc is not { } at)
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

    /// <summary>The station's name, such as Chilton, or its code if this code does not know it.</summary>
    [JsonIgnore]
    public string StationName => IonoStations.Name(Station);

    /// <summary>
    /// The reading in a sentence or two, plain ASCII, as the receiver's log and the journal show
    /// it: "Ionosphere: Chilton foF2 6.05 MHz at 14:30 UTC (12 min old). 40 m: closed at 100 km,
    /// open from about 220 km."
    /// </summary>
    public string Describe(DateTimeOffset now) => !HasSounding
        ? "Ionosphere: no reading yet."
        : "Ionosphere: " + (State == IonoState.Unknown ? "no fresh reading. Last: " : "") + Summary(now);

    /// <summary>
    /// The same without the "Ionosphere:" in front or the "no fresh reading", for a page that
    /// says those already: "Chilton foF2 6.05 MHz at 14:30 UTC (12 min old). 40 m: ...".
    /// </summary>
    public string Summary(DateTimeOffset now)
    {
        if (!HasSounding)
        {
            return "No reading yet.";
        }
        var text = new StringBuilder(StationName);
        text.Append(FoF2 is { } f ? string.Create(CultureInfo.InvariantCulture, $" foF2 {f:0.00} MHz") : " no foF2");
        text.Append(' ').Append(When(SoundingTimeUtc!.Value, now));
        int age = IonoEvaluator.AgeMinutes(SoundingTimeUtc.Value, now);
        text.Append(string.Create(CultureInfo.InvariantCulture, $" ({Age(age)} old)."));
        text.Append(State == IonoState.Unknown ? " Too old to judge 40 m by." : $" {Band()}.");
        return text.ToString();
    }

    /// <summary>
    /// A few words for the top of a tile: "Open near and far", "Open from about 220 km",
    /// "Open, but only just", "Closed", "No fresh reading".
    /// </summary>
    public string Headline() => State switch
    {
        IonoState.Good => "Open near and far",
        IonoState.Poor => "Closed",
        IonoState.Marginal when SkipZoneKm is > 0 and var k => string.Create(CultureInfo.InvariantCulture, $"Open from about {k} km"),
        IonoState.Marginal when SkipZoneKm == 0 => "Open, but only just",
        IonoState.Marginal => "Open at some distances",
        _ => HasSounding ? "No fresh reading" : "No reading yet",
    };

    /// <summary>
    /// 40 m in words, from the skip zone and the three verdicts: "40 m: closed at 100 km, open
    /// from about 220 km", "40 m: open from 0 km out to 1000 km", "40 m: open at 500 and 1000 km",
    /// "40 m: closed at 100, 500 and 1000 km".
    /// </summary>
    public string Band()
    {
        PathVerdict[] all = [At100, At500, At1000];
        if (all.All(v => v == PathVerdict.NoData))
        {
            return "40 m: no MUF to judge by";
        }
        if (SkipZoneKm == 0)
        {
            return At1000 is PathVerdict.Closed ? "40 m: open close in, closed at 1000 km" : "40 m: open from 0 km out to 1000 km";
        }
        if (SkipZoneKm is { } skip)
        {
            return string.Create(CultureInfo.InvariantCulture, $"40 m: closed at 100 km, open from about {skip} km");
        }
        var open = IonoEvaluator.DistancesKm.Where((_, i) => all[i] is PathVerdict.Open or PathVerdict.Reliable).ToList();
        if (open.Count > 0)
        {
            return "40 m: open at " + Join(open) + " km";
        }
        var closed = IonoEvaluator.DistancesKm.Where((_, i) => all[i] == PathVerdict.Closed).ToList();
        return "40 m: closed at " + Join(closed) + " km" + (closed.Count < 3 ? ", no MUF for the rest" : "");
    }

    private static string Join(List<int> km) => km.Count switch
    {
        1 => km[0].ToString(CultureInfo.InvariantCulture),
        2 => string.Create(CultureInfo.InvariantCulture, $"{km[0]} and {km[1]}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{km[0]}, {km[1]} and {km[2]}"),
    };

    /// <summary>The three distances in words: "100 km closed, 500 km closed, 1000 km good".</summary>
    public string Distances() => string.Create(CultureInfo.InvariantCulture,
        $"100 km {Words(At100)}, 500 km {Words(At500)}, 1000 km {Words(At1000)}");

    /// <summary>One distance's verdict in a word or two.</summary>
    public static string Words(PathVerdict verdict) => verdict switch
    {
        PathVerdict.Closed => "closed",
        PathVerdict.Open => "open, just",
        PathVerdict.Reliable => "good",
        _ => "no data",
    };

    /// <summary>
    /// One plain ASCII line for the head end's journal, with the numbers: state, station, foF2,
    /// the three MUFs, the skip zone, the source and the method.
    /// </summary>
    public string JournalLine(DateTimeOffset now)
    {
        if (!HasSounding)
        {
            return "ionosonde: UNKNOWN, no reading";
        }
        static string Mhz(double? v) => v is { } x ? x.ToString("0.00", CultureInfo.InvariantCulture) : "-";
        string state = State switch
        {
            IonoState.Good => "GOOD",
            IonoState.Marginal => "MARGINAL",
            IonoState.Poor => "POOR",
            _ => "UNKNOWN",
        };
        string source = Source switch
        {
            IonoSource.Giro => "GIRO",
            IonoSource.PropQuest => "PROPquest",
            _ => "no source",
        };
        string method = Method switch
        {
            MufMethod.Measured => "MUFs measured",
            MufMethod.Estimated => "MUFs estimated from foF2 and M(3000)F2",
            MufMethod.Mixed => "MUFs partly estimated",
            _ => "no MUFs",
        };
        string skip = SkipZoneKm is { } k ? string.Create(CultureInfo.InvariantCulture, $"skip zone about {k} km") : "no skip zone within 1000 km";
        int age = IonoEvaluator.AgeMinutes(SoundingTimeUtc!.Value, now);
        return string.Create(CultureInfo.InvariantCulture,
            $"ionosonde: {state}, {StationName} {Station} at {SoundingTimeUtc.Value.UtcDateTime:yyyy-MM-dd HH:mm}Z ({age} min old, {source}): foF2 {Mhz(FoF2)} MHz, MUF {Mhz(Mufd100)}/{Mhz(Mufd500)}/{Mhz(Mufd1000)} MHz at 100/500/1000 km ({method}); {Distances()}; {skip}");
    }

    private static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var utc = at.UtcDateTime;
        return utc.Date == now.UtcDateTime.Date
            ? utc.ToString("'at 'HH:mm' UTC'", CultureInfo.InvariantCulture)
            : utc.ToString("'on 'yyyy-MM-dd' at 'HH:mm' UTC'", CultureInfo.InvariantCulture);
    }

    private static string Age(int minutes) => minutes < 120
        ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min")
        : string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60} min");
}

/// <summary>What a value must be to be believed: anything else, infinities and NaN included, is no value.</summary>
public static class IonoLimits
{
    /// <summary>The lowest foF2 or MUF believed, MHz.</summary>
    public const double LowestMhz = 0.5;

    /// <summary>The highest foF2 or MUF believed, MHz.</summary>
    public const double HighestMhz = 50;

    /// <summary>The lowest M(3000)F2 believed (above it, strictly).</summary>
    public const double LowestM3000 = 1;

    /// <summary>The highest M(3000)F2 believed.</summary>
    public const double HighestM3000 = 6;

    /// <summary>A frequency if it is finite and from <see cref="LowestMhz"/> to <see cref="HighestMhz"/>, else null.</summary>
    public static double? Frequency(double? mhz) =>
        mhz is { } v && double.IsFinite(v) && v >= LowestMhz && v <= HighestMhz ? v : null;

    /// <summary>An M(3000)F2 if it is finite, above <see cref="LowestM3000"/> and at most <see cref="HighestM3000"/>, else null.</summary>
    public static double? M3000(double? m) =>
        m is { } v && double.IsFinite(v) && v > LowestM3000 && v <= HighestM3000 ? v : null;
}

/// <summary>The ionosondes this code knows by name.</summary>
public static class IonoStations
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RL052"] = "Chilton",
        ["FF051"] = "Fairford",
        ["DB049"] = "Dourbes",
    };

    /// <summary>The station's name, or its code if it is not one of these.</summary>
    public static string Name(string? code) =>
        code is null ? "no station" : Names.TryGetValue(code, out var name) ? name : code;
}
