using System.Text.Json;
using Mailcast.Receiver.Retune;

namespace Mailcast.Receiver;

/// <summary>
/// Issue #53: a "Listen now" button on the status page opens a web SDR receiver for a few
/// minutes between its scheduled windows, so a new user can see the spectrogram and the level
/// and check things work, without waiting for the next slot.
/// </summary>
/// <remarks>
/// <para>Each use counts against the web SDR's daily listening allowance, the same as a slot
/// window: <see cref="Duration"/> minutes, at most <see cref="MaxPerDay"/> times a UTC day. The
/// count is kept in <see cref="FileName"/> in the state directory, so it survives a restart.</para>
/// <para>It never overlaps a slot window: a request is refused if one starts within
/// <see cref="MinBeforeWindow"/>, and the session it opens is cut short at once if a window
/// would otherwise start before it is due to close (which, given the refusal, only happens if
/// the schedule itself changes mid-session, such as a new directory from GB7RDG). It is a web
/// SDR peek only: no hook is run, no rig is retuned and LinBPQ is not held off.</para>
/// </remarks>
public sealed class ListenNowService
{
    /// <summary>The file in the state directory the day's count is kept in.</summary>
    public const string FileName = "listen-now.json";

    /// <summary>How long one use opens the web SDR for.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(3);

    /// <summary>The most uses a UTC day.</summary>
    public const int MaxPerDay = 3;

    /// <summary>A request is refused if a slot's window starts within this long.</summary>
    public static readonly TimeSpan MinBeforeWindow = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly string? _path;
    private readonly object _gate = new();
    private Usage _usage;
    private DateTimeOffset? _until;

    /// <summary>Creates the service, reading today's count from <paramref name="stateDirectory"/> if there is one kept.</summary>
    public ListenNowService(TimeProvider time, string? stateDirectory)
    {
        _time = time;
        _path = stateDirectory is null ? null : Path.Combine(stateDirectory, FileName);
        _usage = Load();
    }

    private sealed record Usage(DateOnly Day, int Count);

    /// <summary>What became of a request, or what a button would do now.</summary>
    /// <param name="Ok">Whether a session is open (or was just opened).</param>
    /// <param name="Reason">Why not, for a refusal.</param>
    /// <param name="Until">When the open session closes, if one is open.</param>
    public sealed record Result(bool Ok, string? Reason, DateTimeOffset? Until);

    /// <summary>Uses left today, as of <paramref name="now"/>.</summary>
    public int UsesLeft(DateTimeOffset now)
    {
        lock (_gate)
        {
            return Math.Max(0, MaxPerDay - UsedToday(now));
        }
    }

    private int UsedToday(DateTimeOffset now) => _usage.Day == DateOnly.FromDateTime(now.UtcDateTime) ? _usage.Count : 0;

    /// <summary>
    /// The session open now, if any: null once <see cref="Duration"/> (or less, if a slot window
    /// was closer) has passed.
    /// </summary>
    public DateTimeOffset? ActiveUntil(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_until is { } until && until > now)
            {
                return until;
            }
            _until = null;
            return null;
        }
    }

    /// <summary>
    /// Why a request would be refused right now, without making one; null when it would be let
    /// through. <paramref name="nextWindowOpens"/> is when the next slot's window opens.
    /// </summary>
    public string? Problem(DateTimeOffset now, DateTimeOffset nextWindowOpens, bool alreadyListening)
    {
        lock (_gate)
        {
            if (_until is { } until && until > now)
            {
                return null; // a session is already open
            }
        }
        if (alreadyListening)
        {
            return "Already listening for a slot.";
        }
        if (UsesLeft(now) <= 0)
        {
            return $"Listen now has been used {MaxPerDay} times today already. It opens again tomorrow.";
        }
        if (nextWindowOpens - now < MinBeforeWindow)
        {
            return "A slot's window opens in a few minutes: wait for that instead.";
        }
        return null;
    }

    /// <summary>
    /// Opens a session, if one is not refused: see <see cref="Problem"/>. The session closes at
    /// <see cref="Duration"/> from now, or when <paramref name="nextWindowOpens"/> arrives,
    /// whichever is sooner, so it never overlaps a slot's own window.
    /// </summary>
    public Result Request(DateTimeOffset now, DateTimeOffset nextWindowOpens, bool alreadyListening)
    {
        lock (_gate)
        {
            if (_until is { } active && active > now)
            {
                return new Result(true, null, active);
            }
            if (Problem(now, nextWindowOpens, alreadyListening) is { } why)
            {
                return new Result(false, why, null);
            }
            var until = now + Duration;
            if (until > nextWindowOpens)
            {
                until = nextWindowOpens;
            }
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            _usage = new Usage(today, UsedToday(now) + 1);
            _until = until;
            Save();
            return new Result(true, null, until);
        }
    }

    private Usage Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return new Usage(default, 0);
        }
        try
        {
            return JsonSerializer.Deserialize<Usage>(File.ReadAllText(_path), ReceiverConfig.Json) ?? new Usage(default, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Usage(default, 0);
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }
        try
        {
            InterlockFile.WriteDurably(_path, JsonSerializer.SerializeToUtf8Bytes(_usage, ReceiverConfig.Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without it, a restart forgets today's count; the worst that does is let a few extra
            // minutes of listening past the daily allowance, so it is not fatal.
        }
    }
}
