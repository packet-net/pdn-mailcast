using Mailcast.RaptorQ;
using Microsoft.Extensions.Time.Testing;

namespace Packet.Mailcast.Tests;

/// <summary>A second copy of a delivered bulletin is dropped, shown and counted (issue #147).</summary>
public class OtherCopyTests
{
    private static readonly DateOnly Day = new(2026, 10, 30);

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));

    private static ReceiverStore Open(string path, FakeTimeProvider time, List<string>? log = null) =>
        new(path, Compression.Default, TestStores.Fast with { Time = time, Log = log is null ? null : log.Add });

    /// <summary>Two different bulletins that say the same BID (the second in another letter case).</summary>
    private static (TransferObject A, TransferObject B, string Bid) Pair(int seed)
    {
        var a = TestBulletins.Make(seed, 8000);
        var b = Bulletin.FromMessageText(a.Type, a.From, a.To, a.At, a.Bid, "another title for the same bulletin", a.Date, "R:261030/0800Z 1@G8BPQ.#1.GBR.EURO\r\n" + a.MessageText);
        return (TransferObject.ForBulletin(a, ZstdDictionary.Gb7rdg1Id, Compression.Default), TransferObject.ForBulletin(b, ZstdDictionary.Gb7rdg1Id, Compression.Default), a.Bid);
    }

    private static List<MailcastFrame> Frames(TransferObject o, int count) => [.. Enumerable.Range(0, Math.Min(count, o.SourceSymbols)).Select(i => o.Frame((uint)i))];

    private static AcceptResult Feed(ReceiverStore store, IEnumerable<MailcastFrame> frames, string? from = null)
    {
        AcceptResult last = null!;
        foreach (var f in frames)
        {
            last = store.Accept(f.ToBytes(), from);
        }
        return last;
    }

    private static AcceptResult Feed(ReceiverStore store, TransferObject o, string? from = null) => Feed(store, Frames(o, int.MaxValue), from);

    private static void ListBulletins(ReceiverStore store, string? station, params (TransferObject Object, string Bid)[] listed)
    {
        var entries = listed.Select(l => new DirectoryEntry(l.Object.ObjectId, l.Object.DictionaryId, 400, l.Bid, "title")).ToArray();
        var dir = TransferObject.ForDirectory(new BroadcastDirectory(Day, entries, station: station), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Feed(store, dir);
    }

    [Theory]
    [InlineData(BbsVerdict.Accepted)]
    [InlineData(BbsVerdict.AlreadyHad)]
    public void AfterDelivery_AnotherCopyListedByADirectory_IsDroppedItsPartialGoneAndLaterPiecesIgnored(BbsVerdict verdict)
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = Open(dir.Path, Clock(), log);
        var (a, b, bid) = Pair(11);
        ListBulletins(store, null, (a, bid), (b, bid.ToLowerInvariant()));
        Feed(store, Frames(b, 2), "G8BPQ");
        Assert.Equal(1, store.PartialObjects);
        Feed(store, a);
        Assert.Single(store.Pending());

        store.Acknowledge(bid, verdict);

        Assert.True(store.IsOtherCopy(b.ObjectId));
        Assert.False(store.IsOtherCopy(a.ObjectId));
        Assert.Equal(0, store.PartialObjects);
        var again = store.Accept(Frames(b, 5)[4].ToBytes(), "G8BPQ");
        Assert.Equal(FrameOutcome.AlreadyComplete, again.Outcome);
        Assert.Equal(0, store.PartialObjects);
        Assert.DoesNotContain(store.HeldBulletins(), h => h.ObjectId == b.ObjectId);
        Assert.DoesNotContain(store.Progress(), p => p.Entry.ObjectId == b.ObjectId);
        var copy = Assert.Single(store.OtherCopies);
        Assert.Equal("G8BPQ", copy.Station);
        Assert.Equal(a.ObjectId, copy.KeptObjectId);
        Assert.Equal(1, store.OtherCopyCounts(Day)["G8BPQ"]);
        string line = Assert.Single(log, l => l.Contains("other copy", StringComparison.Ordinal));
        Assert.All(line, c => Assert.True(c < 128));
        Assert.Contains("G8BPQ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherCopyCompletingAfterDelivery_IsClassedAsAnOtherCopyNotAFailure()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var (a, b, bid) = Pair(12);
        Feed(store, a);
        store.Acknowledge(bid, BbsVerdict.Accepted);

        var result = Feed(store, b, "G8BPQ");

        Assert.Equal(FrameOutcome.CompletedOtherCopy, result.Outcome);
        Assert.Empty(store.Pending());
        Assert.True(store.IsOtherCopy(b.ObjectId));
        Assert.Equal(1, store.OtherCopyCounts(Day)["G8BPQ"]);
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(b.Frame(0).ToBytes(), "G8BPQ").Outcome);
    }

    [Fact]
    public void TwoCopiesBothInTheOutbox_OneAnswer_OnlyTheOldestIsTheBulletin()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir.Path, time);
        var (a, b, bid) = Pair(13);
        Feed(store, a);
        time.Advance(TimeSpan.FromMinutes(1));
        Feed(store, b, "G8BPQ");
        Assert.Equal(2, store.Pending().Count);

        store.Acknowledge(bid, BbsVerdict.AlreadyHad);

        Assert.Empty(store.Pending());
        Assert.Equal(1, store.Mail.Archived);
        Assert.Equal(a.ObjectId, Assert.Single(store.Mail.NewestFirst).ObjectId);
        Assert.True(store.IsOtherCopy(b.ObjectId));
        Assert.False(store.IsOtherCopy(a.ObjectId));
    }

    [Fact]
    public void NothingDelivered_OrRefused_MeansNoOtherCopy()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var (a, b, bid) = Pair(14);
        ListBulletins(store, null, (a, bid), (b, bid));
        Feed(store, Frames(b, 2), "G8BPQ");
        Assert.False(store.IsOtherCopy(b.ObjectId));
        Feed(store, a);
        store.Acknowledge(bid, BbsVerdict.Refused);
        Assert.False(store.IsOtherCopy(b.ObjectId));
        Assert.Equal(1, store.PartialObjects);
        Assert.Empty(store.OtherCopies);
    }

    [Fact]
    public void ARestart_KeepsTheOtherCopyDroppedAndCounted_AndForgetsItWithTheDoneMarker()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var (a, b, bid) = Pair(15);
        {
            var store = Open(dir.Path, time);
            ListBulletins(store, null, (a, bid), (b, bid));
            Feed(store, Frames(b, 2), "G8BPQ");
            Feed(store, a);
            store.Acknowledge(bid, BbsVerdict.Accepted);
            Assert.True(store.IsOtherCopy(b.ObjectId));
        }

        var reopened = Open(dir.Path, time);
        Assert.True(reopened.IsOtherCopy(b.ObjectId));
        Assert.Equal(1, reopened.OtherCopyCounts(Day)["G8BPQ"]);
        Assert.Equal(0, reopened.PartialObjects);
        Assert.Equal(FrameOutcome.AlreadyComplete, reopened.Accept(Frames(b, 3)[2].ToBytes(), "G8BPQ").Outcome);

        time.Advance(new ReceiverStoreOptions().DoneRetention + TimeSpan.FromDays(1));
        reopened.Expire();
        Assert.False(reopened.IsOtherCopy(b.ObjectId));
        Assert.Empty(reopened.OtherCopies);
    }

    [Fact]
    public void AResentArchivedObject_IsNeverAnOtherCopy()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir.Path, time);
        var (a, b, bid) = Pair(16);
        Feed(store, a);
        time.Advance(TimeSpan.FromMinutes(1));
        Feed(store, b);
        store.Acknowledge(bid, BbsVerdict.Refused);
        Assert.Equal(2, store.Mail.Archived);

        Assert.Equal(ResendOutcome.Resent, store.Resend(a.ObjectId).Outcome);
        store.Acknowledge(bid, BbsVerdict.Accepted);
        Assert.Equal(ResendOutcome.Resent, store.Resend(b.ObjectId).Outcome);
        store.Acknowledge(bid, BbsVerdict.Accepted);

        Assert.Empty(store.OtherCopies);
        Assert.Equal(2, store.Mail.Archived);
        Assert.All(store.Mail.NewestFirst, e => Assert.Equal(BbsVerdict.Accepted, e.Verdict));
    }

    [Fact]
    public void ACanonicalBulletinWaitingForItsEnvelope_IsDroppedWhenItsBidIsDeliveredUnderAnotherObject()
    {
        using var dir = new TempDirectory();
        var signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = Clock(), TrustedKeys = [TrustedKey.Parse("ZZ1AAA", signer.PublicKeyBase64)] });
        var a = TestBulletins.Make(17, 3000);
        var other = Bulletin.FromMessageText(a.Type, a.From, a.To, a.At, a.Bid, "a different full title", a.Date, "R:261030/0800Z 1@G8BPQ.#1.GBR.EURO\r\n" + a.MessageText);
        var canonB = TestBulletins.Canonical(other);
        var ta = TransferObject.ForBulletin(a, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var tb = TransferObject.ForBulletin(canonB, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        // Listed with an envelope link that never arrives, so the canonical object waits.
        var entry = new DirectoryEntry(tb.ObjectId, tb.DictionaryId, 400, a.Bid, canonB.Title, Hash: ObjectId.LongHash(tb.DictionaryId, tb.Bytes), Envelope: new EnvelopeLink(0x1234UL, new string('a', ObjectId.ListedHashOctets * 2)));
        var directory = TransferObject.ForDirectory(new BroadcastDirectory(Day, [entry], signer: signer), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Feed(store, directory);
        var waiting = Feed(store, tb, "G8BPQ");
        Assert.Equal(FrameOutcome.AwaitingEnvelope, waiting.Outcome);
        Assert.Single(store.Awaiting());

        Feed(store, ta);
        store.Acknowledge(a.Bid, BbsVerdict.Accepted);

        Assert.Empty(store.Awaiting());
        Assert.True(store.IsOtherCopy(tb.ObjectId));
    }
}
