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
    /// <summary>TCP port of the page. 8130 keeps clear of KiwiSDR's 8073 and of pdn-soundmodem's ports.</summary>
    public int Port { get; init; } = 8130;

    /// <summary>
    /// Whether the page is reachable from the local network as well as from this machine. It has
    /// no login, so it is off unless asked for.
    /// </summary>
    public bool Lan { get; init; }

    /// <summary>
    /// The page's password, which the browser asks for. Required when <see cref="Lan"/> is set,
    /// since anything on the network could otherwise change the settings; optional otherwise.
    /// </summary>
    public string Password { get; init; } = "";
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

    /// <summary>
    /// When the daily slot starts, UTC, as HH:mm. A web SDR is only listened to from 15 minutes
    /// before it to 90 minutes after, because public UberSDR instances allow each address about
    /// three hours a day. A sound card listens all the time.
    /// </summary>
    public string SlotUtc { get; init; } = "12:00";

    /// <summary>How long before the slot a web SDR is opened.</summary>
    public static readonly TimeSpan WebSdrBefore = TimeSpan.FromMinutes(15);

    /// <summary>How long after the slot starts a web SDR is kept open.</summary>
    public static readonly TimeSpan WebSdrAfter = TimeSpan.FromMinutes(90);

    /// <summary>The slot's start, parsed. Throws <see cref="ConfigException"/> for one that is not HH:mm.</summary>
    public TimeOnly SlotStart => TimeOnly.TryParseExact(SlotUtc, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
        ? t
        : throw new ConfigException($"\"slotUtc\" \"{SlotUtc}\" is not a time like 12:00");

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
    /// <remarks>
    /// The file holds the BBS password, so the new one is made readable by its owner and group
    /// only (0640) before anything is written to it, flushed to disk, and then renamed over the old.
    /// </remarks>
    public void Save(string path)
    {
        Validate();
        string tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        }
        using (var stream = new FileStream(tmp, options))
        {
            if (!OperatingSystem.IsWindows())
            {
                // The create mode is masked by the umask; this is the mode it should have.
                File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            }
            stream.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json) + "\n"));
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Throws <see cref="ConfigException"/> if a setting cannot work.</summary>
    public void Validate()
    {
        // JSON can say null for anything; say which setting rather than fail on it later.
        if (Audio is null)
        {
            throw new ConfigException("\"audio\" is null: give an ALSA device, a ubersdr: web receiver or a wav: recording");
        }
        if (Bbs is null || Bbs.Host is null || Bbs.Login is null || Bbs.Password is null || Bbs.Command is null)
        {
            throw new ConfigException("\"bbs\" or one of its settings is null: give host, port, login, password and command");
        }
        if (Web is null || Web.Password is null)
        {
            throw new ConfigException("\"web\" or its password is null");
        }
        if (StateDirectory is null || SlotUtc is null)
        {
            throw new ConfigException("\"stateDirectory\" or \"slotUtc\" is null");
        }
        _ = AudioSource.Parse(Audio);
        _ = SlotStart;
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
        if (Web.Lan && Web.Password.Length == 0)
        {
            throw new ConfigException("\"web\".\"lan\" is on but \"web\".\"password\" is empty: the page can change the BBS settings, so on the network it needs a password");
        }
        if (string.IsNullOrWhiteSpace(StateDirectory))
        {
            throw new ConfigException("\"stateDirectory\" is empty");
        }
    }
}

/// <summary>A configuration that cannot work, with a sentence saying why.</summary>
public sealed class ConfigException(string message) : Exception(message);
