using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Status;

namespace Mailcast.HeadEnd.Service;

/// <summary>An intake and how often it is asked.</summary>
public sealed record ScheduledIntake(IBulletinIntake Intake, TimeSpan Every);

/// <summary>
/// The long-running head end: collects bulletins on each intake's timer, and once a day runs the
/// slot.
/// </summary>
public sealed class HeadEndService(
    TimeOnly slotTime,
    TimeSpan catchUp,
    ISlotPlanner planner,
    RotationStore store,
    IReadOnlyList<ScheduledIntake> intakes,
    SlotRunner runner,
    StatusStore status,
    IJournal journal,
    TimeProvider time)
{
    /// <summary>Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        var loops = intakes.Select(i => IntakeLoopAsync(i, cancellation)).ToList();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                DateTimeOffset next = NextSlot(time.GetUtcNow(), slotTime, catchUp, status.LastSlot);
                status.SetState("waiting", next);
                status.SetBulletinsHeld(store.Count);
                journal.Write($"next slot {next.UtcDateTime:yyyy-MM-dd HH:mm}Z; {store.Count} bulletins held");
                TimeSpan wait = next - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, cancellation);
                }
                await RunSlotAsync(DateOnly.FromDateTime(next.UtcDateTime), cancellation);
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

    /// <summary>Collects, plans and runs one day's slot, and records it.</summary>
    public async Task<SlotReport> RunSlotAsync(DateOnly day, CancellationToken cancellation)
    {
        status.SetState("in slot", null);
        foreach (var scheduled in intakes)
        {
            await CollectAsync(scheduled.Intake, day, cancellation);
        }
        int forgotten = store.Expire(day);
        if (forgotten > 0)
        {
            journal.Write($"forgot {forgotten} bulletins past their remembering days");
        }

        SlotPlan plan = planner.Plan(day);
        SlotReport report = await runner.RunAsync(day, plan.Frames, plan.BulletinsInRotation, cancellation, queued => planner.RecordQueued(plan, queued));
        planner.RecordQueued(plan, report.FramesQueued);
        status.RecordSlot(report);
        status.SetBulletinsHeld(store.Count);
        return report;
    }

    /// <summary>
    /// When the next slot is: today's if it has not run and it is no more than
    /// <paramref name="catchUp"/> late, otherwise tomorrow's. A slot cut short because the head end
    /// was stopping counts as not run, so a restart carries on with it.
    /// </summary>
    public static DateTimeOffset NextSlot(DateTimeOffset now, TimeOnly slotTime, TimeSpan catchUp, SlotReport? last)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var at = new DateTimeOffset(today.ToDateTime(slotTime, DateTimeKind.Utc));
        bool ranToday = last is not null && last.Day == today
            && !(last.Outcome == SlotOutcome.Aborted && last.Reason == "the head end is stopping");
        if (!ranToday && now <= at + catchUp)
        {
            return now > at ? now : at;
        }
        return at.AddDays(1);
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
