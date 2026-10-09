using Packet.Mailcast;

namespace Mailcast.Receiver;

/// <summary>What a piece told the receiver about the object it belongs to (issue #48).</summary>
public enum PieceStatus
{
    /// <summary>The frame did not parse as a mailcast piece, or its CRC was bad.</summary>
    NotRecognised,

    /// <summary>The object's compression dictionary is not one this receiver has.</summary>
    UnknownDictionary,

    /// <summary>A piece this receiver did not already hold.</summary>
    New,

    /// <summary>A piece already held, or the object it belongs to was already rebuilt.</summary>
    AlreadyHeld,

    /// <summary>This piece completed the object.</summary>
    CompletedObject,

    /// <summary>The piece, or the object it completed, failed a check.</summary>
    Rejected,
}

/// <summary>
/// One mailcast piece as heard, for the status page's "what was heard" view and
/// <c>GET /api/slots/{slot}/frames</c> (issue #48).
/// </summary>
/// <param name="Heard">When the intake worker handled it.</param>
/// <param name="Esi">The piece number (RaptorQ encoding symbol ID), null for a frame that did not parse.</param>
/// <param name="Status">Whether the piece was new, already held, completed its object, or something else.</param>
/// <param name="CrcGood">Whether the frame's own CRC checked out.</param>
/// <param name="What">
/// What the piece belongs to, in words: a bulletin's BID and title, a directory, a propagation
/// reading and its source, or a content type this receiver does not handle. "unknown object" if
/// nothing is known about it yet (no directory entry, and it has not completed).
/// </param>
public sealed record HeardPiece(DateTimeOffset Heard, uint? Esi, PieceStatus Status, bool CrcGood, string What)
{
    /// <summary>
    /// Builds a <see cref="HeardPiece"/> from what the store made of a frame
    /// (<see cref="AcceptResult"/>), naming the object from the completed object itself when the
    /// piece completed one, or from <paramref name="directory"/> when it is still in progress.
    /// </summary>
    public static HeardPiece From(AcceptResult result, uint? esi, DateTimeOffset heard, BroadcastDirectory? directory)
    {
        ArgumentNullException.ThrowIfNull(result);
        bool crcGood = result.Outcome != FrameOutcome.NotAFrame;
        PieceStatus status = result.Outcome switch
        {
            FrameOutcome.NotAFrame => PieceStatus.NotRecognised,
            FrameOutcome.UnknownDictionary => PieceStatus.UnknownDictionary,
            FrameOutcome.Stored => PieceStatus.New,
            FrameOutcome.Duplicate or FrameOutcome.AlreadyComplete => PieceStatus.AlreadyHeld,
            FrameOutcome.Rejected => PieceStatus.Rejected,
            _ => PieceStatus.CompletedObject,
        };
        string what = result.Outcome switch
        {
            FrameOutcome.NotAFrame => "not a mailcast frame, or its CRC was bad",
            FrameOutcome.CompletedBulletin when result.Bulletin is { } bulletin => $"bulletin {bulletin.Bid}, \"{bulletin.Title}\"",
            FrameOutcome.CompletedDirectory or FrameOutcome.AlreadyComplete when result.Directory is { } dir =>
                $"directory for {dir.Date:yyyy-MM-dd}",
            FrameOutcome.CompletedIonosphere => "propagation reading (ionosonde)",
            FrameOutcome.CompletedPskReporter => "propagation reading (PSK Reporter)",
            FrameOutcome.CompletedUnhandled or FrameOutcome.CompletedUnknown when result.ContentType is { } type =>
                ContentType.Describe(type),
            _ => DescribeFromDirectory(result.ObjectId, directory),
        };
        return new HeardPiece(heard, esi, status, crcGood, what);
    }

    /// <summary>
    /// Names a piece still in progress from the directory entry that matches its object ID, when
    /// there is one: a bulletin's BID and title, or its content type in words. "object &lt;id&gt;"
    /// for one not in a directory this receiver has heard (an old object, or one from a directory
    /// not heard yet); "unknown object" without even the object ID, which should not happen for a
    /// frame that parsed.
    /// </summary>
    private static string DescribeFromDirectory(ulong? objectId, BroadcastDirectory? directory)
    {
        if (objectId is not { } id)
        {
            return "unknown object";
        }
        var entry = directory?.Entries.FirstOrDefault(e => e.ObjectId == id);
        if (entry is null)
        {
            return $"object {ObjectId.Format(id)}";
        }
        return entry.Type == (byte)ObjectKind.Bulletin
            ? $"bulletin {entry.Bid}, \"{entry.Title}\""
            : ContentType.Describe(entry.Type);
    }
}

/// <summary>One burst's pieces, grouped the way the modem decoded them (issue #48).</summary>
/// <param name="Started">When this receiver's autobaud locked to the burst, from <see cref="BurstWatch"/>.</param>
/// <param name="Waveform">The waveform the burst came on, by pdn-soundmodem's name (<see cref="Waveform.Name"/>); null if not known.</param>
/// <param name="SnrDb">
/// Signal to noise in 3 kHz for the slot this burst is in, once the slot's channel measurement
/// has run (<see cref="ChannelWatch.Measured"/>); null until then, or if the slot is never measured.
/// </param>
/// <param name="Pieces">The pieces read from this burst, in the order they were heard.</param>
public sealed record HeardBurstPieces(DateTimeOffset Started, string? Waveform, double? SnrDb, IReadOnlyList<HeardPiece> Pieces);

/// <summary>One slot's bursts and pieces (issue #48).</summary>
/// <param name="Slot">The slot's identity, from <see cref="SlotTracker"/>: its scheduled start, or when it began without a schedule.</param>
/// <param name="Bursts">The slot's bursts, oldest first.</param>
public sealed record HeardSlotPieces(DateTimeOffset Slot, IReadOnlyList<HeardBurstPieces> Bursts);

/// <summary>
/// Keeps a bounded, in-memory drill-down of what each recent slot carried: its bursts, and each
/// burst's pieces, for the status page's "what was heard" section and
/// <c>GET /api/slots/{slot}/frames</c> (issue #48).
/// </summary>
/// <remarks>
/// <para>Recorded from the intake worker (<see cref="Intake.Piece"/>), never the audio thread:
/// the audio side only ever hands off a lightweight, already-published snapshot
/// (<see cref="BurstWatch.Shown"/> for a burst's identity and waveform, <see cref="ChannelReport"/>
/// for its SNR once measured), and this class does the recording and bookkeeping on the worker.</para>
/// <para>This is a live debugging view, not an archive: it does not survive a restart, and
/// nothing here is written to disk.</para>
/// <para>Bounded two ways, whichever binds first: at most <see cref="MostSlots"/> slots, and at
/// most <see cref="MostFrames"/> pieces in total. The oldest slot is dropped first, but the
/// newest slot is never dropped even if it alone holds more than <see cref="MostFrames"/> pieces.</para>
/// </remarks>
public sealed class SlotFrameHistory
{
    /// <summary>The most slots kept.</summary>
    public const int MostSlots = 6;

    /// <summary>The most pieces kept across every slot.</summary>
    public const int MostFrames = 2000;

    private readonly object _gate = new();
    private readonly List<WorkingSlot> _slots = [];
    private int _totalPieces;

    private sealed class WorkingSlot(DateTimeOffset slot)
    {
        public DateTimeOffset Slot { get; } = slot;
        public List<WorkingBurst> Bursts { get; } = [];
    }

    private sealed class WorkingBurst(DateTimeOffset started, string? waveform)
    {
        public DateTimeOffset Started { get; } = started;
        public string? Waveform { get; } = waveform;
        public double? SnrDb { get; set; }
        public List<HeardPiece> Pieces { get; } = [];
    }

    /// <summary>
    /// Records one piece. <paramref name="slot"/> is the slot it belongs to, from
    /// <see cref="SlotTracker"/>. <paramref name="burstStarted"/> is the burst it came in, from
    /// the audio thread's own published snapshot (<see cref="BurstWatch.Shown"/>); null puts the
    /// piece in a burst of its own, keyed by when it was heard, rather than losing it.
    /// </summary>
    public void Record(DateTimeOffset slot, DateTimeOffset? burstStarted, string? waveform, HeardPiece piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        lock (_gate)
        {
            var working = _slots.Count > 0 && _slots[^1].Slot == slot ? _slots[^1] : NewSlot(slot);
            var burstKey = burstStarted ?? piece.Heard;
            var burst = working.Bursts.Count > 0 && working.Bursts[^1].Started == burstKey
                ? working.Bursts[^1]
                : NewBurst(working, burstKey, waveform);
            burst.Pieces.Add(piece);
            _totalPieces++;
            Trim();
        }
    }

    private WorkingSlot NewSlot(DateTimeOffset slot)
    {
        var working = new WorkingSlot(slot);
        _slots.Add(working);
        return working;
    }

    private static WorkingBurst NewBurst(WorkingSlot slot, DateTimeOffset started, string? waveform)
    {
        var burst = new WorkingBurst(started, waveform);
        slot.Bursts.Add(burst);
        return burst;
    }

    /// <summary>Drops the oldest slot while either bound is exceeded, keeping at least the newest slot.</summary>
    private void Trim()
    {
        while (_slots.Count > 1 && (_slots.Count > MostSlots || _totalPieces > MostFrames))
        {
            var removed = _slots[0];
            _slots.RemoveAt(0);
            _totalPieces -= removed.Bursts.Sum(b => b.Pieces.Count);
        }
    }

    /// <summary>
    /// A slot's channel measurement has finished (<see cref="ChannelWatch.Measured"/>, itself off
    /// the audio thread): fills in the SNR for that slot's bursts that do not have one yet. A
    /// slot no longer kept is quietly ignored.
    /// </summary>
    public void OnChannelMeasured(ChannelReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            var working = _slots.FirstOrDefault(s => s.Slot == report.Slot);
            if (working is null)
            {
                return;
            }
            foreach (var burst in working.Bursts)
            {
                burst.SnrDb ??= report.SnrDb;
            }
        }
    }

    /// <summary>The slots kept, oldest first.</summary>
    public IReadOnlyList<HeardSlotPieces> Slots
    {
        get
        {
            lock (_gate)
            {
                return [.. _slots.Select(Snapshot)];
            }
        }
    }

    /// <summary>One slot, by its identity, or null if it is not (or no longer) kept.</summary>
    public HeardSlotPieces? Slot(DateTimeOffset slot)
    {
        lock (_gate)
        {
            var working = _slots.FirstOrDefault(s => s.Slot == slot);
            return working is null ? null : Snapshot(working);
        }
    }

    private static HeardSlotPieces Snapshot(WorkingSlot slot) =>
        new(slot.Slot, [.. slot.Bursts.Select(b => new HeardBurstPieces(b.Started, b.Waveform, b.SnrDb, [.. b.Pieces]))]);
}
