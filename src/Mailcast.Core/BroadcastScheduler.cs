namespace Mailcast.Core;

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
    /// in an average hour.
    /// </summary>
    public static ScheduleOptions Hourly { get; } = new()
    {
        SlotMinutes = 60,
        SlotShares = [1.5, 0.7, 0.7, 0.7, 0.7],
        SlotOffsets = [0, 5, 10, 17, 25],
        CarryOverSlots = 3,
        ExtraSymbols = 2,
    };

    /// <summary>How many slots, counting its first, a bulletin stays in rotation.</summary>
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
    internal SlotBroadcast(DateTimeOffset slot, BroadcastDirectory directory, IReadOnlyList<ScheduledObject> objects, IReadOnlyList<MailcastFrame> frames, IReadOnlyList<Bulletin> skipped)
    {
        Slot = slot;
        Directory = directory;
        Objects = objects;
        Frames = frames;
        Skipped = skipped;
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

    /// <summary>Whether a bulletin is in rotation in a slot: carried in it, or still owed from a carrying.</summary>
    public static bool InRotation(CarriedBulletin carried, DateTimeOffset slot, ScheduleOptions options) =>
        SlotIndex(carried, slot, options) is var i && i >= 0 && i < options.SlotsInRotation;

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
            .Where(b => b.Index >= 0 && b.Index < options.SlotsInRotation)
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
            uint first = index == 0 ? 0 : (uint)DueBySlot(transfer.SourceSymbols, index - 1, options);
            carried.Add(new CarriedBulletin(bulletin.Bid, bulletin.Title, serialized.Length, firstSeen, transfer, first, firstSlot));
        }
        var plan = Plan(carried, slot, seed, compression, options);
        return new SlotBroadcast(plan.Slot, plan.Directory, plan.Objects, plan.Frames, skipped);
    }

    /// <summary>Plans a day's slot at midnight UTC: <see cref="Plan(IEnumerable{CarriedBulletin}, DateTimeOffset, int, Compression, ScheduleOptions?, Func{ulong, uint}?)"/>.</summary>
    public static SlotBroadcast Plan(IEnumerable<CarriedBulletin> carried, DateOnly today, int seed, Compression compression, ScheduleOptions? options = null, Func<ulong, uint>? directoryNextEsi = null) =>
        Plan(carried, Midnight(today), seed, compression, options, directoryNextEsi);

    /// <summary>
    /// Plans a slot from the bulletins in rotation. Each sends what is due by the end of this
    /// slot (its carryings so far) less what it has already sent, starting at its
    /// <see cref="CarriedBulletin.NextEsi"/>, so frames an earlier slot did not send are made up
    /// with fresh ESIs; the result's <see cref="ScheduledObject.NextEsi"/> is where to start next
    /// time. A bulletin not yet carried is in its first slot. Of two entries with the same BID,
    /// only the first in BID order is sent.
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
    public static SlotBroadcast Plan(IEnumerable<CarriedBulletin> carried, DateTimeOffset slot, int seed, Compression compression, ScheduleOptions? options = null, Func<ulong, uint>? directoryNextEsi = null)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(compression);
        options ??= new ScheduleOptions();
        Validate(options);

        // This slot's bulletins in a fixed order, so the plan does not depend on the caller's order.
        var inRotation = carried
            .Select(c => (Carried: c, Index: SlotIndex(c, slot, options)))
            .Where(c => c.Index >= 0 && c.Index < options.SlotsInRotation)
            .OrderBy(c => c.Carried.Bid.ToUpperInvariant(), StringComparer.Ordinal)
            .ThenBy(c => c.Index)
            .ThenBy(c => c.Carried.Transfer.ObjectId)
            .ToList();

        var scheduled = new List<ScheduledObject>();
        var entries = new List<DirectoryEntry>();
        var takenBids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenIds = new HashSet<ulong>();
        foreach (var (c, index) in inRotation)
        {
            if (!takenBids.Add(c.Bid) || !takenIds.Add(c.Transfer.ObjectId))
            {
                continue;
            }
            // Everything due up to and including this slot, less what has gone already. For a head
            // end whose every slot went out whole that is exactly this slot's share; frames a
            // cut-short or skipped slot did not send are added to the next slot's, and a second plan
            // of the same slot sends only what the first did not.
            uint due = (uint)DueBySlot(c.Transfer.SourceSymbols, index, options);
            int count = c.NextEsi >= due ? 0 : (int)(due - c.NextEsi);
            scheduled.Add(new ScheduledObject(c.Transfer, index, c.NextEsi, count, c.Bid));
            entries.Add(new DirectoryEntry(c.Transfer.ObjectId, c.Transfer.DictionaryId, c.Size, c.Bid, c.Title));
        }

        var today = DateOnly.FromDateTime(slot.UtcDateTime);
        var directory = new BroadcastDirectory(today, entries);
        var directoryObject = TransferObject.ForDirectory(directory, options.DictionaryId, compression, options.SymbolSize, options.Alignment);
        int bulletinFrames = scheduled.Sum(s => s.Count);
        int directoryFrames = Math.Max(
            directoryObject.SourceSymbols + options.DirectoryExtra,
            (int)Math.Ceiling(bulletinFrames / (double)(options.DirectoryEvery - 1)));
        uint directoryFirst = directoryNextEsi?.Invoke(directoryObject.ObjectId) ?? 0;
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
    }
}
