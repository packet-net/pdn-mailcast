using Mailcast.RaptorQ;

namespace Mailcast.Core.Tests;

public class ReceiverStoreTests
{
    private static readonly DateOnly Day1 = new(2026, 10, 4);

    private static DailyBroadcast PlanDay(IEnumerable<Bulletin> bulletins, DateOnly day, int seed = 1) =>
        BroadcastScheduler.Plan(bulletins.Select(b => new BroadcastBulletin(b, Day1)), day, seed, Compression.Default);

    private static List<Bulletin> Feed(ReceiverStore store, IEnumerable<MailcastFrame> frames)
    {
        var completed = new List<Bulletin>();
        foreach (var frame in frames)
        {
            var result = store.Accept(frame.ToBytes());
            Assert.NotEqual(FrameOutcome.Rejected, result.Outcome);
            if (result.Bulletin is not null)
            {
                completed.Add(result.Bulletin);
            }
        }
        return completed;
    }

    [Fact]
    public void LosslessDay_YieldsEveryBulletinOnce()
    {
        using var dir = new TempDirectory();
        var bulletins = TestBulletins.Day(1, 10);
        var plan = PlanDay(bulletins, Day1);
        var store = new ReceiverStore(dir.Path, Compression.Default);

        var completed = Feed(store, plan.Frames);
        Assert.Equal(bulletins.OrderBy(b => b.Bid), completed.OrderBy(b => b.Bid));

        // Hearing it all again changes nothing.
        Assert.Empty(Feed(store, plan.Frames));
        Assert.All(plan.Frames, f => Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(f.ToBytes()).Outcome));

        Assert.Equal(bulletins.OrderBy(b => b.Bid), store.Pending().OrderBy(b => b.Bid));
        store.Acknowledge(bulletins[0].Bid);
        Assert.Equal(bulletins.Count - 1, store.Pending().Count);
        Assert.Equal(0, store.PartialObjects);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(dir.Path, "objects")));
    }

    [Fact]
    public void Restart_KeepsSymbolsAndNeverYieldsTwice()
    {
        using var dir = new TempDirectory();
        var bulletins = TestBulletins.Day(2, 8);
        var frames = PlanDay(bulletins, Day1).Frames;
        var completed = new List<Bulletin>();
        int chunk = frames.Count / 5;
        for (int i = 0; i < frames.Count; i += chunk)
        {
            var store = new ReceiverStore(dir.Path, Compression.Default); // a restart before every chunk
            completed.AddRange(Feed(store, frames.Skip(i).Take(chunk)));
        }
        Assert.Equal(bulletins.OrderBy(b => b.Bid), completed.OrderBy(b => b.Bid));
        var reopened = new ReceiverStore(dir.Path, Compression.Default);
        Assert.Empty(Feed(reopened, frames));
        Assert.Equal(bulletins.Count, reopened.Pending().Count);
    }

    [Fact]
    public void Open_RebuildsAnObjectWhoseSymbolsAlreadySuffice()
    {
        // As if the process died after storing the last symbol it needed, before rebuilding.
        using var dir = new TempDirectory();
        var bulletin = TestBulletins.Make(5, 6000);
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var objectDir = Path.Combine(dir.Path, "objects",
            $"{ObjectId.Format(obj.ObjectId)}-{obj.DictionaryId:x4}-{Convert.ToHexStringLower(obj.Oti.ToBytes())}");
        Directory.CreateDirectory(objectDir);
        for (uint esi = 3; esi < obj.SourceSymbols + 5; esi++)
        {
            File.WriteAllBytes(Path.Combine(objectDir, $"{esi:x6}.sym"), obj.Frame(esi).Symbol.ToArray());
        }
        File.WriteAllBytes(Path.Combine(objectDir, "000999.sym.tmp"), [1, 2, 3]); // a write cut short

        var store = new ReceiverStore(dir.Path, Compression.Default);
        Assert.Equal([bulletin], store.Pending());
        Assert.True(store.IsComplete(obj.ObjectId));
        Assert.False(File.Exists(Path.Combine(objectDir, "000999.sym.tmp")));
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(obj.Frame(0).ToBytes()).Outcome);
    }

    [Fact]
    public void CorruptRebuild_IsRejectedAndCollectedAgain()
    {
        using var dir = new TempDirectory();
        var bulletin = TestBulletins.Make(6, 9000);
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.True(obj.SourceSymbols >= 3);
        var store = new ReceiverStore(dir.Path, Compression.Default);

        // A frame that passed IL2P's CRC but carries a wrong symbol (another station's mistake,
        // say) makes the rebuild come out wrong; the store notices and starts that object again.
        var bad = obj.Frame(0);
        var badSymbol = bad.Symbol.ToArray();
        badSymbol[100] ^= 0xFF;
        Assert.Equal(FrameOutcome.Stored, store.Accept(new MailcastFrame(bad.ObjectId, bad.DictionaryId, bad.Oti, 0, badSymbol).ToBytes()).Outcome);
        AcceptResult last = new(FrameOutcome.Stored);
        for (uint esi = 1; last.Outcome == FrameOutcome.Stored; esi++)
        {
            last = store.Accept(obj.Frame(esi).ToBytes());
        }
        Assert.Equal(FrameOutcome.Rejected, last.Outcome);
        Assert.False(store.IsComplete(obj.ObjectId));

        var completed = new List<Bulletin>();
        for (uint esi = 100; completed.Count == 0; esi++)
        {
            var result = store.Accept(obj.Frame(esi).ToBytes());
            if (result.Bulletin is not null)
            {
                completed.Add(result.Bulletin);
            }
        }
        Assert.Equal([bulletin], completed);
    }

    [Fact]
    public void Accept_SortsOutOtherTraffic()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default);
        Assert.Equal(FrameOutcome.NotAFrame, store.Accept("CQ CQ de G4ABC"u8).Outcome);

        var oti = new ObjectTransmissionInformation(2000, 940, 1, 1, 4);
        var unknown = new MailcastFrame(1, 999, oti, 0, new byte[940]);
        Assert.Equal(FrameOutcome.UnknownDictionary, store.Accept(unknown.ToBytes()).Outcome);

        var frame = TransferObject.ForBulletin(TestBulletins.Make(1, 5000), 1, Compression.Default).Frame(0);
        Assert.Equal(FrameOutcome.Stored, store.Accept(frame.ToBytes()).Outcome);
        Assert.Equal(FrameOutcome.Duplicate, store.Accept(frame.ToBytes()).Outcome);
    }

    [Fact]
    public void SymbolsOfOneBulletinCompressedTwoWays_DoNotMix()
    {
        using var dir = new TempDirectory();
        var bulletin = TestBulletins.Make(8, 8000);
        var withDictionary = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var without = TransferObject.ForBulletin(bulletin, Compression.NoDictionary, Compression.Default);
        Assert.Equal(withDictionary.ObjectId, without.ObjectId);
        var store = new ReceiverStore(dir.Path, Compression.Default);

        Bulletin? completed = null;
        for (uint esi = 0; completed is null; esi++)
        {
            Assert.True(esi < 100);
            foreach (var obj in new[] { withDictionary, without })
            {
                var result = store.Accept(obj.Frame(esi).ToBytes());
                Assert.NotEqual(FrameOutcome.Rejected, result.Outcome);
                completed ??= result.Bulletin;
            }
        }
        Assert.Equal(bulletin, completed);
        Assert.Single(store.Pending());
    }

    [Fact]
    public void Progress_FollowsTheDirectory()
    {
        using var dir = new TempDirectory();
        var bulletins = TestBulletins.Day(9, 6);
        var plan = PlanDay(bulletins, Day1);
        var store = new ReceiverStore(dir.Path, Compression.Default);
        Assert.Empty(store.Progress());

        // The directory, and every frame of the first bulletin but none of the rest.
        uint first = ObjectId.ForBid(bulletins.OrderBy(b => b.Bid, StringComparer.Ordinal).First().Bid);
        Feed(store, plan.Frames.Where(f => f.ObjectId == plan.Directory.ObjectId || f.ObjectId == first));
        Assert.NotNull(store.Directory);
        var progress = store.Progress();
        Assert.Equal(6, progress.Count);
        Assert.Single(progress, p => p.Complete);
        Assert.Equal(5, progress.Count(p => !p.Complete && p.Received == 0));

        var reopened = new ReceiverStore(dir.Path, Compression.Default);
        Assert.Equal(plan.Directory.Date, reopened.Directory!.Date);
    }
}
