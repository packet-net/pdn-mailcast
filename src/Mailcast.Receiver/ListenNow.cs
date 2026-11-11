namespace Mailcast.Receiver;

/// <summary>
/// Issue #53: a "Listen now" button on the status page opens a web SDR receiver for a few
/// minutes between its scheduled windows, so a new user can see the spectrogram and the level
/// and check things work, without waiting for the next slot.
/// </summary>
/// <remarks>
/// <para>Each use opens it for <see cref="Duration"/>, as often as wanted.</para>
/// <para>It never overlaps a slot window: a request is refused if one starts within
/// <see cref="MinBeforeWindow"/>, and the session it opens is cut short at once if a window
/// would otherwise start before it is due to close (which, given the refusal, only happens if
/// the schedule itself changes mid-session, such as a new directory from M0LTE). It is a web
/// SDR peek only: no hook is run, no rig is retuned and LinBPQ is not held off.</para>
/// </remarks>
public sealed class ListenNowService
{
    /// <summary>How long one use opens the web SDR for.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(3);

    /// <summary>A request is refused if a slot's window starts within this long.</summary>
    public static readonly TimeSpan MinBeforeWindow = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private DateTimeOffset? _until;

    /// <summary>What became of a request, or what a button would do now.</summary>
    /// <param name="Ok">Whether a session is open (or was just opened).</param>
    /// <param name="Reason">Why not, for a refusal.</param>
    /// <param name="Until">When the open session closes, if one is open.</param>
    public sealed record Result(bool Ok, string? Reason, DateTimeOffset? Until);

    /// <summary>
    /// The session open now, if any: null once <see cref="Duration"/> has passed, or (issue #57)
    /// at once if <paramref name="nextWindowOpens"/> has moved to now or the past, or is now
    /// sooner than the session's own end, since a slot's window must never be overlapped however
    /// it came to be sooner than it looked when the session was granted (M0LTE's directory,
    /// heard mid-session, can move it).
    /// </summary>
    public DateTimeOffset? ActiveUntil(DateTimeOffset now, DateTimeOffset nextWindowOpens)
    {
        lock (_gate)
        {
            return EffectiveUntil(now, nextWindowOpens);
        }
    }

    /// <summary>Under <see cref="_gate"/>: the session's current end, capped fresh against <paramref name="nextWindowOpens"/> every time; null once it is over.</summary>
    private DateTimeOffset? EffectiveUntil(DateTimeOffset now, DateTimeOffset nextWindowOpens)
    {
        if (_until is not { } until || until <= now)
        {
            _until = null;
            return null;
        }
        var capped = until < nextWindowOpens ? until : nextWindowOpens;
        if (capped <= now)
        {
            _until = null;
            return null;
        }
        return capped;
    }

    /// <summary>
    /// Why a request would be refused right now, without making one; null when it would be let
    /// through. <paramref name="nextWindowOpens"/> is when the next slot's window opens.
    /// </summary>
    public string? Problem(DateTimeOffset now, DateTimeOffset nextWindowOpens, bool alreadyListening)
    {
        lock (_gate)
        {
            if (EffectiveUntil(now, nextWindowOpens) is not null)
            {
                return null; // a session is already open
            }
        }
        if (alreadyListening)
        {
            return "Already listening for a slot.";
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
            if (EffectiveUntil(now, nextWindowOpens) is { } active)
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
            _until = until;
            return new Result(true, null, until);
        }
    }
}
