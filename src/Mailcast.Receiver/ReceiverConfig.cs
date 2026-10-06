using System.Text.Json;
using System.Text.Json.Serialization;
using Mailcast.Receiver.Hooks;
using Mailcast.Receiver.Retune;
using Packet.Mailcast;

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
    /// Whether the page is reachable from the local network as well as from this machine. Off
    /// unless asked for, and it needs <see cref="Password"/>.
    /// </summary>
    public bool Lan { get; init; }

    /// <summary>
    /// The page's password, asked for on its sign-in page (or given by a script with HTTP Basic).
    /// Required when <see cref="Lan"/> is set, since anything on the network could otherwise
    /// change the settings; optional otherwise.
    /// </summary>
    public string Password { get; init; } = "";
}

/// <summary>The receiver's own copies of bulletins the BBS has answered for.</summary>
public sealed record ArchiveSettings
{
    /// <summary>The longest <see cref="Days"/> can be: ten years.</summary>
    public const int MostDays = 3650;

    /// <summary>How many days a bulletin is kept after the BBS answered for it. 0 keeps none.</summary>
    public int Days { get; init; } = 30;

    /// <summary>The most the copies may add up to, in megabytes (MiB); past it the oldest go first. 0 keeps none.</summary>
    public int MaxMegabytes { get; init; } = 50;
}

/// <summary>
/// The receiver's configuration file. Only what a station has to choose is here; the broadcast's
/// own details (callsigns, the modem, where the signal sits above the dial) are fixed in
/// <see cref="OnAir"/>.
/// </summary>
public sealed record ReceiverConfig
{
    /// <summary>
    /// Where the audio comes from: an ALSA device such as <c>plughw:CARD=Device,DEV=0</c>, an
    /// UberSDR web receiver as <c>ubersdr:wessex.zapto.org</c>, or a recording as
    /// <c>wav:/path/to/file.wav</c>.
    /// </summary>
    public string Audio { get; init; } = "ubersdr:wessex.zapto.org";

    /// <summary>The usual USB dial, in kHz: 7.052 MHz, which puts the signal's centre on 7.0538 MHz.</summary>
    public const double DefaultDialKHz = 7052.0;

    /// <summary>The lowest dial accepted, in kHz: the bottom of 160 m.</summary>
    public const double LowestDialKHz = 1800;

    /// <summary>The highest dial accepted, in kHz: the top of HF.</summary>
    public const double HighestDialKHz = 30000;

    /// <summary>
    /// The USB dial, in kHz. A web SDR is tuned here; a radio on a sound card should be set here.
    /// The signal's centre is always <see cref="OnAir.CentreAudioHz"/> above it.
    /// </summary>
    public double DialKHz { get; init; } = DefaultDialKHz;

    /// <summary>The USB dial in Hz.</summary>
    [JsonIgnore]
    public double DialHz => DialKHz * 1000;

    /// <summary>The signal's centre in Hz: the dial plus <see cref="OnAir.CentreAudioHz"/>.</summary>
    [JsonIgnore]
    public double CentreHz => DialHz + OnAir.CentreAudioHz;

    /// <summary>The BBS the bulletins go to.</summary>
    public BbsSettings Bbs { get; init; } = new();

    /// <summary>The local web page.</summary>
    public WebSettings Web { get; init; } = new();

    /// <summary>How long the receiver keeps its own copy of each bulletin once the BBS has answered for it.</summary>
    public ArchiveSettings Archive { get; init; } = new();

    /// <summary>
    /// When GB7RDG's slots are, UTC, as HH:mm: one slot starts here and then every
    /// <see cref="EveryMinutes"/> round the clock. GB7RDG sends every hour on the hour, so
    /// "00:00" and 60.
    /// </summary>
    public string SlotUtc { get; init; } = "00:00";

    /// <summary>
    /// Minutes from one slot's start to the next: it divides a day (1440) and is at least
    /// <see cref="ShortestEveryMinutes"/>. A config from before hourly slots has only
    /// <see cref="SlotUtc"/> (12:00), and gets the default 60: the same hourly slots.
    /// </summary>
    public int EveryMinutes { get; init; } = 60;

    /// <summary>The shortest gap between slots accepted, as the head end does.</summary>
    public const int ShortestEveryMinutes = 15;

    /// <summary>
    /// How many slots a day a web SDR is listened to, spread evenly through the day (8 of 24
    /// hourly slots is every 3 hours). Public UberSDR receivers allow each address about three
    /// hours a day, and each slot listened to takes <see cref="WebSdrMinutesPerSlot"/>, so
    /// <see cref="MostWebSdrSlotsPerDay"/> is the most. A sound card listens to every slot.
    /// </summary>
    public int WebSdrSlotsPerDay { get; init; } = 8;

    /// <summary>How long before a slot a web SDR is opened.</summary>
    public static readonly TimeSpan WebSdrBefore = TimeSpan.FromMinutes(2);

    /// <summary>How long after a slot starts a web SDR is kept open.</summary>
    public static readonly TimeSpan WebSdrAfter = TimeSpan.FromMinutes(12);

    /// <summary>A web SDR's listening allowance, in minutes a day: public UberSDR allows each address about three hours.</summary>
    public const int WebSdrAllowanceMinutes = 180;

    /// <summary>Minutes of the allowance one slot uses.</summary>
    public static int WebSdrMinutesPerSlot => (int)(WebSdrBefore + WebSdrAfter).TotalMinutes;

    /// <summary>The most slots a day a web SDR can be listened to inside its allowance.</summary>
    public static int MostWebSdrSlotsPerDay => WebSdrAllowanceMinutes / WebSdrMinutesPerSlot;

    /// <summary>
    /// Set when the file had <c>slotUtc</c> but no <c>everyMinutes</c>, as before hourly slots;
    /// it is read as hourly from that time, and the receiver says so in its log.
    /// </summary>
    [JsonIgnore]
    public bool SlotUtcWithoutEveryMinutes { get; init; }

    /// <summary>The first slot's start, parsed. Throws <see cref="ConfigException"/> for one that is not HH:mm.</summary>
    [JsonIgnore]
    public TimeOnly SlotStart => TimeOnly.TryParseExact(SlotUtc, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
        ? t
        : throw new ConfigException($"\"slotUtc\" \"{SlotUtc}\" is not a time like 00:00");

    /// <summary>
    /// GB7RDG's daylight hours: its slots run only from <see cref="DaylightSettings.AfterSunriseMinutes"/>
    /// after sunrise to <see cref="DaylightSettings.BeforeSunsetMinutes"/> before sunset at
    /// <see cref="DaylightSettings.Locator"/>. Left out, GB7RDG's own (IO91lk, 120, 30); null for
    /// every slot. A directory heard from the head end that gives its slots is used instead.
    /// </summary>
    public DaylightSettings? Daylight { get; init; } = new();

    /// <summary>GB7RDG's slots, as this file gives them.</summary>
    [JsonIgnore]
    public SlotSchedule Schedule => new(SlotStart, EveryMinutes, Daylight?.ToRule());

    /// <summary>The slots a web SDR would listen to without a daylight rule, by time of day, earliest first.</summary>
    [JsonIgnore]
    public IReadOnlyList<TimeOnly> WebSdrSlots => ListeningWindow.WebSdrSlots(Schedule, WebSdrSlotsPerDay);

    /// <summary>Where the pieces heard, the rebuilt bulletins and the delivery record are kept.</summary>
    public string StateDirectory { get; init; } = "/var/lib/pdn-mailcast";

    /// <summary>
    /// The radio's rigctld, for a radio shared with packet: the receiver tunes it to
    /// <see cref="DialKHz"/> for each slot and puts it back afterwards. Null (the default) leaves
    /// the radio alone.
    /// </summary>
    public RigSettings? Rig { get; init; }

    /// <summary>
    /// LinBPQ's node telnet port, so the receiver can stop LinBPQ transmitting on the radio while
    /// it is on the bulletin frequency. Needed with <see cref="Rig"/> unless the radio is
    /// <see cref="RigSettings.DedicatedRadio"/>.
    /// </summary>
    public BpqNodeSettings? Bpq { get; init; }

    /// <summary>
    /// Programs to run before and after each slot the receiver listens to, for a radio shared
    /// with something else. Null (the default) runs nothing.
    /// </summary>
    public HooksSettings? Hooks { get; init; }

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
        catch (JsonException e) when (e.Path?.StartsWith("$.hooks", StringComparison.Ordinal) == true)
        {
            if (System.Text.RegularExpressions.Regex.Match(e.Message, "property '([^']*)' could not be mapped") is { Success: true } unknown)
            {
                // The path may or may not end with the unknown key itself: "$.hooks.befor", or "$.hooks.before(.timeout)".
                string name = unknown.Groups[1].Value;
                string[] parts = e.Path.Split('.');
                bool inHook = parts.Length > 3 || (parts.Length == 3 && parts[2] != name);
                throw new ConfigException(inHook
                    ? $"the config file {path}: \"{name}\" is not a setting of a hook (at {e.Path}): a hook has \"command\", \"args\" and \"timeoutSeconds\""
                    : $"the config file {path}: \"{name}\" is not a setting of \"hooks\": it has \"before\" and \"after\"");
            }
            throw new ConfigException(
                $"the config file {path} has a \"hooks\" setting that cannot be read (at {e.Path}): give each hook as "
                + "{ \"command\": \"/full/path/to/program\", \"args\": [\"a list\", \"of strings\"], \"timeoutSeconds\": 30 }");
        }
        catch (JsonException e)
        {
            throw new ConfigException($"the config file {path} is not valid JSON: {e.Message}");
        }

        config ??= new ReceiverConfig();
        if (HasOnlySlotUtc(text))
        {
            config = config with { SlotUtcWithoutEveryMinutes = true };
        }
        config.Validate();
        return config;
    }

    /// <summary>Whether a config file gives <c>slotUtc</c> but not <c>everyMinutes</c>, as one from before hourly slots does.</summary>
    private static bool HasOnlySlotUtc(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("slotUtc", out _)
            && !document.RootElement.TryGetProperty("everyMinutes", out _);
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
        if (Archive is null)
        {
            throw new ConfigException("\"archive\" is null: leave it out for the defaults, or give days and maxMegabytes");
        }
        if (Archive.Days is < 0 or > ArchiveSettings.MostDays || Archive.MaxMegabytes < 0)
        {
            throw new ConfigException($"\"archive\": \"days\" is from 0 to {ArchiveSettings.MostDays} and \"maxMegabytes\" cannot be negative; 0 for either keeps no copies");
        }
        if (StateDirectory is null || SlotUtc is null)
        {
            throw new ConfigException("\"stateDirectory\" or \"slotUtc\" is null");
        }
        _ = AudioSource.Parse(Audio);
        _ = SlotStart;
        if (EveryMinutes < ShortestEveryMinutes || 1440 % EveryMinutes != 0)
        {
            throw new ConfigException($"\"everyMinutes\" {EveryMinutes} must divide a day (1440) and be at least {ShortestEveryMinutes}; GB7RDG's is 60");
        }
        if (Daylight?.Problem(EveryMinutes, SlotStart) is { } daylightProblem)
        {
            throw new ConfigException($"\"daylight\": {daylightProblem}; GB7RDG's is {{ \"locator\": \"IO91lk\", \"afterSunriseMinutes\": 120, \"beforeSunsetMinutes\": 30 }}");
        }
        if (WebSdrSlotsPerDay < 1 || WebSdrSlotsPerDay > MostWebSdrSlotsPerDay)
        {
            throw new ConfigException(
                $"\"webSdrSlotsPerDay\" {WebSdrSlotsPerDay} must be from 1 to {MostWebSdrSlotsPerDay}: each slot keeps a web SDR open {WebSdrMinutesPerSlot} minutes, "
                + "and public UberSDR receivers allow each address about 3 hours a day");
        }
        if (!(DialKHz >= LowestDialKHz && DialKHz <= HighestDialKHz))
        {
            throw new ConfigException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"\"dialKHz\" {DialKHz} is not a USB dial in kHz between {LowestDialKHz:F0} and {HighestDialKHz:F0}; the usual one is {DefaultDialKHz:F1}"));
        }
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
        Rig?.Validate();
        Bpq?.Validate();
        Hooks?.Validate();
        if (Rig is { DedicatedRadio: false } && Bpq is null)
        {
            throw new ConfigException(
                "\"rig\" is set, so the receiver retunes your radio for each slot, but there is no \"bpq\" to stop LinBPQ transmitting while it is on "
                + "the bulletin frequency. Add \"bpq\" (LinBPQ's node telnet port and a SYSOP user), or, if nothing else ever transmits on this radio, "
                + "set \"rig\": { ..., \"dedicatedRadio\": true }");
        }
        if (Rig is null && Bpq is not null)
        {
            throw new ConfigException("\"bpq\" is set but \"rig\" is not: LinBPQ is only held off the air while the receiver has retuned the radio, so add \"rig\" too, or remove \"bpq\"");
        }
    }
}

/// <summary>A configuration that cannot work, with a sentence saying why.</summary>
public sealed class ConfigException(string message) : Exception(message);
