using Mailcast.Core;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Slot;

namespace Mailcast.HeadEnd.Planning;

/// <summary>A day's frames, in sending order.</summary>
/// <param name="Day">The broadcast day.</param>
/// <param name="Frames">What to send.</param>
/// <param name="BulletinsInRotation">How many bulletins the day's directory lists.</param>
/// <param name="Broadcast">The core's plan, which a commit refers back to.</param>
public sealed record SlotPlan(DateOnly Day, IReadOnlyList<SlotFrame> Frames, int BulletinsInRotation, DailyBroadcast? Broadcast)
{
    /// <summary>A plan with nothing in it.</summary>
    public static SlotPlan Empty(DateOnly day) => new(day, [], 0, null);
}

/// <summary>Makes the day's frames and remembers what went out.</summary>
public interface ISlotPlanner
{
    /// <summary>The frames to send on <paramref name="day"/>.</summary>
    SlotPlan Plan(DateOnly day);

    /// <summary>
    /// Records that the first <paramref name="queued"/> frames of a plan have been handed to the
    /// modem, so none of them is ever sent again and the rest are owed to the next plan. Called as
    /// the slot goes, so a crash mid-slot repeats nothing.
    /// </summary>
    void RecordQueued(SlotPlan plan, int queued);
}

/// <summary>
/// The planner on Mailcast.Core: the bulletins in rotation from the head end store, each with its
/// object as first prepared and its next ESI, through <see cref="BroadcastScheduler"/>.
/// </summary>
/// <remarks>
/// Frames a slot did not send are owed: the next plan, the same day or the next, sends them on top
/// of its own share, always with fresh ESIs, because each object's next ESI only ever moves on
/// past what was handed to the modem.
/// </remarks>
public sealed class StoreSlotPlanner(RotationStore store, Compression compression, ScheduleOptions options) : ISlotPlanner
{
    /// <inheritdoc />
    public SlotPlan Plan(DateOnly day)
    {
        DailyBroadcast broadcast = store.Plan(day, Seed(day), compression, options);
        var frames = broadcast.Frames.Select(f => new SlotFrame(f.ToBytes(), f.ObjectId, f.EncodingSymbolId)).ToList();
        return new SlotPlan(day, frames, broadcast.Directory.Entries.Count, broadcast);
    }

    /// <inheritdoc />
    public void RecordQueued(SlotPlan plan, int queued)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (queued > 0 && plan.Broadcast is not null)
        {
            store.Commit(plan.Broadcast, queued);
        }
    }

    /// <summary>The interleaving seed for a day: fixed, so a day's plan is the same however often it is made.</summary>
    public static int Seed(DateOnly day) => day.DayNumber;
}
