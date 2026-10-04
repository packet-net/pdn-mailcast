using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailcast.Receiver;

/// <summary>Which kind of BBS the receiver delivers into; it decides how the receiver logs in.</summary>
public enum BbsKind
{
    /// <summary>
    /// LinBPQ's mail (BPQMail), reached on the Telnet port's FBBPORT. LinBPQ sends no prompts
    /// there: the receiver sends the user name, the password and the BBS application command.
    /// </summary>
    LinBpq,

    /// <summary>
    /// Linux FBB (xfbbd) on a TCP port in port.sys. FBB prompts "Callsign :" and "Password :";
    /// the receiver answers with a dot in front of the callsign, which is FBB's way of asking for
    /// a binary session with no telnet processing.
    /// </summary>
    Fbb,
}

/// <summary>Where the receiver hands bulletins over.</summary>
public sealed record BbsSettings
{
    /// <summary>The kind of BBS.</summary>
    public BbsKind Type { get; init; } = BbsKind.LinBpq;

    /// <summary>Host name or address of the BBS.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>TCP port: LinBPQ's FBBPORT, or FBB's TCP port.</summary>
    public int Port { get; init; } = 8011;

    /// <summary>
    /// The login. For LinBPQ this is the user name of a USER= line in the Telnet port; for FBB it
    /// is the callsign itself.
    /// </summary>
    public string Login { get; init; } = "Q0CAST";

    /// <summary>The password for the login.</summary>
    public string Password { get; init; } = "";

    /// <summary>LinBPQ only: the node command that reaches the mail application, normally BBS.</summary>
    public string Command { get; init; } = "BBS";
}

/// <summary>The local web page.</summary>
public sealed record WebSettings
{
    /// <summary>TCP port of the page.</summary>
    public int Port { get; init; } = 8073;

    /// <summary>
    /// Whether the page is reachable from the local network as well as from this machine. It has
    /// no login, so it is off unless asked for.
    /// </summary>
    public bool Lan { get; init; }
}

/// <summary>
/// The receiver's configuration file. Only what a station has to choose is here; the broadcast's
/// own details (callsigns, frequencies, the modem) are fixed in <see cref="OnAir"/>.
/// </summary>
public sealed record ReceiverConfig
{
    /// <summary>
    /// Where the audio comes from: an ALSA device such as <c>plughw:CARD=Device,DEV=0</c>, an
    /// UberSDR web receiver as <c>ubersdr:wessex.zapto.org</c>, or a recording as
    /// <c>wav:/path/to/file.wav</c>.
    /// </summary>
    public string Audio { get; init; } = "ubersdr:wessex.zapto.org";

    /// <summary>The BBS the bulletins go to.</summary>
    public BbsSettings Bbs { get; init; } = new();

    /// <summary>The local web page.</summary>
    public WebSettings Web { get; init; } = new();

    /// <summary>Where the pieces heard, the rebuilt bulletins and the delivery record are kept.</summary>
    public string StateDirectory { get; init; } = "/var/lib/pdn-mailcast";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    /// <summary>Reads a config file. Throws <see cref="ConfigException"/> with a sentence for the operator.</summary>
    public static ReceiverConfig Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"cannot read the config file {path}: {e.Message}");
        }

        ReceiverConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ReceiverConfig>(text, Json);
        }
        catch (JsonException e)
        {
            throw new ConfigException($"the config file {path} is not valid JSON: {e.Message}");
        }

        config ??= new ReceiverConfig();
        config.Validate();
        return config;
    }

    /// <summary>Writes the config file, to a temporary name first so a crash leaves the old one whole.</summary>
    public void Save(string path)
    {
        Validate();
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json) + "\n");
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Throws <see cref="ConfigException"/> if a setting cannot work.</summary>
    public void Validate()
    {
        _ = AudioSource.Parse(Audio);
        if (string.IsNullOrWhiteSpace(Bbs.Host))
        {
            throw new ConfigException("\"bbs\".\"host\" is empty: give the BBS's address, normally 127.0.0.1");
        }
        if (Bbs.Port is < 1 or > 65535)
        {
            throw new ConfigException($"\"bbs\".\"port\" {Bbs.Port} is not a TCP port");
        }
        if (string.IsNullOrWhiteSpace(Bbs.Login) || Bbs.Login.Any(c => c is ' ' or '\r' or '\n'))
        {
            throw new ConfigException("\"bbs\".\"login\" must be one word, such as Q0CAST");
        }
        if (Bbs.Password.Any(c => c is '\r' or '\n') || Bbs.Command.Any(c => c is '\r' or '\n'))
        {
            throw new ConfigException("\"bbs\".\"password\" and \"bbs\".\"command\" must be one line");
        }
        if (Web.Port is < 1 or > 65535)
        {
            throw new ConfigException($"\"web\".\"port\" {Web.Port} is not a TCP port");
        }
        if (string.IsNullOrWhiteSpace(StateDirectory))
        {
            throw new ConfigException("\"stateDirectory\" is empty");
        }
    }
}

/// <summary>A configuration that cannot work, with a sentence saying why.</summary>
public sealed class ConfigException(string message) : Exception(message);
