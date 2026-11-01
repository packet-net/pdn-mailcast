using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mailcast.Receiver;

/// <summary>
/// Issue #154: the config file as the receiver read it, so that saving from the status page
/// never overwrites a hand edit made since. The receiver reads its config once, at start-up;
/// the page builds each save from that, so a save after a hand edit would quietly drop
/// whatever was added by hand (a <c>"rig"</c>, a <c>"hooks"</c>, comments). This remembers
/// what the file held when it was read (a hash of its bytes, which an editor that keeps the
/// old time cannot fool as it could the file's time), refuses to save over anything else, and
/// notices a hand edit while the receiver runs so the log and the page can say a restart is
/// needed.
/// </summary>
public sealed class ConfigFile
{
    /// <summary>How often <see cref="RunAsync"/> looks at the file for a hand edit.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    /// <summary>What the page says when it will not save over a hand edit.</summary>
    public const string EditedByHand =
        "The config file was edited by hand since the receiver started; restart the receiver to use it, then make this change again";

    /// <summary>
    /// What the page says when the file could not be read to check it, say while an editor on
    /// Windows holds it open: a moment later the save will most likely go through.
    /// </summary>
    public const string Unreadable = "Couldn't read the config file to check it hasn't been edited by hand; try again in a moment";

    /// <summary>What <see cref="TrySave"/> did.</summary>
    public enum SaveOutcome
    {
        /// <summary>Written.</summary>
        Saved,

        /// <summary>Not written: the file has been changed by hand since it was read.</summary>
        EditedByHand,

        /// <summary>Not written: the file could not be read to check it.</summary>
        Unreadable,
    }

    /// <summary>The line the log gets, once for each hand edit noticed.</summary>
    public const string ChangedLine = "config: the config file was changed by hand; restart the receiver to use it";

    private readonly object _gate = new();

    /// <summary>The hash of what the file held when it was read, or when the page last saved it.</summary>
    private byte[] _hash;

    /// <summary>The hash of the hand edit last logged, so each is logged once; null before any.</summary>
    private byte[]? _noticed;

    private ConfigFile(string path, ReceiverConfig config, byte[] bytes, IReadOnlyList<string> unknownKeys)
    {
        Path = path;
        Config = config;
        _hash = SHA256.HashData(bytes);
        UnknownKeys = unknownKeys;
    }

    /// <summary>Where the file is.</summary>
    public string Path { get; }

    /// <summary>The configuration as read at start-up.</summary>
    public ReceiverConfig Config { get; }

    /// <summary>The top-level keys in the file that are not settings and were ignored.</summary>
    public IReadOnlyList<string> UnknownKeys { get; }

    /// <summary>
    /// Whether the file has been found changed by hand since it was read: by <see cref="Check"/>,
    /// or by a save it refused. Cleared if the file goes back to what was read.
    /// </summary>
    public bool ChangedByHand
    {
        get
        {
            lock (_gate)
            {
                return _noticed is not null;
            }
        }
    }

    /// <summary>Reads the config file. Throws <see cref="ConfigException"/> with a sentence for the operator.</summary>
    public static ConfigFile Load(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"cannot read the config file {path}: {e.Message}");
        }
        // As File.ReadAllText reads it: UTF-8 unless the file starts with another encoding's mark.
        string text;
        using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
        }
        var config = ReceiverConfig.Parse(text, path);
        return new ConfigFile(path, config, bytes, ReceiverConfig.UnknownKeys(text));
    }

    /// <summary>
    /// The hash of what is in the file now, or null if it cannot be read just now: an editor
    /// holding it open on Windows, or one that replaces it caught between its steps.
    /// </summary>
    private byte[]? HashNow()
    {
        try
        {
            return SHA256.HashData(File.ReadAllBytes(Path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks at the file once: if it has been changed by hand since it was read, and this change
    /// has not been logged yet, logs <see cref="ChangedLine"/>. Returns whether it is changed.
    /// A file that cannot be read just now is passed over without a word, as it was.
    /// </summary>
    public bool Check(Action<string> log)
    {
        bool say;
        lock (_gate)
        {
            var now = HashNow();
            if (now is null)
            {
                return _noticed is not null;
            }
            if (now.AsSpan().SequenceEqual(_hash))
            {
                _noticed = null;
                return false;
            }
            say = _noticed is null || !now.AsSpan().SequenceEqual(_noticed);
            _noticed = now;
        }
        if (say)
        {
            log(ChangedLine);
        }
        return true;
    }

    /// <summary>Checks the file every <see cref="CheckEvery"/> until <paramref name="cancellation"/>; never throws for that.</summary>
    public async Task RunAsync(TimeProvider time, Action<string> log, CancellationToken cancellation)
    {
        try
        {
            while (true)
            {
                await Task.Delay(CheckEvery, time, cancellation).ConfigureAwait(false);
                Check(log);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // stopping
        }
    }

    /// <summary>
    /// Writes <paramref name="next"/> over the file, unless the file has been changed by hand
    /// since it was read or last saved, or cannot be read to check: then nothing is written, the
    /// log gets one line, and the page says <see cref="EditedByHand"/> or <see cref="Unreadable"/>.
    /// Throws as <see cref="ReceiverConfig.Save"/> does when the file cannot be written.
    /// </summary>
    public SaveOutcome TrySave(ReceiverConfig next, Action<string> log)
    {
        lock (_gate)
        {
            var now = HashNow();
            if (now is null)
            {
                log("config: not saved from the page: the config file could not be read to check it has not been edited by hand; try again in a moment");
                return SaveOutcome.Unreadable;
            }
            if (!now.AsSpan().SequenceEqual(_hash))
            {
                _noticed = now;
                log("config: not saved from the page: the config file was changed by hand since the receiver started; restart the receiver to use it");
                return SaveOutcome.EditedByHand;
            }
            next.Save(Path);
            _hash = SHA256.HashData(next.ToFile());
            _noticed = null;
            return SaveOutcome.Saved;
        }
    }

    /// <summary>
    /// The start-up line saying which optional parts are in force and which are not, so a
    /// missing one is plain to see: <c>config: rig retuning off (no "rig"), hooks off (no "hooks"), daily report on as G8BPQ</c>.
    /// </summary>
    public static string PartsLine(ReceiverConfig config)
    {
        bool soundModem = AudioSource.Parse(config.Audio).Kind == AudioSourceKind.SoundModem;
        string retuning = soundModem && config.Retune
            ? "retuning through pdn-soundmodem's receive window on (\"retune\")" + (config.Rig is null ? "" : "; \"rig\" is not used with this audio")
            : soundModem && config.Rig is not null
                ? "retuning off: \"rig\" is not used with \"audio\": \"soundmodem\"; set \"retune\": true and give pdn-soundmodem a \"rig\" section"
            : config.Rig is { } rig
                ? config.Bpq is not null ? "rig retuning on, with LinBPQ held off the air through \"bpq\""
                    : rig.DedicatedRadio ? "rig retuning on (a dedicated radio)"
                    : "rig retuning on, with the radio kept quiet by \"hooks\".\"before\""
                : "rig retuning off (no \"rig\")";
        string hooks = config.Hooks switch
        {
            null => "hooks off (no \"hooks\")",
            { Before: null, After: null } => "hooks off (\"hooks\" has no \"before\" or \"after\")",
            _ => "hooks on",
        };
        string report = config.Feedback switch
        {
            null => "daily report off (no \"feedback\")",
            { Enabled: true } feedback => $"daily report on as {feedback.From}",
            _ => "daily report off",
        };
        return $"config: {retuning}, {hooks}, {report}";
    }

    /// <summary>
    /// What a save from the page changed, for the log: <c>dial 7052.00 -> 7052.07 kHz, daily report on as G4ABC</c>,
    /// or null for nothing. Never a password, nor anything about one but that it changed.
    /// </summary>
    public static string? Changes(ReceiverConfig before, ReceiverConfig after)
    {
        var changes = new List<string>();
        void Text(string what, string? was, string? now)
        {
            if (!string.Equals(was, now, StringComparison.Ordinal))
            {
                changes.Add($"{what} {Said(was)} -> {Said(now)}");
            }
        }
        static string Said(string? value) => string.IsNullOrEmpty(value) ? "none" : value;

        Text("audio", before.Audio, after.Audio);
        if (before.ResolvedDialKHz != after.ResolvedDialKHz)
        {
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"dial {before.ResolvedDialKHz:F2} -> {after.ResolvedDialKHz:F2} kHz"));
        }
        Text("locator", before.Locator, after.Locator);
        Text("sources", before.AcceptedSources.ToString(), after.AcceptedSources.ToString());
        Text("BBS type", before.Bbs.Type.ToString(), after.Bbs.Type.ToString());
        Text("BBS host", before.Bbs.Host, after.Bbs.Host);
        if (before.Bbs.Port != after.Bbs.Port)
        {
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"BBS port {before.Bbs.Port} -> {after.Bbs.Port}"));
        }
        Text("BBS login", before.Bbs.Login, after.Bbs.Login);
        Text("BBS command", before.Bbs.Command, after.Bbs.Command);
        if (!string.Equals(before.Bbs.Password, after.Bbs.Password, StringComparison.Ordinal))
        {
            changes.Add("BBS password changed");
        }
        if (!string.Equals(before.Web.Password, after.Web.Password, StringComparison.Ordinal))
        {
            changes.Add("page password changed");
        }
        bool wasOn = before.Feedback?.Enabled ?? false, isOn = after.Feedback?.Enabled ?? false;
        if (wasOn != isOn)
        {
            changes.Add(isOn ? $"daily report on as {after.Feedback!.From}" : "daily report off");
        }
        else
        {
            Text("daily report callsign", before.Feedback?.From, after.Feedback?.From);
        }
        return changes.Count == 0 ? null : string.Join(", ", changes);
    }
}
