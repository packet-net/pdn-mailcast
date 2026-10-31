using Mailcast.RaptorQ;

namespace Packet.Mailcast.Tests;

/// <summary>Issue #214: the keep of pieces and rebuilt objects, provenance, and the poisoning defence.</summary>
public class PieceKeepTests
{
    private static ReceiverStoreOptions Options(List<string>? log = null, long keepMax = 64L * 1024 * 1024) =>
        TestStores.Fast with { Log = log is null ? null : log.Add, KeepMaxBytes = keepMax };

    private static TransferObject Experiment(int seed, int size = 3000)
    {
        var rng = new Random(seed);
        var content = new byte[size];
        rng.NextBytes(content);
        return TransferObject.ForContent(0x71, content, default, false, Compression.NoDictionary, Compression.Default, symbolSize: 240);
    }

    private static TransferObject Bulletin(int seed) =>
        TransferObject.ForBulletin(TestBulletins.Make(seed, 4000), ZstdDictionary.Gb7rdg1Id, Compression.Default, 240);

    [Fact]
    public void AnObjectOfAnyType_IsKept_WithItsPiecesAndRebuiltOctets()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options());
        var obj = Experiment(1);
        var frames = Enumerable.Range(0, obj.SourceSymbols + 3).Select(i => obj.Frame((uint)i)).ToList();
        foreach (var f in frames.Take(5))
        {
            Assert.Equal(FrameOutcome.Stored, store.Accept(f.ToBytes(), "GB7RDG").Outcome);
        }
        var partial = Assert.Single(store.KeptObjects(0, 10).Rows);
        Assert.False(partial.Kept.Complete);
        Assert.Equal(5, partial.Kept.PiecesHeld);
        Assert.Equal((byte)0x71, partial.ContentType); // from piece 0
        Assert.Null(store.KeptData(obj.ObjectId));

        foreach (var f in frames.Skip(5))
        {
            store.Accept(f.ToBytes(), "GB7RDG");
        }
        var done = Assert.Single(store.KeptObjects(0, 10).Rows);
        Assert.True(done.Kept.Complete);
        Assert.Equal(obj.ObjectId, done.Kept.ObjectId);
        var data = store.KeptData(obj.ObjectId);
        Assert.NotNull(data);
        Assert.Equal(obj.Bytes.ToArray(), data.Value.Data);
        var pieces = store.KeptPieces(obj.ObjectId, 0, 500)!;
        Assert.All(pieces.Pieces, p => Assert.True(MailcastFrame.TryParse(p.Frame, out _)));
        Assert.All(pieces.Pieces, p => Assert.Equal(new PieceSource(false, "GB7RDG"), p.Source));
        Assert.Equal(0, store.PartialObjects);
    }

    [Fact]
    public void Paging_IsBoundedAndResumesFromTheCursor()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options());
        for (int i = 0; i < 5; i++)
        {
            store.Accept(Experiment(10 + i).Frame(0).ToBytes());
        }
        var first = store.KeptObjects(0, 2);
        Assert.Equal(2, first.Rows.Count);
        Assert.True(first.More);
        var second = store.KeptObjects(first.Next, 10);
        Assert.Equal(3, second.Rows.Count);
        Assert.False(second.More);
        Assert.Empty(store.KeptObjects(second.Next, 10).Rows);
        var obj = Experiment(10);
        store.Accept(obj.Frame(1).ToBytes()); // a change moves the object to the end
        Assert.Equal(obj.ObjectId, Assert.Single(store.KeptObjects(second.Next, 10).Rows).Kept.ObjectId);
    }

    [Fact]
    public void OutsidePieces_AreMarked_AndCountedApart()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options());
        var obj = Experiment(2);
        var air = store.Accept(obj.Frame(0).ToBytes(), "GB7RDG");
        var outside = store.AcceptOutside(obj.Frame(1).ToBytes(), "tool-1");
        Assert.True(air.Kept);
        Assert.True(outside.Kept);
        Assert.False(store.AcceptOutside(obj.Frame(1).ToBytes(), "tool-1").Kept); // a duplicate
        var row = Assert.Single(store.KeptObjects(0, 10).Rows);
        Assert.Equal(2, row.Kept.PiecesHeld);
        Assert.Equal(1, row.Kept.PiecesOutside);
        var pieces = store.KeptPieces(obj.ObjectId, 0, 10)!.Pieces;
        Assert.Equal(new PieceSource(true, "tool-1"), pieces.Single(p => p.EncodingSymbolId == 1).Source);
        Assert.Equal(FrameOutcome.NotAFrame, store.AcceptOutside([1, 2, 3], "x").Outcome);
        var stats = store.KeepStats();
        Assert.Equal((1, 2, 1), (stats.Objects, stats.Pieces, stats.OutsidePieces));
    }

    [Fact]
    public void AnOutsidePiece_HeardLaterOnAir_BecomesAnAirPiece()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options());
        var obj = Experiment(3);
        store.AcceptOutside(obj.Frame(2).ToBytes(), "tool");
        Assert.Equal(FrameOutcome.Duplicate, store.Accept(obj.Frame(2).ToBytes(), "GB7RDG").Outcome);
        Assert.Equal(new PieceSource(false, "GB7RDG"), store.KeptPieces(obj.ObjectId, 0, 10)!.Pieces.Single().Source);
    }

    [Fact]
    public void Restart_KeepsSourcesAndNeverGoesBackOnTheCursor()
    {
        using var dir = new TempDirectory();
        var obj = Experiment(4);
        long before;
        {
            var store = new ReceiverStore(dir.Path, Compression.Default, Options());
            store.Accept(obj.Frame(0).ToBytes(), "GB7RDG");
            store.AcceptOutside(obj.Frame(1).ToBytes(), "tool-2");
            before = store.KeptObjects(0, 10).Next;
            Assert.True(before > 0);
        }
        var reopened = new ReceiverStore(dir.Path, Compression.Default, Options());
        var pieces = reopened.KeptPieces(obj.ObjectId, 0, 10)!.Pieces;
        Assert.Equal(new PieceSource(false, "GB7RDG"), pieces.Single(p => p.EncodingSymbolId == 0).Source);
        Assert.Equal(new PieceSource(true, "tool-2"), pieces.Single(p => p.EncodingSymbolId == 1).Source);
        var again = reopened.KeptObjects(before, 10); // the old cursor still finds it, once
        Assert.Single(again.Rows);
        Assert.True(again.Next > before);
        Assert.True(pieces.All(p => p.Cursor > before));
    }

    [Fact]
    public void ABadPieceFromOutside_ThatPassesItsCrc_IsLeftOut_AndDropped()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options(log));
        var obj = Bulletin(5);
        int k = obj.SourceSymbols;
        for (int i = 0; i < k - 1; i++)
        {
            store.Accept(obj.Frame((uint)i).ToBytes(), "GB7RDG");
        }
        var good = obj.Frame((uint)(k - 1));
        var wrong = good.Symbol.ToArray();
        wrong[3] ^= 0x55;
        var poisoned = new MailcastFrame(good.ObjectId, good.DictionaryId, good.Oti, good.EncodingSymbolId, wrong).ToBytes();
        Assert.True(MailcastFrame.TryParse(poisoned, out _)); // passes its CRC
        var first = store.AcceptOutside(poisoned, "tool");
        Assert.Equal(FrameOutcome.Rejected, first.Outcome); // rebuilt wrong, waits
        var last = store.Accept(obj.Frame((uint)k).ToBytes(), "GB7RDG");
        Assert.Equal(FrameOutcome.CompletedBulletin, last.Outcome); // the bad piece is dropped, so what rebuilt it was all heard on air
        Assert.Contains(log, l => l.Contains("bad piece(s) from outside (tool)", StringComparison.Ordinal));
        Assert.All(log, l => Assert.True(l.All(c => c < 128)));
        Assert.DoesNotContain(store.KeptPieces(obj.ObjectId, 0, 500)!.Pieces, p => p.Source.Outside);
        Assert.Single(store.Pending());
    }

    [Fact]
    public void ABadPieceHeardOnAir_IsFoundByItsStation_BeforeWaitingForMore()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options(log));
        var obj = Bulletin(8);
        int k = obj.SourceSymbols;
        for (int i = 0; i < k - 1; i++)
        {
            store.Accept(obj.Frame((uint)i).ToBytes(), "GB7RDG");
        }
        var good = obj.Frame((uint)(k - 1));
        var wrong = good.Symbol.ToArray();
        wrong[5] ^= 0x33;
        var poisoned = new MailcastFrame(good.ObjectId, good.DictionaryId, good.Oti, good.EncodingSymbolId, wrong).ToBytes();
        Assert.Equal(FrameOutcome.Rejected, store.Accept(poisoned, "G8BAD").Outcome); // k pieces, one wrong: no match, waits

        var last = store.Accept(obj.Frame((uint)k).ToBytes(), "GB7RDG");

        Assert.Equal(FrameOutcome.CompletedBulletin, last.Outcome);
        var line = Assert.Single(log, l => l.Contains("heard from G8BAD", StringComparison.Ordinal));
        Assert.Contains("1 piece(s)", line, StringComparison.Ordinal);
        Assert.All(log, l => Assert.True(l.All(c => c < 128)));
        Assert.DoesNotContain(store.KeptPieces(obj.ObjectId, 0, 500)!.Pieces, p => p.Source.Label == "G8BAD");
    }

    [Fact]
    public void ABadPiece_IsFoundByLabel_WhenThereAreGoodOnesFromAnotherLabel()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options(log));
        var obj = Bulletin(6);
        int k = obj.SourceSymbols;
        for (int i = 0; i < k - 2; i++)
        {
            store.Accept(obj.Frame((uint)i).ToBytes(), "GB7RDG");
        }
        var bad = obj.Frame((uint)(k - 2));
        var wrong = bad.Symbol.ToArray();
        wrong[0] ^= 0x01;
        store.AcceptOutside(new MailcastFrame(bad.ObjectId, bad.DictionaryId, bad.Oti, bad.EncodingSymbolId, wrong).ToBytes(), "liar");
        store.AcceptOutside(obj.Frame((uint)(k - 1)).ToBytes(), "honest");
        var result = store.AcceptOutside(obj.Frame((uint)k).ToBytes(), "honest");
        Assert.Equal(FrameOutcome.CompletedBulletin, result.Outcome);
        Assert.Contains(log, l => l.Contains("(liar)", StringComparison.Ordinal));
        var held = store.KeptPieces(obj.ObjectId, 0, 500)!.Pieces;
        Assert.DoesNotContain(held, p => p.Source.Label == "liar");
        Assert.Contains(held, p => p.Source.Label == "honest");
    }

    [Fact]
    public void ABulletinFinishedWithOutsidePieces_IsDeliveredLikeOneHeardOnAir()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options());
        var obj = Bulletin(7);
        int n = obj.SourceSymbols + 2;
        for (int i = 0; i < n; i++)
        {
            var r = i % 2 == 0 ? store.Accept(obj.Frame((uint)i).ToBytes(), "GB7RDG") : store.AcceptOutside(obj.Frame((uint)i).ToBytes(), "tool");
            Assert.NotEqual(FrameOutcome.Rejected, r.Outcome);
        }
        Assert.Single(store.Pending());
        using var dir2 = new TempDirectory();
        var onAir = new ReceiverStore(dir2.Path, Compression.Default, Options());
        var outcomes = Enumerable.Range(0, n).Select(i => onAir.Accept(obj.Frame((uint)i).ToBytes(), "GB7RDG").Outcome).ToList();
        Assert.Equal(FrameOutcome.CompletedBulletin, outcomes.Single(o => o != FrameOutcome.Stored && o != FrameOutcome.AlreadyComplete));
        Assert.Equal(0, onAir.KeepStats().OutsidePieces);
        Assert.Single(onAir.Pending());
    }

    [Fact]
    public void OutsideFrames_OfATooLargeObject_AreRefused_AndNoPartialIsStartedWhenFull()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options() with { KeepMaxObjectBytes = 1000, MaxPartials = 1 });
        Assert.Equal(FrameOutcome.Rejected, store.AcceptOutside(Experiment(8, 5000).Frame(0).ToBytes(), "x").Outcome);
        var small = Experiment(9, 500);
        Assert.Equal(FrameOutcome.Stored, store.AcceptOutside(small.Frame(0).ToBytes(), "x").Outcome);
        Assert.Equal(FrameOutcome.Rejected, store.AcceptOutside(Experiment(11, 500).Frame(0).ToBytes(), "x").Outcome);
    }

    [Fact]
    public void TheKeep_StaysWithinItsByteLimit_DroppingTheOldest()
    {
        using var dir = new TempDirectory();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero));
        var store = new ReceiverStore(dir.Path, Compression.Default, Options(keepMax: 900) with { Time = time });
        var ids = new List<ulong>();
        for (int i = 0; i < 6; i++)
        {
            var o = Experiment(20 + i, 600);
            ids.Add(o.ObjectId);
            store.Accept(o.Frame(0).ToBytes());
            time.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.True(store.KeepStats().Bytes <= 900);
        var held = store.KeptObjects(0, 100).Rows.Select(r => r.Kept.ObjectId).ToList();
        Assert.Contains(ids[^1], held);
        Assert.DoesNotContain(ids[0], held);
    }

    [Fact]
    public void TheApiKey_IsMadeChangedClearedAndKeptAcrossRestarts()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options());
        Assert.Null(store.ApiKey);
        Assert.False(store.ApiKeyMatches("anything"));
        string key = store.NewApiKey();
        Assert.True(store.ApiKeyMatches(key));
        Assert.False(store.ApiKeyMatches(key + "x"));
        string newer = store.NewApiKey();
        Assert.NotEqual(key, newer);
        Assert.False(store.ApiKeyMatches(key));
        Assert.Equal(newer, new ReceiverStore(dir.Path, Compression.Default, Options()).ApiKey);
        store.ClearApiKey();
        Assert.Null(new ReceiverStore(dir.Path, Compression.Default, Options()).ApiKey);
    }
}
