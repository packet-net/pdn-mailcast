namespace Packet.Mailcast.Tests;

/// <summary>
/// The budget rule in the core, with an airtime of one second a frame so the budget is a frame
/// count: the priority order, the cap, retiring, fresh pieces only, and the directory.
/// </summary>
public class BudgetSchedulerTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private static readonly ScheduleOptions Options = ScheduleOptions.HourlyBudget with { };

    /// <summary>A budget of <paramref name="frames"/> frames, the directory's included.</summary>
    private static SlotBudget Frames(int frames) => new(TimeSpan.FromSeconds(frames), lengths => TimeSpan.FromSeconds(lengths.Count));

    private static CarriedBulletin Held(int seed, int size, uint nextEsi = 0, DateTimeOffset? firstSlot = null)
    {
        var b = TestBulletins.Make(seed, size);
        var transfer = TransferObject.ForBulletin(b, Options.DictionaryId, Compression.Default, MailcastFrame.FixedSymbolSize, MailcastFrame.StandardAlignment);
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
    public void Leftover_AfterSixK_OrThirtySixHours_RetiredAfterTenK_OrFortyEightHours()
    {
        var rule = Options.Budget!;
        var c = Held(6, 6000);
        int k = c.Transfer.SourceSymbols;
        Assert.Equal(6 * k, BroadcastScheduler.LeftoverSymbols(k, rule));
        Assert.Equal(10 * k, BroadcastScheduler.RetireSymbols(k, rule));
        Assert.Equal(TimeSpan.FromHours(48), BroadcastScheduler.RetireAge(rule));

        bool Leftover(uint next, DateTimeOffset? first) => BroadcastScheduler.Leftover(c with { NextEsi = next, FirstSlot = first }, Slot, rule);
        bool Retired(uint next, DateTimeOffset? first) => BroadcastScheduler.Retired(c with { NextEsi = next, FirstSlot = first }, Slot, rule);
        Assert.False(Leftover(0, null) || Retired(0, null));
        Assert.False(Leftover((uint)(6 * k) - 1, Slot));
        Assert.True(Leftover((uint)(6 * k), Slot));
        Assert.False(Retired((uint)(10 * k) - 1, Slot));
        Assert.True(Retired((uint)(10 * k), Slot));
        Assert.False(Leftover((uint)(10 * k), Slot));
        Assert.False(Leftover(3, Slot.AddHours(-35)));
        Assert.True(Leftover(3, Slot.AddHours(-36)));
        Assert.False(Retired(3, Slot.AddHours(-47)));
        Assert.True(Retired(3, Slot.AddHours(-48)));
        // No first slot, no age: one an older head end sent without recording it is still in rotation.
        Assert.False(BroadcastScheduler.Retired(c with { NextEsi = 3, FirstSlot = null }, Slot.AddDays(10), rule));
        Assert.True(BroadcastScheduler.InRotation(c with { NextEsi = (uint)(6 * k), FirstSlot = Slot }, Slot, Options));
        Assert.False(BroadcastScheduler.InRotation(c with { NextEsi = (uint)(10 * k), FirstSlot = Slot }, Slot, Options));

        // Retired ones are neither sent nor listed; leftover ones are listed, and with room to
        // spare get up to their cap, never past 10 K.
        var gone = c with { NextEsi = (uint)(10 * k), FirstSlot = Slot.AddHours(-3) };
        var old = AtCoverage(7, 6000, 1, Slot.AddHours(-48));
        var aged = AtCoverage(8, 6000, 1, Slot.AddHours(-40));
        var nearly = AtCoverage(9, 6000, 0) is var n ? n with { NextEsi = (uint)(10 * n.Transfer.SourceSymbols) - 2, FirstSlot = Slot.AddHours(-3) } : null!;
        var fresh = AtCoverage(10, 6000, 0);
        var plan = BroadcastScheduler.Plan([gone, old, aged, nearly, fresh], Slot, 1, Compression.Default, Options, null, Frames(1000));
        Assert.Equal(new[] { aged.Bid, fresh.Bid, nearly.Bid }.Order(StringComparer.Ordinal), plan.Directory.Entries.Select(e => e.Bid).Order(StringComparer.Ordinal));
        Assert.Equal(2, plan.LeftoverBulletins);
        Assert.Equal(2, ByBid(plan)[nearly.Bid].Count);
        Assert.Equal(BroadcastScheduler.CapSymbols(aged.Transfer.SourceSymbols, rule), ByBid(plan)[aged.Bid].Count);

        var none = BroadcastScheduler.Plan([gone, old], Slot, 1, Compression.Default, Options, null, Frames(1000));
        Assert.Equal(0, none.BulletinFrames);
        Assert.Empty(none.Directory.Entries);
    }

    [Fact]
    public void Leftover_LimitsAreTheRulesOwn_WhenThoseAreLater()
    {
        var c = Held(11, 6000);
        int k = c.Transfer.SourceSymbols;
        var rule = new BudgetRule { RetireCoverage = 12, RetireAfter = TimeSpan.FromHours(60) };
        Assert.Equal(12 * k, BroadcastScheduler.LeftoverSymbols(k, rule));
        Assert.Equal(12 * k, BroadcastScheduler.RetireSymbols(k, rule));
        Assert.Equal(TimeSpan.FromHours(60), BroadcastScheduler.RetireAge(rule));
        // No leftover tier by coverage: from normal carrying straight to retired.
        Assert.False(BroadcastScheduler.Leftover(c with { NextEsi = (uint)(12 * k) - 1, FirstSlot = Slot }, Slot, rule));
        Assert.True(BroadcastScheduler.Retired(c with { NextEsi = (uint)(12 * k), FirstSlot = Slot }, Slot, rule));
        Assert.True(BroadcastScheduler.Retired(c with { NextEsi = 3, FirstSlot = Slot.AddHours(-60) }, Slot, rule));
    }

    [Fact]
    public void Leftover_NeverTakesAPieceFromTheRest()
    {
        var rule = Options.Budget!;
        var rng = new Random(23);
        int shared = 0;
        for (int trial = 0; trial < 60; trial++)
        {
            var normal = Enumerable.Range(0, rng.Next(1, 12))
                .Select(i => AtCoverage((trial * 100) + i, 1000 + rng.Next(12000), rng.NextDouble() * 5.5))
                .ToList();
            var leftover = Enumerable.Range(0, rng.Next(1, 8))
                .Select(i => AtCoverage((trial * 100) + 50 + i, 1000 + rng.Next(12000), 6 + (rng.NextDouble() * 3.5)))
                .ToList();
            int budget = 10 + rng.Next(200);
            var plan = BroadcastScheduler.Plan([.. normal, .. leftover], Slot, trial, Compression.Default, Options, null, Frames(budget));
            var objects = ByBid(plan);
            Assert.Equal(leftover.Count, plan.LeftoverBulletins);
            Assert.True(plan.Frames.Count <= budget || plan.BulletinFrames == 0);
            if (leftover.Sum(l => objects[l.Bid].Count) == 0)
            {
                continue;
            }
            shared++;
            // A leftover piece went out, so every other bulletin had all a slot gives it.
            foreach (var c in normal)
            {
                int k = c.Transfer.SourceSymbols;
                int room = Math.Min(BroadcastScheduler.CapSymbols(k, rule), BroadcastScheduler.LeftoverSymbols(k, rule) - (int)c.NextEsi);
                Assert.True(objects[c.Bid].Count >= room, $"trial {trial}: {c.Bid} got {objects[c.Bid].Count} of {room} while the leftover tier was sent");
            }
            Assert.All(leftover, l => Assert.True(objects[l.Bid].NextEsi <= BroadcastScheduler.RetireSymbols(l.Transfer.SourceSymbols, rule)));
        }
        Assert.True(shared > 10, $"only {shared} trials had room for the leftover tier");

        // A tight slot: the leftover tier gets nothing, the rest fill it, and both are listed.
        var busy = Enumerable.Range(0, 6).Select(i => AtCoverage(900 + i, 6000, 1)).ToList();
        var spare = AtCoverage(950, 6000, 6.5);
        var tight = BroadcastScheduler.Plan([.. busy, spare], Slot, 1, Compression.Default, Options, null, Frames(30));
        Assert.Equal(0, ByBid(tight)[spare.Bid].Count);
        Assert.True(tight.BulletinFrames > 0);
        Assert.Contains(tight.Directory.Entries, e => e.Bid == spare.Bid);
    }

    [Fact]
    public void Leftover_GoesAfterEveryOtherPiece_SoASlotCutShortLosesItFirst()
    {
        var rule = Options.Budget!;
        var normal = Enumerable.Range(0, 5).Select(i => AtCoverage(700 + i, 3000 + (i * 1500), i * 0.8)).ToList();
        // One crossing 6 K in this slot, whose pieces past 6 K come from the leftover tier's turn.
        var crossing = AtCoverage(710, 6000, 5.8);
        var leftover = Enumerable.Range(0, 4).Select(i => AtCoverage(720 + i, 2000 + (i * 2000), 6.5 + i)).ToList();
        var held = new List<CarriedBulletin>([.. normal, crossing, .. leftover]);
        var plan = BroadcastScheduler.Plan(held, Slot, 3, Compression.Default, Options, null, Frames(1000));
        var byId = held.ToDictionary(c => c.Transfer.ObjectId);
        bool Spare(MailcastFrame f) => byId[f.ObjectId] is var c
            && (BroadcastScheduler.Leftover(c, Slot, rule) || f.EncodingSymbolId >= BroadcastScheduler.LeftoverSymbols(c.Transfer.SourceSymbols, rule));
        var pieces = plan.Frames.Select((f, i) => (f, i)).Where(x => byId.ContainsKey(x.f.ObjectId)).ToList();
        Assert.Contains(pieces, x => Spare(x.f));
        Assert.Contains(pieces, x => !Spare(x.f));
        Assert.Contains(pieces, x => byId[x.f.ObjectId] == crossing && Spare(x.f));

        // Cut the slot at every frame: whatever went, no leftover-turn piece went before any other.
        for (int cut = 0; cut <= plan.Frames.Count; cut++)
        {
            var sent = pieces.Where(x => x.i < cut).ToList();
            if (sent.Any(x => Spare(x.f)))
            {
                Assert.Equal(pieces.Count(x => !Spare(x.f)), sent.Count(x => !Spare(x.f)));
            }
        }

        // The directory still runs through the whole slot, the leftover tier's part included.
        ulong directoryId = plan.Objects[0].Transfer.ObjectId;
        int firstSpare = pieces.First(x => Spare(x.f)).i;
        Assert.Equal(directoryId, plan.Frames[0].ObjectId);
        Assert.Contains(plan.Frames.Skip(firstSpare), f => f.ObjectId == directoryId);
    }

    [Fact]
    public void Leftover_Alone_KeysNothing_ButIsStillListed()
    {
        var spare = AtCoverage(12, 6000, 7);
        var aged = AtCoverage(13, 3000, 2, Slot.AddHours(-40));
        var plan = BroadcastScheduler.Plan([spare, aged], Slot, 1, Compression.Default, Options, null, Frames(1000));
        Assert.Equal(0, plan.BulletinFrames);
        Assert.Equal(2, plan.Directory.Entries.Count);
        Assert.Equal(2, plan.LeftoverBulletins);

        // One fresh bulletin and the slot keys, the leftover tier with it.
        var fresh = AtCoverage(14, 2000, 0);
        var keyed = BroadcastScheduler.Plan([spare, aged, fresh], Slot, 1, Compression.Default, Options, null, Frames(1000));
        Assert.All(ByBid(keyed).Values, o => Assert.True(o.Count > 0, $"{o.Bid} got nothing"));
    }

    [Fact]
    public void Leftover_SurvivesARestart_FromTheStore()
    {
        string root = Directory.CreateTempSubdirectory("mailcast-leftover-").FullName;
        try
        {
            var rule = Options.Budget!;
            var store = new HeadEndStore(root, Compression.Default, Options);
            var a = store.Offer(TestBulletins.Make(15, 6000), new DateOnly(2026, 10, 6))!;
            int k = a.Transfer.SourceSymbols;
            var slot = Slot;
            // Plenty of room in every slot: 0.6 K a slot, so twelve slots take it past 6 K.
            for (int i = 0; i < 12; i++, slot = slot.AddHours(1))
            {
                store.Commit(BroadcastScheduler.Plan(store.InRotation(slot), slot, 1, Compression.Default, Options, store.DirectoryNextEsi, Frames(1000)));
            }
            var held = store.InRotation(slot).Single(c => c.Bid == a.Bid);
            Assert.True(BroadcastScheduler.Leftover(held, slot, rule), $"{held.NextEsi} of K {k} sent");
            Assert.Equal(Slot, held.FirstSlot);

            // A restart reads the same NextEsi and FirstSlot, so the same tier, and carries on from there.
            var reopened = new HeadEndStore(root, Compression.Default, Options);
            var again = reopened.InRotation(slot).Single(c => c.Bid == a.Bid);
            Assert.Equal(held, again with { Transfer = held.Transfer, Envelope = held.Envelope });
            Assert.True(BroadcastScheduler.Leftover(again, slot, rule));
            // A new bulletin keys the slot, and the leftover tier goes with it.
            _ = reopened.Offer(TestBulletins.Make(17, 3000), new DateOnly(2026, 10, 6))!;
            var plan = BroadcastScheduler.Plan(reopened.InRotation(slot), slot, 1, Compression.Default, Options, reopened.DirectoryNextEsi, Frames(1000));
            Assert.Equal(again.NextEsi, ByBid(plan)[a.Bid].FirstEsi);
            Assert.True(ByBid(plan)[a.Bid].Count > 0);
            Assert.Contains(plan.Directory.Entries, e => e.Bid == a.Bid);
            reopened.Commit(plan);

            // And it is gone 48 hours after its first slot, from the store's view as from the scheduler's.
            Assert.Contains(new HeadEndStore(root, Compression.Default, Options).InRotation(Slot.AddHours(47)), c => c.Bid == a.Bid);
            Assert.DoesNotContain(new HeadEndStore(root, Compression.Default, Options).InRotation(Slot.AddHours(48)), c => c.Bid == a.Bid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Issue #69's bigger directory floor made this reachable: with a big enough rotation, the
    /// directory alone can be over a tight budget, with no bulletin frames for the fill to give
    /// up instead (it gives every one up first, since none of them fit either). The directory's
    /// own frame count must then give way, one at a time, rather than the plan going over budget.
    /// </summary>
    [Fact]
    public void Fill_CapsTheDirectoryItself_WhenEvenItAloneIsOverBudget()
    {
        var bulletins = Enumerable.Range(0, 27).Select(i => AtCoverage(400 + i, 1000 + (i * 300), 0)).ToList();
        var tiny = Frames(5);
        var plan = BroadcastScheduler.Plan(bulletins, Slot, 1, Compression.Default, Options, null, tiny);
        Assert.Equal(0, plan.BulletinFrames);
        Assert.True(plan.Objects[0].Count >= 1);
        Assert.True(tiny.Airtime([.. plan.Frames.Select(f => f.ToBytes().Length)]) <= tiny.Limit,
            $"{plan.Frames.Count} frames in a budget of 5, directory alone");

        // A budget too tight even for one frame: still only ever the one, never zero.
        var impossible = Frames(0);
        var smallest = BroadcastScheduler.Plan(bulletins, Slot, 1, Compression.Default, Options, null, impossible);
        Assert.Equal(1, smallest.Objects[0].Count);
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
        var transfer = TransferObject.ForBulletin(b, RealisticOptions.DictionaryId, Compression.Default, MailcastFrame.FixedSymbolSize, MailcastFrame.StandardAlignment);
        return new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, new DateOnly(2026, 10, 5), transfer, 0, null);
    }

    [Fact]
    public void Fill_DirectoryFloor_BeatsK_PlusDirectoryExtra_ForGb7rdgsUsualRotation()
    {
        // A realistic GB7RDG directory, 15 bulletins in rotation: its K is small (RaptorQ with a
        // short, well-compressed object at the fixed 240-octet piece size), so the old
        // K + DirectoryExtra floor (3) left a quarter decoder under 60% for a whole directory;
        // the new floor (6) gets it above 80%.
        var bulletins = Enumerable.Range(0, 15).Select(i => RealisticHeld(200 + i, 1000 + (i * 300))).ToList();
        var plan = BroadcastScheduler.Plan(bulletins, Slot, 1, Compression.Default, RealisticOptions, null, Frames(500), "ms110d-wn3");
        int k = plan.Objects[0].Transfer.SourceSymbols;
        Assert.True(k >= 1);
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
        var shares = ScheduleOptions.HourlyDaylight with { };
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
            var options = ScheduleOptions.HourlyBudget with { Timetable = new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg) };
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
