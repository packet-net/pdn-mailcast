using Mailcast.RaptorQ;
using Microsoft.Extensions.Time.Testing;

namespace Packet.Mailcast.Tests;

/// <summary>A dropped partial bulletin counts as given up only with real progress, and never when the bulletin arrived (issue #240).</summary>
public class GivenUpCountTests
{
    private static readonly DateOnly Day = new(2026, 10, 30);

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));

    private static ReceiverStore Open(string path, FakeTimeProvider time, List<string>? log = null) =>
        new(path, Compression.Default, TestStores.Fast with { Time = time, Log = log is null ? null : log.Add });

    private static TransferObject Object(int seed, string? bid = null)
    {
        var a = TestBulletins.Make(seed, 8000);
        var b = bid is null ? a : Bulletin.FromMessageText(a.Type, a.From, a.To, a.At, bid, "another title", a.Date, "R:261030/0800Z 1@G8BPQ.#1.GBR.EURO\r\n" + a.MessageText);
        return TransferObject.ForBulletin(b, ZstdDictionary.Dictionary1Id, Compression.Default);
    }

    private static void Feed(ReceiverStore store, TransferObject o, int pieces)
    {
        for (int i = 0; i < pieces; i++)
        {
            store.Accept(o.Frame((uint)i).ToBytes());
        }
    }

    private static void List(ReceiverStore store, params (TransferObject Object, string Bid)[] listed)
    {
        var entries = listed.Select(l => new DirectoryEntry(l.Object.ObjectId, l.Object.DictionaryId, 400, l.Bid, "title")).ToArray();
        var dir = TransferObject.ForDirectory(new BroadcastDirectory(Day, entries), ZstdDictionary.Dictionary1Id, Compression.Default);
        Feed(store, dir, dir.SourceSymbols);
    }

    /// <summary>Retires everything held: the next directory lists only a bulletin nobody holds, then a day passes.</summary>
    private static void Retire(ReceiverStore store, FakeTimeProvider time)
    {
        time.Advance(TimeSpan.FromHours(1));
        List(store, (Object(999), "999_OTHER"));
        time.Advance(TimeSpan.FromHours(25));
        store.DropNoLongerSent();
    }

    [Fact]
    public void AStrayPiece_IsTouched_HalfTheKOrMore_IsGivenUp()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var log = new List<string>();
        var store = Open(dir.Path, time, log);
        var stray = Object(1);
        var short1 = Object(2);
        var half = Object(3);
        var most = Object(4);
        Assert.True(short1.SourceSymbols > 3);
        int halfNeeded = (half.SourceSymbols + 1) / 2;
        List(store, (stray, "1_TEST"), (short1, "2_TEST"), (half, "3_TEST"), (most, "4_TEST"));
        time.Advance(TimeSpan.FromMinutes(5));
        Feed(store, stray, 1);
        Feed(store, short1, (short1.SourceSymbols + 1) / 2 - 1);
        Feed(store, half, halfNeeded);
        Feed(store, most, most.SourceSymbols - 1);

        Retire(store, time);

        var dropped = store.TakeGivenUp().ToDictionary(d => d.ObjectId);
        Assert.Equal(4, dropped.Count);
        Assert.False(dropped[stray.ObjectId].GaveUp);
        Assert.False(dropped[short1.ObjectId].GaveUp);
        Assert.True(dropped[half.ObjectId].GaveUp);
        Assert.Equal((halfNeeded, half.SourceSymbols, "3_TEST"), (dropped[half.ObjectId].Held, dropped[half.ObjectId].Needed, dropped[half.ObjectId].Bid));
        Assert.True(dropped[most.ObjectId].GaveUp);
        Assert.Contains(log, l => l.Contains("3_TEST", StringComparison.Ordinal) && l.Contains($"dropped with {halfNeeded} of {half.SourceSymbols} pieces", StringComparison.Ordinal) && l.EndsWith("counted as given up", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("1_TEST", StringComparison.Ordinal) && l.EndsWith("counted as touched only", StringComparison.Ordinal));
        Assert.All(log, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), l));
    }

    [Fact]
    public void ADroppedPartial_WhoseBidWasDeliveredViaAnotherObject_IsNotCounted()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var log = new List<string>();
        var store = Open(dir.Path, time, log);
        string bid = "1_SAME";
        // One copy is delivered; the other, under another object, is a partial.
        var delivered = Object(12, bid);
        var partial = Object(13, bid);
        List(store, (delivered, bid), (partial, bid));
        Feed(store, partial, (partial.SourceSymbols + 1) / 2 + 1);
        Feed(store, delivered, delivered.SourceSymbols);
        store.Acknowledge(bid, BbsVerdict.Accepted);
        time.Advance(TimeSpan.FromMinutes(5));

        Retire(store, time);

        Assert.Empty(store.TakeGivenUp());
        Assert.DoesNotContain(log, l => l.EndsWith("counted as given up", StringComparison.Ordinal));
    }

    [Fact]
    public void ADroppedPartial_WhoseBidIsAlreadyDoneUnderAnotherObject_IsNotCounted()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir.Path, time);
        var finished = Object(21, "1_SAME");
        var partial = Object(22, "1_SAME");
        List(store, (finished, "1_SAME"), (partial, "1_SAME"));
        time.Advance(TimeSpan.FromMinutes(5));
        Feed(store, partial, (partial.SourceSymbols + 1) / 2 + 1);
        Feed(store, finished, finished.SourceSymbols); // complete, waiting for the BBS (not acknowledged)

        Retire(store, time);

        Assert.Empty(store.TakeGivenUp());
    }
}
