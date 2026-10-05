namespace Packet.Mailcast.Tests;

public class SchedulerTests
{
    private static readonly DateOnly Day1 = new(2026, 10, 4);

    private static List<BroadcastBulletin> Offer(IEnumerable<Bulletin> bulletins, DateOnly firstSeen) =>
        bulletins.Select(b => new BroadcastBulletin(b, firstSeen)).ToList();

    [Fact]
    public void Plan_IsAPureFunctionOfItsInputsAndSeed()
    {
        var offered = Offer(TestBulletins.Day(1, 15), Day1);
        var a = BroadcastScheduler.Plan(offered, Day1, 42, Compression.Default);
        var b = BroadcastScheduler.Plan(offered.AsEnumerable().Reverse(), Day1, 42, Compression.Default);
        Assert.Equal(a.Frames.Select(f => f.ToBytes()), b.Frames.Select(f => f.ToBytes()));

        var c = BroadcastScheduler.Plan(offered, Day1, 43, Compression.Default);
        Assert.NotEqual(a.Frames.Select(f => f.ToBytes()), c.Frames.Select(f => f.ToBytes()));
        Assert.Equal(
            a.Frames.Select(f => Convert.ToHexString(f.ToBytes())).Order(),
            c.Frames.Select(f => Convert.ToHexString(f.ToBytes())).Order());
    }

    [Fact]
    public void DailyDefaults_CarryThreeDaysMostOnTheFirst_AboutTwiceKInAll()
    {
        var options = new ScheduleOptions();
        Assert.Equal(1440, options.SlotMinutes);
        Assert.Equal(3, options.SlotsInRotation);
        foreach (int k in new[] { 1, 2, 3, 10, 35 })
        {
            var perDay = BroadcastScheduler.SymbolsPerCarrying(k, options);
            Assert.Equal(3, perDay.Length);
            Assert.True(perDay[0] > perDay[1] + perDay[2], $"K={k}: day one should get most");
            Assert.InRange(perDay.Sum(), (2 * k) + 1, (2 * k) + 3);
            Assert.Equal(perDay[0], BroadcastScheduler.DueBySlot(k, 0, options));
            Assert.Equal(perDay[0] + perDay[1], BroadcastScheduler.DueBySlot(k, 1, options));
            Assert.Equal(perDay.Sum(), BroadcastScheduler.DueBySlot(k, 2, options));
        }
    }

    [Fact]
    public void Plan_CarriesEachBulletinThreeDaysThenDropsIt()
    {
        var bulletin = TestBulletins.Make(7, 8000);
        var offered = Offer([bulletin], Day1);
        ulong id = Ids.Of(bulletin);
        var seen = new HashSet<uint>();
        for (int day = 0; day < 4; day++)
        {
            var plan = BroadcastScheduler.Plan(offered, Day1.AddDays(day), 1, Compression.Default);
            var esis = plan.Frames.Where(f => f.ObjectId == id).Select(f => f.EncodingSymbolId).ToList();
            if (day < 3)
            {
                Assert.NotEmpty(esis);
                Assert.Single(plan.Directory.Entries);
                Assert.All(esis, e => Assert.True(seen.Add(e), $"ESI {e} repeated on day {day + 1}"));
            }
            else
            {
                Assert.Empty(esis);
                Assert.Empty(plan.Directory.Entries);
            }
        }
        Assert.Equal(Enumerable.Range(0, seen.Count).Select(i => (uint)i), seen.Order());
    }

    [Fact]
    public void Plan_SendsAboutTwiceKOverThreeDays()
    {
        var offered = Offer(TestBulletins.Day(2, 20), Day1);
        int sourceSymbols = 0;
        int frames = 0;
        for (int day = 0; day < 3; day++)
        {
            var plan = BroadcastScheduler.Plan(offered, Day1.AddDays(day), 5, Compression.Default);
            var bulletinObjects = plan.Objects.Where(o => o.Transfer.Kind == ObjectKind.Bulletin).ToList();
            if (day == 0)
            {
                sourceSymbols = bulletinObjects.Sum(o => o.Transfer.SourceSymbols);
            }
            frames += bulletinObjects.Sum(o => o.Count);
        }
        // 2K + 1 each, and a little more where a small bulletin's later days round up to one symbol.
        Assert.InRange(frames / (double)sourceSymbols, 2.0, 4.0);
    }

    [Fact]
    public void Plan_SpreadsEveryObjectAcrossTheBroadcast()
    {
        var offered = Offer(TestBulletins.Day(3, 20), Day1);
        var plan = BroadcastScheduler.Plan(offered, Day1, 9, Compression.Default);
        int n = plan.Frames.Count;
        foreach (var group in plan.Frames.Select((f, i) => (f.ObjectId, i)).GroupBy(x => x.ObjectId))
        {
            var positions = group.Select(x => x.i).ToList();
            if (positions.Count < 2)
            {
                continue;
            }
            // Frame i of n goes in the i-th n-th of the broadcast, give or take the frames around it.
            Assert.True(positions[0] < n / positions.Count + 25, $"object {group.Key:x8} starts late");
            Assert.True(positions[^1] > n - (n / positions.Count) - 25, $"object {group.Key:x8} ends early");
        }
    }

    [Fact]
    public void Plan_RepeatsTheDirectoryRegularly()
    {
        var offered = Offer(TestBulletins.Day(4, 25), Day1);
        var options = new ScheduleOptions();
        var plan = BroadcastScheduler.Plan(offered, Day1, 3, Compression.Default, options);
        ulong directoryId = Ids.DirectoryOf(plan);
        var positions = plan.Frames.Select((f, i) => (f, i)).Where(x => x.f.ObjectId == directoryId).Select(x => x.i).ToList();
        Assert.Equal(0, positions[0]);
        Assert.True(positions.Count >= plan.Objects[0].Transfer.SourceSymbols + options.DirectoryExtra);
        var gaps = positions.Zip(positions.Skip(1), (a, b) => b - a);
        Assert.All(gaps, g => Assert.InRange(g, 2, options.DirectoryEvery));
        Assert.Equal(25, plan.Directory.Entries.Count);
    }

    [Fact]
    public void Plan_SkipsBulletinsOverTheSizeCap()
    {
        var small = TestBulletins.Make(10, 1000);
        var big = TestBulletins.Make(11, 40_000);
        var plan = BroadcastScheduler.Plan(Offer([small, big], Day1), Day1, 1, Compression.Default);
        Assert.Equal([big], plan.Skipped);
        Assert.Equal([small.Bid], plan.Directory.Entries.Select(e => e.Bid));
    }

    [Fact]
    public void Plan_ListsEachBulletinInTheDirectory()
    {
        var bulletins = TestBulletins.Day(5, 5);
        var plan = BroadcastScheduler.Plan(Offer(bulletins, Day1), Day1, 1, Compression.Default);
        foreach (var b in bulletins)
        {
            var entry = plan.Directory.Find(Ids.Of(b));
            Assert.NotNull(entry);
            Assert.Equal(b.Bid, entry.Bid);
            Assert.Equal(b.Title, entry.Title);
            Assert.Equal(b.Serialize().Length, entry.Size);
        }
    }
}
