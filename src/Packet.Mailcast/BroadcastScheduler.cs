namespace Packet.Mailcast;

/// <summary>A bulletin offered for broadcast, with the day the head end first saw it.</summary>
/// <param name="Bulletin">The bulletin.</param>
/// <param name="FirstSeen">The day the head end first saw it.</param>
/// <param name="FirstSlot">
/// The slot it was first carried in. Null counts it as first carried at midnight UTC on
/// <paramref name="FirstSeen"/>, so a daily station counts its carrying days from that day.
/// </param>
public sealed record BroadcastBulletin(Bulletin Bulletin, DateOnly FirstSeen, DateTimeOffset? FirstSlot = null);

/// <summary>
/// A bulletin in rotation at the head end: its object exactly as prepared when it was first
/// seen, and the next ESI to send. <see cref="HeadEndStore"/> keeps these from slot to slot.
/// </summary>
/// <param name="Bid">The bulletin ID.</param>
/// <param name="Title">The subject line, for the directory.</param>
/// <param name="Size">The serialised bulletin's length, for the directory.</param>
/// <param name="FirstSeen">The day the head end first saw it, which counts for <see cref="ScheduleOptions.RememberDays"/>.</param>
/// <param name="Transfer">The compressed, coded object.</param>
/// <param name="NextEsi">The first ESI not yet sent.</param>
/// <param name="FirstSlot">
/// The start of the slot it was first carried in, or null while it has not been: the next slot
/// planned is then its first.
/// </param>
public sealed record CarriedBulletin(string Bid, string Title, int Size, DateOnly FirstSeen, TransferObject Transfer, uint NextEsi, DateTimeOffset? FirstSlot = null);

/// <summary>
/// How slots are filled under the budget rule: each slot is filled up to an airtime budget with
/// fresh symbols of every bulletin in rotation, the least covered first.
/// </summary>
/// <remarks>
/// A bulletin's coverage is the symbols sent of it so far divided by its K. Each slot gives the
/// next symbol to whichever bulletin in rotation has the least coverage and room left, so new
/// bulletins come first and the rest share what is left evenly. No bulletin gets more than
/// <see cref="SlotCap"/> K in one slot, which spreads it over the day's slots rather than
/// spending a whole slot on it, and it leaves the rotation once it has had
/// <see cref="RetireCoverage"/> K in all, or <see cref="RetireAfter"/> after its first slot,
/// whichever comes first. So that a listener who hears only a few slots still rebuilds each
/// bulletin, every slot first gives each bulletin in rotation a floor, the least covered first,
/// enough that any <see cref="SpreadSlots"/> slots carry K and a spare piece; only then does
/// the rest of the budget go, again the least covered first, up to the cap.
/// </remarks>
public sealed record BudgetRule
{
    /// <summary>
    /// The floor each slot gives each bulletin in rotation, while the budget allows: a
    /// <see cref="SpreadSlots"/>th of K plus a spare piece, so that any that many slots rebuild it.
    /// </summary>
    public int SpreadSlots { get; init; } = 3;

    /// <summary>The most of one bulletin a slot carries, as a multiple of its K (at least one symbol).</summary>
    public double SlotCap { get; init; } = 0.6;

    /// <summary>A bulletin retires once this many K of it have been sent in all.</summary>
    public double RetireCoverage { get; init; } = 6.0;

    /// <summary>A bulletin retires this long after its first slot, whatever its coverage.</summary>
    public TimeSpan RetireAfter { get; init; } = TimeSpan.FromHours(36);
}

/// <summary>
/// One slot's airtime budget under <see cref="ScheduleOptions.Budget"/>: the most it may be on the
/// air, and how long a slot of frames would take.
/// </summary>
/// <param name="Limit">The most the slot may take, as <paramref name="Airtime"/> counts it.</param>
/// <param name="Airtime">
/// How long a slot of frames with these payload lengths (in sending order, the frames as
/// <see cref="MailcastFrame.ToBytes"/> gives them) takes on the air, tone and idents included.
/// </param>
public sealed record SlotBudget(TimeSpan Limit, Func<IReadOnlyList<int>, TimeSpan> Airtime);

/// <summary>How much of each bulletin goes out in which slot.</summary>
/// <remarks>
/// A bulletin is carried in <see cref="SlotShares"/>.Count slots: the first slot planned after it
/// is taken in, then the slots <see cref="SlotOffsets"/> after that. Each carrying sends fresh
/// ESIs, never repeats, so a receiver's pieces from different slots add up. The defaults are a
/// daily station's: three days running, most on the first.
/// </remarks>
public sealed record ScheduleOptions
{
    /// <summary>Minutes from one slot to the next: 1440 for a daily station, 60 for an hourly one.</summary>
    public int SlotMinutes { get; init; } = 1440;

    /// <summary>
    /// Symbols for each carrying, as a multiple of the bulletin's K (its source symbols), the
    /// first entry for its first slot. Every carrying gets at least one symbol whatever its share.
    /// </summary>
    public IReadOnlyList<double> SlotShares { get; init; } = [1.4, 0.3, 0.3];

    /// <summary>
    /// Which slots each carrying is in, counted from the first (so the first entry is 0), one for
    /// each of <see cref="SlotShares"/> and rising.
    /// </summary>
    public IReadOnlyList<int> SlotOffsets { get; init; } = [0, 1, 2];

    /// <summary>
    /// Slots after a bulletin's last carrying in which it may still make up pieces that a skipped
    /// or cut-short slot did not send. Past them it leaves the rotation.
    /// </summary>
    public int CarryOverSlots { get; init; }

    /// <summary>Spare symbols on top of the first carrying's share, which matter most for the smallest bulletins.</summary>
    public int ExtraSymbols { get; init; } = 1;

    /// <summary>One frame in this many carries the directory.</summary>
    public int DirectoryEvery { get; init; } = 25;

    /// <summary>Directory symbols beyond its K, at least, in each slot.</summary>
    public int DirectoryExtra { get; init; } = 2;

    /// <summary>The zstd dictionary to compress with.</summary>
    public ushort DictionaryId { get; init; } = ZstdDictionary.Gb7rdg1Id;

    /// <summary>Bulletins whose serialised form is longer than this are not broadcast.</summary>
    public int MaxBulletinSize { get; init; } = 32 * 1024;

    /// <summary>
    /// How many days, counting the first, the head end remembers a bulletin, so that one offered
    /// again after its carrying slots is not sent as new.
    /// </summary>
    public int RememberDays { get; init; } = 14;

    /// <summary>The RaptorQ symbol size.</summary>
    public int SymbolSize { get; init; } = MailcastFrame.StandardSymbolSize;

    /// <summary>The RaptorQ symbol alignment.</summary>
    public int Alignment { get; init; } = MailcastFrame.StandardAlignment;

    /// <summary>
    /// An hourly station's carrying: enough for a clean rebuild from the first slot alone (1.5 K
    /// and two spare symbols, which survives a fifth of the frames lost), then four repeats of
    /// 0.7 K, 5, 10, 17 and 25 hours later, so a bulletin goes out in five different hours of the
    /// day over a little more than a day. A web SDR receiver hears every third slot; two of the
    /// repeats fall in each of the two sets of every third slot that miss the first, so whichever
    /// set it hears, it gets at least 1.4 K. On GB7RDG's volume this is about 3 minutes on the air
    /// in an average hour (a little more with the directory's wider spread, issue #69).
    /// </summary>
    public static ScheduleOptions Hourly { get; } = new()
    {
        SlotMinutes = 60,
        SlotShares = [1.5, 0.7, 0.7, 0.7, 0.7],
        SlotOffsets = [0, 5, 10, 17, 25],
        CarryOverSlots = 3,
        ExtraSymbols = 2,
    };

    /// <summary>
    /// The head end's timetable, which the directory carries so receivers can follow it. With a
    /// daylight rule, a carrying whose slot does not run (it is dark) moves to the next slot
    /// that does, never to one an earlier carrying already has, and <see cref="CarryOverSlots"/>
    /// counts only slots that run. Null for none: every slot runs, and the directory says nothing.
    /// </summary>
    public SlotTimetable? Timetable { get; init; }

    /// <summary>
    /// An hourly station that sends in daylight only. All of a day's carrying then has to fit in
    /// the daylight slots, only 5 of them at GB7RDG in December, so each bulletin is carried less:
    /// 1.2 K and two spare symbols in its first slot, enough to rebuild it from that slot alone
    /// with about a fifth of the frames lost, then one repeat of 0.4 K 5 hours later (moved on to
    /// the next daylight slot when that is dark) for a receiver that lost more. On GB7RDG's volume
    /// that is about 4 minutes on the air in an average daylight slot in December (a little more
    /// with the directory's wider spread, issue #69), under 4 in October and about 2 in June; the
    /// hourly station's five carryings would need more than the 10 minute hard stop allows in
    /// every December slot.
    /// </summary>
    public static ScheduleOptions HourlyDaylight { get; } = Hourly with
    {
        SlotShares = [1.2, 0.4],
        SlotOffsets = [0, 5],
    };

    /// <summary>
    /// The budget rule, which fills each slot to an airtime budget (see <see cref="BudgetRule"/>)
    /// in place of <see cref="SlotShares"/> and <see cref="SlotOffsets"/>. Null for the shares rule.
    /// </summary>
    public BudgetRule? Budget { get; init; }

    /// <summary>
    /// An hourly station that fills each slot to its airtime budget: the budget rule with its
    /// defaults, which is GB7RDG's.
    /// </summary>
    public static ScheduleOptions HourlyBudget { get; } = HourlyDaylight with { Budget = new BudgetRule() };

    /// <summary>How many slots, counting its first, a bulletin stays in rotation, without a daylight rule.</summary>
    public int SlotsInRotation => SlotOffsets[^1] + 1 + CarryOverSlots;
}

/// <summary>One object's part in a slot.</summary>
/// <param name="Transfer">The object.</param>
/// <param name="SlotIndex">0 in the slot it was first carried in, then the slots since; the directory is always 0.</param>
/// <param name="FirstEsi">The first ESI sent in this slot.</param>
/// <param name="Count">How many symbols are sent in this slot, with consecutive ESIs.</param>
/// <param name="Bid">The bulletin's BID, or null for the directory.</param>
public sealed record ScheduledObject(TransferObject Transfer, int SlotIndex, uint FirstEsi, int Count, string? Bid = null)
{
    /// <summary>The first ESI for the object's next slot.</summary>
    public uint NextEsi => FirstEsi + (uint)Count;
}

/// <summary>One slot's plan: the directory, every object's share, and the frames in sending order.</summary>
public sealed class SlotBroadcast
{
    internal SlotBroadcast(DateTimeOffset slot, BroadcastDirectory directory, IReadOnlyList<ScheduledObject> objects, IReadOnlyList<MailcastFrame> frames, IReadOnlyList<Bulletin> skipped, IReadOnlyList<MailcastFrame>? extras = null)
    {
        Slot = slot;
        Directory = directory;
        Objects = objects;
        Frames = frames;
        Skipped = skipped;
        ExtraFrames = extras?.Count ?? 0;
        ExtraObjects = extras is null ? [] : [.. extras.Select(f => f.ObjectId).Distinct()];
    }

    /// <summary>The slot's start.</summary>
    public DateTimeOffset Slot { get; }

    /// <summary>The slot's day, UTC.</summary>
    public DateOnly Date => DateOnly.FromDateTime(Slot.UtcDateTime);

    /// <summary>The objects in rotation in this slot.</summary>
    public BroadcastDirectory Directory { get; }

    /// <summary>Each object's share of this slot, the directory first.</summary>
    public IReadOnlyList<ScheduledObject> Objects { get; }

    /// <summary>The frames in sending order.</summary>
    public IReadOnlyList<MailcastFrame> Frames { get; }

    /// <summary>How many of <see cref="Frames"/> carry bulletins rather than the directory.</summary>
    public int BulletinFrames => Objects.Where(o => o.Bid is not null).Sum(o => o.Count);

    /// <summary>
    /// How many of <see cref="Frames"/> are the extra frames the plan was given (the ionosonde
    /// reading): in <see cref="Frames"/> and the airtime, but not in <see cref="Objects"/> or the directory.
    /// </summary>
    public int ExtraFrames { get; }

    /// <summary>
    /// The objects of the extra frames. <see cref="HeadEndStore.Commit(SlotBroadcast, int)"/>
    /// keeps the next ESI of each by object ID, as for a directory, so an object sent again
    /// never repeats an ESI.
    /// </summary>
    public IReadOnlyList<ulong> ExtraObjects { get; }

    /// <summary>Bulletins in their carrying slots that were left out: too large, or a BID already taken.</summary>
    public IReadOnlyList<Bulletin> Skipped { get; }
}

/// <summary>
/// Plans the head end's slots. A plan is a pure function of the bulletins, their first slots,
/// the slot, the options and a seed.
/// </summary>
/// <remarks>
/// <para>
/// Each bulletin is carried in <see cref="ScheduleOptions.SlotShares"/>.Count slots, the first
/// slot planned after it is taken in and then the ones <see cref="ScheduleOptions.SlotOffsets"/> after it.
/// Its first slot gets enough for a rebuild on its own and later ones continue with fresh ESIs,
/// never repeats, so a receiver's pieces from different slots add up. The object is compressed
/// once, when the bulletin is first seen, and the same octets are sent every time:
/// <see cref="HeadEndStore"/> keeps them and the next ESI.
/// </para>
/// <para>
/// The frames are interleaved so that every object is spread over the whole slot: object j
/// with n frames puts its i-th frame at position (i + u) / n, u a random phase for the object,
/// and the frames are sent in order of position. When every object has the same number of frames
/// that is plain round robin; in general it means a fade takes a little from everyone rather
/// than all of one small bulletin. The directory's frames go in evenly spaced places, the first
/// frame of the slot and then at least one in every <see cref="ScheduleOptions.DirectoryEvery"/>.
/// There are enough of them that a station decoding only a quarter of the slot's frames still
/// has a good chance of a whole directory from it alone, not just <see cref="ScheduleOptions.DirectoryExtra"/>
/// above its K (see <see cref="DirectoryFloor"/>, issue #69).
/// </para>
/// </remarks>
public static class BroadcastScheduler
{
    /// <summary>How many symbols an object of K source symbols gets in each of its carrying slots.</summary>
    public static int[] SymbolsPerCarrying(int sourceSymbols, ScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceSymbols, 1);
        var counts = new int[options.SlotShares.Count];
        for (int c = 0; c < counts.Length; c++)
        {
            // Each carrying rounded up on its own, so any set of them a receiver hears carries at
            // least the sum of their shares: what a receiver that hears only some slots relies on.
            counts[c] = Math.Max(1, (int)Math.Ceiling((options.SlotShares[c] * sourceSymbols) - 1e-9) + (c == 0 ? options.ExtraSymbols : 0));
        }
        return counts;
    }

    /// <summary>
    /// How many symbols an object of K source symbols is due in total by the end of the slot
    /// <paramref name="slotIndex"/> slots after its first: every carrying at or before it.
    /// </summary>
    public static int DueBySlot(int sourceSymbols, int slotIndex, ScheduleOptions options)
    {
        var counts = SymbolsPerCarrying(sourceSymbols, options);
        int due = 0;
        for (int c = 0; c < counts.Length && options.SlotOffsets[c] <= slotIndex; c++)
        {
            due += counts[c];
        }
        return due;
    }

    /// <summary>
    /// Which slot this is for a bulletin: 0 for its first (or for one not yet carried), then the
    /// slots since, negative for a slot before its first.
    /// </summary>
    public static int SlotIndex(CarriedBulletin carried, DateTimeOffset slot, ScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(options);
        return carried.FirstSlot is DateTimeOffset first ? Since(first, slot, options) : 0;
    }

    private static int Since(DateTimeOffset first, DateTimeOffset slot, ScheduleOptions options)
    {
        long length = TimeSpan.FromMinutes(options.SlotMinutes).Ticks;
        long since = slot.UtcTicks - first.UtcTicks;
        return (int)Math.Clamp(Math.Floor(since / (double)length), int.MinValue, int.MaxValue);
    }

    /// <summary>
    /// Whether a bulletin is in rotation in a slot: carried in it, or still owed from a carrying.
    /// Under the budget rule, whether it has not yet retired (<see cref="Retired"/>).
    /// </summary>
    public static bool InRotation(CarriedBulletin carried, DateTimeOffset slot, ScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Budget is { } rule)
        {
            return !Retired(carried, slot, rule);
        }
        if (carried.FirstSlot is not DateTimeOffset first)
        {
            return true;
        }
        if (options.Timetable?.Daylight is null)
        {
            return SlotIndex(carried, slot, options) is var i && i >= 0 && i < options.SlotsInRotation;
        }
        return slot >= first && slot < RotationEnd(first, options);
    }

    /// <summary>
    /// Whether a bulletin has left the rotation under the budget rule: it has had
    /// <see cref="BudgetRule.RetireCoverage"/> K in all, or its first slot was
    /// <see cref="BudgetRule.RetireAfter"/> or more before <paramref name="slot"/>. One never
    /// carried has no age yet, so a bulletin a head end took in but never sent under this rule
    /// (one an older head end sent without recording its first slot, say) is in rotation from its
    /// next ESI on.
    /// </summary>
    public static bool Retired(CarriedBulletin carried, DateTimeOffset slot, BudgetRule rule)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(rule);
        if (carried.NextEsi >= RetireSymbols(carried.Transfer.SourceSymbols, rule))
        {
            return true;
        }
        return carried.FirstSlot is DateTimeOffset first && slot - first >= rule.RetireAfter;
    }

    /// <summary>How many symbols in all a bulletin of K source symbols retires after.</summary>
    public static int RetireSymbols(int sourceSymbols, BudgetRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Math.Max(1, (int)Math.Ceiling((rule.RetireCoverage * sourceSymbols) - 1e-9));
    }

    /// <summary>The floor a slot gives a bulletin of K source symbols before any gets more: <see cref="BudgetRule.SpreadSlots"/>.</summary>
    public static int FloorSymbols(int sourceSymbols, BudgetRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Math.Min(CapSymbols(sourceSymbols, rule), (sourceSymbols + 1 + rule.SpreadSlots - 1) / rule.SpreadSlots);
    }

    /// <summary>The most symbols of a bulletin of K source symbols that one slot carries.</summary>
    public static int CapSymbols(int sourceSymbols, BudgetRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Math.Max(1, (int)Math.Ceiling((rule.SlotCap * sourceSymbols) - 1e-9));
    }

    /// <summary>
    /// The slot each carrying of a bulletin first carried at <paramref name="first"/> is due in,
    /// one for each of <see cref="ScheduleOptions.SlotShares"/>: <paramref name="first"/>, then
    /// the slots <see cref="ScheduleOptions.SlotOffsets"/> after it. With a daylight rule, a
    /// carrying whose slot does not run moves on to the next slot that does and that no earlier
    /// carrying has, so none is lost and none doubles up. <see cref="DateTimeOffset.MaxValue"/>
    /// for one with no such slot within <see cref="SlotTimetable.SearchDays"/>.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> CarryingSlots(DateTimeOffset first, ScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var length = TimeSpan.FromMinutes(options.SlotMinutes);
        var slots = new DateTimeOffset[options.SlotOffsets.Count];
        slots[0] = first;
        var timetable = options.Timetable;
        for (int c = 1; c < slots.Length; c++)
        {
            var nominal = first + (length * options.SlotOffsets[c]);
            if (timetable?.Daylight is null)
            {
                slots[c] = nominal;
                continue;
            }
            if (slots[c - 1] == DateTimeOffset.MaxValue)
            {
                slots[c] = DateTimeOffset.MaxValue;
                continue;
            }
            var after = slots[c - 1].AddTicks(1);
            slots[c] = timetable.NextActiveAtOrAfter(nominal > after ? nominal : after) ?? DateTimeOffset.MaxValue;
        }
        return slots;
    }

    /// <summary>
    /// The first moment a bulletin first carried at <paramref name="first"/> is out of rotation:
    /// the slot after its last carrying's <see cref="ScheduleOptions.CarryOverSlots"/> carry-over
    /// slots, which with a daylight rule are slots that run.
    /// </summary>
    public static DateTimeOffset RotationEnd(DateTimeOffset first, ScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var length = TimeSpan.FromMinutes(options.SlotMinutes);
        if (options.Timetable?.Daylight is null)
        {
            return first + (length * options.SlotsInRotation);
        }
        var end = CarryingSlots(first, options)[^1];
        for (int i = 0; i < options.CarryOverSlots && end != DateTimeOffset.MaxValue; i++)
        {
            end = options.Timetable!.NextActiveAfter(end) ?? DateTimeOffset.MaxValue;
        }
        return end == DateTimeOffset.MaxValue || DateTimeOffset.MaxValue - end < length ? DateTimeOffset.MaxValue : end + length;
    }

    /// <summary>
    /// How many symbols an object of K source symbols, first carried at <paramref name="first"/>,
    /// is due in total by the end of the slot at <paramref name="slot"/>: every carrying due at or
    /// before it (<see cref="CarryingSlots"/>), or before it with <paramref name="before"/>.
    /// </summary>
    public static int DueAt(int sourceSymbols, DateTimeOffset first, DateTimeOffset slot, ScheduleOptions options, bool before = false)
    {
        var counts = SymbolsPerCarrying(sourceSymbols, options);
        var slots = CarryingSlots(first, options);
        int due = 0;
        for (int c = 0; c < counts.Length; c++)
        {
            if (before ? slots[c] < slot : slots[c] <= slot)
            {
                due += counts[c];
            }
        }
        return due;
    }

    /// <summary>
    /// Plans a day's slot from bulletins alone, the slot at midnight UTC (see
    /// <see cref="Plan(IEnumerable{BroadcastBulletin}, DateTimeOffset, int, Compression, ScheduleOptions?)"/>).
    /// </summary>
    public static SlotBroadcast Plan(IEnumerable<BroadcastBulletin> bulletins, DateOnly today, int seed, Compression compression, ScheduleOptions? options = null) =>
        Plan(bulletins, Midnight(today), seed, compression, options);

    /// <summary>
    /// Plans a slot from bulletins alone, compressing each afresh and taking each one's first ESI
    /// as if every earlier slot had gone out whole. That gives the same frames as a head end that
    /// keeps state, as long as the compression and options are unchanged; a real head end should
    /// use <see cref="HeadEndStore"/> and the other overload.
    /// </summary>
    public static SlotBroadcast Plan(IEnumerable<BroadcastBulletin> bulletins, DateTimeOffset slot, int seed, Compression compression, ScheduleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bulletins);
        ArgumentNullException.ThrowIfNull(compression);
        options ??= new ScheduleOptions();
        Validate(options);
        var carried = new List<CarriedBulletin>();
        var skipped = new List<Bulletin>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = bulletins
            .Select(b => (b.Bulletin, b.FirstSeen, FirstSlot: b.FirstSlot ?? Midnight(b.FirstSeen)))
            .Select(b => (b.Bulletin, b.FirstSeen, b.FirstSlot, Index: Since(b.FirstSlot, slot, options)))
            .Where(b => options.Timetable?.Daylight is null
                ? b.Index >= 0 && b.Index < options.SlotsInRotation
                : slot >= b.FirstSlot && slot < RotationEnd(b.FirstSlot, options))
            .OrderBy(b => b.Bulletin.Bid.ToUpperInvariant(), StringComparer.Ordinal)
            .ThenBy(b => b.Index);
        foreach (var (bulletin, firstSeen, firstSlot, index) in ordered)
        {
            var serialized = bulletin.Serialize();
            if (serialized.Length > options.MaxBulletinSize || !taken.Add(bulletin.Bid))
            {
                skipped.Add(bulletin);
                continue;
            }
            var transfer = TransferObject.ForBulletin(bulletin, options.DictionaryId, compression, options.SymbolSize, options.Alignment);
            uint first = options.Timetable?.Daylight is not null ? (uint)DueAt(transfer.SourceSymbols, firstSlot, slot, options, before: true)
                : index == 0 ? 0 : (uint)DueBySlot(transfer.SourceSymbols, index - 1, options);
            carried.Add(new CarriedBulletin(bulletin.Bid, bulletin.Title, serialized.Length, firstSeen, transfer, first, firstSlot));
        }
        var plan = Plan(carried, slot, seed, compression, options);
        return new SlotBroadcast(plan.Slot, plan.Directory, plan.Objects, plan.Frames, skipped);
    }

    /// <summary>Plans a day's slot at midnight UTC: <see cref="Plan(IEnumerable{CarriedBulletin}, DateTimeOffset, int, Compression, ScheduleOptions?, Func{ulong, uint}?, SlotBudget?, string?)"/>.</summary>
    public static SlotBroadcast Plan(IEnumerable<CarriedBulletin> carried, DateOnly today, int seed, Compression compression, ScheduleOptions? options = null, Func<ulong, uint>? directoryNextEsi = null) =>
        Plan(carried, Midnight(today), seed, compression, options, directoryNextEsi);

    /// <summary>
    /// Plans a slot from the bulletins in rotation. Under the shares rule each sends what is due
    /// by the end of this slot (its carryings so far) less what it has already sent; under the
    /// budget rule (<see cref="ScheduleOptions.Budget"/>) the slot is filled to
    /// <paramref name="budget"/>, the least covered bulletins first. Either way each starts at its
    /// <see cref="CarriedBulletin.NextEsi"/>, so every symbol is fresh and frames an earlier slot
    /// did not send are made up; the result's <see cref="ScheduledObject.NextEsi"/> is where to
    /// start next time. A bulletin not yet carried is in its first slot. Of two entries with the
    /// same BID, only the first in BID order is sent.
    /// </summary>
    /// <param name="carried">The bulletins in rotation, with their objects and next ESIs.</param>
    /// <param name="slot">The slot's start.</param>
    /// <param name="seed">Seeds the interleaving.</param>
    /// <param name="compression">Compresses the directory.</param>
    /// <param name="options">The schedule settings.</param>
    /// <param name="directoryNextEsi">
    /// The first unsent ESI of a directory object, by object ID: <see cref="HeadEndStore.DirectoryNextEsi"/>.
    /// A second plan with the same rotation the same day makes the same directory object, and
    /// carries on with fresh ESIs from here instead of repeating the first plan's. Null starts
    /// every directory at ESI 0.
    /// </param>
    /// <param name="budget">The slot's airtime budget, which the budget rule needs and the shares rule ignores.</param>
    /// <param name="mode">The waveform the slot goes out on, which the directory names; null names none.</param>
    /// <param name="extras">
    /// Frames of objects outside the rotation to send as well (the ionosonde reading), placed
    /// among the others. Under the budget rule their airtime counts against the budget, so they
    /// take the place of bulletin frames; they never make a slot key on their own, and if with
    /// them no bulletin frame would fit where some would without, they are left out.
    /// </param>
    public static SlotBroadcast Plan(IEnumerable<CarriedBulletin> carried, DateTimeOffset slot, int seed, Compression compression, ScheduleOptions? options, Func<ulong, uint>? directoryNextEsi, SlotBudget? budget, string? mode, IReadOnlyList<MailcastFrame>? extras)
    {
        var plan = Plan(carried, slot, seed, compression, options, directoryNextEsi, budget, mode, extras ?? [], out bool anyRoom);
        if (extras is { Count: > 0 } && plan.BulletinFrames == 0 && anyRoom)
        {
            return Plan(carried, slot, seed, compression, options, directoryNextEsi, budget, mode, [], out _);
        }
        return plan;
    }

    /// <summary>
    /// Plans a slot from the bulletins in rotation: the same with no extra frames
    /// (<see cref="Plan(IEnumerable{CarriedBulletin}, DateTimeOffset, int, Compression, ScheduleOptions?, Func{ulong, uint}?, SlotBudget?, string?, IReadOnlyList{MailcastFrame}?)"/>).
    /// </summary>
    public static SlotBroadcast Plan(IEnumerable<CarriedBulletin> carried, DateTimeOffset slot, int seed, Compression compression, ScheduleOptions? options = null, Func<ulong, uint>? directoryNextEsi = null, SlotBudget? budget = null, string? mode = null) =>
        Plan(carried, slot, seed, compression, options, directoryNextEsi, budget, mode, null);

    private static SlotBroadcast Plan(IEnumerable<CarriedBulletin> carried, DateTimeOffset slot, int seed, Compression compression, ScheduleOptions? options, Func<ulong, uint>? directoryNextEsi, SlotBudget? budget, string? mode, IReadOnlyList<MailcastFrame> extras, out bool anyRoom)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(compression);
        options ??= new ScheduleOptions();
        Validate(options);
        if (options.Budget is not null && budget is null)
        {
            throw new ArgumentException("The budget rule needs the slot's airtime budget.", nameof(budget));
        }

        // This slot's bulletins in a fixed order, so the plan does not depend on the caller's order.
        var inRotation = carried
            .Where(c => InRotation(c, slot, options))
            .Select(c => (Carried: c, Index: SlotIndex(c, slot, options)))
            .OrderBy(c => c.Carried.Bid.ToUpperInvariant(), StringComparer.Ordinal)
            .ThenBy(c => c.Index)
            .ThenBy(c => c.Carried.Transfer.ObjectId)
            .ToList();

        var bulletins = new List<(CarriedBulletin Carried, int Index)>();
        var entries = new List<DirectoryEntry>();
        var takenBids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenIds = new HashSet<ulong>();
        foreach (var (c, index) in inRotation)
        {
            if (!takenBids.Add(c.Bid) || !takenIds.Add(c.Transfer.ObjectId))
            {
                continue;
            }
            bulletins.Add((c, index));
            entries.Add(new DirectoryEntry(c.Transfer.ObjectId, c.Transfer.DictionaryId, c.Size, c.Bid, c.Title, (byte)c.Transfer.Kind));
        }

        var today = DateOnly.FromDateTime(slot.UtcDateTime);
        var directory = new BroadcastDirectory(today, entries, options.Timetable, mode);
        var directoryObject = TransferObject.ForDirectory(directory, options.DictionaryId, compression, options.SymbolSize, options.Alignment);
        uint directoryFirst = directoryNextEsi?.Invoke(directoryObject.ObjectId) ?? 0;

        int[] counts;
        anyRoom = false;
        if (options.Budget is { } rule)
        {
            counts = Fill(bulletins.Select(b => b.Carried).ToList(), directoryObject, options, rule, budget!, extras, out anyRoom);
        }
        else
        {
            counts = new int[bulletins.Count];
            for (int i = 0; i < bulletins.Count; i++)
            {
                // Everything due up to and including this slot, less what has gone already. For a head
                // end whose every slot went out whole that is exactly this slot's share; frames a
                // cut-short or skipped slot did not send are added to the next slot's, and a second plan
                // of the same slot sends only what the first did not.
                var c = bulletins[i].Carried;
                uint due = (uint)DueAt(c.Transfer.SourceSymbols, c.FirstSlot ?? slot, slot, options);
                counts[i] = c.NextEsi >= due ? 0 : (int)(due - c.NextEsi);
            }
        }

        while (true)
        {
            var plan = Build(slot, directory, directoryObject, directoryFirst, bulletins, counts, seed, options, extras);
            if (budget is null || options.Budget is null || plan.BulletinFrames == 0
                || budget.Airtime([.. plan.Frames.Select(f => f.ToBytes().Length)]) <= budget.Limit)
            {
                return plan;
            }
            // The fill counts frames in a different order from the one they go in, which only matters
            // when objects have different symbol sizes: if the real order runs over, take a symbol
            // from the best covered bulletin and try again.
            int most = -1;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] > 0 && (most < 0 || Coverage(bulletins[i].Carried, counts[i]) > Coverage(bulletins[most].Carried, counts[most])))
                {
                    most = i;
                }
            }
            counts[most]--;
        }
    }

    private static double Coverage(CarriedBulletin c, int more) => (c.NextEsi + (double)more) / c.Transfer.SourceSymbols;

    /// <summary>
    /// The frame rate issue #69 designs the directory's redundancy for: a weak station that
    /// decodes about a quarter of a slot's frames (G7TAJ, reported 2026-10-09).
    /// </summary>
    private const double WeakStationFrameRate = 0.25;

    /// <summary>
    /// How sure <see cref="DirectoryFloor"/> aims to make it that a station at
    /// <see cref="WeakStationFrameRate"/> gets the whole directory from one slot.
    /// </summary>
    private const double WeakStationConfidence = 0.80;

    /// <summary>
    /// The fewest directory frames so that a station decoding <see cref="WeakStationFrameRate"/>
    /// of a slot's frames gets at least <paramref name="sourceSymbols"/> of them (so the whole
    /// directory, its K being small) with at least <see cref="WeakStationConfidence"/> probability:
    /// the smallest N with P(Binomial(N, <see cref="WeakStationFrameRate"/>) &gt;= sourceSymbols) at
    /// least <see cref="WeakStationConfidence"/>. Found by counting up N from sourceSymbols, since
    /// the directory's K is always small (1 or 2 for GB7RDG's usual 14 to 20 bulletins in rotation),
    /// so this never runs more than a few dozen times.
    /// </summary>
    internal static int DirectoryFloor(int sourceSymbols)
    {
        for (int n = sourceSymbols; ; n++)
        {
            if (BinomialAtLeast(n, sourceSymbols, WeakStationFrameRate) >= WeakStationConfidence)
            {
                return n;
            }
        }
    }

    /// <summary>P(X &gt;= k) for X ~ Binomial(n, p), by building up the probability mass function one term at a time.</summary>
    private static double BinomialAtLeast(int n, int k, double p)
    {
        double term = Math.Pow(1 - p, n); // P(X = 0)
        double below = k > 0 ? term : 0; // P(X < k), built up as P(X = 0) + P(X = 1) + ...
        for (int i = 1; i < k; i++)
        {
            term *= (n - i + 1) / (double)i * p / (1 - p);
            below += term;
        }
        return 1 - below;
    }

    private static int DirectoryFrames(TransferObject directory, int bulletinFrames, ScheduleOptions options) =>
        Math.Max(Math.Max(directory.SourceSymbols + options.DirectoryExtra, DirectoryFloor(directory.SourceSymbols)),
            (int)Math.Ceiling(bulletinFrames / (double)(options.DirectoryEvery - 1)));

    /// <summary>
    /// The budget rule's share of the slot for each bulletin, in order: one symbol at a time to the
    /// bulletin with the least coverage that still has room, ties to the first in order, until the
    /// next symbol would take the slot over budget. Room is first each bulletin's floor
    /// (<see cref="FloorSymbols"/>), then its slot cap, and never past its retiring coverage.
    /// </summary>
    private static int[] Fill(List<CarriedBulletin> bulletins, TransferObject directory, ScheduleOptions options, BudgetRule rule, SlotBudget budget, IReadOnlyList<MailcastFrame> extras, out bool anyRoom)
    {
        var counts = new int[bulletins.Count];
        int[] extraLengths = [.. extras.Select(f => f.ToBytes().Length)];
        var room = new int[bulletins.Count];
        var lengths = new int[bulletins.Count];
        var floor = new int[bulletins.Count];
        for (int i = 0; i < bulletins.Count; i++)
        {
            var c = bulletins[i];
            int left = RetireSymbols(c.Transfer.SourceSymbols, rule) - (int)Math.Min(c.NextEsi, int.MaxValue);
            room[i] = Math.Clamp(left, 0, CapSymbols(c.Transfer.SourceSymbols, rule));
            floor[i] = Math.Min(room[i], FloorSymbols(c.Transfer.SourceSymbols, rule));
            lengths[i] = c.Transfer.Frame(c.NextEsi).ToBytes().Length;
        }
        int directoryLength = directory.Frame(0).ToBytes().Length;
        var bulletinLengths = new List<int>();
        bool Fits()
        {
            int directoryFrames = DirectoryFrames(directory, bulletinLengths.Count, options);
            var all = new List<int>(directoryFrames + bulletinLengths.Count + extraLengths.Length);
            all.AddRange(Enumerable.Repeat(directoryLength, directoryFrames));
            all.AddRange(bulletinLengths);
            all.AddRange(extraLengths);
            return budget.Airtime(all) <= budget.Limit;
        }
        anyRoom = room.Any(r => r > 0);
        foreach (var limit in new[] { floor, room })
        {
            var queue = new PriorityQueue<int, (double Coverage, int Order)>();
            for (int i = 0; i < bulletins.Count; i++)
            {
                if (counts[i] < limit[i])
                {
                    queue.Enqueue(i, (Coverage(bulletins[i], counts[i]), i));
                }
            }
            while (queue.TryDequeue(out int i, out _))
            {
                bulletinLengths.Add(lengths[i]);
                if (!Fits())
                {
                    return counts;
                }
                counts[i]++;
                if (counts[i] < limit[i])
                {
                    queue.Enqueue(i, (Coverage(bulletins[i], counts[i]), i));
                }
            }
        }
        return counts;
    }

    /// <summary>Makes the slot's frames from each bulletin's count: the directory's share, then the interleaving.</summary>
    private static SlotBroadcast Build(DateTimeOffset slot, BroadcastDirectory directory, TransferObject directoryObject, uint directoryFirst, List<(CarriedBulletin Carried, int Index)> bulletins, int[] counts, int seed, ScheduleOptions options, IReadOnlyList<MailcastFrame> extras)
    {
        var scheduled = new List<ScheduledObject>();
        for (int i = 0; i < bulletins.Count; i++)
        {
            var (c, index) = bulletins[i];
            scheduled.Add(new ScheduledObject(c.Transfer, index, c.NextEsi, counts[i], c.Bid));
        }
        int bulletinFrames = scheduled.Sum(s => s.Count);
        int directoryFrames = DirectoryFrames(directoryObject, bulletinFrames, options);
        scheduled.Insert(0, new ScheduledObject(directoryObject, 0, directoryFirst, directoryFrames));

        // Interleave the bulletins by position (i + u) / n.
        var rng = new Random(seed);
        var order = new List<(double Position, int Object, int Index)>();
        for (int j = 1; j < scheduled.Count; j++)
        {
            double phase = rng.NextDouble();
            int n = scheduled[j].Count;
            for (int i = 0; i < n; i++)
            {
                order.Add(((i + phase) / n, j, i));
            }
        }
        order.Sort((a, b) =>
        {
            int c = a.Position.CompareTo(b.Position);
            return c != 0 ? c : a.Object != b.Object ? a.Object.CompareTo(b.Object) : a.Index.CompareTo(b.Index);
        });

        // Then the directory's frames at evenly spaced places, the first at place 0.
        int totalFrames = bulletinFrames + directoryFrames;
        var frames = new List<MailcastFrame>(totalFrames);
        int nextDirectory = 0;
        int nextBulletin = 0;
        for (int place = 0; place < totalFrames; place++)
        {
            if (nextDirectory < directoryFrames && place == (int)((long)nextDirectory * totalFrames / directoryFrames))
            {
                frames.Add(directoryObject.Frame(directoryFirst + (uint)nextDirectory++));
            }
            else
            {
                var o = order[nextBulletin++];
                frames.Add(scheduled[o.Object].Transfer.Frame(scheduled[o.Object].FirstEsi + (uint)o.Index));
            }
        }

        // Then any extra frames, spread evenly, never before the directory's first. A plan with
        // no bulletin frames keys nothing on a schedule; a one-off slot sends them all the same.
        if (extras.Count > 0)
        {
            int before = frames.Count;
            for (int i = extras.Count - 1; i >= 0; i--)
            {
                int place = 1 + (int)((((2L * i) + 1) * before) / (2L * extras.Count));
                frames.Insert(Math.Min(place, frames.Count), extras[i]);
            }
            return new SlotBroadcast(slot, directory, scheduled, frames, [], extras);
        }
        return new SlotBroadcast(slot, directory, scheduled, frames, []);
    }

    /// <summary>Midnight UTC at the start of a day.</summary>
    public static DateTimeOffset Midnight(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    /// <summary>Throws <see cref="ArgumentException"/> for options the scheduler cannot work with.</summary>
    public static void Validate(ScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SlotMinutes < 1)
        {
            throw new ArgumentException("SlotMinutes must be at least 1.", nameof(options));
        }
        if (options.SlotShares.Count < 1 || options.SlotShares.Any(s => s < 0 || double.IsNaN(s)) || options.SlotShares[0] <= 0)
        {
            throw new ArgumentException("There must be at least one slot share, none negative and the first above 0.", nameof(options));
        }
        if (options.SlotOffsets.Count != options.SlotShares.Count || options.SlotOffsets[0] != 0
            || options.SlotOffsets.Zip(options.SlotOffsets.Skip(1)).Any(p => p.Second <= p.First))
        {
            throw new ArgumentException("There must be one slot offset for each slot share, the first 0 and each later one above the one before.", nameof(options));
        }
        if (options.ExtraSymbols < 0 || options.DirectoryExtra < 0 || options.CarryOverSlots < 0)
        {
            throw new ArgumentException("The extras must be at least 0.", nameof(options));
        }
        if (options.DirectoryEvery < 2)
        {
            throw new ArgumentException("DirectoryEvery must be at least 2.", nameof(options));
        }
        if (options.Budget is { } rule
            && (!(rule.SlotCap > 0) || rule.SpreadSlots < 1 || !(rule.RetireCoverage > 0) || rule.RetireAfter <= TimeSpan.Zero
                || double.IsInfinity(rule.SlotCap) || double.IsInfinity(rule.RetireCoverage)))
        {
            throw new ArgumentException("The budget rule's slot cap, spread, retiring coverage and retiring age must all be above 0.", nameof(options));
        }
        if (options.Timetable is { } timetable && timetable.EveryMinutes != options.SlotMinutes)
        {
            throw new ArgumentException("The timetable's interval must be SlotMinutes.", nameof(options));
        }
    }
}
