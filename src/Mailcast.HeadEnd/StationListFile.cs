using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.Mailcast;

namespace Mailcast.HeadEnd;

/// <summary>
/// The station list as Tom writes it before signing: the JSON file <c>--sign-list</c> reads.
/// </summary>
public sealed record StationListSource
{
    /// <summary>Which list this is: a higher serial is a newer list. YYYYMMDDNN works well.</summary>
    public long Serial { get; init; }

    /// <summary>The stations, in the order the list gives them.</summary>
    public IReadOnlyList<StationSource> Stations { get; init; } = [];
}

/// <summary>One station in <see cref="StationListSource"/>.</summary>
public sealed record StationSource
{
    /// <summary>The callsign, upper case.</summary>
    public string Callsign { get; init; } = "";

    /// <summary>The station's Maidenhead locator.</summary>
    public string Locator { get; init; } = "";

    /// <summary>Where the signal's centre is on the air, in Hz.</summary>
    public long FrequencyHz { get; init; }

    /// <summary><c>usb</c> or <c>lsb</c>.</summary>
    public string Sideband { get; init; } = "usb";

    /// <summary>Its slots, as in a head end's own configuration.</summary>
    public StationSlotSource Slot { get; init; } = new();

    /// <summary>The waveforms it sends on, such as <c>ms110d-wn4</c>.</summary>
    public IReadOnlyList<string> Modes { get; init; } = [];

    /// <summary>The station's directory key: its head end's <c>--check-config</c> prints it.</summary>
    public string PublicKey { get; init; } = "";
}

/// <summary>A station's slots in <see cref="StationSource"/>, with the head end configuration's names.</summary>
public sealed record StationSlotSource
{
    /// <summary>One slot's start, HH:mm UTC.</summary>
    public string TimeUtc { get; init; } = "";

    /// <summary>Minutes between slots.</summary>
    public int EveryMinutes { get; init; } = 60;

    /// <summary>The daylight rule, or null when every slot runs.</summary>
    public HeadEndDaylight? Daylight { get; init; }
}

/// <summary>Signing a station list, and the signed list a head end carries.</summary>
public static class StationListFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Signs the list in <paramref name="json"/> with the list key. Throws
    /// <see cref="FormatException"/>, saying what is wrong in plain words, if the file is not a
    /// list that can be signed.
    /// </summary>
    public static StationList Sign(string json, DirectorySigner listKey)
    {
        ArgumentNullException.ThrowIfNull(listKey);
        StationListSource source;
        try
        {
            source = JsonSerializer.Deserialize<StationListSource>(json, Options) ?? new StationListSource();
        }
        catch (JsonException e)
        {
            throw new FormatException(e.Message, e);
        }
        if (source.Serial < 1)
        {
            throw new FormatException("\"serial\" must be a whole number from 1 up, higher than the last list's (YYYYMMDDNN works well)");
        }
        if (source.Stations.Count == 0)
        {
            throw new FormatException("\"stations\" is empty: list at least one");
        }
        var stations = new List<StationEntry>();
        foreach (var s in source.Stations)
        {
            stations.Add(ToEntry(s));
        }
        try
        {
            return StationList.Sign(source.Serial, stations, listKey);
        }
        catch (ArgumentException e)
        {
            throw new FormatException(e.Message.Split(" (Parameter")[0], e);
        }
    }

    private static StationEntry ToEntry(StationSource s)
    {
        string name = string.IsNullOrWhiteSpace(s.Callsign) ? "a station" : s.Callsign;
        if (!TimeOnly.TryParseExact(s.Slot.TimeUtc, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor))
        {
            throw new FormatException($"{name}: \"slot\".\"timeUtc\" '{s.Slot.TimeUtc}' is not HH:mm");
        }
        if (s.Slot.EveryMinutes < 1 || 1440 % s.Slot.EveryMinutes != 0)
        {
            throw new FormatException($"{name}: \"slot\".\"everyMinutes\" must divide a day");
        }
        DaylightRule? daylight = null;
        if (s.Slot.Daylight is { } d)
        {
            var settings = d.ToSettings();
            if (settings.Problem(s.Slot.EveryMinutes, anchor) is { } problem)
            {
                throw new FormatException($"{name}: \"slot\".\"daylight\": {problem}");
            }
            daylight = settings.ToRule();
        }
        Sideband sideband = s.Sideband.ToLowerInvariant() switch
        {
            "usb" => Sideband.Usb,
            "lsb" => Sideband.Lsb,
            _ => throw new FormatException($"{name}: \"sideband\" is usb or lsb, not '{s.Sideband}'"),
        };
        TrustedKey key;
        try
        {
            key = TrustedKey.Parse(name, s.PublicKey);
        }
        catch (FormatException e)
        {
            throw new FormatException($"{name}: \"publicKey\": {e.Message}", e);
        }
        var entry = new StationEntry(s.Callsign, s.Locator, s.FrequencyHz, sideband, new SlotTimetable(anchor, s.Slot.EveryMinutes, daylight), [.. s.Modes], key);
        if (StationList.Problem(entry) is { } wrong)
        {
            throw new FormatException(wrong);
        }
        return entry;
    }

    /// <summary>
    /// The signed list at <paramref name="path"/> if it is there and checks against
    /// <paramref name="listKeys"/>, with one line saying what was found. Null when there is no
    /// file, it cannot be read, or its signature does not check: the head end then sends no list,
    /// and the line says why.
    /// </summary>
    public static (StationList? List, string Line) Load(string path, IReadOnlyList<TrustedKey> listKeys)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(listKeys);
        if (!File.Exists(path))
        {
            return (null, $"station list: none at {path}, so none is sent");
        }
        StationList list;
        try
        {
            list = StationList.Parse(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return (null, $"station list: {path} cannot be read, so none is sent: {Ascii.Plain(e.Message)}");
        }
        var check = list.Check(listKeys);
        if (check.State != SignatureState.Trusted)
        {
            return (null, $"station list: {path} ({list.Describe()}) is not sent: {check.Reason}");
        }
        var skipped = list.Skipped.Count == 0 ? "" : $"; skips {string.Join("; ", list.Skipped)}";
        return (list, $"station list: sends {path}, {list.Describe()}, signed by the station list key{skipped}");
    }
}
