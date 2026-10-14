using Mailcast.RaptorQ;
using Microsoft.Extensions.Time.Testing;

namespace Packet.Mailcast.Tests;

public class ReceiverStoreTests
{
    private static readonly DateOnly Day1 = new(2026, 10, 4);

    private static SlotBroadcast PlanDay(IEnumerable<Bulletin> bulletins, DateOnly day, int seed = 1) =>
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
        store.Acknowledge(bulletins[0].Bid, BbsVerdict.Accepted);
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
            Assert.True(esi < k + 6, $"needed more than five spare pieces, K = {k}: {string.Join(", ", outcomes)} | {string.Join(" / ", log)}");
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
        Assert.Contains(log, l => l.Contains($"piece {badEsi} was bad", StringComparison.Ordinal) || l.Contains("of its", StringComparison.Ordinal));
        Assert.All(log, l => Assert.True(l.All(char.IsAscii)));
    }

    [Theory]
    [InlineData(0u, 1u)] // an early burst
    [InlineData(2u, 9u)] // apart
    [InlineData(1u, 4u)]
    public void TwoBadPieces_AreRecovered(uint bad1, uint bad2)
    {
        using var dir = new TempDirectory();
        var bulletin = TestBulletins.Make(16, 20000);
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        int k = obj.SourceSymbols;
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });
        Bulletin? completed = null;
        uint esi = 0;
        for (; completed is null; esi++)
        {
            Assert.True(esi < 3 * k, $"not recovered after {esi} pieces, K = {k}");
            var frame = obj.Frame(esi);
            if (esi == bad1 || esi == bad2)
            {
                var symbol = frame.Symbol.ToArray();
                symbol[7] ^= 0x10;
                frame = new MailcastFrame(frame.ObjectId, frame.DictionaryId, frame.Oti, esi, symbol);
            }
            completed = store.Accept(frame.ToBytes()).Bulletin;
        }
        Assert.Equal(bulletin, completed);
        Assert.Contains(log, l => l.Contains("rebuilt from", StringComparison.Ordinal) && l.Contains("of its", StringComparison.Ordinal));
    }

    [Fact]
    public void HashMatchesButUnusable_IsNotMarkedDone()
    {
        // The head end sent an object that is what it says but does not decompress. Its pieces
        // go, it is not marked done, and later frames of it are ignored until the next start.
        using var dir = new TempDirectory();
        var junk = new byte[3000];
        new Random(5).NextBytes(junk);
        junk[0] = (byte)ObjectKind.Bulletin;
        var oti = new ObjectTransmissionInformation(junk.Length, MailcastFrame.StandardSymbolSize, 1, 1, MailcastFrame.StandardAlignment);
        var obj = TransferObject.FromStored(junk, ZstdDictionary.Gb7rdg1Id, oti);
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });
        AcceptResult result = new(FrameOutcome.Stored);
        uint esi = 0;
        while (result.Outcome == FrameOutcome.Stored)
        {
            result = store.Accept(obj.Frame(esi++).ToBytes());
        }
        Assert.Equal(FrameOutcome.Rejected, result.Outcome);
        Assert.Contains("does not decompress", result.Detail, StringComparison.Ordinal);
        Assert.False(store.IsComplete(obj.ObjectId));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir.Path, "done")));
        Assert.Equal(FrameOutcome.Rejected, store.Accept(obj.Frame(esi++).ToBytes()).Outcome);
        Assert.Equal(0, store.PartialObjects);

        var reopened = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        Assert.Equal(FrameOutcome.Stored, reopened.Accept(obj.Frame(esi).ToBytes()).Outcome);
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
        for (uint esi = (uint)k; completed is null && esi < k + 6; esi++)
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
        store.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);

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
        Assert.Equal([bulletin], store.Pending()); // the outbox is in memory, so it is only seen on opening

        var reopened = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });
        Assert.Equal([bulletin], reopened.Pending());
        Assert.True(File.Exists(Path.Combine(dir.Path, "quarantine", "0000000000000001.bulletin")));
        Assert.Single(log, l => l.Contains("quarantined", StringComparison.Ordinal));
        Assert.Equal([bulletin], reopened.Pending());
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
        store.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);
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

    /// <summary>
    /// Issue #68: a directory heard days ago is still the newest one heard (nothing newer has
    /// come in since) and is still remembered inside <see cref="ReceiverStore.KnownDirectoriesRetention"/>.
    /// A bulletin completed since, that no directory, old or new, ever named (G7TAJ's IARU
    /// 08-OCT, completed from its own pieces without the day's directory ever reaching him
    /// whole), still shows on the held list, named from the bulletin itself.
    /// </summary>
    [Fact]
    public void HeldBulletins_AStaleDirectoryPlusABulletinNoDirectoryNames_ShowsBoth()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });
        var bulletins = TestBulletins.Day(30, 2);
        var plan = PlanDay(bulletins, Day1);
        ulong first = Ids.Of(bulletins.OrderBy(b => b.Bid, StringComparer.Ordinal).First());
        Feed(store, plan.Frames.Where(f => f.ObjectId == Ids.DirectoryOf(plan) || f.ObjectId == first));
        Assert.NotNull(store.Directory);
        var namedBid = bulletins.OrderBy(b => b.Bid, StringComparer.Ordinal).First().Bid;

        // Four days pass; nothing newer is heard, so the same directory is still "the newest
        // heard" and is still within the 48 h retention that keeps it remembered.
        time.Advance(TimeSpan.FromHours(20));

        // A bulletin no directory, old or new, ever named: rebuilt from its own pieces alone.
        var orphan = TestBulletins.Make(999, 2000);
        var obj = TransferObject.ForBulletin(orphan, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        for (uint esi = 0; !store.IsComplete(obj.ObjectId); esi++)
        {
            store.Accept(obj.Frame(esi).ToBytes());
        }

        var held = store.HeldBulletins();
        var namedRow = Assert.Single(held, h => h.Bid == namedBid);
        Assert.True(namedRow.Complete);
        var orphanRow = Assert.Single(held, h => h.ObjectId == obj.ObjectId);
        Assert.True(orphanRow.Complete);
        Assert.Equal(orphan.Bid, orphanRow.Bid);
        Assert.Equal(orphan.Title, orphanRow.Title);
    }

    /// <summary>
    /// Issue #68: a part-received object that no known directory names (today's or any other
    /// remembered one) still gets a row, with its progress bar, so a weak station sees the
    /// pieces it has of a bulletin it has not heard named yet.
    /// </summary>
    [Fact]
    public void HeldBulletins_APartialObjectNoDirectoryNames_ShowsWithItsBar()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        var bulletins = TestBulletins.Day(31, 2);
        var plan = PlanDay(bulletins, Day1);
        Feed(store, plan.Frames.Where(f => f.ObjectId == Ids.DirectoryOf(plan)));
        Assert.NotNull(store.Directory);

        // A big bulletin no directory names, with only its first piece heard: genuinely partial.
        var partial = TestBulletins.Make(998, 9000);
        var obj = TransferObject.ForBulletin(partial, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.True(obj.SourceSymbols > 1);
        store.Accept(obj.Frame(0).ToBytes());

        var row = Assert.Single(store.HeldBulletins(), h => h.ObjectId == obj.ObjectId);
        Assert.False(row.Complete);
        Assert.Null(row.Bid);
        Assert.Null(row.Title);
        Assert.Equal(1, row.Received);
        Assert.Equal(obj.Oti.SourceBlockSymbols(0), row.Needed);
        Assert.True(row.Needed > 1);
    }

    /// <summary>
    /// Issue #68 review: a station can be stuck on one directory for well over the 48 h
    /// <see cref="ReceiverStore.KnownDirectoriesRetention"/>, with nothing newer ever heard to
    /// refresh <c>_knownDirectories</c>. Its entries still get their titles from the persisted
    /// <see cref="ReceiverStore.Directory"/> field, which never ages out on its own: on the real
    /// CT 150 store this was checked against (G4WNC, stuck on the 2026-10-07 directory), all 10
    /// entries lost their names without this fallback.
    /// </summary>
    [Fact]
    public void HeldBulletins_ADirectoryHeardOnceMoreThan48HoursAgo_StillNamesItsEntries()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });
        var bulletins = TestBulletins.Day(41, 3);
        var plan = PlanDay(bulletins, Day1);
        Feed(store, plan.Frames); // the directory and every bulletin in it, all rebuilt
        Assert.NotNull(store.Directory);

        // Heard once, then nothing ever again: no repeat to refresh _knownDirectories, and well
        // past the 48 h it keeps an entry for.
        time.Advance(TimeSpan.FromHours(72));
        store.Expire(); // Accept() would do this anyway at the next frame; forced to be sure

        var held = store.HeldBulletins();
        Assert.Equal(bulletins.Count, held.Count);
        foreach (var bulletin in bulletins)
        {
            var row = Assert.Single(held, h => h.Bid == bulletin.Bid);
            Assert.True(row.Complete);
            Assert.Equal(bulletin.Title, row.Title);
        }
    }

    /// <summary>
    /// Issue #68 review: a directory repeated every so often, each gap well inside the 48 h
    /// retention, must keep being "remembered" past the point where 48 h have passed since it
    /// was first (and only) rebuilt - the window means <em>last</em> heard, not first.
    /// </summary>
    [Fact]
    public void Accept_ARepeatOfAKnownDirectory_RefreshesWhenItWasLastHeard()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });
        var bulletins = TestBulletins.Day(42, 2);
        var plan = PlanDay(bulletins, Day1);
        var directoryFrame = plan.Frames.First(f => f.ObjectId == Ids.DirectoryOf(plan));
        Assert.Equal(FrameOutcome.CompletedDirectory, store.Accept(directoryFrame.ToBytes()).Outcome);

        // Three repeats 20 h apart: 60 h in all, more than the 48 h retention counted from the
        // original rebuild, but each gap on its own is well inside it.
        for (int i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromHours(20));
            var repeat = store.Accept(directoryFrame.ToBytes());
            Assert.Equal(FrameOutcome.AlreadyComplete, repeat.Outcome);
            Assert.NotNull(repeat.Directory);
        }
    }

    /// <summary>A big bulletin's object, several pieces long, so one piece leaves it part-received.</summary>
    private static TransferObject BigObject(int number)
    {
        var obj = TransferObject.ForBulletin(TestBulletins.Make(number, 9000), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.True(obj.SourceSymbols > 1);
        return obj;
    }

    /// <summary>The one frame of a small directory of <paramref name="date"/> listing <paramref name="listed"/>.</summary>
    private static MailcastFrame DirectoryFrame(DateOnly date, params TransferObject[] listed)
    {
        var directory = new BroadcastDirectory(date, listed.Select((o, i) => new DirectoryEntry(o.ObjectId, o.DictionaryId, 9000, $"{i + 1}_TEST", $"Bulletin {i + 1}")));
        var obj = TransferObject.ForDirectory(directory, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(1, obj.SourceSymbols);
        return obj.Frame(0);
    }

    /// <summary>
    /// A part-received object that a directory heard after its last piece leaves out has been
    /// retired by the head end: it is marked, shows when it will be cleared, and goes 24 h later,
    /// not 14 days. Named by an earlier directory or never named at all, the same.
    /// </summary>
    [Fact]
    public void NoLongerSent_AbsentFromALaterDirectory_IsMarkedThenDroppedAfter24Hours()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time, Log = log.Add });
        var named = BigObject(51);
        var unnamed = BigObject(52);
        var stays = BigObject(53);
        Assert.Equal(FrameOutcome.CompletedDirectory, store.Accept(DirectoryFrame(Day1, named, stays).ToBytes()).Outcome);
        time.Advance(TimeSpan.FromMinutes(5));
        store.Accept(named.Frame(0).ToBytes());
        store.Accept(unnamed.Frame(0).ToBytes());
        store.Accept(stays.Frame(0).ToBytes());
        Assert.All(store.HeldBulletins(), h => Assert.Null(h.NoLongerSentSince));

        time.Advance(TimeSpan.FromHours(1)); // the next slot's directory has only "stays"
        var heard = time.GetUtcNow();
        var result = store.Accept(DirectoryFrame(Day1, stays).ToBytes());
        Assert.Equal(FrameOutcome.CompletedDirectory, result.Outcome);
        Assert.Equal(new[] { named.ObjectId, unnamed.ObjectId }.Order(), result.NoLongerSent!.Order());

        var held = store.HeldBulletins();
        var namedRow = Assert.Single(held, h => h.ObjectId == named.ObjectId);
        Assert.Equal("1_TEST", namedRow.Bid);
        Assert.Equal(heard, namedRow.NoLongerSentSince);
        Assert.Equal(heard + TimeSpan.FromHours(24), namedRow.ClearedAt);
        Assert.Equal(heard, Assert.Single(held, h => h.ObjectId == unnamed.ObjectId).NoLongerSentSince);
        var staysRow = Assert.Single(held, h => h.ObjectId == stays.ObjectId);
        Assert.Null(staysRow.NoLongerSentSince);
        Assert.Null(staysRow.ClearedAt);
        Assert.Equal(stays.ObjectId, held[0].ObjectId); // still being sent: above the given-up ones
        Assert.Contains(log, l => l.Contains(ObjectId.Format(named.ObjectId) + " (1_TEST) has 1 of", StringComparison.Ordinal)
            && l.Contains("no longer in the directory", StringComparison.Ordinal) && l.Contains("2026-10-05 10:05 UTC", StringComparison.Ordinal));
        Assert.All(log, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), l));

        // Hearing the same directory again marks nothing twice.
        Assert.Null(store.Accept(DirectoryFrame(Day1, stays).ToBytes()).NoLongerSent);

        time.Advance(TimeSpan.FromHours(23));
        store.DropNoLongerSent();
        Assert.Equal(3, store.PartialObjects);
        time.Advance(TimeSpan.FromHours(1));
        store.Accept(stays.Frame(1).ToBytes()); // any frame drops what is due
        Assert.Equal(1, store.PartialObjects);
        Assert.Equal(stays.ObjectId, Assert.Single(store.HeldBulletins(), h => !h.Complete).ObjectId);
        Assert.Contains(log, l => l.Contains(ObjectId.Format(unnamed.ObjectId) + " dropped with 1 of", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir.Path, "stopped")));
    }

    /// <summary>
    /// A directory heard before the object's last piece says nothing about it: the object may
    /// have joined the rotation since. Nor does one older than the newest directory heard.
    /// </summary>
    [Fact]
    public void NoLongerSent_NotMarkedByADirectoryBeforeItsLastPiece_OrByAnOlderDay()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time });
        var obj = BigObject(54);
        var other = BigObject(55);
        store.Accept(DirectoryFrame(Day1.AddDays(1), other).ToBytes());
        time.Advance(TimeSpan.FromMinutes(1));
        store.Accept(obj.Frame(0).ToBytes()); // after the directory: it may be new to the rotation
        Assert.Null(Assert.Single(store.HeldBulletins(), h => h.ObjectId == obj.ObjectId).NoLongerSentSince);

        time.Advance(TimeSpan.FromHours(1));
        var stale = store.Accept(DirectoryFrame(Day1).ToBytes()); // yesterday's: not about now
        Assert.Equal(FrameOutcome.CompletedDirectory, stale.Outcome);
        Assert.Null(stale.NoLongerSent);
        Assert.Null(Assert.Single(store.HeldBulletins(), h => h.ObjectId == obj.ObjectId).NoLongerSentSince);
    }

    /// <summary>
    /// The mark is kept across a restart, and undone by a new piece (the head end sending it
    /// again) or by a later directory that lists it again (the one that left it out was a stray).
    /// </summary>
    [Fact]
    public void NoLongerSent_SurvivesARestart_AndANewPieceOrAListingUndoesIt()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        var options = TestStores.Fast with { Time = time, Log = log.Add };
        var store = new ReceiverStore(dir.Path, Compression.Default, options);
        var requeued = BigObject(56);
        var relisted = BigObject(57);
        var gone = BigObject(58);
        store.Accept(requeued.Frame(0).ToBytes());
        store.Accept(relisted.Frame(0).ToBytes());
        store.Accept(gone.Frame(0).ToBytes());
        time.Advance(TimeSpan.FromHours(1));
        var marked = time.GetUtcNow();
        Assert.Equal(3, store.Accept(DirectoryFrame(Day1).ToBytes()).NoLongerSent!.Count);

        time.Advance(TimeSpan.FromHours(2));
        var reopened = new ReceiverStore(dir.Path, Compression.Default, options);
        Assert.All(reopened.HeldBulletins(), h => Assert.Equal(marked, h.NoLongerSentSince));

        reopened.Accept(requeued.Frame(1).ToBytes());
        Assert.Contains(log, l => l.Contains(ObjectId.Format(requeued.ObjectId) + " has a new piece", StringComparison.Ordinal));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(reopened.Accept(DirectoryFrame(Day1, requeued, relisted).ToBytes()).NoLongerSent);
        Assert.Contains(log, l => l.Contains(ObjectId.Format(relisted.ObjectId) + " (2_TEST) is in the directory again", StringComparison.Ordinal));
        var held = reopened.HeldBulletins();
        Assert.Null(Assert.Single(held, h => h.ObjectId == requeued.ObjectId).NoLongerSentSince);
        Assert.Null(Assert.Single(held, h => h.ObjectId == relisted.ObjectId).NoLongerSentSince);
        Assert.Equal(marked, Assert.Single(held, h => h.ObjectId == gone.ObjectId).NoLongerSentSince);

        time.Advance(TimeSpan.FromHours(22)); // 24 h and a minute after the mark
        var again = new ReceiverStore(dir.Path, Compression.Default, options);
        Assert.Equal(2, again.PartialObjects);
        Assert.DoesNotContain(again.HeldBulletins(), h => h.ObjectId == gone.ObjectId);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(dir.Path, "objects")), d => Path.GetFileName(d).StartsWith(ObjectId.Format(requeued.ObjectId), StringComparison.Ordinal));
    }
}
