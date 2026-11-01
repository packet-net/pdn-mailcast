using System.Text.Json;

namespace Packet.Mailcast;

/// <summary>
/// A station a receiver is told about by its stations file: its callsign and signing key, the
/// slots it is followed at until its own directory is heard, and where it is on the air.
/// </summary>
/// <param name="Callsign">The station's callsign, as its signed directories name it.</param>
/// <param name="Key">The key its directories are signed with, pinned to <paramref name="Callsign"/>.</param>
/// <param name="Slots">The slots it is listened at until its own directory gives them.</param>
/// <param name="FrequencyHz">Where its signal is centred.</param>
/// <param name="Sideband">Its sideband.</param>
/// <param name="Locator">Where it transmits from, a Maidenhead locator.</param>
/// <param name="Formerly">Names an older version of the receiver kept state under, read as this station's.</param>
public sealed record KnownStation(string Callsign, TrustedKey Key, SlotTimetable Slots, long FrequencyHz, Sideband Sideband, string Locator, IReadOnlyList<string> Formerly)
{
    /// <summary>The station as an advert, which is how a station only known from a file is listened at.</summary>
    public StationAdvert ToAdvert() => new(Callsign, Slots, FrequencyHz, Sideband, Locator);

    /// <summary>Its frequency, sideband and place, as a directory would give them.</summary>
    public StationDetails Details => new(FrequencyHz, Sideband, Locator);
}

/// <summary>
/// The stations file: the stations a receiver trusts and follows before it has heard anything,
/// and the address the daily report goes to. The code names no station; this is where they come from.
/// </summary>
/// <remarks>
/// JSON: <c>{ "report": "MCAST@HOST.#1.GBR.EURO", "stations": [ { "callsign": "G4ABC", "publicKey": "base64",
/// "slots": { "minute": 0, "everyMinutes": 60, "daylight": { "locator": "IO91lk", "afterSunriseMinutes": 120, "beforeSunsetMinutes": 30 } },
/// "frequencyHz": 7053800, "sideband": "usb", "locator": "IO91lk", "formerly": ["OLDNAME"] } ] }</c>.
/// <c>daylight</c> and <c>formerly</c> may be left out. A package ships one; the operator's own,
/// beside the config file, is used instead of it, and may list no stations.
/// </remarks>
public sealed class StationsFile
{
    /// <summary>The file's name, shipped and as an operator's override.</summary>
    public const string FileName = "stations.json";

    private readonly List<KnownStation> _stations;

    /// <summary>Creates a file's contents.</summary>
    public StationsFile(IReadOnlyList<KnownStation> stations, string? report, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(stations);
        _stations = [.. stations];
        Report = report;
        Path = path;
    }

    /// <summary>No stations and no report address.</summary>
    public static StationsFile Empty { get; } = new([], null);

    /// <summary>The stations, in the file's order.</summary>
    public IReadOnlyList<KnownStation> Stations => _stations;

    /// <summary>The daily report's address as <c>BULLETIN@ADDRESS</c>, or null when the file gives none.</summary>
    public string? Report { get; }

    /// <summary>Where it was read from, or null for one made in code.</summary>
    public string? Path { get; }

    /// <summary>The first station in the file, or null when there are none.</summary>
    public KnownStation? First => _stations.Count > 0 ? _stations[0] : null;

    /// <summary>The station with this callsign, or null.</summary>
    public KnownStation? Find(string? callsign) => _stations.Find(s => string.Equals(s.Callsign, callsign, StringComparison.Ordinal));

    /// <summary>
    /// <paramref name="name"/> as the station it stands for now: the callsign itself, or the
    /// callsign of the entry that lists it under <c>formerly</c>; unchanged when neither.
    /// </summary>
    public string Current(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var station in _stations)
        {
            if (string.Equals(station.Callsign, name, StringComparison.Ordinal) || station.Formerly.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return station.Callsign;
            }
        }
        return name;
    }

    /// <summary>
    /// The station that state kept without a station's name belongs to: the first that lists a
    /// former name, since a version that kept one timetable and one receipt kept them for the one
    /// station it knew, which is the one that was renamed. Null when none does.
    /// </summary>
    public string? Legacy => _stations.Find(s => s.Formerly.Count > 0)?.Callsign;

    /// <summary>Splits a report address <c>BULLETIN@ADDRESS</c>; null parts when it is not one.</summary>
    public static (string To, string At)? SplitAddress(string? address)
    {
        if (address is null)
        {
            return null;
        }
        int at = address.IndexOf('@', StringComparison.Ordinal);
        string to = at < 0 ? "" : address[..at];
        string where = at < 0 ? "" : address[(at + 1)..];
        return to.Length is >= 1 and <= 6 && where.Length is >= 1 and <= 40 && !where.Contains('@', StringComparison.Ordinal)
            && (to + where).All(c => c is > ' ' and <= '~') ? (to, where) : null;
    }

    /// <summary>
    /// The stations file in force for a config at <paramref name="configPath"/>: <c>stations.json</c>
    /// beside it if there is one, else the shipped one if there is one, else none. Throws
    /// <see cref="FormatException"/> with a one-line sentence for the operator if the file used is bad.
    /// </summary>
    public static StationsFile Locate(string configPath, string? shippedPath = null)
    {
        string? folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(configPath));
        string overridden = System.IO.Path.Combine(folder ?? ".", FileName);
        if (File.Exists(overridden))
        {
            return Load(overridden);
        }
        foreach (string shipped in shippedPath is null ? ShippedPaths : [shippedPath])
        {
            if (File.Exists(shipped))
            {
                return Load(shipped);
            }
        }
        return Empty;
    }

    /// <summary>Where the package puts the file: on Linux /usr/share/pdn-mailcast-receiver, else beside the program.</summary>
    public static IReadOnlyList<string> ShippedPaths { get; } =
    [
        "/usr/share/pdn-mailcast-receiver/" + FileName,
        System.IO.Path.Combine(AppContext.BaseDirectory, FileName),
    ];

    /// <summary>Reads a stations file. Throws <see cref="FormatException"/> with a one-line sentence for the operator.</summary>
    public static StationsFile Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FormatException($"cannot read the stations file {path}: {e.Message}");
        }
        return Parse(text, path);
    }

    /// <summary>Reads a stations file's <paramref name="text"/>; <paramref name="path"/> is only for the messages.</summary>
    public static StationsFile Parse(string text, string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new FormatException($"the stations file {path} is not valid JSON: {e.Message}");
        }
        using (document)
        {
            var root = document.RootElement;
            string where = $"the stations file {path}";
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"{where} must be a JSON object with \"stations\" and optionally \"report\"");
            }
            string? report = null;
            if (root.TryGetProperty("report", out var r) && r.ValueKind != JsonValueKind.Null)
            {
                report = r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                if (SplitAddress(report) is null)
                {
                    throw new FormatException($"{where}: \"report\" must be an address such as MCAST@HOST.#42.GBR.EURO (a bulletin name, @, the BBS's hierarchical address)");
                }
            }
            var stations = new List<KnownStation>();
            if (root.TryGetProperty("stations", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException($"{where}: \"stations\" must be a list (it may be empty)");
                }
                int n = 0;
                foreach (var entry in list.EnumerateArray())
                {
                    n++;
                    stations.Add(ParseStation(entry, $"{where}, station {n}"));
                }
            }
            else
            {
                throw new FormatException($"{where} has no \"stations\" list (it may be empty)");
            }
            if (stations.GroupBy(s => s.Callsign, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            {
                throw new FormatException($"{where} lists {twice.Key} twice");
            }
            return new StationsFile(stations, report, path);
        }
    }

    private static KnownStation ParseStation(JsonElement e, string where)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"{where} must be an object");
        }
        string call = Text(e, "callsign", where) ?? throw new FormatException($"{where} has no \"callsign\"");
        if (!StationAdvert.IsCallsign(call))
        {
            throw new FormatException($"{where}: \"callsign\" '{Clean(call)}' is not a callsign (upper case, A to Z and 0 to 9, an optional -SSID)");
        }
        where = $"{where} ({call})";
        string keyText = Text(e, "publicKey", where) ?? throw new FormatException($"{where} has no \"publicKey\" (base64)");
        TrustedKey key;
        try
        {
            key = TrustedKey.Parse(call, keyText);
        }
        catch (FormatException x)
        {
            throw new FormatException($"{where}: \"publicKey\": {x.Message}");
        }
        if (!e.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"{where} has no \"slots\": give \"minute\", \"everyMinutes\" and optionally \"daylight\"");
        }
        int minute = Number(slots, "minute", where + " slots") ?? throw new FormatException($"{where}: slots need a \"minute\" (0 to 59)");
        int every = Number(slots, "everyMinutes", where + " slots") ?? throw new FormatException($"{where}: slots need \"everyMinutes\" (it divides a day, 1440)");
        if (minute is < 0 or > 59)
        {
            throw new FormatException($"{where}: slots \"minute\" is 0 to 59");
        }
        if (every < 1 || 1440 % every != 0)
        {
            throw new FormatException($"{where}: slots \"everyMinutes\" must divide a day (1440)");
        }
        DaylightRule? rule = null;
        if (slots.TryGetProperty("daylight", out var d) && d.ValueKind != JsonValueKind.Null)
        {
            if (d.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"{where}: \"daylight\" must be an object, or left out for every slot");
            }
            var settings = new DaylightSettings
            {
                Locator = Text(d, "locator", where + " daylight") ?? throw new FormatException($"{where}: \"daylight\" needs a \"locator\""),
                AfterSunriseMinutes = Number(d, "afterSunriseMinutes", where + " daylight") ?? throw new FormatException($"{where}: \"daylight\" needs \"afterSunriseMinutes\""),
                BeforeSunsetMinutes = Number(d, "beforeSunsetMinutes", where + " daylight") ?? throw new FormatException($"{where}: \"daylight\" needs \"beforeSunsetMinutes\""),
            };
            if (settings.Problem(every, new TimeOnly(0, minute)) is { } problem)
            {
                throw new FormatException($"{where}: \"daylight\": {problem}");
            }
            rule = settings.ToRule();
        }
        long hz = e.TryGetProperty("frequencyHz", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out long v) && v > 0
            ? v : throw new FormatException($"{where} needs \"frequencyHz\": where its signal is centred, in Hz");
        string side = (Text(e, "sideband", where) ?? "usb").ToLowerInvariant();
        if (side is not ("usb" or "lsb"))
        {
            throw new FormatException($"{where}: \"sideband\" is usb or lsb");
        }
        string locator = Text(e, "locator", where) ?? throw new FormatException($"{where} needs a \"locator\": where it transmits from");
        if (!Maidenhead.TryParse(locator, out _, out _))
        {
            throw new FormatException($"{where}: \"locator\" '{Clean(locator)}' is not a 4 or 6 character Maidenhead locator");
        }
        var formerly = new List<string>();
        if (e.TryGetProperty("formerly", out var old) && old.ValueKind != JsonValueKind.Null)
        {
            if (old.ValueKind != JsonValueKind.Array || old.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())))
            {
                throw new FormatException($"{where}: \"formerly\" must be a list of names");
            }
            formerly.AddRange(old.EnumerateArray().Select(x => x.GetString()!.Trim()));
        }
        return new KnownStation(call, key, new SlotTimetable(new TimeOnly(0, minute), every, rule), hz, side == "lsb" ? Sideband.Lsb : Sideband.Usb, locator, formerly);
    }

    private static string? Text(JsonElement e, string name, string where)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()!.Trim()
            : throw new FormatException($"{where}: \"{name}\" must be text");
    }

    private static int? Number(JsonElement e, string name, string where)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v)
            ? v
            : throw new FormatException($"{where}: \"{name}\" must be a whole number");
    }

    private static string Clean(string text) => new([.. text.Select(c => c is >= ' ' and <= '~' ? c : '?')]);
}
