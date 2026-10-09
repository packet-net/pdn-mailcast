namespace Packet.Mailcast.Tests;

/// <summary>
/// The budget rule in the core, with an airtime of one second a frame so the budget is a frame
/// count: the priority order, the cap, retiring, fresh pieces only, and the directory.
/// </summary>
public class BudgetSchedulerTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private static readonly ScheduleOptions Options = ScheduleOptions.HourlyBudget with { SymbolSize = 240 };

    /// <summary>A budget of <paramref name="frames"/> frames, the directory's included.</summary>
    private static SlotBudget Frames(int frames) => new(TimeSpan.FromSeconds(frames), lengths => TimeSpan.FromSeconds(lengths.Count));

    private static CarriedBulletin Held(int seed, int size, uint nextEsi = 0, DateTimeOffset? firstSlot = null)
    {
        var b = TestBulletins.Make(seed, size);
        var transfer = TransferObject.ForBulletin(b, Options.DictionaryId, Compression.Default, Options.SymbolSize, Options.Alignment);
        return new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, new DateOnly(2026, 10, 5), transfer, nextEsi, firstSlot);
    }

    /// <summary>Starts a bulletin at a coverage of <paramref name="k"/> times its K.</summary>
    private static CarriedBulletin AtCoverage(int seed, int size, double k, DateTimeOffset? firstSlot = null)
    {
        var c = Held(seed, size);
        return c with { NextEsi = (uint)Math.Round(k * c.Transfer.SourceSymbols), FirstSlot = firstSlot ?? (k > 0 ? Slot.AddHours(-2) : null) };
    }

    private static Dictionary<string, ScheduledObject> ByBid(SlotBroadcast plan) =>
        plan.Objects.Where(o => o.Bid is not null).ToDictionary(o => o.Bid!, StringComparer.Ordinal);

    [Fact]
    public void Fill_GivesTheLeastCoveredFirst_SoNewBulletinsGoFirst()
    {
        var fresh = AtCoverage(1, 6000, 0);
        var half = AtCoverage(2, 6000, 0.5);
        var old = AtCoverage(3, 6000, 3.0);
        int k = fresh.Transfer.SourceSymbols;
        Assert.True(k >= 8);

        // Room for the directory (its floor, issue #69, comes first) and a few more: the rest goes to the new bulletin.
        var tight = BroadcastScheduler.Plan([old, half, fresh], Slot, 1, Compression.Default, Options, null, Frames(10));
        var objects = ByBid(tight);
        int directory = tight.Objects[0].Count;
        Assert.Equal(10 - directory, objects[fresh.Bid].Count);
        Assert.True(objects[fresh.Bid].Count >= 2);
        Assert.Equal(0, objects[half.Bid].Count);
        Assert.Equal(0, objects[old.Bid].Count);

        // Room for a little more: the floors go first, the least covered first, so the new one
        // and the half-covered one reach theirs and the well covered one waits.
        var rule = Options.Budget!;
        int floorFresh = BroadcastScheduler.FloorSymbols(k, rule);
        int floorHalf = BroadcastScheduler.FloorSymbols(half.Transfer.SourceSymbols, rule);
        var floors = BroadcastScheduler.Plan([old, half, fresh], Slot, 1, Compression.Default, Options, null, Frames(directory + floorFresh + floorHalf));
        objects = ByBid(floors);
        Assert.Equal(floorFresh, objects[fresh.Bid].Count);
        Assert.Equal(floorHalf, objects[half.Bid].Count);
        Assert.Equal(0, objects[old.Bid].Count);

        // Plenty of room: every floor, then the rest to the least covered, each up to its cap.
        var plenty = BroadcastScheduler.Plan([old, half, fresh], Slot, 1, Compression.Default, Options, null, Frames(1000));
        objects = ByBid(plenty);
        Assert.Equal(BroadcastScheduler.CapSymbols(k, rule), objects[fresh.Bid].Count);
        Assert.Equal(BroadcastScheduler.CapSymbols(half.Transfer.SourceSymbols, rule), objects[half.Bid].Count);
        Assert.Equal(BroadcastScheduler.CapSymbols(old.Transfer.SourceSymbols, rule), objects[old.Bid].Count);
    }

    [Fact]
    public void Floor_IsEnoughThatAnyThreeSlotsRebuild()
    {
        var rule = Options.Budget!;
        foreach (int k in new[] { 1, 2, 3, 5, 10, 17, 40 })
        {
            int floor = BroadcastScheduler.FloorSymbols(k, rule);
            Assert.True(3 * floor >= k + 1, $"K {k}: floor {floor}");
            Assert.True(floor <= BroadcastScheduler.CapSymbols(k, rule));
        }
    }

    [Fact]
    public void Fill_CapsEachBulletinAtSixTenthsOfKASlot_HoweverQuietTheSlot()
    {
        var only = Held(4, 9000);
        int k = only.Transfer.SourceSymbols;
        var plan = BroadcastScheduler.Plan([only], Slot, 1, Compression.Default, Options, null, Frames(1000));
        Assert.Equal(BroadcastScheduler.CapSymbols(k, Options.Budget!), ByBid(plan)[only.Bid].Count);
        Assert.Equal((int)Math.Ceiling(0.6 * k), ByBid(plan)[only.Bid].Count);
        // A one-symbol bulletin still gets its one.
        var tiny = Held(5, 100);
        Assert.Equal(1, tiny.Transfer.SourceSymbols);
        Assert.Equal(1, ByBid(BroadcastScheduler.Plan([tiny], Slot, 1, Compression.Default, Options, null, Frames(1000)))[tiny.Bid].Count);
    }

    [Fact]
    public void Fill_NeverPassesTheBudget_AndSendsOnlyFreshPieces()
    {
        var rng = new Random(11);
        for (int trial = 0; trial < 30; trial++)
        {
            var held = Enumerable.Range(0, rng.Next(1, 25))
                .Select(i => AtCoverage((trial * 100) + i, 1000 + rng.Next(15000), rng.NextDouble() * 5))
                .ToList();
            int budget = 5 + rng.Next(150);
            var plan = BroadcastScheduler.Plan(held, Slot, trial, Compression.Default, Options, null, Frames(budget));
            if (plan.BulletinFrames > 0)
            {
                Assert.True(plan.Frames.Count <= budget, $"{plan.Frames.Count} frames in a budget of {budget}");
            }
            foreach (var o in plan.Objects.Where(o => o.Bid is not null))
            {
                var c = held.Single(h => h.Bid == o.Bid);
                Assert.Equal(c.NextEsi, o.FirstEsi);
                Assert.True(o.Count <= BroadcastScheduler.CapSymbols(c.Transfer.SourceSymbols, Options.Budget!));
                Assert.True(o.NextEsi <= BroadcastScheduler.RetireSymbols(c.Transfer.SourceSymbols, Options.Budget!));
            }
            // No ESI twice within the slot either.
            Assert.Equal(plan.Frames.Count, plan.Frames.Select(f => (f.ObjectId, f.EncodingSymbolId)).Distinct().Count());
        }
    }

    [Fact]
    public void Retire_AfterSixK_OrThirtySixHoursFromTheFirstSlot()
    {
        var rule = Options.Budget!;
        var c = Held(6, 6000);
        int k = c.Transfer.SourceSymbols;
        Assert.False(BroadcastScheduler.Retired(c, Slot, rule));
        Assert.False(BroadcastScheduler.Retired(c with { NextEsi = (uint)(6 * k) - 1, FirstSlot = Slot }, Slot, rule));
        Assert.True(BroadcastScheduler.Retired(c with { NextEsi = (uint)(6 * k), FirstSlot = Slot }, Slot, rule));
        Assert.False(BroadcastScheduler.Retired(c with { NextEsi = 3, FirstSlot = Slot.AddHours(-35) }, Slot, rule));
        Assert.True(BroadcastScheduler.Retired(c with { NextEsi = 3, FirstSlot = Slot.AddHours(-36) }, Slot, rule));
        // No first slot, no age: one an older head end sent without recording it is still in rotation.
        Assert.False(BroadcastScheduler.Retired(c with { NextEsi = 3, FirstSlot = null }, Slot.AddDays(10), rule));

        // Retired ones are neither sent nor listed; nearly retired ones get only what is left.
        var gone = c with { NextEsi = (uint)(6 * k), FirstSlot = Slot.AddHours(-3) };
        var aged = AtCoverage(7, 6000, 1, Slot.AddHours(-40));
        var nearly = AtCoverage(8, 6000, 0) is var n ? n with { NextEsi = (uint)(6 * n.Transfer.SourceSymbols) - 2, FirstSlot = Slot.AddHours(-3) } : null!;
        var plan = BroadcastScheduler.Plan([gone, aged, nearly], Slot, 1, Compression.Default, Options, null, Frames(1000));
        Assert.Equal([nearly.Bid], plan.Directory.Entries.Select(e => e.Bid));
        Assert.Equal(2, ByBid(plan)[nearly.Bid].Count);

        var none = BroadcastScheduler.Plan([gone, aged], Slot, 1, Compression.Default, Options, null, Frames(1000));
        Assert.Equal(0, none.BulletinFrames);
        Assert.Empty(none.Directory.Entries);
    }

    [Fact]
    public void Fill_AlwaysSendsTheDirectory_WithTheModeAndTheTimetable()
    {
        var timetable = new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);
        var options = Options with { Timetable = timetable };
        var held = Enumerable.Range(0, 40).Select(i => AtCoverage(20 + i, 5000, i % 4)).ToList();
        var plan = BroadcastScheduler.Plan(held, Slot, 1, Compression.Default, options, null, Frames(120), "ms110d-wn3");
        var directory = plan.Objects[0];
        Assert.Null(directory.Bid);
        Assert.True(directory.Count >= directory.Transfer.SourceSymbols + options.DirectoryExtra);
        Assert.True(directory.Count >= Math.Ceiling(plan.BulletinFrames / (double)(options.DirectoryEvery - 1)));
        Assert.Equal(40, plan.Directory.Entries.Count);
        Assert.Equal("ms110d-wn3", plan.Directory.Mode);
        var parsed = BroadcastDirectory.Parse(plan.Directory.Serialize());
        Assert.Equal("ms110d-wn3", parsed.Mode);
        Assert.Equal(timetable, parsed.Schedule);
    }

    [Fact]
    public void Directory_ModeIsOneMoreFieldAfterTheTitle_WhichOlderReadersIgnore()
    {
        var entry = new DirectoryEntry(0x0123456789abcdef, 1, 2345, "12345_GB7RDG", "Title text");
        var text = Bulletin.TextEncoding.GetString(new BroadcastDirectory(new DateOnly(2026, 10, 6), [entry], new SlotTimetable(TimeOnly.MinValue, 60), "ms110d-wn4").Serialize());
        Assert.Equal("MAILCAST DIRECTORY 1\n2026-10-06\n0123456789abcdef\t1\t2345\t12345_GB7RDG\tTitle text\ttype=1\tslots=00:00/60\tmode=ms110d-wn4\n", text);
        Assert.Null(BroadcastDirectory.Parse(new BroadcastDirectory(new DateOnly(2026, 10, 6), [entry]).Serialize()).Mode);
        Assert.Throws<ArgumentException>(() => new BroadcastDirectory(new DateOnly(2026, 10, 6), [entry], null, "ms110d wn4"));
    }

    /// <summary>
    /// Issue #69: a station decoding only a quarter of a slot's frames should still have a good
    /// chance (binomial, p = 0.25, at least 80%) of a whole directory from one slot, not just
    /// <see cref="ScheduleOptions.DirectoryExtra"/> above its K.
    /// </summary>
    [Theory]
    [InlineData(1, 6)] // GB7RDG's usual rotation (14 to 20 bulletins): K is 1, needs 6 frames for 80%.
    [InlineData(2, 11)] // A big rotation (around 27 bulletins): K is 2, needs 11.
    [InlineData(3, 16)]
    [InlineData(4, 21)]
    public void DirectoryFloor_IsEnoughForAnEightyPercentChanceAtAQuarterDecoded(int k, int expected)
    {
        Assert.Equal(expected, BroadcastScheduler.DirectoryFloor(k));
        double atFloor = Binomial(BroadcastScheduler.DirectoryFloor(k), k, 0.25);
        Assert.True(atFloor >= 0.80, $"K {k}: {atFloor} at the floor");
        double oneFewer = Binomial(BroadcastScheduler.DirectoryFloor(k) - 1, k, 0.25);
        Assert.True(oneFewer < 0.80, $"K {k}: {oneFewer} one frame short of the floor, should already be under 80%");
    }

    /// <summary>P(X &gt;= k) for X ~ Binomial(n, p), computed independently of <see cref="BroadcastScheduler.DirectoryFloor"/>.</summary>
    private static double Binomial(int n, int k, double p)
    {
        double sum = 0;
        for (int i = 0; i < k; i++)
        {
            double term = 1.0;
            for (int j = 0; j < i; j++)
            {
                term *= (n - j) / (double)(j + 1);
            }
            term *= Math.Pow(p, i) * Math.Pow(1 - p, n - i);
            sum += term;
        }
        return 1 - sum;
    }

    /// <summary>GB7RDG's own symbol size (940, <see cref="Options"/> in this file uses 240 so airtime is a frame count).</summary>
    private static readonly ScheduleOptions RealisticOptions = ScheduleOptions.HourlyBudget;

    private static CarriedBulletin RealisticHeld(int seed, int size)
    {
        var b = TestBulletins.Make(seed, size);
        var transfer = TransferObject.ForBulletin(b, RealisticOptions.DictionaryId, Compression.Default, RealisticOptions.SymbolSize, RealisticOptions.Alignment);
        return new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, new DateOnly(2026, 10, 5), transfer, 0, null);
    }

    [Fact]
    public void Fill_DirectoryFloor_BeatsK_PlusDirectoryExtra_ForGb7rdgsUsualRotation()
    {
        // A realistic GB7RDG directory, 15 bulletins in rotation: its K is small (RaptorQ with a
        // short, well-compressed object at GB7RDG's own 940-octet symbol size), so the old
        // K + DirectoryExtra floor (3) left a quarter decoder under 60% for a whole directory;
        // the new floor (6) gets it above 80%.
        var bulletins = Enumerable.Range(0, 15).Select(i => RealisticHeld(200 + i, 1000 + (i * 300))).ToList();
        var plan = BroadcastScheduler.Plan(bulletins, Slot, 1, Compression.Default, RealisticOptions, null, Frames(500), "ms110d-wn3");
        int k = plan.Objects[0].Transfer.SourceSymbols;
        Assert.Equal(1, k); // confirms the realistic case: K is 1 for a 15-entry directory
        Assert.Equal(BroadcastScheduler.DirectoryFloor(k), plan.Objects[0].Count);
        Assert.True(plan.Objects[0].Count > k + RealisticOptions.DirectoryExtra);
    }

    /// <summary>The directory's extra frames still spread across the whole slot, the first at place 0.</summary>
    [Fact]
    public void Fill_DirectoryFloor_StillSpreadsAcrossTheSlot()
    {
        var bulletins = Enumerable.Range(0, 15).Select(i => RealisticHeld(300 + i, 1000 + (i * 300))).ToList();
        var plan = BroadcastScheduler.Plan(bulletins, Slot, 1, Compression.Default, RealisticOptions, null, Frames(500), "ms110d-wn3");
        ulong directoryId = plan.Objects[0].Transfer.ObjectId;
        var positions = plan.Frames.Select((f, i) => (f, i)).Where(x => x.f.ObjectId == directoryId).Select(x => x.i).ToList();
        Assert.True(positions.Count >= 6);
        Assert.Equal(0, positions[0]);
        var gaps = positions.Zip(positions.Skip(1), (a, b) => b - a);
        Assert.All(gaps, g => Assert.InRange(g, 1, RealisticOptions.DirectoryEvery));
    }

    [Fact]
    public void BudgetRule_NeedsABudget_AndTheSharesRuleIgnoresOne()
    {
        var c = Held(9, 3000);
        Assert.Throws<ArgumentException>(() => BroadcastScheduler.Plan([c], Slot, 1, Compression.Default, Options));
        var shares = ScheduleOptions.HourlyDaylight with { SymbolSize = 240 };
        var a = BroadcastScheduler.Plan([c], Slot, 1, Compression.Default, shares);
        var b = BroadcastScheduler.Plan([c], Slot, 1, Compression.Default, shares, null, Frames(3));
        Assert.Equal(a.Frames.Select(f => f.ToBytes()), b.Frames.Select(f => f.ToBytes()));
        Assert.Throws<ArgumentException>(() => BroadcastScheduler.Validate(Options with { Budget = new BudgetRule { SlotCap = 0 } }));
    }
}

/// <summary>
/// GB7RDG's own store as head end 0.3.0 and older left it: three bulletins copied from
/// /var/lib/pdn-mailcast-headend/bulletins on 2026-10-06, two of them sent by an older head end
/// without a FirstSlot, one first carried at 09:00 that day.
/// </summary>
public class Gb7rdgStoreTests
{
    private static readonly DateTimeOffset Eleven = new(2026, 10, 6, 11, 0, 0, TimeSpan.Zero);

    private static string Copy(string into)
    {
        string from = Path.Combine(AppContext.BaseDirectory, "Gb7rdgStore");
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(into, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return into;
    }

    [Fact]
    public void TheLiveStore_ReadsWhole_AndItsStrandedBulletinsRejoinFromTheirNextEsi()
    {
        string root = Copy(Directory.CreateTempSubdirectory("mailcast-gb7rdg-").FullName);
        try
        {
            var options = ScheduleOptions.HourlyBudget with { SymbolSize = 240, Timetable = new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg) };
            var log = new List<string>();

            // The shares rule, as 0.3.0 runs it, has only the bulletin it first carried at 09:00.
            var shares = new HeadEndStore(root, Compression.Default, options with { Budget = null }, log.Add);
            Assert.Equal(3, shares.Count);
            Assert.Equal(["TMGA7C_DB0HB"], shares.InRotation(Eleven).Select(c => c.Bid));

            var store = new HeadEndStore(root, Compression.Default, options, log.Add);
            Assert.Empty(log);
            Assert.Equal(3, store.Count);
            var held = store.InRotation(Eleven).ToDictionary(c => c.Bid, StringComparer.Ordinal);
            Assert.Equal(["19936_VE2PKT", "23673_LU9DCE", "TMGA7C_DB0HB"], held.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(5u, held["19936_VE2PKT"].NextEsi);
            Assert.Null(held["19936_VE2PKT"].FirstSlot);
            Assert.Equal(3u, held["23673_LU9DCE"].NextEsi);
            Assert.Null(held["23673_LU9DCE"].FirstSlot);
            Assert.Equal(10u, held["TMGA7C_DB0HB"].NextEsi);
            Assert.Equal(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero), held["TMGA7C_DB0HB"].FirstSlot);
            Assert.Equal([3, 1, 6], held.Values.OrderBy(c => c.Bid, StringComparer.Ordinal).Select(c => c.Transfer.SourceSymbols));

            var budget = new SlotBudget(TimeSpan.FromSeconds(100), lengths => TimeSpan.FromSeconds(lengths.Count));
            var plan = BroadcastScheduler.Plan(store.InRotation(Eleven), Eleven, 1, Compression.Default, options, store.DirectoryNextEsi, budget, "ms110d-wn4");
            foreach (var o in plan.Objects.Where(o => o.Bid is not null))
            {
                Assert.Equal(held[o.Bid!].NextEsi, o.FirstEsi);
                Assert.True(o.Count > 0, $"{o.Bid} got nothing");
            }
            store.Commit(plan);

            // Their first slot is now this one, in state.txt; the 09:00 bulletin keeps its own.
            var again = new HeadEndStore(root, Compression.Default, options, log.Add).InRotation(Eleven.AddHours(1)).ToDictionary(c => c.Bid, StringComparer.Ordinal);
            Assert.Equal(Eleven, again["19936_VE2PKT"].FirstSlot);
            Assert.Equal(Eleven, again["23673_LU9DCE"].FirstSlot);
            Assert.Equal(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero), again["TMGA7C_DB0HB"].FirstSlot);
            Assert.All(again.Values, c => Assert.True(c.NextEsi > held[c.Bid].NextEsi));
            Assert.Contains(File.ReadAllLines(Path.Combine(root, "bulletins", "088ca22833990d44", "state.txt")), l => l == "FirstSlot: 2026-10-06T11:00Z");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
