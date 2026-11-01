using System.Globalization;
using Packet.Mailcast;

namespace Mailcast.Receiver;

/// <summary>
/// One station the operator asked this receiver to listen for by hand (issue #224): a hand-entered
/// advert, shaped like a head end's <c>advertise</c> entry. It is treated exactly like an advert
/// heard on air until the station's own signed directory is heard, or <see cref="Lifetime"/> passes.
/// </summary>
public sealed record ListenForEntry
{
    /// <summary>How long an entry that is never heard stays before it is removed.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Its callsign, as its own directories name it. Required.</summary>
    public string Callsign { get; init; } = "";

    /// <summary>The minute past the hour its slots start at, 0 to 59.</summary>
    public int Minute { get; init; }

    /// <summary>Minutes between its slots; it divides a day. Default 60.</summary>
    public int EveryMinutes { get; init; } = 60;

    /// <summary>Where its signal is centred, in Hz. Required.</summary>
    public long FrequencyHz { get; init; }

    /// <summary><c>usb</c> or <c>lsb</c>.</summary>
    public string Sideband { get; init; } = "usb";

    /// <summary>Where it transmits from, a Maidenhead locator. Optional.</summary>
    public string? Locator { get; init; }

    /// <summary>Its daylight rule, or null when every slot runs.</summary>
    public DaylightSettings? Daylight { get; init; }

    /// <summary>What is wrong with the entry, in a sentence, or null. The same checks as a head end's <c>advertise</c> entry.</summary>
    public string? Problem()
    {
        if (!StationAdvert.IsCallsign(Callsign))
        {
            return $"\"callsign\" '{Ascii.Clean(Callsign)}' is not a callsign (upper case, A to Z and 0 to 9, an optional -SSID)";
        }
        if (FrequencyHz < 1)
        {
            return "\"frequencyHz\" is required: where the station's signal is centred, in Hz";
        }
        if (!Sideband.Equals("usb", StringComparison.OrdinalIgnoreCase) && !Sideband.Equals("lsb", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"sideband\" is usb or lsb, not '{Ascii.Clean(Sideband)}'";
        }
        if (Locator is not null && !Maidenhead.TryParse(Locator, out _, out _))
        {
            return $"\"locator\" '{Ascii.Clean(Locator)}' is not a 4 or 6 character Maidenhead locator, such as IO91lk";
        }
        if (Minute is < 0 or > 59)
        {
            return "\"minute\" is 0 to 59";
        }
        if (EveryMinutes < 1 || 1440 % EveryMinutes != 0)
        {
            return "\"everyMinutes\" must divide a day (1440)";
        }
        return Daylight?.Problem(EveryMinutes, new TimeOnly(0, Minute)) is { } daylight ? $"\"daylight\": {daylight}" : null;
    }

    /// <summary>The entry as the advert it stands for. Call only when <see cref="Problem"/> is null.</summary>
    public StationAdvert ToAdvert() => new(Callsign, new SlotTimetable(new TimeOnly(0, Minute), EveryMinutes, Daylight?.ToRule()),
        FrequencyHz, Sideband.Equals("lsb", StringComparison.OrdinalIgnoreCase) ? Packet.Mailcast.Sideband.Lsb : Packet.Mailcast.Sideband.Usb, Locator);

    /// <summary>Whether <paramref name="other"/> is the same station on the same frequency and sideband.</summary>
    public bool SamePlaceAs(ListenForEntry other) =>
        Callsign == other.Callsign && FrequencyHz == other.FrequencyHz && Sideband.Equals(other.Sideband, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first problem among <paramref name="entries"/>, worded for the config file, or null.</summary>
    public static string? FirstProblem(IReadOnlyList<ListenForEntry>? entries)
    {
        for (int i = 0; i < (entries?.Count ?? 0); i++)
        {
            if (entries![i] is null)
            {
                return $"\"listenFor\" entry {i + 1} is null";
            }
            if (entries[i].Problem() is { } problem)
            {
                return $"\"listenFor\" entry {i + 1}: {problem}";
            }
        }
        if (entries?.GroupBy(e => (e.Callsign, e.FrequencyHz, e.Sideband.ToLowerInvariant())).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return string.Create(CultureInfo.InvariantCulture, $"\"listenFor\" lists {twice.Key.Callsign} on {twice.Key.FrequencyHz} Hz twice");
        }
        return null;
    }
}
