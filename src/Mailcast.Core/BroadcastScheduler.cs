namespace Mailcast.Core;

/// <summary>A bulletin offered for broadcast, with the day the head end first saw it.</summary>
public sealed record BroadcastBulletin(Bulletin Bulletin, DateOnly FirstSeen);

/// <summary>
/// A bulletin in rotation at the head end: its object exactly as prepared on the day it was first
/// seen, and the next ESI to send. <see cref="HeadEndStore"/> keeps these from day to day.
/// </summary>
/// <param name="Bid">The bulletin ID.</param>
/// <param name="Title">The subject line, for the directory.</param>
/// <param name="Size">The serialised bulletin's length, for the directory.</param>
/// <param name="FirstSeen">The day the head end first saw it.</param>
/// <param name="Transfer">The compressed, coded object.</param>
/// <param name="NextEsi">The first ESI not yet sent.</param>
public sealed record CarriedBulletin(string Bid, string Title, int Size, DateOnly FirstSeen, TransferObject Transfer, uint NextEsi);

/// <summary>How much of each bulletin goes out on which day.</summary>
public sealed record ScheduleOptions
{
    /// <summary>How many days running each bulletin is carried.</summary>
    public int DaysCarried { get; init; } = 3;

    /// <summary>Symbols sent over all days, as a multiple of K, before <see cref="ExtraSymbols"/>.</summary>
    public double TotalOverhead { get; init; } = 2.0;

    /// <summary>Symbols added to each bulletin's total, which matters most for the smallest.</summary>
    public int ExtraSymbols { get; init; } = 1;

    /// <summary>
    /// How the total is split between the days, one share per day, summing to 1. Every day gets
    /// at least one symbol whatever its share.
    /// </summary>
    public IReadOnlyList<double> DayShares { get; init; } = [0.7, 0.15, 0.15];

    /// <summary>One frame in this many carries the directory.</summary>
    public int DirectoryEvery { get; init; } = 25;

    /// <summary>Directory symbols beyond its K, at least, each day.</summary>
    public int DirectoryExtra { get; init; } = 2;

    /// <summary>The zstd dictionary to compress with.</summary>
    public ushort DictionaryId { get; init; } = ZstdDictionary.Gb7rdg1Id;

    /// <summary>Bulletins whose serialised form is longer than this are not broadcast.</summary>
    public int MaxBulletinSize { get; init; } = 32 * 1024;

    /// <summary>
    /// How many days, counting the first, the head end remembers a bulletin, so that one offered
    /// again after its carrying days is not sent as new.
    /// </summary>
    public int RememberDays { get; init; } = 14;

    /// <summary>The RaptorQ symbol size.</summary>
    public int SymbolSize { get; init; } = MailcastFrame.StandardSymbolSize;

    /// <summary>The RaptorQ symbol alignment.</summary>
    public int Alignment { get; init; } = MailcastFrame.StandardAlignment;
}

/// <summary>One object's part in a day's broadcast.</summary>
/// <param name="Transfer">The object.</param>
/// <param name="DayIndex">0 on the day it was first seen, then 1, 2 and so on; the directory is always 0.</param>
/// <param name="FirstEsi">The first ESI sent today.</param>
/// <param name="Count">How many symbols are sent today, with consecutive ESIs.</param>
/// <param name="Bid">The bulletin's BID, or null for the directory.</param>
public sealed record ScheduledObject(TransferObject Transfer, int DayIndex, uint FirstEsi, int Count, string? Bid = null)
{
    /// <summary>The first ESI for the object's next broadcast.</summary>
    public uint NextEsi => FirstEsi + (uint)Count;
}

/// <summary>One day's broadcast: the directory, every object's share, and the frames in sending order.</summary>
public sealed class DailyBroadcast
{
    internal DailyBroadcast(DateOnly date, BroadcastDirectory directory, IReadOnlyList<ScheduledObject> objects, IReadOnlyList<MailcastFrame> frames, IReadOnlyList<Bulletin> skipped)
    {
        Date = date;
        Directory = directory;
        Objects = objects;
        Frames = frames;
        Skipped = skipped;
    }

    /// <summary>The broadcast day.</summary>
    public DateOnly Date { get; }

    /// <summary>The objects in rotation today.</summary>
    public BroadcastDirectory Directory { get; }

    /// <summary>Each object's share of today, the directory first.</summary>
    public IReadOnlyList<ScheduledObject> Objects { get; }

    /// <summary>The frames in sending order.</summary>
    public IReadOnlyList<MailcastFrame> Frames { get; }

    /// <summary>Bulletins in their carrying days that were left out: too large, or a BID already taken.</summary>
    public IReadOnlyList<Bulletin> Skipped { get; }
}

/// <summary>
/// Plans the head end's daily broadcast. The plan is a pure function of the bulletins, their
/// first-seen dates, today's date, the options and a seed.
/// </summary>
/// <remarks>
/// <para>
/// Each bulletin is carried on <see cref="ScheduleOptions.DaysCarried"/> days running. Its first
/// day gets most of its symbols and later days continue with fresh ESIs, never repeats, so a
/// receiver's pieces from different days add up. The object is compressed once, on the day the
/// bulletin is first seen, and the same octets are sent every day: <see cref="HeadEndStore"/>
/// keeps them and the next ESI.
/// </para>
/// <para>
/// The frames are interleaved so that every object is spread over the whole broadcast: object j
/// with n frames puts its i-th frame at position (i + u) / n, u a random phase for the object,
/// and the frames are sent in order of position. When every object has the same number of frames
/// that is plain round robin; in general it means a fade takes a little from everyone rather
/// than all of one small bulletin. The directory's frames go in evenly spaced slots, the first
/// frame of the day and then at least one in every <see cref="ScheduleOptions.DirectoryEvery"/>.
/// </para>
/// </remarks>
public static class BroadcastScheduler
{
    /// <summary>How many symbols an object of K source symbols gets on each of its days.</summary>
    public static int[] SymbolsPerDay(int sourceSymbols, ScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceSymbols, 1);
        int total = (int)Math.Ceiling(options.TotalOverhead * sourceSymbols) + options.ExtraSymbols;
        var counts = new int[options.DaysCarried];
        double share = 0;
        int sent = 0;
        for (int d = 0; d < counts.Length; d++)
        {
            share += options.DayShares[d];
            int upTo = d == counts.Length - 1 ? total : (int)Math.Ceiling((share * total) - 1e-9);
            counts[d] = Math.Max(1, upTo - sent);
            sent += counts[d];
        }
        return counts;
    }

    /// <summary>The ESIs an object of K source symbols gets on a given day of its carrying.</summary>
    public static (uint FirstEsi, int Count) Allocation(int sourceSymbols, int dayIndex, ScheduleOptions options)
    {
        var counts = SymbolsPerDay(sourceSymbols, options);
        ArgumentOutOfRangeException.ThrowIfNegative(dayIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(dayIndex, counts.Length);
        uint first = 0;
        for (int d = 0; d < dayIndex; d++)
        {
            first += (uint)counts[d];
        }
        return (first, counts[dayIndex]);
    }

    /// <summary>
    /// Plans today's broadcast from bulletins alone, compressing each afresh and taking each
    /// day's first ESI from <see cref="Allocation"/>. That gives the same frames as a head end
    /// that keeps state, as long as the compression and options are unchanged; a real head end
    /// should use <see cref="HeadEndStore"/> and the other overload.
    /// </summary>
    public static DailyBroadcast Plan(IEnumerable<BroadcastBulletin> bulletins, DateOnly today, int seed, Compression compression, ScheduleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bulletins);
        ArgumentNullException.ThrowIfNull(compression);
        options ??= new ScheduleOptions();
        Validate(options);
        var carried = new List<CarriedBulletin>();
        var skipped = new List<Bulletin>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = bulletins
            .Select(b => (b.Bulletin, b.FirstSeen, DayIndex: today.DayNumber - b.FirstSeen.DayNumber))
            .Where(b => b.DayIndex >= 0 && b.DayIndex < options.DaysCarried)
            .OrderBy(b => b.Bulletin.Bid.ToUpperInvariant(), StringComparer.Ordinal)
            .ThenBy(b => b.DayIndex);
        foreach (var (bulletin, firstSeen, dayIndex) in ordered)
        {
            var serialized = bulletin.Serialize();
            if (serialized.Length > options.MaxBulletinSize || !taken.Add(bulletin.Bid))
            {
                skipped.Add(bulletin);
                continue;
            }
            var transfer = TransferObject.ForBulletin(bulletin, options.DictionaryId, compression, options.SymbolSize, options.Alignment);
            var (first, _) = Allocation(transfer.SourceSymbols, dayIndex, options);
            carried.Add(new CarriedBulletin(bulletin.Bid, bulletin.Title, serialized.Length, firstSeen, transfer, first));
        }
        var plan = Plan(carried, today, seed, compression, options);
        return new DailyBroadcast(plan.Date, plan.Directory, plan.Objects, plan.Frames, skipped);
    }

    /// <summary>
    /// Plans today's broadcast from the bulletins in rotation. Each sends today's share of
    /// symbols starting at its <see cref="CarriedBulletin.NextEsi"/>; the result's
    /// <see cref="ScheduledObject.NextEsi"/> is where to start next time. Of two entries with
    /// the same BID, only the first in BID order is sent.
    /// </summary>
    /// <param name="carried">The bulletins in rotation, with their objects and next ESIs.</param>
    /// <param name="today">The broadcast day.</param>
    /// <param name="seed">Seeds the interleaving.</param>
    /// <param name="compression">Compresses the directory.</param>
    /// <param name="options">The schedule settings.</param>
    public static DailyBroadcast Plan(IEnumerable<CarriedBulletin> carried, DateOnly today, int seed, Compression compression, ScheduleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(compression);
        options ??= new ScheduleOptions();
        Validate(options);

        // Today's bulletins in a fixed order, so the plan does not depend on the caller's order.
        var inRotation = carried
            .Select(c => (Carried: c, DayIndex: today.DayNumber - c.FirstSeen.DayNumber))
            .Where(c => c.DayIndex >= 0 && c.DayIndex < options.DaysCarried)
            .OrderBy(c => c.Carried.Bid.ToUpperInvariant(), StringComparer.Ordinal)
            .ThenBy(c => c.DayIndex)
            .ThenBy(c => c.Carried.Transfer.ObjectId)
            .ToList();

        var scheduled = new List<ScheduledObject>();
        var entries = new List<DirectoryEntry>();
        var takenBids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenIds = new HashSet<ulong>();
        foreach (var (c, dayIndex) in inRotation)
        {
            if (!takenBids.Add(c.Bid) || !takenIds.Add(c.Transfer.ObjectId))
            {
                continue;
            }
            int count = SymbolsPerDay(c.Transfer.SourceSymbols, options)[dayIndex];
            scheduled.Add(new ScheduledObject(c.Transfer, dayIndex, c.NextEsi, count, c.Bid));
            entries.Add(new DirectoryEntry(c.Transfer.ObjectId, c.Transfer.DictionaryId, c.Size, c.Bid, c.Title));
        }

        var directory = new BroadcastDirectory(today, entries);
        var directoryObject = TransferObject.ForDirectory(directory, options.DictionaryId, compression, options.SymbolSize, options.Alignment);
        int bulletinFrames = scheduled.Sum(s => s.Count);
        int directoryFrames = Math.Max(
            directoryObject.SourceSymbols + options.DirectoryExtra,
            (int)Math.Ceiling(bulletinFrames / (double)(options.DirectoryEvery - 1)));
        scheduled.Insert(0, new ScheduledObject(directoryObject, 0, 0, directoryFrames));

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

        // Then the directory's frames at evenly spaced slots, the first in slot 0.
        int totalFrames = bulletinFrames + directoryFrames;
        var frames = new List<MailcastFrame>(totalFrames);
        int nextDirectory = 0;
        int nextBulletin = 0;
        for (int slot = 0; slot < totalFrames; slot++)
        {
            if (nextDirectory < directoryFrames && slot == (int)((long)nextDirectory * totalFrames / directoryFrames))
            {
                frames.Add(directoryObject.Frame((uint)nextDirectory++));
            }
            else
            {
                var o = order[nextBulletin++];
                frames.Add(scheduled[o.Object].Transfer.Frame(scheduled[o.Object].FirstEsi + (uint)o.Index));
            }
        }
        return new DailyBroadcast(today, directory, scheduled, frames, []);
    }

    private static void Validate(ScheduleOptions options)
    {
        if (options.DaysCarried < 1 || options.DayShares.Count != options.DaysCarried)
        {
            throw new ArgumentException("There must be one day share for each day carried.", nameof(options));
        }
        if (options.DayShares.Any(s => s < 0) || Math.Abs(options.DayShares.Sum() - 1) > 1e-9)
        {
            throw new ArgumentException("The day shares must be non-negative and sum to 1.", nameof(options));
        }
        if (options.TotalOverhead < 1 || options.ExtraSymbols < 0 || options.DirectoryExtra < 0)
        {
            throw new ArgumentException("TotalOverhead must be at least 1 and the extras at least 0.", nameof(options));
        }
        if (options.DirectoryEvery < 2)
        {
            throw new ArgumentException("DirectoryEvery must be at least 2.", nameof(options));
        }
    }
}
