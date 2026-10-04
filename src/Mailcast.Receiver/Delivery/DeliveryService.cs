using Mailcast.Core;

namespace Mailcast.Receiver.Delivery;

/// <summary>Something that can run one forwarding session; the real one is <see cref="BbsClient"/>.</summary>
public interface IBbsSession
{
    /// <summary>Offers <paramref name="bulletins"/> to the BBS in one session.</summary>
    Task<SessionReport> DeliverAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation);
}

/// <summary>
/// Hands every rebuilt bulletin to the BBS: as soon as one is complete, and again after a failure,
/// with a growing wait while the BBS cannot be reached.
/// </summary>
/// <remarks>
/// <para>A bulletin leaves the store's outbox only once the BBS has answered for it for good:
/// accepted (FS + and a clean close), already had (FS -) or refused. Anything else (FS =, a
/// session that broke off, a BBS that cannot be reached) leaves it in the outbox, which is on disk,
/// so a restart picks it up again. Nothing is lost, and nothing is delivered twice: the BBS's own
/// BID check answers FS - to a bulletin it already took.</para>
/// </remarks>
public sealed class DeliveryService
{
    /// <summary>The waits after successive failures to reach the BBS; the last repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30),
    ];

    /// <summary>How long to wait before offering again bulletins the BBS asked to have later.</summary>
    public static readonly TimeSpan LaterRetry = TimeSpan.FromMinutes(10);

    /// <summary>The most bulletins offered in one session; any more wait for the next.</summary>
    public const int MaxPerSession = 50;

    private readonly Intake _intake;
    private readonly IBbsSession _bbs;
    private readonly DeliveryLedger _ledger;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _failures;

    /// <summary>Creates the service. Call <see cref="RunAsync"/> to start it.</summary>
    public DeliveryService(Intake intake, IBbsSession bbs, DeliveryLedger ledger, TimeProvider time, Action<string> log)
    {
        _intake = intake;
        _bbs = bbs;
        _ledger = ledger;
        _time = time;
        _log = log;
        intake.BulletinCompleted += _ => Nudge();
    }

    /// <summary>The last session's failure, or null if the last session worked.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>When the next attempt is due, if one is waiting on a timer.</summary>
    public DateTimeOffset? NextAttempt { get; private set; }

    /// <summary>Raised after each session, whatever happened (for tests and the page).</summary>
    public event Action<SessionReport>? SessionFinished;

    /// <summary>
    /// Raised once the service is waiting, with the wait if it is on a timer (null: waiting for a
    /// bulletin). Its timer is already running on the clock when this is raised, so a test can
    /// move a fake clock on from here.
    /// </summary>
    internal event Action<TimeSpan?>? Waiting;

    /// <summary>Asks for a session now, if the service is idle rather than backing off.</summary>
    public void Nudge()
    {
        lock (_gate)
        {
            _wake.TrySetResult();
        }
    }

    /// <summary>Delivers until <paramref name="cancellation"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            Task woken;
            lock (_gate)
            {
                if (_wake.Task.IsCompleted)
                {
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                woken = _wake.Task;
            }

            var pending = _intake.Pending();
            if (pending.Count == 0)
            {
                NextAttempt = null;
                await WaitAsync(woken, null, cancellation).ConfigureAwait(false);
                continue;
            }

            TimeSpan? wait = await AttemptAsync([.. pending.Take(MaxPerSession)], cancellation).ConfigureAwait(false);
            if (wait is TimeSpan delay)
            {
                NextAttempt = _time.GetUtcNow() + delay;
                // Waiting out a failure: a new bulletin does not make an unreachable BBS reachable.
                await WaitAsync(_failures > 0 ? null : woken, delay, cancellation).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// For a one-off run: offers what is in the outbox, session after session, until it is empty,
    /// the BBS asks to have the rest later, or a session fails. Returns the last failure, if any.
    /// </summary>
    public async Task<string?> DeliverPendingAsync(CancellationToken cancellation)
    {
        while (_intake.Pending() is { Count: > 0 } pending)
        {
            TimeSpan? wait = await AttemptAsync([.. pending.Take(MaxPerSession)], cancellation).ConfigureAwait(false);
            if (wait is not null)
            {
                return LastFailure ?? "the BBS asked to have some bulletins later";
            }
        }
        return null;
    }

    /// <summary>One session. Returns how long to wait before the next, or null to go straight on.</summary>
    private async Task<TimeSpan?> AttemptAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation)
    {
        SessionReport report;
        try
        {
            report = await _bbs.DeliverAsync(bulletins, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }

        var byBid = bulletins.ToDictionary(b => b.Bid, StringComparer.OrdinalIgnoreCase);
        bool waitingOnBbs = false;
        foreach (var outcome in report.Outcomes)
        {
            var bulletin = byBid[outcome.Bid];
            switch (outcome.Verdict)
            {
                case DeliveryVerdict.Accepted or DeliveryVerdict.AlreadyHad or DeliveryVerdict.Refused:
                    var record = _ledger.Record(bulletin, outcome, _time.GetUtcNow());
                    _intake.Acknowledge(bulletin.Bid);
                    _log($"bbs: {Ascii.Clean(bulletin.Bid)} {Describe(record.Verdict)}{(record.Detail is null ? "" : ": " + Ascii.Clean(record.Detail))}");
                    break;
                case DeliveryVerdict.Deferred or DeliveryVerdict.Unconfirmed:
                    _ledger.Record(bulletin, outcome, _time.GetUtcNow());
                    _log($"bbs: {Ascii.Clean(bulletin.Bid)} {Describe(outcome.Verdict)}{(outcome.Detail is null ? "" : ": " + Ascii.Clean(outcome.Detail))}");
                    waitingOnBbs = true;
                    break;
                default:
                    waitingOnBbs = true;
                    break;
            }
        }

        if (report.ReverseOffered > 0)
        {
            _log($"bbs: the BBS tried to send the receiver {report.ReverseOffered} message(s) and was told to keep them. "
                + "The receiver's login should have no forwarding routes (TO, AT or HR) on the BBS.");
        }

        SessionFinished?.Invoke(report);
        LastFailure = report.Failure;
        if (report.Failure is not null)
        {
            TimeSpan delay = Backoff[Math.Min(_failures, Backoff.Count - 1)];
            _failures++;
            _log($"bbs: {Ascii.Clean(report.Failure)}. Trying again in {Describe(delay)}.");
            return delay;
        }

        _failures = 0;
        return waitingOnBbs ? LaterRetry : null;
    }

    private async Task WaitAsync(Task? woken, TimeSpan? delay, CancellationToken cancellation)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var waits = new List<Task> { Task.Delay(Timeout.InfiniteTimeSpan, done.Token) };
        if (delay is TimeSpan d)
        {
            waits.Add(Task.Delay(d, _time, done.Token));
        }
        if (woken is not null)
        {
            waits.Add(woken);
        }
        Waiting?.Invoke(delay);
        await Task.WhenAny(waits).ConfigureAwait(false);
        await done.CancelAsync().ConfigureAwait(false);
    }

    internal static string Describe(DeliveryVerdict verdict) => verdict switch
    {
        DeliveryVerdict.Accepted => "accepted by the BBS",
        DeliveryVerdict.AlreadyHad => "rejected by the BBS: it already has this BID",
        DeliveryVerdict.Refused => "refused",
        DeliveryVerdict.Deferred => "deferred by the BBS: offering it again later",
        DeliveryVerdict.Unconfirmed => "sent but not confirmed: offering it again later",
        _ => "not offered",
    };

    private static string Describe(TimeSpan delay) =>
        delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:F0} min" : $"{delay.TotalSeconds:F0} s";
}
