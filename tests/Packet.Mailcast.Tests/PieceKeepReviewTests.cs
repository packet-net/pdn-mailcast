using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast.Propagation;

namespace Packet.Mailcast.Tests;

/// <summary>Issue #214 review: what pieces from outside may and may not do.</summary>
public class PieceKeepReviewTests
{
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly DirectorySigner Signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly TrustedKey Trusted = TrustedKey.Parse("TEST", Signer.PublicKeyBase64);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static ReceiverStoreOptions Options(FakeTimeProvider? time = null) =>
        new() { FlushToDisk = false, TrustedKeys = [Trusted], Time = time ?? new FakeTimeProvider(Now) };

    private static TransferObject Bulletin(int seed) => TransferObject.ForBulletin(TestBulletins.Make(seed, 3000), ZstdDictionary.Gb7rdg1Id, Compression.Default, 240);

    private static TransferObject SignedDirectory(params TransferObject[] listed)
    {
        var entries = listed.Select((o, i) => new DirectoryEntry(o.ObjectId, o.DictionaryId, 3000, "B" + i, "t" + i, Hash: ObjectId.LongHash(o.DictionaryId, o.Bytes))).ToList();
        var directory = new BroadcastDirectory(Day, entries, new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg), "ms110d-wn3", Signer);
        return TransferObject.ForDirectory(directory, ZstdDictionary.Gb7rdg1Id, Compression.Default, 240);
    }

    private static byte[] RandomBytes(int seed, int size)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static void FeedAir(ReceiverStore store, TransferObject o)
    {
        for (int i = 0; i < o.SourceSymbols + 2; i++)
        {
            store.Accept(o.Frame((uint)i).ToBytes(), "TEST");
        }
    }

    [Fact]
    public void ABulletinBuiltFromOutsidePieces_FollowsTheSameRulesAsOneHeardOnAir()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options());
        var listed = Bulletin(1);
        var unlisted = Bulletin(2);
        var wrongHash = Bulletin(4);
        // A signed directory lists the first with its hash, and the third with another object's hash.
        var entries = new[]
        {
            new DirectoryEntry(listed.ObjectId, listed.DictionaryId, 3000, "B1", "t1", Hash: ObjectId.LongHash(listed.DictionaryId, listed.Bytes)),
            new DirectoryEntry(wrongHash.ObjectId, wrongHash.DictionaryId, 3000, "B4", "t4", Hash: ObjectId.LongHash(listed.DictionaryId, listed.Bytes)),
        };
        var directory = new BroadcastDirectory(Day, entries, new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg), "ms110d-wn3", Signer);
        FeedAir(store, TransferObject.ForDirectory(directory, ZstdDictionary.Gb7rdg1Id, Compression.Default, 240));

        List<byte[]> All(TransferObject o) => [.. Enumerable.Range(0, o.SourceSymbols + 1).Select(i => o.Frame((uint)i).ToBytes())];
        Assert.Contains(store.AcceptOutsideBatch(All(unlisted), "poster"), r => r.Outcome == FrameOutcome.CompletedBulletin); // nothing lists it: delivered, as on air
        Assert.Contains(store.AcceptOutsideBatch(All(listed), "poster"), r => r.Outcome == FrameOutcome.CompletedBulletin);
        Assert.Equal(2, store.Pending().Count);
        Assert.DoesNotContain(store.AcceptOutsideBatch(All(wrongHash), "poster"), r => r.Outcome == FrameOutcome.CompletedBulletin); // a signed hash that does not match refuses it
        Assert.Equal(2, store.Pending().Count);
    }

    [Fact]
    public void AnOnAirBulletin_NoDirectoryLists_IsStillDelivered()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options());
        FeedAir(store, Bulletin(3));
        Assert.Single(store.Pending());
    }

    [Fact]
    public void ReadingsFromOutsideCount_ButNoneDatedInTheFuture_FromAnySource()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        var store = new ReceiverStore(dir.Path, Compression.Default, Options(time));
        TransferObject Reading(DateTimeOffset at) => TransferObject.ForRecord((byte)ObjectKind.Propagation, IonoRecord.Encode(new IonoReading { Station = "DB049", SoundingTimeUtc = at, Source = IonoSource.Giro }).AsSpan(1));

        store.AcceptOutside(Reading(Now.AddHours(3)).Frame(0).ToBytes(), "poster");
        Assert.Null(store.Ionosphere);
        store.Accept(Reading(Now.AddHours(4)).Frame(0).ToBytes(), "TEST");
        Assert.Null(store.Ionosphere);
        Assert.Equal(FrameOutcome.CompletedIonosphere, store.AcceptOutside(Reading(Now.AddMinutes(-10)).Frame(0).ToBytes(), "poster").Outcome);
        Assert.NotNull(store.Ionosphere);
    }

    [Fact]
    public void ABigObjectPostedInThousandsOfFrames_TakesOnlyWhatItNeedsAndRebuildsOnce()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, Options());
        var content = new byte[200_000];
        new Random(5).NextBytes(content);
        var big = TransferObject.ForContent(0x71, content, default, false, Compression.NoDictionary, Compression.Default, symbolSize: 240);
        int k = big.SourceSymbols;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int kept = 0;
        for (uint i = 0; i < 2000; i += 500)
        {
            var batch = Enumerable.Range((int)i, 500).Select(n => big.Frame((uint)n).ToBytes()).ToList();
            kept += store.AcceptOutsideBatch(batch, "poster").Count(r => r.Kept);
        }
        Assert.True(kept <= k + 16, $"{kept} kept of {k} needed");
        Assert.True(store.KeptObjects(0, 10).Rows.Single().Kept.Complete);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), watch.Elapsed.ToString());
    }

    [Fact]
    public void OutsideOnlyObjects_AreCapped_GoFirst_AndDoNotRefreshOnAirExpiry()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        var store = new ReceiverStore(dir.Path, Compression.Default, Options(time) with { MaxPartials = 40, MaxOutsidePartials = 3 });
        var air = TransferObject.ForContent(0x71, RandomBytes(99, 3000), default, false, Compression.NoDictionary, Compression.Default, symbolSize: 240);
        store.Accept(air.Frame(0).ToBytes(), "TEST");
        for (int i = 0; i < 6; i++)
        {
            var o = TransferObject.ForContent(0x71, RandomBytes(100 + i, 3000), default, false, Compression.NoDictionary, Compression.Default, symbolSize: 240);
            var r = store.AcceptOutside(o.Frame(0).ToBytes(), "poster");
            Assert.Equal(i < 3 ? FrameOutcome.Stored : FrameOutcome.Rejected, r.Outcome);
        }
        Assert.Equal(4, store.PartialObjects);

        // Outside pieces do not keep an on-air partial alive.
        time.Advance(TimeSpan.FromDays(10));
        store.AcceptOutside(air.Frame(1).ToBytes(), "poster");
        time.Advance(TimeSpan.FromDays(5));
        store.Expire();
        Assert.Equal(0, store.PartialObjects);
    }

    [Fact]
    public void OutsideOnlyObjects_CannotEvictOnAirOnes_AndAreStoppedAtHalfTheLimit()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        var store = new ReceiverStore(dir.Path, Compression.Default, Options(time) with { KeepMaxBytes = 300_000, MaxPartials = 100 });
        var onAir = new List<ulong>();
        for (int i = 0; i < 20; i++)
        {
            var o = TransferObject.ForContent(0x71, RandomBytes(200 + i, 3000), default, false, Compression.NoDictionary, Compression.Default, symbolSize: 240);
            onAir.Add(o.ObjectId);
            store.Accept(o.Frame(0).ToBytes(), "TEST");
            time.Advance(TimeSpan.FromSeconds(1));
        }
        for (int i = 0; i < 40; i++)
        {
            var o = TransferObject.ForContent(0x71, RandomBytes(300 + i, 60_000), default, false, Compression.NoDictionary, Compression.Default, symbolSize: 240);
            for (int from = 0; from < 200; from += 100)
            {
                store.AcceptOutsideBatch([.. Enumerable.Range(from, 100).Select(n => o.Frame((uint)n).ToBytes())], "poster");
            }
        }
        var held = store.KeptObjects(0, 1000).Rows.Select(r => r.Kept.ObjectId).ToHashSet();
        Assert.All(onAir, id => Assert.Contains(id, held));
        var stats = store.KeepStats();
        Assert.True(stats.OutsidePieces * 270L < 300_000, $"{stats.OutsidePieces} outside pieces");
    }
}
