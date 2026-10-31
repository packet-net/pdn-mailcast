using Packet.Mailcast.Discovery;

namespace Mailcast.Receiver.Discovery;

/// <summary>
/// Announces the receiver's status page and API on the local network as <c>_pdn-mailcast._tcp</c> (multicast DNS), so a
/// head end finds it with no setting (issue #218). Not announced at all when the page is only on this machine, because a head
/// end elsewhere could not use it; a head end on this machine looks on the usual port without being told.
/// </summary>
public sealed class ReceiverAnnouncement : IDisposable
{
    private readonly MdnsAnnouncer _announcer;

    private ReceiverAnnouncement(MdnsAnnouncer announcer) => _announcer = announcer;

    /// <summary>The instance name this receiver gives: its daily-report callsign if it has one, then the computer's name.</summary>
    public static string InstanceName(ReceiverConfig config)
    {
        string host = ReceiverService.Label(Environment.MachineName);
        return config.Feedback?.Callsign is { } call && Feedback.FeedbackSettings.IsPlausibleCallsign(call)
            ? ReceiverService.Label(call.Trim().ToUpperInvariant() + " on " + host)
            : host;
    }

    /// <summary>The TXT strings for this configuration.</summary>
    public static IReadOnlyList<string> Text(ReceiverConfig config, string version) =>
        ReceiverService.Text(version, config.Web.Password.Length > 0);

    /// <summary>
    /// Starts announcing, or returns null having said why in one line (the page is not on the network, or multicast does
    /// not work here). Never throws.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="pageOnLan">Whether the page is really being served to the network (the setting, and the system letting it).</param>
    /// <param name="version">This receiver's version.</param>
    /// <param name="transport">The network side; the caller gives it up to the announcement.</param>
    /// <param name="log">One plain line at a time.</param>
    public static ReceiverAnnouncement? Start(ReceiverConfig config, bool pageOnLan, string version, IMdnsTransport transport, Action<string> log)
    {
        if (!pageOnLan)
        {
            transport.Dispose();
            log("discovery: not announcing this receiver on the network, because its page is only on this machine; a head end here finds it by itself");
            return null;
        }
        var announcer = new MdnsAnnouncer(transport, InstanceName(config), config.Web.Port, Text(config, version), log);
        if (!announcer.Start())
        {
            announcer.Dispose();
            return null;
        }
        log($"discovery: announcing this receiver on the network as \"{announcer.InstanceName}\" (_pdn-mailcast._tcp, port {config.Web.Port})");
        return new ReceiverAnnouncement(announcer);
    }

    /// <inheritdoc />
    public void Dispose() => _announcer.Dispose();
}
