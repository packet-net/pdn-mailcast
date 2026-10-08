using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mailcast.Receiver.Feedback;

/// <summary>
/// The config's <c>feedback</c>: a short daily report of what this receiver heard, sent once a
/// day through the listener's own BBS as a public bulletin to MCAST at GB7RDG, where anyone can
/// read it. Off unless asked for; any other key is refused.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed partial record FeedbackSettings
{
    /// <summary>The report's type: a bulletin, public on GB7RDG. Fixed.</summary>
    public const char Type = 'B';

    /// <summary>Who the report is addressed to. Fixed.</summary>
    public const string To = Packet.Mailcast.Feedback.DailyReport.BulletinTo;

    /// <summary>The report's @ field, GB7RDG's full address, so it is routed to GB7RDG and stays there. Fixed.</summary>
    public const string At = Packet.Mailcast.Feedback.DailyReport.BulletinAt;

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
    /// no SSID. G4ABC, 2E0ABC and GB7RDG are; G4ABC-1, NOCALL, N0CALL and 1234 are not.
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
        if (!IsPlausibleCallsign(Callsign))
        {
            throw new ConfigException($"\"feedback\".\"callsign\" \"{Ascii.Clean(Callsign)}\" does not look like a callsign: give your own, such as \"G4ABC\", with no SSID");
        }
    }

    [GeneratedRegex(@"^(?=.{3,6}$)(?=[A-Z0-9]*[A-Z][A-Z0-9]*[0-9])[A-Z0-9]{1,3}[0-9][A-Z0-9]{0,3}[A-Z]$")]
    private static partial Regex Callsign_();
}
