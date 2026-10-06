using Packet.Mailcast;
using Packet.Mailcast.Propagation;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Slot;

namespace Mailcast.HeadEnd.Planning;

/// <summary>A slot's frames, in sending order.</summary>
/// <param name="Slot">The slot's start.</param>
/// <param name="Frames">What to send: empty when no bulletin has anything due, since the directory alone is not worth keying for.</param>
/// <param name="BulletinsInRotation">How many bulletins the slot's directory lists.</param>
/// <param name="Broadcast">The core's plan, which a commit refers back to.</param>
/// <param name="Waveform">The waveform the slot goes out on; null for the broadcast modem's own, left as it is.</param>
/// <param name="Ionosphere">The ionosonde reading as the slot was planned; null when the head end takes none.</param>
/// <param name="IonosphereFrames">How many of <paramref name="Frames"/> carry the reading (content type 4).</param>
/// <param name="PskReporter">The PSK Reporter reading as the slot was planned; null when the head end takes none.</param>
/// <param name="PskReporterFrames">How many of <paramref name="Frames"/> carry it (content type 4, source 3).</param>
public sealed record SlotPlan(DateTimeOffset Slot, IReadOnlyList<SlotFrame> Frames, int BulletinsInRotation, SlotBroadcast? Broadcast, SlotWaveform? Waveform = null, IonoReading? Ionosphere = null, int IonosphereFrames = 0, PskReading? PskReporter = null, int PskReporterFrames = 0)
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
/// <para>Under the shares rule, frames a slot did not send are owed: the next plan, of the same slot
/// or a later one, sends them on top of its own share. Under the budget rule nothing is owed as
/// such: each slot is filled afresh, the least covered bulletins first. Either way every symbol is
/// fresh, because each object's next ESI only ever moves on past what was handed to the modem. A
/// scheduled slot in which no bulletin has anything to send sends nothing, not even the
/// directory.</para>
/// <para>The waveform is chosen first, since the budget rule fills the slot by that waveform's
/// airtime: a slot at 600 bps carries about half what one at 1200 bps does.</para>
/// </remarks>
/// <param name="store">The bulletins.</param>
/// <param name="compression">Compresses the directory.</param>
/// <param name="options">The scheduler's options.</param>
/// <param name="waveforms">Each slot's waveform; null sends every slot on the modem's own, and needs the shares rule.</param>
/// <param name="settings">How the slot runs, for the airtime estimate.</param>
/// <param name="fillLimit">What the budget rule fills a slot to, tone and idents included, for a slot that starts this late: <see cref="HeadEndConfig.FillLimitAfter"/>.</param>
/// <param name="time">The clock, which says how late a slot is planned; null plans every slot as on time.</param>
/// <param name="ionosphere">
/// The ionosonde reading as of now (<see cref="IonosondeMonitor.Current"/>), which must answer at
/// once; null sends none. A slot that keys anyway carries it in <see cref="IonosphereFrames"/>
/// frames of content type 4, inside the airtime budget. It never decides whether or when a slot
/// keys, nor what else goes in it beyond the room its frames take.
/// </param>
/// <param name="pskReporter">
/// The PSK Reporter reading as of now (<see cref="PskReporterMonitor.Current"/>), which must
/// answer at once; null sends none. Carried the same way as the ionosonde's, in
/// <see cref="PskReporterFrames"/> more frames: both readings go in a slot, or neither.
/// </param>
public sealed class StoreSlotPlanner(RotationStore store, Compression compression, ScheduleOptions options, Waveforms? waveforms = null, SlotSettings? settings = null, Func<TimeSpan, TimeSpan>? fillLimit = null, TimeProvider? time = null, Func<IonoReading>? ionosphere = null, Func<PskReading>? pskReporter = null) : ISlotPlanner
{
    /// <summary>Frames of the reading in each slot, either of which rebuilds it alone.</summary>
    public const int IonosphereFrames = 2;

    /// <summary>Frames of the PSK Reporter reading in each slot, either of which rebuilds it alone.</summary>
    public const int PskReporterFrames = 2;

    /// <summary>A reading at least this old is not sent: it is UNKNOWN long before, and the record's age stops at 255.</summary>
    public const int OldestSentMinutes = 255;

    private readonly SlotSettings _settings = settings ?? new SlotSettings();

    /// <inheritdoc />
    public SlotPlan Plan(DateTimeOffset slot, bool evenIfNothingDue = false)
    {
        SlotWaveform? waveform = waveforms?.For(slot);
        TimeSpan late = time is null ? TimeSpan.Zero : time.GetUtcNow() - slot;
        SlotBudget? budget = Budget(waveform, late);
        IonoReading? reading = Reading();
        PskReading? spots = Spots();
        IReadOnlyList<MailcastFrame> ionoFrames = Sendable(reading) ? ReadingFrames(reading!, store.NextEsi) : [];
        IReadOnlyList<MailcastFrame> spotFrames = Sendable(spots) ? ReadingFrames(spots!, store.NextEsi) : [];
        IReadOnlyList<MailcastFrame> extras = [.. ionoFrames, .. spotFrames];
        SlotBroadcast broadcast = store.Plan(slot, Seed(slot), compression, options, budget, waveform?.Mode, extras);
        if (broadcast.BulletinFrames == 0 && !evenIfNothingDue)
        {
            return new SlotPlan(slot, [], broadcast.Directory.Entries.Count, null, waveform, reading, PskReporter: spots);
        }
        var frames = broadcast.Frames.Select(f => new SlotFrame(f.ToBytes(), f.ObjectId, f.EncodingSymbolId)).ToList();
        bool carried = broadcast.ExtraFrames > 0;
        return new SlotPlan(slot, frames, broadcast.Directory.Entries.Count, broadcast, waveform, reading, carried ? ionoFrames.Count : 0, spots, carried ? spotFrames.Count : 0);
    }

    /// <summary>Whether a reading goes on the air: it has a sounding, and one under <see cref="OldestSentMinutes"/> old.</summary>
    public static bool Sendable(IonoReading? reading) =>
        reading is { HasSounding: true, AgeMinutes: < OldestSentMinutes };

    /// <summary>
    /// The reading's frames: <see cref="IonosphereFrames"/> fresh ESIs from where the object last
    /// got to (<paramref name="nextEsi"/>, by object ID; 0 for a new one), each one that rebuilds
    /// the object on its own. The object normally changes every slot, as its age does, so these
    /// are ESI 0 and 1; if the same object comes round again its ESIs carry on, never repeat.
    /// </summary>
    public static IReadOnlyList<MailcastFrame> ReadingFrames(IonoReading reading, Func<ulong, uint>? nextEsi = null) =>
        RecordFrames(IonoRecord.ToTransferObject(reading), IonosphereFrames, nextEsi);

    /// <summary>Whether a PSK Reporter reading goes on the air: it has an observation, UNKNOWN or not, under <see cref="OldestSentMinutes"/> old.</summary>
    public static bool Sendable(PskReading? reading) =>
        reading is { HasObservation: true, AgeMinutes: < OldestSentMinutes };

    /// <summary>The PSK Reporter reading's frames, the same way as the ionosonde's: <see cref="PskReporterFrames"/> fresh ESIs, each one that rebuilds the object on its own.</summary>
    public static IReadOnlyList<MailcastFrame> ReadingFrames(PskReading reading, Func<ulong, uint>? nextEsi = null) =>
        RecordFrames(PskRecord.ToTransferObject(reading), PskReporterFrames, nextEsi);

    private static List<MailcastFrame> RecordFrames(TransferObject transfer, int count, Func<ulong, uint>? nextEsi)
    {
        var frames = new List<MailcastFrame>(count);
        for (uint esi = nextEsi?.Invoke(transfer.ObjectId) ?? 0; frames.Count < count && esi <= Mailcast.RaptorQ.PayloadId.MaxEncodingSymbolId; esi++)
        {
            var frame = transfer.Frame(esi);
            if (RebuildsAlone(frame, transfer))
            {
                frames.Add(frame);
            }
        }
        return frames;
    }

    private static bool RebuildsAlone(MailcastFrame frame, TransferObject transfer)
    {
        var decoder = new Mailcast.RaptorQ.ObjectDecoder(frame.Oti);
        decoder.Add(new Mailcast.RaptorQ.PayloadId(0, frame.EncodingSymbolId), frame.Symbol.Span);
        return decoder.TryDecode() is { } rebuilt && rebuilt.AsSpan().SequenceEqual(transfer.Bytes);
    }

    private PskReading? Spots()
    {
        if (pskReporter is null)
        {
            return null;
        }
        try
        {
            return pskReporter();
        }
#pragma warning disable CA1031 // observe only: a reading that cannot be had is no reading, never a failed slot
        catch (Exception)
#pragma warning restore CA1031
        {
            return PskReading.None;
        }
    }

    private IonoReading? Reading()
    {
        if (ionosphere is null)
        {
            return null;
        }
        try
        {
            return ionosphere();
        }
#pragma warning disable CA1031 // observe only: a reading that cannot be had is no reading, never a failed slot
        catch (Exception)
#pragma warning restore CA1031
        {
            return IonoReading.None;
        }
    }

    /// <summary>
    /// The budget rule's budget for a slot on <paramref name="waveform"/> that starts
    /// <paramref name="late"/> after its time; null under the shares rule.
    /// </summary>
    public SlotBudget? Budget(SlotWaveform? waveform, TimeSpan late = default)
    {
        if (options.Budget is null)
        {
            return null;
        }
        if (waveform is null || fillLimit is null)
        {
            throw new InvalidOperationException("The budget rule needs each slot's waveform and the fill limit.");
        }
        var model = new SlotAirtime(_settings, waveform.Airtime);
        return new SlotBudget(fillLimit(late > TimeSpan.Zero ? late : TimeSpan.Zero), model.ForPayloads);
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
