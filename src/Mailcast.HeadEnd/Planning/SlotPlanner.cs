using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Slot;

namespace Mailcast.HeadEnd.Planning;

/// <summary>A slot's frames, in sending order.</summary>
/// <param name="Slot">The slot's start.</param>
/// <param name="Frames">What to send: empty when no bulletin has anything due, since the directory alone is not worth keying for.</param>
/// <param name="BulletinsInRotation">How many bulletins the slot's directory lists.</param>
/// <param name="Broadcast">The core's plan, which a commit refers back to.</param>
public sealed record SlotPlan(DateTimeOffset Slot, IReadOnlyList<SlotFrame> Frames, int BulletinsInRotation, SlotBroadcast? Broadcast)
{
    /// <summary>A plan with nothing in it.</summary>
    public static SlotPlan Empty(DateTimeOffset slot) => new(slot, [], 0, null);
}

/// <summary>Makes a slot's frames and remembers what went out.</summary>
public interface ISlotPlanner
{
    /// <summary>
    /// The frames to send in the slot starting at <paramref name="slot"/>. With
    /// <paramref name="evenIfNothingDue"/>, as for a one-off slot someone asked for, the directory
    /// goes out even when no bulletin has anything due.
    /// </summary>
    SlotPlan Plan(DateTimeOffset slot, bool evenIfNothingDue = false);

    /// <summary>
    /// Records that the first <paramref name="queued"/> frames of a plan have been handed to the
    /// modem, so none of them is ever sent again and the rest are owed to the next plan. Called as
    /// the slot goes, so a crash mid-slot repeats nothing.
    /// </summary>
    void RecordQueued(SlotPlan plan, int queued);
}

/// <summary>
/// The planner on Packet.Mailcast: the bulletins in rotation from the head end store, each with its
/// object as first prepared, its first slot and its next ESI, through <see cref="BroadcastScheduler"/>.
/// </summary>
/// <remarks>
/// Frames a slot did not send are owed: the next plan, of the same slot or a later one, sends them
/// on top of its own share, always with fresh ESIs, because each object's next ESI only ever moves
/// on past what was handed to the modem. A scheduled slot in which no bulletin has anything due
/// sends nothing, not even the directory.
/// </remarks>
public sealed class StoreSlotPlanner(RotationStore store, Compression compression, ScheduleOptions options) : ISlotPlanner
{
    /// <inheritdoc />
    public SlotPlan Plan(DateTimeOffset slot, bool evenIfNothingDue = false)
    {
        SlotBroadcast broadcast = store.Plan(slot, Seed(slot), compression, options);
        if (broadcast.BulletinFrames == 0 && !evenIfNothingDue)
        {
            return new SlotPlan(slot, [], broadcast.Directory.Entries.Count, null);
        }
        var frames = broadcast.Frames.Select(f => new SlotFrame(f.ToBytes(), f.ObjectId, f.EncodingSymbolId)).ToList();
        return new SlotPlan(slot, frames, broadcast.Directory.Entries.Count, broadcast);
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

    /// <summary>The interleaving seed for a slot: fixed, so a slot's plan is the same however often it is made.</summary>
    public static int Seed(DateTimeOffset slot) => (int)(slot.UtcTicks / TimeSpan.TicksPerMinute % int.MaxValue);
}
