namespace Mailcast.Core.Tests;

public class HeadEndStoreTests
{
    private static readonly DateOnly Day1 = new(2026, 10, 4);

    [Fact]
    public void Offer_KeepsTheFirstObjectWhateverComesLater()
    {
        using var dir = new TempDirectory();
        var store = new HeadEndStore(dir.Path, Compression.Default);
        var bulletin = TestBulletins.Make(1, 5000);
        var first = store.Offer(bulletin, Day1)!;
        Assert.Equal(0u, first.NextEsi);
        Assert.Equal(Day1, first.FirstSeen);

        // The same BID again, later, with other text: what was kept comes back unchanged.
        var changed = Bulletin.FromMessageText('B', bulletin.From, bulletin.To, bulletin.At, bulletin.Bid.ToLowerInvariant(), "other", bulletin.Date, "other text\r\n");
        var again = store.Offer(changed, Day1.AddDays(1))!;
        Assert.Equal(first.Transfer.ObjectId, again.Transfer.ObjectId);
        Assert.Equal(Day1, again.FirstSeen);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Offer_RefusesBulletinsOverTheCap()
    {
        using var dir = new TempDirectory();
        var store = new HeadEndStore(dir.Path, Compression.Default);
        Assert.Null(store.Offer(TestBulletins.Make(2, 40_000), Day1));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ThreeDays_WithRestarts_SendTheSameObjectWithFreshEsis()
    {
        using var dir = new TempDirectory();
        var bulletins = TestBulletins.Day(3, 6);
        var esis = new Dictionary<ulong, HashSet<uint>>();
        ulong[]? ids = null;
        for (int day = 0; day < 4; day++)
        {
            var today = Day1.AddDays(day);
            // A restart every day, and on day two the compression changes: the kept objects must not.
            var compression = day == 1 ? new Compression([]) : Compression.Default;
            var options = day == 1 ? new ScheduleOptions { DictionaryId = Compression.NoDictionary } : new ScheduleOptions();
            var store = new HeadEndStore(dir.Path, compression, options);
            foreach (var b in bulletins)
            {
                store.Offer(b, today); // offered every day; only the first counts
            }
            var plan = BroadcastScheduler.Plan(store.InRotation(today), today, day, compression, options);
            store.Commit(plan);

            var bulletinIds = plan.Objects.Skip(1).Select(o => o.Transfer.ObjectId).Order().ToArray();
            if (day == 3)
            {
                Assert.Empty(bulletinIds);
                continue;
            }
            ids ??= bulletinIds;
            Assert.Equal(ids, bulletinIds);
            foreach (var f in plan.Frames.Where(f => ids.Contains(f.ObjectId)))
            {
                var seen = esis.TryGetValue(f.ObjectId, out var set) ? set : esis[f.ObjectId] = [];
                Assert.True(seen.Add(f.EncodingSymbolId), $"ESI {f.EncodingSymbolId} of {ObjectId.Format(f.ObjectId)} sent twice");
            }
        }
        foreach (var (_, set) in esis)
        {
            Assert.Equal(Enumerable.Range(0, set.Count).Select(i => (uint)i), set.Order());
        }
    }

    [Fact]
    public void Commit_TwiceInADay_StillNeverRepeats()
    {
        using var dir = new TempDirectory();
        var store = new HeadEndStore(dir.Path, Compression.Default);
        store.Offer(TestBulletins.Make(4, 8000), Day1);
        var morning = BroadcastScheduler.Plan(store.InRotation(Day1), Day1, 1, Compression.Default);
        store.Commit(morning);
        var evening = BroadcastScheduler.Plan(store.InRotation(Day1), Day1, 2, Compression.Default);
        var a = morning.Objects[1];
        var b = evening.Objects[1];
        Assert.Equal(a.NextEsi, b.FirstEsi);
    }

    [Fact]
    public void Expire_ForgetsAfterRememberDays()
    {
        using var dir = new TempDirectory();
        var store = new HeadEndStore(dir.Path, Compression.Default);
        var bulletin = TestBulletins.Make(5, 3000);
        store.Offer(bulletin, Day1);
        Assert.Equal(0, store.Expire(Day1.AddDays(13)));
        Assert.Empty(store.InRotation(Day1.AddDays(13)));
        Assert.Equal(1, store.Expire(Day1.AddDays(14)));
        Assert.Equal(0, new HeadEndStore(dir.Path, Compression.Default).Count);
    }

    [Fact]
    public void Open_DiscardsAnInterruptedOffer()
    {
        using var dir = new TempDirectory();
        var store = new HeadEndStore(dir.Path, Compression.Default);
        store.Offer(TestBulletins.Make(6, 3000), Day1);
        var folder = Directory.EnumerateDirectories(Path.Combine(dir.Path, "bulletins")).Single();
        File.Delete(Path.Combine(folder, "state.txt"));
        var log = new List<string>();
        Assert.Equal(0, new HeadEndStore(dir.Path, Compression.Default, log: log.Add).Count);
        Assert.Single(log);
    }

    [Fact]
    public void Open_DiscardsAnObjectThatDoesNotMatchItsState()
    {
        using var dir = new TempDirectory();
        var store = new HeadEndStore(dir.Path, Compression.Default);
        store.Offer(TestBulletins.Make(7, 3000), Day1);
        var folder = Directory.EnumerateDirectories(Path.Combine(dir.Path, "bulletins")).Single();
        var bytes = File.ReadAllBytes(Path.Combine(folder, "object.bin"));
        bytes[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(folder, "object.bin"), bytes);
        Assert.Equal(0, new HeadEndStore(dir.Path, Compression.Default).Count);
    }

    [Fact]
    public void HeadEndToReceiver_EndToEnd()
    {
        using var head = new TempDirectory();
        using var rx = new TempDirectory();
        var bulletins = TestBulletins.Day(8, 10);
        var loss = new Random(8);
        var delivered = new List<Bulletin>();
        for (int day = 0; day < 3; day++)
        {
            var today = Day1.AddDays(day);
            var store = new HeadEndStore(head.Path, Compression.Default);
            foreach (var b in bulletins)
            {
                store.Offer(b, Day1);
            }
            var plan = BroadcastScheduler.Plan(store.InRotation(today), today, day, Compression.Default);
            store.Commit(plan);
            var receiver = new ReceiverStore(rx.Path, Compression.Default, TestStores.Fast);
            foreach (var frame in plan.Frames.Where(_ => loss.NextDouble() >= 0.3))
            {
                var result = receiver.Accept(frame.ToBytes());
                Assert.NotEqual(FrameOutcome.Rejected, result.Outcome);
                if (result.Bulletin is not null)
                {
                    delivered.Add(result.Bulletin);
                }
            }
        }
        Assert.Equal(bulletins.OrderBy(b => b.Bid), delivered.OrderBy(b => b.Bid));
    }
}
