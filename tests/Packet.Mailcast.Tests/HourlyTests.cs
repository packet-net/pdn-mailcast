namespace Packet.Mailcast.Tests;

/// <summary>Carrying across hourly slots: shares, offsets, fresh ESIs, and what a receiver gets.</summary>
public class HourlyTests
{
    private static readonly ScheduleOptions Hourly = ScheduleOptions.Hourly;
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Hour(int h) => Noon.AddHours(h);

    /// <summary>
    /// Whether a frame survives a simulated loss, by the frame's own identity rather than its
    /// place in the slot: so adding or moving frames elsewhere (a bigger directory, say) never
    /// changes which of a seed's frames these tests lose. A stable mix (splitmix64), not
    /// <see cref="HashCode.Combine"/>: that is randomised per process by design, so these tests
    /// would pass or fail a different sub-case on about three runs in five.
    /// </summary>
    private static bool Survives(int seed, MailcastFrame frame, double loss)
    {
        ulong h = SplitMix64((ulong)(uint)seed);
        h = SplitMix64(h ^ frame.ObjectId);
        h = SplitMix64(h ^ frame.EncodingSymbolId);
        return h / (double)ulong.MaxValue >= loss;
    }

    /// <summary>The fixed point mix from Vigna's splitmix64 (the finaliser also used in xoshiro's seeding).</summary>
    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>A head end that keeps its state in memory: every slot planned goes out whole.</summary>
    private sealed class Head(ScheduleOptions options)
    {
        private readonly Dictionary<string, CarriedBulletin> _held = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, uint> _directories = [];

        public void Offer(Bulletin bulletin, DateOnly seen) =>
            _held[bulletin.Bid] = new CarriedBulletin(bulletin.Bid, bulletin.Title, bulletin.Serialize().Length, seen,
                TransferObject.ForBulletin(bulletin, options.DictionaryId, Compression.Default, options.SymbolSize, options.Alignment), 0);

        public SlotBroadcast Slot(DateTimeOffset slot)
        {
            var plan = BroadcastScheduler.Plan(_held.Values.Where(c => BroadcastScheduler.InRotation(c, slot, options)), slot, (int)slot.ToUnixTimeSeconds(), Compression.Default, options,
                id => _directories.GetValueOrDefault(id));
            foreach (var o in plan.Objects)
            {
                if (o.Bid is null)
                {
                    _directories[o.Transfer.ObjectId] = o.NextEsi;
                }
                else if (o.Count > 0)
                {
                    var c = _held[o.Bid];
                    _held[o.Bid] = c with { NextEsi = o.NextEsi, FirstSlot = c.FirstSlot ?? slot };
                }
            }
            return plan;
        }
    }

    [Fact]
    public void HourlyDefaults_CarryInFiveDifferentHoursOverMoreThanADay()
    {
        BroadcastScheduler.Validate(Hourly);
        Assert.Equal(60, Hourly.SlotMinutes);
        Assert.Equal([0, 5, 10, 17, 25], Hourly.SlotOffsets);
        Assert.True(Hourly.SlotOffsets[^1] >= 24, "carried over at least a day");
        Assert.Equal(Hourly.SlotOffsets.Count, Hourly.SlotOffsets.Select(o => o % 24).Distinct().Count());

        // A web SDR receiver hears every third slot. Whichever third, it hears the first slot or two repeats.
        for (int phase = 0; phase < 3; phase++)
        {
            var heard = Enumerable.Range(0, Hourly.SlotOffsets.Count).Where(c => Hourly.SlotOffsets[c] % 3 == phase).ToList();
            double share = heard.Sum(c => Hourly.SlotShares[c]);
            Assert.True(share >= 1.4, $"every third slot from phase {phase} carries {share} K");
        }
    }

    [Fact]
    public void ABulletin_GoesOutAtItsOffsets_WithFreshEsis_ThenLeavesTheRotation()
    {
        var head = new Head(Hourly);
        var bulletin = TestBulletins.Make(1, 6000);
        head.Offer(bulletin, DateOnly.FromDateTime(Noon.UtcDateTime));
        ulong id = Ids.Of(bulletin);
        var esis = new List<uint>();
        var carriedIn = new List<int>();
        for (int h = 0; h < 40; h++)
        {
            var plan = head.Slot(Hour(h));
            var mine = plan.Frames.Where(f => f.ObjectId == id).Select(f => f.EncodingSymbolId).ToList();
            if (mine.Count > 0)
            {
                carriedIn.Add(h);
            }
            esis.AddRange(mine);
            Assert.Equal(h < Hourly.SlotsInRotation, plan.Directory.Entries.Any(e => e.ObjectId == id));
        }
        Assert.Equal([0, 5, 10, 17, 25], carriedIn);
        Assert.Equal(Enumerable.Range(0, esis.Count).Select(i => (uint)i), esis);
        int k = TransferObject.ForBulletin(bulletin, Hourly.DictionaryId, Compression.Default, Hourly.SymbolSize, Hourly.Alignment).SourceSymbols;
        Assert.Equal(BroadcastScheduler.SymbolsPerCarrying(k, Hourly).Sum(), esis.Count);
        Assert.True(BroadcastScheduler.SymbolsPerCarrying(k, Hourly)[0] >= Math.Ceiling(1.5 * k) + 2);
    }

    [Fact]
    public void ABulletinTakenInMidSlot_IsFirstCarriedInTheNextSlotPlanned()
    {
        var head = new Head(Hourly);
        var early = TestBulletins.Make(2, 3000);
        head.Offer(early, DateOnly.FromDateTime(Noon.UtcDateTime));
        head.Slot(Hour(0));
        var late = TestBulletins.Make(3, 3000);
        head.Offer(late, DateOnly.FromDateTime(Noon.UtcDateTime));
        var next = head.Slot(Hour(1));
        var o = Assert.Single(next.Objects, o => o.Bid == late.Bid);
        Assert.Equal(0, o.SlotIndex);
        Assert.Equal(0u, o.FirstEsi);
        Assert.Equal(BroadcastScheduler.SymbolsPerCarrying(o.Transfer.SourceSymbols, Hourly)[0], o.Count);
        Assert.Equal(0, Assert.Single(next.Objects, o => o.Bid == early.Bid).Count);
    }

    [Theory]
    [InlineData(0.0, 1, 940)]
    [InlineData(0.2, 2, 940)]
    [InlineData(0.0, 3, 240)]
    [InlineData(0.2, 1, 240)]
    [InlineData(0.2, 2, 240)]
    public void TheFirstSlotAlone_RebuildsEveryBulletinForAFreshReceiver(double loss, int seed, int symbolSize)
    {
        using var rx = new TempDirectory();
        var head = new Head(Hourly with { SymbolSize = symbolSize });
        var bulletins = TestBulletins.Day(20 + seed, 16);
        foreach (var b in bulletins)
        {
            head.Offer(b, DateOnly.FromDateTime(Noon.UtcDateTime));
        }
        var plan = head.Slot(Noon);
        var receiver = new ReceiverStore(rx.Path, Compression.Default, TestStores.Fast);
        var rebuilt = new List<Bulletin>();
        foreach (var frame in plan.Frames.Where(f => Survives(seed, f, loss)))
        {
            if (receiver.Accept(frame.ToBytes()).Bulletin is { } b)
            {
                rebuilt.Add(b);
            }
        }
        Assert.Equal(bulletins.OrderBy(b => b.Bid), rebuilt.OrderBy(b => b.Bid));
    }

    [Theory]
    [InlineData(0, 940)]
    [InlineData(1, 940)]
    [InlineData(2, 940)]
    [InlineData(0, 240)]
    [InlineData(1, 240)]
    [InlineData(2, 240)]
    public void AWebSdrHearingEveryThirdSlot_RebuildsEveryBulletin(int phase, int symbolSize)
    {
        // A day's bulletins, one each hour; the receiver hears only slots with h % 3 == phase,
        // as a web SDR receiver listening to 8 slots a day does, and loses a tenth of those frames.
        using var rx = new TempDirectory();
        var head = new Head(Hourly with { SymbolSize = symbolSize });
        var bulletins = TestBulletins.Day(30, 24);
        var receiver = new ReceiverStore(rx.Path, Compression.Default, TestStores.Fast);
        var rebuilt = new HashSet<string>();
        for (int h = 0; h < 24 + Hourly.SlotsInRotation; h++)
        {
            if (h < 24)
            {
                head.Offer(bulletins[h], DateOnly.FromDateTime(Hour(h).UtcDateTime));
            }
            var plan = head.Slot(Hour(h));
            if (h % 3 != phase)
            {
                continue;
            }
            foreach (var frame in plan.Frames.Where(f => Survives(phase, f, 0.1)))
            {
                if (receiver.Accept(frame.ToBytes()).Bulletin is { } b)
                {
                    rebuilt.Add(b.Bid);
                }
            }
        }
        Assert.Equal(bulletins.Select(b => b.Bid).Order(), rebuilt.Order());
    }

    [Fact]
    public void AnOffGridSlot_CountsLikeAnyOther_AndTheScheduleCarriesOn()
    {
        // A one-off slot at 12:37 carries a new bulletin first; 13:00 sends nothing more of it,
        // and its first repeat is five hours after its first slot.
        var head = new Head(Hourly);
        var bulletin = TestBulletins.Make(4, 5000);
        head.Offer(bulletin, DateOnly.FromDateTime(Noon.UtcDateTime));
        var oneOff = head.Slot(Noon.AddMinutes(37));
        var first = Assert.Single(oneOff.Objects, o => o.Bid == bulletin.Bid);
        Assert.True(first.Count > 0);
        Assert.Equal(0, Assert.Single(head.Slot(Hour(1)).Objects, o => o.Bid == bulletin.Bid).Count);
        for (int h = 2; h < 5; h++)
        {
            Assert.Equal(0, head.Slot(Hour(h)).Objects.Single(o => o.Bid == bulletin.Bid).Count);
        }
        var repeat = Assert.Single(head.Slot(Hour(5)).Objects, o => o.Bid == bulletin.Bid);
        Assert.Equal(4, repeat.SlotIndex);
        Assert.Equal(0, repeat.Count);
        repeat = Assert.Single(head.Slot(Hour(6)).Objects, o => o.Bid == bulletin.Bid);
        Assert.Equal(5, repeat.SlotIndex);
        Assert.Equal(first.NextEsi, repeat.FirstEsi);
        Assert.True(repeat.Count > 0);
    }

    [Fact]
    public void ARestartMidSlot_StoreCarriesOnTheSameSlotWithoutRepeatingAnyPiece()
    {
        using var dir = new TempDirectory();
        var store = new HeadEndStore(dir.Path, Compression.Default, Hourly);
        foreach (var b in TestBulletins.Day(40, 6))
        {
            store.Offer(b, DateOnly.FromDateTime(Noon.UtcDateTime));
        }
        var whole = BroadcastScheduler.Plan(store.InRotation(Noon), Noon, 1, Compression.Default, Hourly, store.DirectoryNextEsi);
        int cut = whole.Frames.Count / 3;
        store.Commit(whole, cut);

        // Reopened, as after a restart, and the same slot planned again.
        store = new HeadEndStore(dir.Path, Compression.Default, Hourly);
        var rest = BroadcastScheduler.Plan(store.InRotation(Noon), Noon, 1, Compression.Default, Hourly, store.DirectoryNextEsi);
        var sent = whole.Frames.Take(cut).Select(f => (f.ObjectId, f.EncodingSymbolId)).ToHashSet();
        Assert.DoesNotContain(rest.Frames.Select(f => (f.ObjectId, f.EncodingSymbolId)), sent.Contains);
        Assert.Equal(whole.BulletinFrames, sent.Count(f => whole.Objects.Skip(1).Any(o => o.Transfer.ObjectId == f.ObjectId)) + rest.BulletinFrames);
        store.Commit(rest);

        // An hour later nothing is due for them until their first repeat, five hours after the first slot.
        store = new HeadEndStore(dir.Path, Compression.Default, Hourly);
        Assert.Equal(0, BroadcastScheduler.Plan(store.InRotation(Hour(1)), Hour(1), 1, Compression.Default, Hourly, store.DirectoryNextEsi).BulletinFrames);
        Assert.True(BroadcastScheduler.Plan(store.InRotation(Hour(5)), Hour(5), 1, Compression.Default, Hourly, store.DirectoryNextEsi).BulletinFrames > 0);
    }

    [Fact]
    public void ASkippedRepeatSlot_IsMadeUpInTheNext()
    {
        var head = new Head(Hourly);
        var bulletin = TestBulletins.Make(5, 7000);
        head.Offer(bulletin, DateOnly.FromDateTime(Noon.UtcDateTime));
        var first = head.Slot(Noon).Objects.Single(o => o.Bid == bulletin.Bid);
        // 17:00, the first repeat's slot, never runs; 18:00 sends it.
        var made = head.Slot(Hour(6)).Objects.Single(o => o.Bid == bulletin.Bid);
        Assert.Equal(first.NextEsi, made.FirstEsi);
        Assert.Equal(BroadcastScheduler.SymbolsPerCarrying(first.Transfer.SourceSymbols, Hourly)[1], made.Count);

        // The last carrying's slot skipped too: made up within the carry-over, not after.
        var last = new Head(Hourly);
        last.Offer(bulletin, DateOnly.FromDateTime(Noon.UtcDateTime));
        last.Slot(Noon);
        Assert.True(last.Slot(Hour(27)).Objects.Single(o => o.Bid == bulletin.Bid).Count > 0);
        var other = new Head(Hourly);
        other.Offer(bulletin, DateOnly.FromDateTime(Noon.UtcDateTime));
        other.Slot(Noon);
        Assert.DoesNotContain(other.Slot(Hour(29)).Objects, o => o.Bid == bulletin.Bid);
    }
}
