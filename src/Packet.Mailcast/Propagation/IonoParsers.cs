using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packet.Mailcast.Propagation;

/// <summary>
/// Reads the Lowell GIRO Data Center's DIDBase text, as <c>https://lgdc.uml.edu/fastchar/getbest</c>
/// gives it (the old <c>common/DIDBGetValues</c> servlet answers 404 now, with the same format).
/// </summary>
/// <remarks>
/// <para>Comment lines start with <c>#</c>. One says <c>URSI-Code RL052, CHILTON</c>; the last
/// names the columns: <c># Time CS foF2 QD MUF(D) QD M(D) QD</c>, each value followed by its
/// qualifier column. A data line is an ISO 8601 time, the autoscaling confidence score (0 to 100,
/// 999 for manual scaling, -1 when unknown), then the values; <c>---</c> is no value. A query with
/// no data says <c># STATUS: ERROR (No measurement data ...)</c> and has no header line. The MUF
/// distance is fixed at 3000 km whatever <c>DMUF</c> asks for.</para>
/// </remarks>
public static partial class GiroParser
{
    /// <summary>Soundings whose confidence score is below this are dropped (manual scaling, 999, is kept).</summary>
    public const int MinConfidence = 50;

    /// <summary>
    /// The soundings in a response, oldest first. A sounding with a confidence score under
    /// <see cref="MinConfidence"/> is dropped, as is one with no foF2 and no MUF. Throws
    /// <see cref="FormatException"/> if the text is not DIDBase's at all.
    /// </summary>
    public static IReadOnlyList<IonoSounding> Parse(string text, string? station = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n');
        if (!lines.Any(l => l.Contains("GIRO", StringComparison.Ordinal) || l.Contains("DIDBase", StringComparison.Ordinal)))
        {
            throw new FormatException("Not a GIRO DIDBase answer.");
        }
        string? code = station;
        string[]? columns = null;
        int distanceKm = 3000;
        var soundings = new List<IonoSounding>();
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
            {
                if (UrsiCode().Match(line) is { Success: true } m)
                {
                    code = m.Groups[1].Value.ToUpperInvariant();
                }
                if (Distance().Match(line) is { Success: true } d)
                {
                    distanceKm = int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                string body = line.TrimStart('#').Trim();
                if (body.StartsWith("Time", StringComparison.Ordinal))
                {
                    columns = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }
                continue;
            }
            if (columns is null || code is null || line.Trim().Length == 0)
            {
                continue;
            }
            string[] cells = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length < 2 || !DateTimeOffset.TryParse(cells[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            {
                continue;
            }
            double? Value(string name)
            {
                int i = Array.IndexOf(columns, name);
                return i > 0 && i < cells.Length
                    && double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : null;
            }
            int cs = Array.IndexOf(columns, "CS");
            if (cs > 0 && cs < cells.Length && int.TryParse(cells[cs], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int confidence)
                && confidence < MinConfidence)
            {
                continue;
            }
            double? foF2 = Value("foF2");
            bool at3000 = distanceKm == 3000;
            var sounding = new IonoSounding(code, time, IonoSource.Giro, foF2,
                Muf3000: at3000 ? Value("MUF(D)") : null,
                M3000: at3000 ? Value("M(D)") : null);
            if (sounding.Usable)
            {
                soundings.Add(sounding);
            }
        }
        return [.. soundings.OrderBy(s => s.Time)];
    }

    [GeneratedRegex(@"URSI-Code\s+([A-Za-z]{2}\d{3})")]
    private static partial Regex UrsiCode();

    [GeneratedRegex(@"Distance D for MUF calculations:\s*(\d+)\s*km")]
    private static partial Regex Distance();
}

/// <summary>
/// Reads PROPquest's JSON (<c>https://propquest.org/JSON-grab-ionosphere?DATE1=...&amp;DATE2=...&amp;OBSERVATORY=...</c>;
/// the <c>.php</c> name redirects there).
/// </summary>
/// <remarks>
/// <c>Observations[0]</c> holds comma-separated series on a 5-minute UTC grid, one set for DATE2
/// (<c>_TODAY</c>) and one for DATE1 (<c>_YDAY</c>): <c>DATE_</c>, <c>TIME_</c>, <c>foF2_</c>,
/// <c>MUFD3000_</c>, <c>MUFD1000_</c>, <c>MUFD500_</c>, <c>MUFD100_</c> and <c>OBSERVATORY_</c>,
/// which says which of the asked-for stations each point is from. A missing value is the literal
/// <c>null</c>. With DATE1 and DATE2 the same day, both sets are that day.
/// </remarks>
public static class PropQuestParser
{
    /// <summary>
    /// The soundings in a response, oldest first, each once. Throws <see cref="FormatException"/>
    /// if it is not PROPquest's JSON.
    /// </summary>
    public static IReadOnlyList<IonoSounding> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonElement observation;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Observations", out var observations)
                || observations.ValueKind != JsonValueKind.Array || observations.GetArrayLength() == 0)
            {
                throw new FormatException("No Observations in the PROPquest answer.");
            }
            observation = observations[0].Clone();
        }
        catch (JsonException e)
        {
            throw new FormatException("Not JSON: " + e.Message, e);
        }

        var found = new Dictionary<(string, DateTimeOffset), IonoSounding>();
        foreach (string day in new[] { "TODAY", "YDAY" })
        {
            string[]? Series(string name) =>
                observation.TryGetProperty($"{name}_{day}", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()!.Split(',')
                    : null;
            string[]? dates = Series("DATE");
            string[]? times = Series("TIME");
            string[]? stations = Series("OBSERVATORY");
            if (dates is null || times is null || stations is null)
            {
                continue;
            }
            string[]? foF2 = Series("foF2");
            string[]? muf3000 = Series("MUFD3000");
            string[]? muf1000 = Series("MUFD1000");
            string[]? muf500 = Series("MUFD500");
            string[]? muf100 = Series("MUFD100");
            int n = Math.Min(Math.Min(dates.Length, times.Length), stations.Length);
            for (int i = 0; i < n; i++)
            {
                string station = stations[i].Trim();
                if (station.Length == 0 || station == "null"
                    || !DateTimeOffset.TryParseExact($"{dates[i].Trim()} {times[i].Trim()}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
                {
                    continue;
                }
                var sounding = new IonoSounding(station.ToUpperInvariant(), time, IonoSource.PropQuest,
                    At(foF2, i), Muf3000: At(muf3000, i), Mufd100: At(muf100, i), Mufd500: At(muf500, i), Mufd1000: At(muf1000, i));
                if (sounding.Usable)
                {
                    found.TryAdd((sounding.Station, time), sounding);
                }
            }
        }
        return [.. found.Values.OrderBy(s => s.Time).ThenBy(s => s.Station, StringComparer.Ordinal)];
    }

    private static double? At(string[]? series, int i) =>
        series is not null && i < series.Length
        && double.TryParse(series[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : null;
}
