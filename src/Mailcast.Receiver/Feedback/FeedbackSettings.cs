using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mailcast.Receiver.Feedback;

/// <summary>
/// The config's <c>feedback</c>: a short daily report of what this receiver heard, sent once a
/// day through the listener's own BBS as a public bulletin to the address in <see cref="To"/>, where anyone can
/// read it. Off unless asked for; any other key is refused.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed partial record FeedbackSettings
{
    /// <summary>The report's type: a bulletin, public where it is sent. Fixed.</summary>
    public const char Type = 'B';

    /// <summary>
    /// Where the report goes, as <c>BULLETIN@ADDRESS</c>: the bulletin name and the BBS's hierarchical address, so
    /// mail routing carries it there and it stays there, such as <c>MCAST@HOST.#42.GBR.EURO</c>. Null (the default)
    /// is the stations file's report address.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; init; }

    /// <summary>The report's bulletin name and @ field: <see cref="To"/>, else the stations file's report address; null when neither.</summary>
    public (string To, string At)? Address(Packet.Mailcast.StationsFile stations) =>
        Packet.Mailcast.StationsFile.SplitAddress(To ?? stations.Report);

    /// <summary>Whether the report is sent.</summary>
    public bool Enabled { get; init; }

    /// <summary>The listener's callsign, the report's From. Required when <see cref="Enabled"/>.</summary>
    public string? Callsign { get; init; }

    /// <summary>The callsign as the report gives it: upper case, trimmed.</summary>
    [JsonIgnore]
    public string From => (Callsign ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// Whether <paramref name="callsign"/> looks like an amateur callsign that a BBS takes as a
    /// From: 3 to 6 letters and digits, a prefix, a digit, and a suffix ending in a letter, with
    /// no SSID. G4ABC, 2E0ABC and GB7ABC are; G4ABC-1, NOCALL, N0CALL and 1234 are not.
    /// </summary>
    public static bool IsPlausibleCallsign(string? callsign) =>
        callsign is not null && Callsign_().IsMatch(callsign.Trim().ToUpperInvariant())
        && !callsign.Trim().Equals("N0CALL", StringComparison.OrdinalIgnoreCase);

    internal void Validate()
    {
        if (!Enabled)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Callsign))
        {
            throw new ConfigException("\"feedback\" is enabled but has no \"callsign\": give your own callsign, such as \"G4ABC\", which the daily report is sent from");
        }
        if (To is not null && Packet.Mailcast.StationsFile.SplitAddress(To) is null)
        {
            throw new ConfigException($"\"feedback\".\"to\" \"{Ascii.Clean(To)}\" is not an address: give a bulletin name, @, and the BBS's hierarchical address, such as \"MCAST@HOST.#42.GBR.EURO\"");
        }
        if (!IsPlausibleCallsign(Callsign))
        {
            throw new ConfigException($"\"feedback\".\"callsign\" \"{Ascii.Clean(Callsign)}\" does not look like a callsign: give your own, such as \"G4ABC\", with no SSID");
        }
    }

    [GeneratedRegex(@"^(?=.{3,6}$)(?=[A-Z0-9]*[A-Z][A-Z0-9]*[0-9])[A-Z0-9]{1,3}[0-9][A-Z0-9]{0,3}[A-Z]$")]
    private static partial Regex Callsign_();
}
