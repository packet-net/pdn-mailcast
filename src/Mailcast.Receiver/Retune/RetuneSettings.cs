using System.Text.Json.Serialization;
using Packet.SoundModem.Rig;

namespace Mailcast.Receiver.Retune;

/// <summary>
/// The config's <c>rig</c>: a radio shared with packet, retuned through Hamlib's rigctld to the
/// bulletin frequency for each slot and put back afterwards.
/// </summary>
public sealed record RigSettings
{
    /// <summary>Where rigctld listens, <c>host:port</c>. flrig users run <c>rigctld -m 4</c> beside flrig.</summary>
    public string Rigctld { get; init; } = RigctldEndpoint.Default;

    /// <summary>
    /// True when no other program ever transmits on this radio, so the receiver may retune it
    /// without holding LinBPQ off the air (<see cref="ReceiverConfig.Bpq"/>).
    /// </summary>
    public bool DedicatedRadio { get; init; }

    /// <summary>The rigctld setting, parsed. Throws <see cref="ConfigException"/> for one that cannot work.</summary>
    [JsonIgnore]
    public RigctldEndpoint Endpoint => RigctldEndpoint.TryParse(Rigctld, out string why) ?? throw new ConfigException($"\"rig\".\"rigctld\" \"{Rigctld}\" is not host:port ({why}); rigctld's own is 127.0.0.1:4532");

    internal void Validate()
    {
        if (Rigctld is null)
        {
            throw new ConfigException("\"rig\".\"rigctld\" is null: give rigctld's host:port, normally 127.0.0.1:4532");
        }
        _ = Endpoint;
    }
}

/// <summary>
/// The config's <c>bpq</c>: LinBPQ's node telnet port and a SYSOP user, with which the receiver
/// turns transmit off on <see cref="HfPort"/> (<c>XMITOFF</c>) while the radio is on the bulletin
/// frequency.
/// </summary>
public sealed record BpqNodeSettings
{
    /// <summary>LinBPQ's host.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>The Telnet port's TCPPORT, the node's own telnet port (not the FBBPORT).</summary>
    public int Port { get; init; } = 8010;

    /// <summary>The user name of a <c>USER=</c> line in the Telnet port with SYSOP as its fifth field.</summary>
    public string User { get; init; } = "";

    /// <summary>That user's password.</summary>
    public string Password { get; init; } = "";

    /// <summary>The number of the LinBPQ port on the shared radio, as in its <c>PORTNUM=</c>.</summary>
    public int HfPort { get; init; }

    /// <summary>The longest <see cref="DrainSeconds"/> accepted.</summary>
    public const int MostDrainSeconds = 120;

    /// <summary>
    /// How long to wait after XMITOFF before tuning, in seconds, for frames LinBPQ had already
    /// handed to the TNC to go out: XMITOFF only stops LinBPQ's own queue.
    /// </summary>
    public int DrainSeconds { get; init; } = 15;

    /// <summary>
    /// If set, the ID LinBPQ's PORTS command must give <see cref="HfPort"/>, as a check that the
    /// number is the port on the shared radio; nothing is retuned if it is not.
    /// </summary>
    public string? ExpectedPortId { get; init; }

    /// <summary>Where it is, for the log and the page: never the password.</summary>
    public override string ToString() => $"LinBPQ's node at {Host}:{Port} as {User}, port {HfPort}";

    internal void Validate()
    {
        if (Host is null || User is null || Password is null)
        {
            throw new ConfigException("\"bpq\" or one of its settings is null: give host, port, user, password and hfPort");
        }
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ConfigException("\"bpq\".\"host\" is empty: give LinBPQ's address, normally 127.0.0.1");
        }
        if (Port is < 1 or > 65535)
        {
            throw new ConfigException($"\"bpq\".\"port\" {Port} is not a TCP port; it is the TCPPORT in LinBPQ's Telnet port, often 8010");
        }
        if (string.IsNullOrWhiteSpace(User) || User.Any(c => c is ' ' or '\r' or '\n'))
        {
            throw new ConfigException("\"bpq\".\"user\" must be one word: the user name of a USER= line in LinBPQ's Telnet port that ends in SYSOP");
        }
        if (Password.Length == 0 || Password.Any(c => c is '\r' or '\n'))
        {
            throw new ConfigException("\"bpq\".\"password\" must be that user's password, on one line");
        }
        if (DrainSeconds is < 0 or > MostDrainSeconds)
        {
            throw new ConfigException($"\"bpq\".\"drainSeconds\" {DrainSeconds} must be from 0 to {MostDrainSeconds}; the usual 15 lets frames already with the TNC go out before the radio is retuned");
        }
        if (ExpectedPortId is { } id && (id.Trim().Length == 0 || id.Any(c => c is '\r' or '\n')))
        {
            throw new ConfigException("\"bpq\".\"expectedPortId\" must be the port's ID as LinBPQ's PORTS lists it, on one line, or left out");
        }
        if (HfPort is < 1 or > 64)
        {
            throw new ConfigException($"\"bpq\".\"hfPort\" {HfPort} is not a LinBPQ port number: give the PORTNUM of the port on the shared radio (1 to 64)");
        }
    }
}
