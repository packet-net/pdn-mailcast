using System.Globalization;
using System.Text.Json.Serialization;

namespace Packet.Mailcast;

/// <summary>
/// A program a receiver runs around a slot: <see cref="Command"/>, an absolute path run directly
/// (never through a shell), with <see cref="Args"/>, stopped if it takes longer than
/// <see cref="TimeoutSeconds"/>. A config file gives it as
/// <c>{ "command": "/path/script", "args": ["..."], "timeoutSeconds": 30 }</c>.
/// </summary>
public sealed record HookCommand
{
    /// <summary>The timeout when none is given.</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>The shortest timeout accepted.</summary>
    public const int ShortestTimeoutSeconds = 1;

    /// <summary>The longest timeout accepted: five minutes.</summary>
    public const int LongestTimeoutSeconds = 300;

    /// <summary>The program's absolute path.</summary>
    public string Command { get; init; } = "";

    /// <summary>Its arguments, each passed as it is: no shell splits or expands them.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>How long it may run, in seconds, before it and everything it started are killed.</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    /// <summary><see cref="TimeoutSeconds"/> as a time span.</summary>
    [JsonIgnore]
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>
    /// Why this cannot be run, as a sentence naming the setting (<c>"command"</c>, <c>"args"</c>
    /// or <c>"timeoutSeconds"</c>), or null when it can: the command must be an absolute path to
    /// an existing executable file, the arguments strings, the timeout from
    /// <see cref="ShortestTimeoutSeconds"/> to <see cref="LongestTimeoutSeconds"/>.
    /// </summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Command))
        {
            return "\"command\" is empty: give the full path of the program to run, such as /usr/local/bin/mailcast-hook";
        }
        if (!Path.IsPathFullyQualified(Command))
        {
            return $"\"command\" \"{Command}\" is not a full path: give it from the root, such as /usr/local/bin/{Path.GetFileName(Command)}. It is run directly, without a shell or PATH";
        }
        if (Directory.Exists(Command))
        {
            return $"\"command\" {Command} is a folder, not a program";
        }
        if (!File.Exists(Command))
        {
            return $"\"command\" {Command} does not exist (or cannot be seen by this user)";
        }
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(Command) & anyExecute) == 0)
            {
                return $"\"command\" {Command} is not executable: make it so with chmod +x {Command}";
            }
        }
        if (Args is null)
        {
            return "\"args\" is null: leave it out, or give a list of strings such as [\"stop\"]";
        }
        if (Args.Any(a => a is null))
        {
            return "\"args\" has a null in it: give each argument as a string in quotes";
        }
        if (Args.Any(a => a.Contains('\0', StringComparison.Ordinal)))
        {
            return "\"args\" has a NUL character in it, which no program can be given";
        }
        if (TimeoutSeconds is < ShortestTimeoutSeconds or > LongestTimeoutSeconds)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"\"timeoutSeconds\" {TimeoutSeconds} must be from {ShortestTimeoutSeconds} to {LongestTimeoutSeconds}; {DefaultTimeoutSeconds} if left out");
        }
        return null;
    }

    /// <summary>The command line, for the log: the path, then each argument, quoted where it has a space.</summary>
    public string Describe() =>
        string.Join(' ', new[] { Command }.Concat((Args ?? []).Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a)));
}

/// <summary>
/// What a slot hook is told, in environment variables, so the same script can serve both hooks
/// and any receiver that runs them.
/// </summary>
public static class SlotHookEnvironment
{
    /// <summary>"before" or "after".</summary>
    public const string Hook = "MAILCAST_HOOK";

    /// <summary>The slot's start, UTC, ISO 8601: <c>2026-10-05T12:00:00Z</c>.</summary>
    public const string SlotUtc = "MAILCAST_SLOT_UTC";

    /// <summary>The USB dial, kHz: <c>7052.0</c>.</summary>
    public const string DialKHz = "MAILCAST_DIAL_KHZ";

    /// <summary>The signal's centre, kHz: <c>7053.8</c>.</summary>
    public const string CentreKHz = "MAILCAST_CENTRE_KHZ";

    /// <summary>For "after" only: 1 if "before" worked (or there is no "before"), 0 if not.</summary>
    public const string BeforeOk = "MAILCAST_BEFORE_OK";

    /// <summary>The name of the hook run before a slot.</summary>
    public const string BeforeName = "before";

    /// <summary>The name of the hook run after a slot.</summary>
    public const string AfterName = "after";

    /// <summary>
    /// The variables for <paramref name="hook"/> (<see cref="BeforeName"/> or
    /// <see cref="AfterName"/>) around <paramref name="slot"/>; <paramref name="beforeOk"/> is
    /// given to "after" only.
    /// </summary>
    public static IReadOnlyDictionary<string, string> For(string hook, DateTimeOffset slot, double dialKHz, double centreKHz, bool? beforeOk = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Hook] = hook,
            [SlotUtc] = slot.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            [DialKHz] = dialKHz.ToString("0.0##", CultureInfo.InvariantCulture),
            [CentreKHz] = centreKHz.ToString("0.0##", CultureInfo.InvariantCulture),
        };
        if (beforeOk is { } ok)
        {
            variables[BeforeOk] = ok ? "1" : "0";
        }
        return variables;
    }
}
