using Mailcast.Core;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Status;

namespace Mailcast.HeadEnd.Service;

/// <summary>An intake and how often it is asked.</summary>
public sealed record ScheduledIntake(IBulletinIntake Intake, TimeSpan Every);

/// <summary>The answer to a request for a one-off slot.</summary>
/// <param name="Accepted">True when the slot is starting.</param>
/// <param name="Slot">The slot that is starting, when accepted.</param>
/// <param name="Problem">Why not, when refused.</param>
public sealed record RunNowAnswer(bool Accepted, DateTimeOffset? Slot, string? Problem);

/// <summary>
/// When slots are: <see cref="Anchor"/> UTC and then every <see cref="Every"/>, which divides a
/// day, so every day has the same slots. A daily station has one, at the anchor. With a
/// <see cref="Daylight"/> rule only the slots that start in its window run.
/// </summary>
public sealed record SlotSchedule
{
    public SlotSchedule(TimeOnly anchor, TimeSpan every, DaylightRule? daylight = null)
    {
        if (every <= TimeSpan.Zero || TimeSpan.FromDays(1).Ticks % every.Ticks != 0 || every.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentException("A slot interval must be whole minutes and divide a day.", nameof(every));
        }
        Anchor = anchor;
        Every = every;
        Timetable = new SlotTimetable(anchor, (int)every.TotalMinutes, daylight);
    }

    /// <summary>The same, as Mailcast.Core and the directory have it.</summary>
    public SlotTimetable Timetable { get; }

    /// <summary>The daylight rule, or null when every slot runs.</summary>
    public DaylightRule? Daylight => Timetable.Daylight;

    /// <summary>The latest slot that runs starting at or before <paramref name="t"/>, if any.</summary>
    public DateTimeOffset? ActiveAtOrBefore(DateTimeOffset t) => Timetable.ActiveAtOrBefore(t);

    /// <summary>
    /// The first slot that runs starting after <paramref name="t"/>. A rule with no slot in a
    /// year is refused by the configuration, so there is always one; failing that, a day later.
    /// </summary>
    public DateTimeOffset NextActiveAfter(DateTimeOffset t) => Timetable.NextActiveAfter(t) ?? t.AddDays(1);

    /// <summary>
    /// The day's slots in one line for the journal: "daylight 2026-10-05 at IO91lk: sunrise
    /// 06:11Z, sunset 17:33Z; 9 slots, 09:00, 10:00 ... and 17:00 UTC". Null without a rule.
    /// </summary>
    public string? DescribeDay(DateOnly day)
    {
        if (Daylight is not { } rule)
        {
            return null;
        }
        var window = rule.WindowOn(day);
        var slots = Timetable.ActiveSlotsOn(day);
        string sun = window.Sun.Kind switch
        {
            SunDay.AlwaysUp => "the sun does not set",
            SunDay.AlwaysDown => "the sun does not rise",
            _ => $"sunrise {window.Sun.Sunrise!.Value.UtcDateTime:HH:mm}Z, sunset {window.Sun.Sunset!.Value.UtcDateTime:HH:mm}Z",
        };
        return $"daylight {day:yyyy-MM-dd} at {rule.Locator}: {sun}; {slots.Count} slot{(slots.Count == 1 ? "" : "s")}{(slots.Count == 0 ? "" : ", " + SlotTimetable.Times(slots))}";
    }

    /// <summary>One slot a day.</summary>
    public static SlotSchedule Daily(TimeOnly at) => new(at, TimeSpan.FromDays(1));

    /// <summary>The time of day the slots are counted from.</summary>
    public TimeOnly Anchor { get; }

    /// <summary>From one slot to the next.</summary>
    public TimeSpan Every { get; }

    /// <summary>The start of the slot running at <paramref name="t"/>: the latest at or before it.</summary>
    public DateTimeOffset SlotAtOrBefore(DateTimeOffset t)
    {
        var first = new DateTimeOffset(t.UtcDateTime.Date, TimeSpan.Zero) + Anchor.ToTimeSpan();
        long k = (long)Math.Floor((t - first).Ticks / (double)Every.Ticks);
        return first + TimeSpan.FromTicks(Every.Ticks * k);
    }

    /// <summary>The first slot that starts after <paramref name="t"/>.</summary>
    public DateTimeOffset SlotAfter(DateTimeOffset t) => SlotAtOrBefore(t) + Every;

    /// <summary>
    /// The slot a report is for. One written before slots had times names only its day, and was
    /// that day's slot at the anchor.
    /// </summary>
    public DateTimeOffset SlotOf(SlotReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Slot != default ? report.Slot : new DateTimeOffset(report.Day.ToDateTime(Anchor, DateTimeKind.Utc));
    }
}

/// <summary>
/// The long-running head end: collects bulletins on each intake's timer, and runs a slot at each
/// time the <see cref="SlotSchedule"/> gives.
/// </summary>
public sealed class HeadEndService(
    SlotSchedule schedule,
    TimeSpan catchUp,
    TimeSpan retryAfter,
    TimeSpan preSlotIntakeLimit,
    ISlotPlanner planner,
    RotationStore store,
    IReadOnlyList<ScheduledIntake> intakes,
    SlotRunner runner,
    StatusStore status,
    IJournal journal,
    TimeProvider time) : IDisposable
{
    /// <summary>The longest the wait for a slot sleeps before looking at the clock again.</summary>
    public static readonly TimeSpan WakeEvery = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private bool _inSlot;
    private (DateTimeOffset Slot, string By)? _runNow;
    // Cancelled to wake the wait for a slot when a one-off slot is asked for; replaced after it.
    private CancellationTokenSource _wakeup = new();

    /// <summary>
    /// Asks for a one-off slot now, between the scheduled ones: the same slot as any other (the
    /// clock check, the lease, the tone, the bursts and the idents), counted like any other so no
    /// piece is ever sent twice, named by the minute it starts in. The schedule carries on
    /// afterwards. Refused while a slot is running or already asked for, and when the clock is
    /// behind the last slot run.
    /// </summary>
    /// <param name="requestedBy">Who asked, for the journal and the report.</param>
    public RunNowAnswer RequestRunNow(string requestedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        DateTimeOffset now = time.GetUtcNow();
        var slot = new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
        CancellationTokenSource toWake;
        lock (_gate)
        {
            string? problem = _inSlot ? "a slot is running"
                : _runNow is not null ? "a one-off slot has already been asked for and is starting"
                : status.LastSlot is { } last && schedule.SlotOf(last) > slot ? $"the clock ({SlotRunner.Name(slot)}) is behind the last slot run ({SlotRunner.Name(schedule.SlotOf(last))})"
                : null;
            if (problem is not null)
            {
                journal.Write($"run now: asked for by {requestedBy}, refused: {problem}");
                return new RunNowAnswer(false, null, problem);
            }
            _runNow = (slot, requestedBy);
            toWake = _wakeup;
        }
        journal.Write($"run now: asked for by {requestedBy}; slot {SlotRunner.Name(slot)} starts now");
        toWake.Cancel();
        return new RunNowAnswer(true, slot, null);
    }

    /// <summary>Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        var loops = intakes.Select(i => IntakeLoopAsync(i, cancellation)).ToList();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                DateTimeOffset next = NextSlot(time.GetUtcNow(), schedule, catchUp, status.LastSlot, retryAfter);
                status.SetState("waiting", next);
                status.SetBulletinsHeld(store.Count);
                AnnounceDay(next);
                journal.Write($"next slot {SlotRunner.Name(next)}; {store.Count} bulletins held");
                // Woken at least every minute to look at the clock again, so a box that booted with a
                // stale clock and is then corrected does not sleep through the real slot.
                while (true)
                {
                    DateTimeOffset now = time.GetUtcNow();
                    DateTimeOffset due = NextSlot(now, schedule, catchUp, status.LastSlot, retryAfter);
                    if (due != next)
                    {
                        next = due;
                        status.SetState("waiting", next);
                        AnnounceDay(next);
                        journal.Write($"next slot {SlotRunner.Name(next)} (the clock moved)");
                    }
                    TimeSpan wait = next - now;
                    if (wait <= TimeSpan.Zero || RunNowPending)
                    {
                        break;
                    }
                    await SleepAsync(wait < WakeEvery ? wait : WakeEvery, cancellation);
                }
                (DateTimeOffset Slot, string By)? asked;
                lock (_gate)
                {
                    // In the slot from here, so a request from now on waits for the next.
                    asked = _runNow;
                    _inSlot = true;
                }
                if (asked is { } oneOff)
                {
                    await RunSlotAsync(oneOff.Slot, cancellation, oneOff.By);
                    continue;
                }
                // A late start or a retry runs the slot it falls in.
                await RunSlotAsync(schedule.SlotAtOrBefore(next), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        foreach (var loop in loops)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private DateOnly? _announced;

    /// <summary>Once a day, with a daylight rule: the day's slots, in the journal.</summary>
    private void AnnounceDay(DateTimeOffset next)
    {
        var day = DateOnly.FromDateTime(next.UtcDateTime);
        if (_announced != day && schedule.DescribeDay(day) is { } line)
        {
            _announced = day;
            journal.Write(line);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _wakeup.Dispose();
        }
    }

    private bool RunNowPending
    {
        get
        {
            lock (_gate)
            {
                return _runNow is not null;
            }
        }
    }

    /// <summary>Sleeps for <paramref name="wait"/>, or until a one-off slot is asked for.</summary>
    private async Task SleepAsync(TimeSpan wait, CancellationToken cancellation)
    {
        CancellationToken wakeup;
        lock (_gate)
        {
            wakeup = _wakeup.Token;
        }
        using (var sleeping = CancellationTokenSource.CreateLinkedTokenSource(cancellation, wakeup))
        {
            try
            {
                await Task.Delay(wait, time, sleeping.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
            }
        }
        // Woken on the asker's thread: carry on on our own, so the request is answered at once.
        await Task.Yield();
    }

    /// <summary>Collects, plans and runs one slot, and records it.</summary>
    /// <param name="slot">The slot's start.</param>
    /// <param name="cancellation">Stops the slot.</param>
    /// <param name="requestedBy">Who asked for a one-off slot, or null for a scheduled one.</param>
    public async Task<SlotReport> RunSlotAsync(DateTimeOffset slot, CancellationToken cancellation, string? requestedBy = null)
    {
        lock (_gate)
        {
            _inSlot = true;
        }
        try
        {
            return await RunSlotInsideAsync(slot, requestedBy, cancellation);
        }
        finally
        {
            lock (_gate)
            {
                _inSlot = false;
                if (_runNow is { } asked && asked.Slot == slot && asked.By == requestedBy)
                {
                    _runNow = null;
                    // The old one is cancelled and holds nothing, and a sleep may still be reading
                    // its token, so it is left for the collector rather than disposed.
                    _wakeup = new CancellationTokenSource();
                }
            }
        }
    }

    private async Task<SlotReport> RunSlotInsideAsync(DateTimeOffset slot, string? requestedBy, CancellationToken cancellation)
    {
        var day = DateOnly.FromDateTime(slot.UtcDateTime);
        status.SetState("in slot", null);
        // Tightly bounded: a BBS that does not answer must not hold the slot up. Whatever it has
        // not handed over by then goes in the next slot.
        using (var limit = new CancellationTokenSource(preSlotIntakeLimit, time))
        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation, limit.Token))
        {
            foreach (var scheduled in intakes)
            {
                try
                {
                    await CollectAsync(scheduled.Intake, day, bounded.Token);
                }
                catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellation.IsCancellationRequested)
                {
                    journal.Write($"intake ({scheduled.Intake.Name}): not finished within {preSlotIntakeLimit.TotalSeconds:0} s before the slot; going ahead with what is held");
                    break;
                }
            }
        }
        int forgotten = store.Expire(day);
        if (forgotten > 0)
        {
            journal.Write($"forgot {forgotten} bulletins past their remembering days");
        }

        SlotPlan plan = planner.Plan(slot, evenIfNothingDue: requestedBy is not null);
        SlotReport report = await runner.RunAsync(slot, plan.Frames, plan.BulletinsInRotation, cancellation, queued => planner.RecordQueued(plan, queued), requestedBy);
        planner.RecordQueued(plan, report.FramesQueued);
        status.RecordSlot(report);
        status.SetBulletinsHeld(store.Count);
        SlotsToday today = status.SlotsToday;
        journal.Write($"slots today ({today.Date:yyyy-MM-dd}): {today.Slots}, {today.Completed} completed, {today.Aborted} cut short, {today.Skipped} skipped");
        return report;
    }

    /// <summary>
    /// When the next slot is: the current one (the latest start at or before
    /// <paramref name="now"/> of a slot that runs) if it has not run and it is no more than
    /// <paramref name="catchUp"/> late, otherwise the next that runs. A slot cut short because
    /// the head end was stopping counts as not run, so a restart carries on with it; one skipped
    /// for a reason at the station that may clear (no KISS port, no lease, no Flex) is tried again
    /// <paramref name="retryAfter"/> later, while that is still inside the catch-up window. A slot
    /// earlier than the last one run is never run, whatever the clock says. With a daylight rule,
    /// slots in the dark are passed over.
    /// </summary>
    public static DateTimeOffset NextSlot(DateTimeOffset now, SlotSchedule schedule, TimeSpan catchUp, SlotReport? last, TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        DateTimeOffset? latest = schedule.ActiveAtOrBefore(now);
        DateTimeOffset following = schedule.NextActiveAfter(latest ?? now);
        DateTimeOffset? lastSlot = last is null ? null : schedule.SlotOf(last);
        if (latest is not DateTimeOffset current)
        {
            return lastSlot > now ? schedule.NextActiveAfter(lastSlot.Value) : following;
        }
        if (lastSlot > current)
        {
            // The clock has gone back behind a slot already run (or a slot on demand ran since the
            // last scheduled one): never run an earlier one.
            return schedule.NextActiveAfter(lastSlot.Value);
        }
        if (lastSlot == current)
        {
            if (last!.Outcome == SlotOutcome.Aborted && last.Reason == "the head end is stopping")
            {
                return now <= current + catchUp ? now : following;
            }
            if (last.Retryable && retryAfter is TimeSpan wait)
            {
                DateTimeOffset retry = last.End + wait;
                retry = retry > now ? retry : now;
                return retry <= current + catchUp ? retry : following;
            }
            return following;
        }
        return now <= current + catchUp ? now : following;
    }

    private async Task IntakeLoopAsync(ScheduledIntake scheduled, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            await CollectAsync(scheduled.Intake, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), cancellation);
            await Task.Delay(scheduled.Every, time, cancellation);
        }
    }

    private async Task CollectAsync(IBulletinIntake intake, DateOnly today, CancellationToken cancellation)
    {
        IntakeResult result;
        try
        {
            result = await intake.CollectAsync(today, cancellation);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result = new IntakeResult(0, 0, e.Message);
        }
        if (result.Problem is not null)
        {
            journal.Write($"intake ({intake.Name}): {result.Problem}");
        }
        else if (result.Accepted + result.Refused > 0)
        {
            journal.Write($"intake ({intake.Name}): {result.Accepted} taken, {result.Refused} refused; {store.Count} held");
        }
        status.RecordIntake(intake.Name, result);
        status.SetBulletinsHeld(store.Count);
    }
}
