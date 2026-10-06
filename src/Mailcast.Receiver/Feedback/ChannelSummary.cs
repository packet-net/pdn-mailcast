using Packet.Mailcast;
using Packet.Mailcast.Feedback;

namespace Mailcast.Receiver.Feedback;

/// <summary>The Channel tile's measurements and place, as the daily report gives them.</summary>
internal static class ChannelSummary
{
    /// <summary>
    /// A slot's channel measurement for its line: the modes, the 2F mode against the first (or,
    /// with no hops named, the second mode), the spreads, the virtual height and the basis. Null
    /// when there is none, or too little was decoded to measure.
    /// </summary>
    public static ReportChannel? From(ChannelReport? report)
    {
        if (report is not { Enough: true } || report.Modes.Count == 0)
        {
            return null;
        }
        var first = report.Modes[0];
        var twoF = report.Modes.Any(m => m.Label is not null)
            ? report.Modes.FirstOrDefault(m => m.Label == "2F")
            : report.Modes.Skip(1).FirstOrDefault();
        char basis = report.Basis switch
        {
            "bursts" => 'b',
            "probe" => 'p',
            { Length: > 0 } other when char.IsAsciiLetter(other[0]) => char.ToLowerInvariant(other[0]),
            _ => '-',
        };
        return new ReportChannel(
            report.Modes.Count,
            twoF is null ? null : twoF.DelayMs - first.DelayMs,
            twoF is null ? null : twoF.PowerDb - first.PowerDb,
            report.DelaySpreadMs,
            report.DopplerSpreadHz,
            report.VirtualHeightKm,
            basis);
    }

    /// <summary>
    /// The receiver's locator for the header, from where the web SDR says it is: its own locator
    /// if it gave one, or else its position as 6 characters. Null with no place.
    /// </summary>
    public static string? Locator(GroundPlace? place) => place switch
    {
        null => null,
        { Locator: { } given } when Maidenhead.TryParse(given, out _, out _) => given,
        _ => Maidenhead.Format(place.Latitude, place.Longitude),
    };
}
