using System.Diagnostics;
using Mailcast.RaptorQ;

namespace Packet.Mailcast.Tests;

/// <summary>Issue #143: each station sends a bulletin's pieces in its own keyed order.</summary>
public class EsiShuffleTests
{
    private static readonly DateOnly Day1 = new(2026, 10, 4);

    [Theory]
    [InlineData("GB7RDG", 0x0123456789ABCDEFUL, new uint[] { 2585490, 4674432, 12490028, 1902506, 7468273, 16331862 })]
    [InlineData("GB7RDG", 1UL, new uint[] { 4842829, 12080494, 14199429, 11397889, 10215548, 832341 })]
    [InlineData("M0LTE", 0x0123456789ABCDEFUL, new uint[] { 9133786, 6358447, 10953560, 12477515, 9969752, 14056484 })]
    [InlineData("M0LTE", 1UL, new uint[] { 37009, 7927528, 11611762, 15052195, 7108766, 13162541 })]
    public void Map_MatchesTheTestVectors(string callsign, ulong objectId, uint[] expected)
    {
        uint[] counters = [0, 1, 2, 3, 100, 16777215];
        var shuffle = EsiShuffle.For(callsign, objectId);
        Assert.Equal(expected, counters.Select(shuffle.Map));
        Assert.Equal(counters, expected.Select(shuffle.Unmap));
        Assert.Equal(expected, counters.Select(EsiShuffle.For(callsign.ToLowerInvariant(), objectId).Map)); // upper-cased
    }

    [Fact]
    public void Map_IsABijectionOverTheWholeEsiSpace()
    {
        var shuffle = EsiShuffle.For("GB7RDG", 0xFEEDFACECAFEBEEFUL);
        var seen = new bool[1 << 24];
        for (uint c = 0; c < (1u << 24); c++)
        {
            uint esi = shuffle.Map(c);
            Assert.True(esi <= PayloadId.MaxEncodingSymbolId);
            Assert.False(seen[esi], $"count {c} maps to {esi} again");
            seen[esi] = true;
            Assert.Equal(c, shuffle.Unmap(esi));
        }
    }

    [Fact]
    public void Map_RejectsOutOfRange()
    {
        var shuffle = EsiShuffle.For("GB7RDG", 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => shuffle.Map(1u << 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => shuffle.Unmap(1u << 24));
    }

    [Fact]
    public void TwoStations_FirstSixtyPieces_DoNotCollideForAnObject()
    {
        // Chance of a collision is about n squared over 2^24 = 0.02% for n = 60; fixed inputs, so this is deterministic.
        var a = EsiShuffle.For("GB7RDG", 42);
        var b = EsiShuffle.For("M0LTE", 42);
        var first = Enumerable.Range(0, 60).Select(i => a.Map((uint)i)).ToHashSet();
        Assert.DoesNotContain(Enumerable.Range(0, 60).Select(i => b.Map((uint)i)), first.Contains);
        Assert.NotEqual(Enumerable.Range(0, 20).Select(i => a.Map((uint)i)), Enumerable.Range(0, 20).Select(i => EsiShuffle.For("GB7RDG", 43).Map((uint)i)));
    }

    private static HeadEndStore Store(string dir, string? station) =>
        new(dir, Compression.Default, new ScheduleOptions { Station = station });

    /// <summary>Plans and commits one slot a day for some days, returning every ESI sent per object ID (in order).</summary>
    private static Dictionary<ulong, List<uint>> RunDays(string dir, string? station, IEnumerable<Bulletin> offer, int days, bool restartEachDay = true)
    {
        var sent = new Dictionary<ulong, List<uint>>();
        var store = Store(dir, station);
        foreach (var b in offer)
        {
            store.Offer(b, Day1);
        }
        for (int d = 0; d < days; d++)
        {
            if (restartEachDay)
            {
                store = Store(dir, station);
            }
            var day = Day1.AddDays(d);
            var plan = BroadcastScheduler.Plan(store.InRotation(day), day, d, Compression.Default, new ScheduleOptions { Station = station }, store.DirectoryNextEsi);
            store.Commit(plan);
            foreach (var f in plan.Frames)
            {
                if (plan.Objects.Any(o => o.Bid is not null && o.Transfer.ObjectId == f.ObjectId))
                {
                    (sent.TryGetValue(f.ObjectId, out var list) ? list : sent[f.ObjectId] = []).Add(f.EncodingSymbolId);
                }
            }
        }
        return sent;
    }

    [Fact]
    public void TwoHeadEnds_SendingTheSameBulletin_SendDisjointPieces_AndNeverRepeatThemselves_AcrossRestarts()
    {
        using var dirA = new TempDirectory();
        using var dirB = new TempDirectory();
        var bulletins = TestBulletins.Day(7, 4);
        var a = RunDays(dirA.Path, "GB7RDG", bulletins, 3);
        var b = RunDays(dirB.Path, "M0LTE", bulletins, 3);
        Assert.Equal(a.Keys.Order(), b.Keys.Order()); // the same objects
        Assert.NotEmpty(a);
        foreach (var (id, esis) in a)
        {
            Assert.True(esis.Count >= 3, $"only {esis.Count} pieces");
            Assert.Equal(esis.Count, esis.Distinct().Count());
            Assert.Equal(b[id].Count, b[id].Distinct().Count());
            Assert.Empty(esis.Intersect(b[id]));
            Assert.All(esis, e => Assert.True(e > 1000)); // scattered over the whole range, not counting up from 0
        }
    }

    [Fact]
    public void Restart_MidSlot_NeverRepeatsAShuffledPiece_AndStateSaysShuffled()
    {
        using var dir = new TempDirectory();
        var store = Store(dir.Path, "GB7RDG");
        foreach (var b in TestBulletins.Day(8, 3))
        {
            store.Offer(b, Day1);
        }
        var first = BroadcastScheduler.Plan(store.InRotation(Day1), Day1, 1, Compression.Default, new ScheduleOptions { Station = "GB7RDG" }, store.DirectoryNextEsi);
        int cut = first.Frames.Count / 2;
        store.Commit(first, cut);
        var sent = first.Frames.Take(cut).Select(f => (f.ObjectId, f.EncodingSymbolId)).ToHashSet();

        Assert.All(Directory.EnumerateFiles(Path.Combine(dir.Path, "bulletins"), "state.txt", SearchOption.AllDirectories),
            f => Assert.Contains("EsiOrder: shuffled\nEsiStation: GB7RDG\n", File.ReadAllText(f)));
        store = Store(dir.Path, "M0LTE"); // even a changed callsign keeps a held bulletin's order
        var second = BroadcastScheduler.Plan(store.InRotation(Day1), Day1, 1, Compression.Default, new ScheduleOptions { Station = "M0LTE" }, store.DirectoryNextEsi);
        var bulletinIds = second.Objects.Where(o => o.Bid is not null).Select(o => o.Transfer.ObjectId).ToHashSet();
        Assert.DoesNotContain(second.Frames.Where(f => bulletinIds.Contains(f.ObjectId)).Select(f => (f.ObjectId, f.EncodingSymbolId)), sent.Contains);
        foreach (var o in second.Objects.Where(o => o.Bid is not null))
        {
            uint done = (uint)first.Frames.Take(cut).Count(f => f.ObjectId == o.Transfer.ObjectId);
            Assert.Equal(done, o.FirstEsi); // NextEsi still counts pieces
            Assert.Equal(EsiShuffle.For("GB7RDG", o.Transfer.ObjectId).Map(done), second.Frames.First(f => f.ObjectId == o.Transfer.ObjectId).EncodingSymbolId);
        }
    }

    [Fact]
    public void HeldBulletins_KeepTheirSequentialPieces_AndNewOnesAreShuffled()
    {
        using var dir = new TempDirectory();
        var old = TestBulletins.Make(9, 4000);
        var store = Store(dir.Path, null); // before the change: no station named
        store.Offer(old, Day1);
        store.Commit(BroadcastScheduler.Plan(store.InRotation(Day1), Day1, 1, Compression.Default));

        store = Store(dir.Path, "GB7RDG");
        var fresh = TestBulletins.Make(10, 4000);
        store.Offer(fresh, Day1);
        var plan = BroadcastScheduler.Plan(store.InRotation(Day1), Day1, 2, Compression.Default, new ScheduleOptions { Station = "GB7RDG" }, store.DirectoryNextEsi);
        store.Commit(plan);
        var held = plan.Objects.Single(o => o.Bid == old.Bid);
        var taken = plan.Objects.Single(o => o.Bid == fresh.Bid);
        Assert.Null(held.Shuffle);
        Assert.NotNull(taken.Shuffle);
        Assert.True(held.FirstEsi > 0);
        Assert.Equal(Enumerable.Range((int)held.FirstEsi, held.Count).Select(i => (uint)i), plan.Frames.Where(f => f.ObjectId == held.Transfer.ObjectId).Select(f => f.EncodingSymbolId).Order());
        Assert.Equal(Enumerable.Range(0, taken.Count).Select(i => taken.Shuffle!.Map((uint)i)).Order(), plan.Frames.Where(f => f.ObjectId == taken.Transfer.ObjectId).Select(f => f.EncodingSymbolId).Order());
        Assert.DoesNotContain("EsiOrder", File.ReadAllText(Directory.EnumerateFiles(Path.Combine(dir.Path, "bulletins"), "state.txt", SearchOption.AllDirectories)
            .Single(f => File.ReadAllText(f).Contains(old.Bid, StringComparison.Ordinal))));
        // And after a restart the held one is still sequential.
        var reopened = Store(dir.Path, "GB7RDG").InRotation(Day1.AddDays(1));
        Assert.Null(reopened.Single(c => c.Bid == old.Bid).Shuffle);
        Assert.NotNull(reopened.Single(c => c.Bid == fresh.Bid).Shuffle);
    }

    [Fact]
    public void Receiver_RebuildsFromTwoStationsShuffledPieces_AndFromRepairOnlyPieces()
    {
        var bulletin = TestBulletins.Make(11, 6000);
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var a = EsiShuffle.For("GB7RDG", obj.ObjectId);
        var b = EsiShuffle.For("M0LTE", obj.ObjectId);
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        int fed = 0;
        for (uint i = 0; !store.IsComplete(obj.ObjectId); i++)
        {
            // Half from each station, as a receiver in range of both would hear them.
            Assert.True(i < obj.SourceSymbols + 20, "did not rebuild");
            Assert.NotEqual(FrameOutcome.Rejected, store.Accept(obj.Frame(i % 2 == 0 ? a.Map(i / 2) : b.Map(i / 2)).ToBytes()).Outcome);
            fed++;
        }
        Assert.True(fed <= obj.SourceSymbols + 4, $"needed {fed} pieces for K = {obj.SourceSymbols}");

        // And a restart part way keeps what was heard (pieces are stored under their ESI).
        using var dir2 = new TempDirectory();
        var half = new ReceiverStore(dir2.Path, Compression.Default, TestStores.Fast);
        for (uint i = 0; i < obj.SourceSymbols / 2; i++)
        {
            half.Accept(obj.Frame(a.Map(i)).ToBytes());
        }
        var reopened = new ReceiverStore(dir2.Path, Compression.Default, TestStores.Fast);
        for (uint i = 0; !reopened.IsComplete(obj.ObjectId); i++)
        {
            Assert.True(i < obj.SourceSymbols + 20);
            reopened.Accept(obj.Frame(b.Map(i)).ToBytes());
        }
    }

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    public void Decoder_WithScatteredRepairPiecesOnly_IsFastAndSmall_ForRealisticK(int k)
    {
        int symbolSize = MailcastFrame.FixedSymbolSize;
        var data = new byte[k * symbolSize - 7];
        new Random(k).NextBytes(data);
        var oti = new ObjectTransmissionInformation(data.Length, symbolSize, 1, 1, MailcastFrame.StandardAlignment);
        var encoder = new ObjectEncoder(data, oti);
        var shuffle = EsiShuffle.For("GB7RDG", 7);
        var decoder = new ObjectDecoder(oti);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        byte[]? rebuilt = null;
        int fed = 0;
        for (uint i = 0; rebuilt is null; i++)
        {
            Assert.True(i < k + 30, "did not rebuild");
            var id = new PayloadId(0, shuffle.Map(i));
            decoder.Add(id, encoder.Encode(id));
            fed++;
            if (fed >= k)
            {
                rebuilt = decoder.TryDecode();
            }
        }
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(data, rebuilt);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"K={k}: {clock.Elapsed} for {fed} pieces");
        Assert.True(allocated < 2_000_000_000L, $"K={k}: {allocated} bytes allocated");
        Console.WriteLine($"K={k}: rebuilt from {fed} scattered pieces in {clock.ElapsedMilliseconds} ms, {allocated / 1_000_000} MB allocated");
    }
}
