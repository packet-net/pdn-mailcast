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
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);

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
            var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast); // a restart before every chunk
            completed.AddRange(Feed(store, frames.Skip(i).Take(chunk)));
        }
        Assert.Equal(bulletins.OrderBy(b => b.Bid), completed.OrderBy(b => b.Bid));
        var reopened = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void BadPiece_IsFoundAndDropped_NothingElseLost(uint badEsi)
    {
        // A frame that passed its CRC but carries a wrong symbol (another station's mistake, say)
        // makes the rebuild come out wrong. The store keeps its pieces, finds the bad one once it
        // has a spare, drops it, and finishes without throwing away the good ones.
        using var dir = new TempDirectory();
        var bulletin = TestBulletins.Make(6, 20000);
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        int k = obj.SourceSymbols;
        Assert.True(k >= 6, $"K = {k}");
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, new ReceiverStoreOptions { Log = log.Add });

        var outcomes = new List<FrameOutcome>();
        Bulletin? completed = null;
        for (uint esi = 0; completed is null; esi++)
        {
            Assert.True(esi < k + 3, "needed more than two spare pieces");
            var frame = obj.Frame(esi);
            if (esi == badEsi)
            {
                var symbol = frame.Symbol.ToArray();
                symbol[100] ^= 0xFF;
                frame = new MailcastFrame(frame.ObjectId, frame.DictionaryId, frame.Oti, esi, symbol);
            }
            var result = store.Accept(frame.ToBytes());
            outcomes.Add(result.Outcome);
            completed = result.Bulletin;
        }
        Assert.Equal(bulletin, completed);
        Assert.Contains(FrameOutcome.Rejected, outcomes); // the wrong rebuild at K was noticed
        Assert.Contains(log, l => l.Contains($"piece {badEsi} was bad", StringComparison.Ordinal));
        Assert.All(log, l => Assert.True(l.All(char.IsAscii)));
    }

    [Fact]
    public void WrongRebuild_KeepsPiecesOnDiskAcrossARestart()
    {
        using var dir = new TempDirectory();
        var obj = TransferObject.ForBulletin(TestBulletins.Make(7, 9000), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        int k = obj.SourceSymbols;
        var store = new ReceiverStore(dir.Path, Compression.Default);
        var bad = obj.Frame(0);
        var symbol = bad.Symbol.ToArray();
        symbol[0] ^= 1;
        store.Accept(new MailcastFrame(bad.ObjectId, bad.DictionaryId, bad.Oti, 0, symbol).ToBytes());
        for (uint esi = 1; esi < k; esi++)
        {
            store.Accept(obj.Frame(esi).ToBytes());
        }
        Assert.False(store.IsComplete(obj.ObjectId));
        Assert.Equal(k, Directory.EnumerateFiles(Path.Combine(dir.Path, "objects"), "*.sym", SearchOption.AllDirectories).Count());

        var reopened = new ReceiverStore(dir.Path, Compression.Default);
        Assert.False(reopened.IsComplete(obj.ObjectId));
        Bulletin? completed = null;
        for (uint esi = (uint)k; completed is null && esi < k + 3; esi++)
        {
            completed = reopened.Accept(obj.Frame(esi).ToBytes()).Bulletin;
        }
        Assert.NotNull(completed);
    }

    [Fact]
    public void RejectedSymbol_LeavesNoFolderBehind()
    {
        using var dir = new TempDirectory();
        var obj = TransferObject.ForBulletin(TestBulletins.Make(8, 9000), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var frame = obj.Frame(0);
        var shortFrame = new MailcastFrame(frame.ObjectId, frame.DictionaryId, frame.Oti, 0, frame.Symbol[..10]);
        var store = new ReceiverStore(dir.Path, Compression.Default);
        Assert.Equal(FrameOutcome.Rejected, store.Accept(shortFrame.ToBytes()).Outcome);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(dir.Path, "objects")));
        Assert.Equal(0, store.PartialObjects);
    }

    [Fact]
    public void DoneMarkers_ExpireAfterTheirRetention()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var options = TestStores.Fast with { Time = time };
        var bulletin = TestBulletins.Make(9, 1000);
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var store = new ReceiverStore(dir.Path, Compression.Default, options);
        for (uint esi = 0; !store.IsComplete(obj.ObjectId); esi++)
        {
            store.Accept(obj.Frame(esi).ToBytes());
        }
        store.Acknowledge(bulletin.Bid);

        time.Now += TimeSpan.FromDays(13);
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(obj.Frame(50).ToBytes()).Outcome);

        // Past 14 days the marker is gone and the object is collected again: here one piece
        // rebuilds it, and it is handed over a second time for the BBS's BID check to refuse.
        time.Now += TimeSpan.FromDays(2);
        Assert.Equal(1, obj.SourceSymbols);
        Assert.Equal(FrameOutcome.CompletedBulletin, store.Accept(obj.Frame(51).ToBytes()).Outcome);
    }

    [Fact]
    public void DoneMarker_StaysWhileTheBulletinIsUnacknowledged()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var obj = TransferObject.ForBulletin(TestBulletins.Make(10, 1000), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });
        for (uint esi = 0; !store.IsComplete(obj.ObjectId); esi++)
        {
            store.Accept(obj.Frame(esi).ToBytes());
        }
        time.Now += TimeSpan.FromDays(30);
        store.Expire();
        Assert.True(store.IsComplete(obj.ObjectId));
        Assert.Single(store.Pending());
    }

    [Fact]
    public void Partials_ExpireAfterTheirLastPiece()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var options = TestStores.Fast with { Time = time };
        var a = TransferObject.ForBulletin(TestBulletins.Make(11, 9000), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var b = TransferObject.ForBulletin(TestBulletins.Make(12, 9000), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var store = new ReceiverStore(dir.Path, Compression.Default, options);
        store.Accept(a.Frame(0).ToBytes());
        time.Now += TimeSpan.FromDays(10);
        store.Accept(b.Frame(0).ToBytes());
        time.Now += TimeSpan.FromDays(5); // a's last piece 15 days ago, b's 5
        store.Expire();
        Assert.Equal(1, store.PartialObjects);

        // The age survives a restart: it comes from the symbol files.
        var reopened = new ReceiverStore(dir.Path, Compression.Default, options);
        Assert.Equal(1, reopened.PartialObjects);
        time.Now += TimeSpan.FromDays(10);
        Assert.Equal(0, new ReceiverStore(dir.Path, Compression.Default, options).PartialObjects);
    }

    [Fact]
    public void Partials_AreCapped_OldestLastPieceGoesFirst()
    {
        using var dir = new TempDirectory();
        var time = new ManualTime(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time, MaxPartials = 3, Log = log.Add });
        var objects = Enumerable.Range(0, 5)
            .Select(i => TransferObject.ForBulletin(TestBulletins.Make(20 + i, 9000), ZstdDictionary.Gb7rdg1Id, Compression.Default))
            .ToList();
        foreach (var obj in objects)
        {
            store.Accept(obj.Frame(0).ToBytes());
            time.Now += TimeSpan.FromMinutes(1);
        }
        store.Accept(objects[0].Frame(1).ToBytes()); // too late for the first; it went when the fourth arrived
        Assert.Equal(3, store.PartialObjects);
        Assert.Equal(3, log.Count(l => l.Contains("too many partial objects", StringComparison.Ordinal)));
    }

    [Fact]
    public void Pending_QuarantinesAnUnreadableOutboxFile()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var bulletin = TestBulletins.Make(30, 1000);
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });
        for (uint esi = 0; !store.IsComplete(obj.ObjectId); esi++)
        {
            store.Accept(obj.Frame(esi).ToBytes());
        }
        File.WriteAllText(Path.Combine(dir.Path, "outbox", "0000000000000001.bulletin"), "not a bulletin");
        Assert.Equal([bulletin], store.Pending());
        Assert.True(File.Exists(Path.Combine(dir.Path, "quarantine", "0000000000000001.bulletin")));
        Assert.Single(log);
        Assert.Equal([bulletin], store.Pending());
    }

    [Fact]
    public void UnknownDictionary_AtStartUp_IsSkippedNotFatal()
    {
        using var dir = new TempDirectory();
        var obj = TransferObject.ForBulletin(TestBulletins.Make(31, 9000), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        for (uint esi = 0; esi < obj.SourceSymbols - 1; esi++)
        {
            store.Accept(obj.Frame(esi).ToBytes());
        }

        var log = new List<string>();
        var withoutDictionary = new ReceiverStore(dir.Path, new Compression([]), TestStores.Fast with { Log = log.Add });
        Assert.Single(log);
        Assert.Equal(FrameOutcome.UnknownDictionary, withoutDictionary.Accept(obj.Frame(100).ToBytes()).Outcome);

        // The pieces are still there for when the dictionary is back.
        var again = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        Assert.NotNull(again.Accept(obj.Frame(101).ToBytes()).Bulletin);
    }

    [Fact]
    public void SecondDirectoryOnOneDay_IsAnotherObject_AndTheNewerOneWins()
    {
        using var dir = new TempDirectory();
        var day = new DateOnly(2026, 10, 4);
        var first = TransferObject.ForDirectory(new BroadcastDirectory(day, [new DirectoryEntry(1, 1, 1, "1_X", "one")]), 1, Compression.Default);
        var second = TransferObject.ForDirectory(new BroadcastDirectory(day, [new DirectoryEntry(2, 1, 1, "2_X", "two")]), 1, Compression.Default);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        Assert.Equal(FrameOutcome.CompletedDirectory, store.Accept(first.Frame(0).ToBytes()).Outcome);
        Assert.Equal(FrameOutcome.CompletedDirectory, store.Accept(second.Frame(0).ToBytes()).Outcome);
        Assert.Equal("2_X", store.Directory!.Entries.Single().Bid);
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
    public void OneBulletinCompressedTwoWays_IsTwoObjectsThatDoNotMix()
    {
        // Content-addressed: different octets, different IDs. Both rebuild; the BBS's BID check
        // keeps one.
        using var dir = new TempDirectory();
        var bulletin = TestBulletins.Make(8, 8000);
        var withDictionary = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var without = TransferObject.ForBulletin(bulletin, Compression.NoDictionary, Compression.Default);
        Assert.NotEqual(withDictionary.ObjectId, without.ObjectId);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);

        var completed = new List<Bulletin>();
        for (uint esi = 0; completed.Count < 2; esi++)
        {
            Assert.True(esi < 100);
            foreach (var obj in new[] { withDictionary, without })
            {
                var result = store.Accept(obj.Frame(esi).ToBytes());
                Assert.NotEqual(FrameOutcome.Rejected, result.Outcome);
                if (result.Bulletin is not null)
                {
                    completed.Add(result.Bulletin);
                }
            }
        }
        Assert.All(completed, b => Assert.Equal(bulletin, b));
        Assert.Equal(2, store.Pending().Count);
        store.Acknowledge(bulletin.Bid);
        Assert.Empty(store.Pending());
    }

    [Fact]
    public void Progress_FollowsTheDirectory()
    {
        using var dir = new TempDirectory();
        var bulletins = TestBulletins.Day(9, 6);
        var plan = PlanDay(bulletins, Day1);
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        Assert.Empty(store.Progress());

        // The directory, and every frame of the first bulletin but none of the rest.
        ulong first = Ids.Of(bulletins.OrderBy(b => b.Bid, StringComparer.Ordinal).First());
        Feed(store, plan.Frames.Where(f => f.ObjectId == Ids.DirectoryOf(plan) || f.ObjectId == first));
        Assert.NotNull(store.Directory);
        var progress = store.Progress();
        Assert.Equal(6, progress.Count);
        Assert.Single(progress, p => p.Complete);
        Assert.Equal(5, progress.Count(p => !p.Complete && p.Received == 0));

        var reopened = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        Assert.Equal(plan.Directory.Date, reopened.Directory!.Date);
    }
}
