using System.Text.RegularExpressions;
using Mailcast.RaptorQ;
using Microsoft.Extensions.Time.Testing;

namespace Packet.Mailcast.Tests;

/// <summary>A receiver rebuilds and delivers canonical bulletins: waiting for an envelope, the busiest station's envelope, the R: line (issue #145).</summary>
public class CanonicalDeliveryTests
{
    private static readonly DateOnly Day = new(2026, 10, 30);
    private static readonly DirectorySigner SignerA = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly DirectorySigner SignerB = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly TrustedKey KeyA = TrustedKey.Parse("ZZ1AAA", SignerA.PublicKeyBase64);
    private static readonly DirectorySigner SignerL = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly TrustedKey KeyL = TrustedKey.Parse("my home key", SignerL.PublicKeyBase64);
    private static readonly TrustedKey KeyB = TrustedKey.Parse("AA1BBB", SignerB.PublicKeyBase64);

    private sealed record Made(TransferObject Bulletin, Bulletin Canonical, byte[] Original, string FullTitle);

    private static Made MakeCanonical(int seed, bool bigBody = false)
    {
        string title = "A fairly long title for canonical bulletin number " + seed + " that goes past the shared fifty nine octets";
        byte[] original = Bulletin.TextEncoding.GetBytes($"R:261019/0850Z {seed}@GB7AAA.#1.GBR.EURO LinBPQ6.0.25\r\n\r\nBody {seed}.\r\nSecond line.\r\n" + (bigBody ? BigText() : ""));
        var (canonical, _) = CanonicalForm.ToCanonicalBulletin('B', "G4ABC", "ALL", "WW", $"{seed}_TEST", title, original);
        return new Made(TransferObject.ForBulletin(canonical, ZstdDictionary.Gb7rdg1Id, Compression.Default), canonical, original, title);
    }

    private static string BigText()
    {
        var rng = new Random(42);
        return string.Concat(Enumerable.Range(0, 3000).Select(_ => new string(Enumerable.Range(0, 30).Select(_ => (char)('a' + rng.Next(26))).ToArray()) + "\r\n"));
    }

    /// <summary>One station's envelope for the same message, its routing differing by <paramref name="route"/>.</summary>
    private static TransferObject EnvelopeOf(Made m, string route)
    {
        byte[] copy = Bulletin.TextEncoding.GetBytes($"R:261019/0900Z {route} LinBPQ6.0.25\r\n" + Bulletin.TextEncoding.GetString(m.Original));
        var (_, envelope) = CanonicalForm.ToCanonicalBulletin('B', "G4ABC", "ALL", "WW", m.Canonical.Bid, m.FullTitle, copy);
        return TransferObject.ForEnvelope(envelope, ZstdDictionary.Gb7rdg1Id, Compression.Default);
    }

    private static DirectoryEntry Entry(Made m, TransferObject? envelope) =>
        new(m.Bulletin.ObjectId, m.Bulletin.DictionaryId, 400, m.Canonical.Bid, m.Canonical.Title,
            Hash: ObjectId.LongHash(m.Bulletin.DictionaryId, m.Bulletin.Bytes),
            Envelope: envelope is null ? null : new EnvelopeLink(envelope.ObjectId, ObjectId.LongHash(envelope.DictionaryId, envelope.Bytes)));

    private static List<MailcastFrame> DirectoryFrames(DirectorySigner? signer, params DirectoryEntry[] entries) => DirectoryFramesOf(signer, null, entries);

    private static List<MailcastFrame> DirectoryFramesOf(DirectorySigner? signer, string? station, params DirectoryEntry[] entries)
    {
        var obj = TransferObject.ForDirectory(new BroadcastDirectory(Day, entries, signer: signer, station: station), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        return [.. Enumerable.Range(0, obj.SourceSymbols).Select(i => obj.Frame((uint)i))];
    }

    private static AcceptResult Feed(ReceiverStore store, IEnumerable<MailcastFrame> frames, string? from = null)
    {
        AcceptResult last = null!;
        foreach (var f in frames)
        {
            last = store.Accept(f.ToBytes(), from);
        }
        return last;
    }

    private static AcceptResult Feed(ReceiverStore store, TransferObject o, string? from = null) => Feed(store, Enumerable.Range(0, o.SourceSymbols).Select(i => o.Frame((uint)i)), from);

    private static ReceiverStore Open(string path, FakeTimeProvider time, List<string>? log = null, string? call = "M0LTE-1") =>
        new(path, Compression.Default, TestStores.Fast with { Time = time, TrustedKeys = [KeyA, KeyB, KeyL], Log = log is null ? null : log.Add, DeliveryCall = () => call });

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));

    private static string RLine(string via, string call = "M0LTE-1", string time = "0900") => $@"^R:261030/{time}Z @:{Regex.Escape(call)} \[pdn-mailcast via {via}\]\r\n";

    [Fact]
    public void ACanonicalBulletinWithoutAnEnvelope_WaitsAndIsNotDelivered_ThenDeliversWhenTheEnvelopeArrives()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = Open(dir.Path, Clock(), log);
        var m = MakeCanonical(1);
        var env = EnvelopeOf(m, "1@GB7ZZZ.#1.GBR.EURO");
        Feed(store, DirectoryFrames(SignerA, Entry(m, env)));

        var result = Feed(store, m.Bulletin, "ZZ1AAA");
        Assert.Equal(FrameOutcome.AwaitingEnvelope, result.Outcome);
        Assert.Empty(store.Pending());
        var waiting = Assert.Single(store.Awaiting());
        Assert.Equal(m.Bulletin.ObjectId, waiting.ObjectId);
        Assert.Contains(store.HeldBulletins(), h => h.ObjectId == m.Bulletin.ObjectId);

        var done = Feed(store, env, "ZZ1AAA");
        Assert.Equal(FrameOutcome.CompletedEnvelope, done.Outcome);
        Assert.Single(done.Delivered!);
        Assert.Empty(store.Awaiting());
        var delivered = Assert.Single(store.Pending());

        string original = Bulletin.TextEncoding.GetString(CanonicalForm.Rebuild(Bulletin.TextEncoding.GetBytes(m.Canonical.MessageText.Replace("\r\n", "\n")), BulletinEnvelopeOf(m, "1@GB7ZZZ.#1.GBR.EURO")).Original);
        Assert.Matches(RLine("ZZ1AAA"), delivered.MessageText);
        Assert.Equal(original, delivered.MessageText[(delivered.MessageText.IndexOf("\r\n", StringComparison.Ordinal) + 2)..]);
        Assert.Equal(m.FullTitle, delivered.Title);
        Assert.False(delivered.Canonical);
        Assert.Contains(log, l => l.Contains("waiting for its station's details", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("delivered with the envelope of ZZ1AAA", StringComparison.Ordinal));
        Assert.All(log, l => Assert.True(l.All(c => c < 128), l));
    }

    private static BulletinEnvelope BulletinEnvelopeOf(Made m, string route)
    {
        byte[] copy = Bulletin.TextEncoding.GetBytes($"R:261019/0900Z {route} LinBPQ6.0.25\r\n" + Bulletin.TextEncoding.GetString(m.Original));
        return CanonicalForm.ToCanonicalBulletin('B', "G4ABC", "ALL", "WW", m.Canonical.Bid, m.FullTitle, copy).Envelope;
    }

    [Fact]
    public void AnEnvelopeAlreadyHeld_MeansTheBulletinIsDeliveredAtOnce()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var m = MakeCanonical(2);
        var env = EnvelopeOf(m, "2@GB7ZZZ.#1.GBR.EURO");
        Feed(store, DirectoryFrames(SignerA, Entry(m, env)));
        Feed(store, env);
        var result = Feed(store, m.Bulletin);
        Assert.Equal(FrameOutcome.CompletedBulletin, result.Outcome);
        Assert.Null(result.Delivered);
        Assert.Matches(RLine("unused").Replace("unused", @"\S+"), result.Bulletin!.MessageText);
        Assert.Empty(store.Awaiting());
        Assert.Single(store.Pending());
    }

    [Fact]
    public void AWaitingBulletin_IsDeliveredBare_OnceNoDirectoryListsIt_AndNotBefore()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir.Path, time);
        var m = MakeCanonical(3);
        var other = MakeCanonical(4);
        Feed(store, DirectoryFrames(SignerA, Entry(m, EnvelopeOf(m, "3@GB7ZZZ.#1.GBR.EURO"))));
        Feed(store, m.Bulletin, "ZZ1AAA");
        Assert.Single(store.Awaiting());

        // A directory that still lists it: keeps waiting, however often it is heard again.
        Feed(store, DirectoryFrames(SignerA, Entry(m, EnvelopeOf(m, "3@GB7ZZZ.#1.GBR.EURO")), Entry(other, null)));
        Assert.Single(store.Awaiting());
        Assert.Empty(store.Pending());

        time.Advance(TimeSpan.FromMinutes(5));
        Feed(store, DirectoryFrames(SignerA, Entry(other, null)));
        Assert.Empty(store.Awaiting());
        var delivered = Assert.Single(store.Pending());
        Assert.Matches(RLine("ZZ1AAA", time: "0905"), delivered.MessageText);
        Assert.Equal(m.Canonical.MessageText, delivered.MessageText[(delivered.MessageText.IndexOf("\r\n", StringComparison.Ordinal) + 2)..]);
        Assert.Equal(m.Canonical.Title, delivered.Title);
    }

    [Fact]
    public void TheWaitSurvivesARestart_AndSoDoesTheCountOfFramesHeard()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var m = MakeCanonical(5);
        var envA = EnvelopeOf(m, "5@GB7ZZZ.#1.GBR.EURO");
        var envB = EnvelopeOf(m, "5@GB7BBB.#1.GBR.EURO");
        {
            var store = Open(dir.Path, time);
            Feed(store, DirectoryFrames(SignerA, Entry(m, envA)));
            Feed(store, DirectoryFrames(SignerB, Entry(m, envB)));
            Feed(store, m.Bulletin, "ZZ1AAA");
            Assert.Single(store.Awaiting());
        }
        var again = Open(dir.Path, time);
        var waiting = Assert.Single(again.Awaiting());
        Assert.Equal(m.Canonical, waiting.Bulletin);
        Assert.Empty(again.Pending());
        Assert.Equal(FrameOutcome.AlreadyComplete, again.Accept(m.Bulletin.Frame(0).ToBytes()).Outcome);

        Feed(again, envA);
        Feed(again, envB);
        var delivered = Assert.Single(again.Pending());
        Assert.Matches(RLine("ZZ1AAA"), delivered.MessageText); // the one heard, not the first by callsign
    }

    [Fact]
    public void WithEnvelopesFromTwoStations_TheStationHeardMostIsUsed_AndATieGoesToTheFirstCallsign()
    {
        foreach (var (fromA, fromB, expected) in new[] { (5, 1, "ZZ1AAA"), (1, 5, "AA1BBB"), (0, 0, "AA1BBB"), (3, 3, "AA1BBB") })
        {
            using var dir = new TempDirectory();
            var store = Open(dir.Path, Clock());
            var m = MakeCanonical(6, bigBody: true);
            Assert.True(m.Bulletin.SourceSymbols > 12, "the test needs an object of more than a dozen pieces");
            var envA = EnvelopeOf(m, "6@GB7ZZZ.#1.GBR.EURO");
            var envB = EnvelopeOf(m, "6@GB7BBB.#1.GBR.EURO");
            Feed(store, DirectoryFrames(SignerA, Entry(m, envA)));
            Feed(store, DirectoryFrames(SignerB, Entry(m, envB)));
            Feed(store, envA);
            Feed(store, envB);
            // The envelopes are here already, so the bulletin goes the moment it completes; the pieces heard
            // before that are from the two stations in these numbers, the rest from nobody in particular.
            int esi = 0;
            AcceptResult last = null!;
            for (int i = 0; i < fromA; i++)
            {
                last = store.Accept(m.Bulletin.Frame((uint)esi++).ToBytes(), "ZZ1AAA");
            }
            for (int i = 0; i < fromB; i++)
            {
                last = store.Accept(m.Bulletin.Frame((uint)esi++).ToBytes(), "AA1BBB");
            }
            Assert.Empty(store.Pending());
            while (esi < m.Bulletin.SourceSymbols)
            {
                last = store.Accept(m.Bulletin.Frame((uint)esi++).ToBytes());
            }
            var delivered = Assert.Single(store.Pending());
            Assert.Matches(RLine(expected), delivered.MessageText);
            string route = expected == "ZZ1AAA" ? "GB7ZZZ" : "GB7BBB";
            Assert.Contains(route, delivered.MessageText[delivered.MessageText.IndexOf("\r\n", StringComparison.Ordinal)..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DeliverNow_DeliversAWaitingBulletinBare_AndIsRefusedForOneNotWaiting()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var m = MakeCanonical(7);
        Feed(store, DirectoryFrames(SignerA, Entry(m, EnvelopeOf(m, "7@GB7ZZZ.#1.GBR.EURO"))));
        Feed(store, m.Bulletin, "ZZ1AAA");
        Assert.Null(store.DeliverNow(m.Bulletin.ObjectId ^ 1));
        Assert.Empty(store.Pending());

        var delivered = store.DeliverNow(m.Bulletin.ObjectId);
        Assert.NotNull(delivered);
        Assert.Empty(store.Awaiting());
        Assert.Single(store.Pending());
        Assert.Matches(RLine("ZZ1AAA"), delivered.MessageText);
        Assert.Null(store.DeliverNow(m.Bulletin.ObjectId));

        // And nothing is left over to deliver again after a restart.
        var again = Open(dir.Path, Clock());
        Assert.Empty(again.Awaiting());
        Assert.Single(again.Pending());
    }

    [Fact]
    public void WithoutABbsLoginTheRLineLeavesTheCallOut_AndWithoutAnyStationTheViaToo()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock(), call: null);
        var m = MakeCanonical(8);
        Feed(store, m.Bulletin);
        var delivered = store.DeliverNow(m.Bulletin.ObjectId)!;
        Assert.StartsWith("R:261030/0900Z [pdn-mailcast]\r\n", delivered.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public void ABulletinWithoutTheCanonicalKey_IsDeliveredExactlyAsBefore()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var plain = TestBulletins.Make(9, 400);
        var obj = TransferObject.ForBulletin(plain, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(FrameOutcome.CompletedBulletin, Feed(store, obj).Outcome);
        Assert.Empty(store.Awaiting());
        Assert.Equal(plain, Assert.Single(store.Pending()));
    }

    [Fact]
    public void TheStationIsTheCallsignTheDirectoryNames_NotTheKeysLabel()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var m = MakeCanonical(10, bigBody: true);
        var envL = EnvelopeOf(m, "10@GB7LLL.#1.GBR.EURO");
        var envB = EnvelopeOf(m, "10@GB7BBB.#1.GBR.EURO");
        Feed(store, DirectoryFramesOf(SignerL, "G9XYZ", Entry(m, envL)));
        Feed(store, DirectoryFrames(SignerB, Entry(m, envB)));
        Feed(store, envL);
        Feed(store, envB);
        Assert.Equal(["AA1BBB", "G9XYZ"], store.EnvelopesFor(m.Bulletin.ObjectId).Select(e => e.Station));

        int esi = 0;
        for (int i = 0; i < 5; i++)
        {
            store.Accept(m.Bulletin.Frame((uint)esi++).ToBytes(), "G9XYZ");
        }
        store.Accept(m.Bulletin.Frame((uint)esi++).ToBytes(), "AA1BBB");
        while (esi < m.Bulletin.SourceSymbols)
        {
            store.Accept(m.Bulletin.Frame((uint)esi++).ToBytes());
        }
        var delivered = Assert.Single(store.Pending());
        Assert.Matches(RLine("G9XYZ"), delivered.MessageText);
        Assert.Contains("GB7LLL", delivered.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryOfAnotherStation_DoesNotReleaseABulletinBeforeItsOwnStationWasHeard_AndOnlyTheListingStationsCount()
    {
        using var dir = new TempDirectory();
        var time0 = Clock();
        var store = Open(dir.Path, time0);
        var m = MakeCanonical(11);
        var other = MakeCanonical(12);
        // Completed before any directory: nobody has listed it yet, so a directory that does not is no reason to give up.
        Feed(store, m.Bulletin, "ZZ1AAA");
        Assert.Single(store.Awaiting());
        time0.Advance(TimeSpan.FromMinutes(1));
        Feed(store, DirectoryFrames(SignerB, Entry(other, null)));
        Assert.Single(store.Awaiting());
        Assert.Empty(store.Pending());

        // Station A lists it (with an envelope on offer): it waits, and B still not listing it changes nothing.
        var env = EnvelopeOf(m, "11@GB7ZZZ.#1.GBR.EURO");
        Feed(store, DirectoryFrames(SignerA, Entry(m, env)));
        Assert.Single(store.Awaiting());
        time0.Advance(TimeSpan.FromMinutes(1));
        Feed(store, DirectoryFrames(SignerB, Entry(other, null), Entry(MakeCanonical(13), null)));
        Assert.Single(store.Awaiting());
        time0.Advance(TimeSpan.FromMinutes(1));

        // The only station that listed it stops: now it goes, bare.
        Feed(store, DirectoryFrames(SignerA, Entry(other, null)));
        Assert.Empty(store.Awaiting());
        Assert.Single(store.Pending());

        // And the wait is remembered across a restart: a station that listed it, then a restart, then it stops.
        using var dir2 = new TempDirectory();
        var time = Clock();
        var m2 = MakeCanonical(14);
        {
            var first = Open(dir2.Path, time);
            Feed(first, DirectoryFrames(SignerA, Entry(m2, EnvelopeOf(m2, "14@GB7ZZZ.#1.GBR.EURO"))));
            Feed(first, m2.Bulletin);
        }
        var again = Open(dir2.Path, time);
        time.Advance(TimeSpan.FromMinutes(1));
        Feed(again, DirectoryFrames(SignerB, Entry(other, null)));
        Assert.Single(again.Awaiting());
        time.Advance(TimeSpan.FromMinutes(1));
        Feed(again, DirectoryFrames(SignerA, Entry(other, null)));
        Assert.Empty(again.Awaiting());
    }

    [Fact]
    public void NothingToWaitFor_MeansDeliveryAtOnce_WhetherTheDirectoryCameBeforeOrAfter()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var m = MakeCanonical(15);
        // An unsigned directory lists it, and cannot link an envelope.
        Feed(store, DirectoryFrames(null, Entry(m, null)));
        var result = Feed(store, m.Bulletin);
        Assert.Equal(FrameOutcome.CompletedBulletin, result.Outcome);
        Assert.Empty(store.Awaiting());
        Assert.Single(store.Pending());

        // A signed directory without env= arrives after the bulletin completed.
        using var dir2 = new TempDirectory();
        var store2 = Open(dir2.Path, Clock());
        var m2 = MakeCanonical(16);
        Assert.Equal(FrameOutcome.AwaitingEnvelope, Feed(store2, m2.Bulletin).Outcome);
        var dirResult = Feed(store2, DirectoryFrames(SignerA, Entry(m2, null)));
        Assert.Equal(FrameOutcome.CompletedDirectory, dirResult.Outcome);
        Assert.Single(dirResult.Delivered!);
        Assert.Single(store2.Pending());
    }

    [Fact]
    public void AfterFortyEightHoursItGoesBare_FromExpire_AndAtOpen()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir.Path, time);
        var m = MakeCanonical(17);
        Feed(store, m.Bulletin, "ZZ1AAA");
        time.Advance(TimeSpan.FromHours(47));
        store.Expire();
        Assert.Single(store.Awaiting());
        time.Advance(TimeSpan.FromHours(2));
        store.Expire();
        Assert.Empty(store.Awaiting());
        Assert.Single(store.Pending());

        using var dir2 = new TempDirectory();
        var store2 = Open(dir2.Path, time);
        var m2 = MakeCanonical(18);
        Feed(store2, m2.Bulletin);
        time.Advance(TimeSpan.FromHours(49));
        var reopened = Open(dir2.Path, time);
        Assert.Empty(reopened.Awaiting());
        Assert.Single(reopened.Pending());
    }

    [Fact]
    public void TheDateKeptIsTheOldestRoutingTime_OrElseWhenItCompleted()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var m = MakeCanonical(19);
        var env = EnvelopeOf(m, "19@GB7ZZZ.#1.GBR.EURO");
        Feed(store, DirectoryFrames(SignerA, Entry(m, env)));
        Feed(store, m.Bulletin);
        Feed(store, env);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 8, 50, 0, TimeSpan.Zero), Assert.Single(store.Pending()).Date);

        var m2 = MakeCanonical(20);
        Feed(store, m2.Bulletin);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero), store.DeliverNow(m2.Bulletin.ObjectId)!.Date);
    }

    [Fact]
    public void OnAClockWithFractionsOfASecond_BareDeliveryAndTheBackstopAtOpenStillWork()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        time.Advance(TimeSpan.FromMilliseconds(123));
        var store = Open(dir.Path, time);
        var m = MakeCanonical(21);
        Feed(store, m.Bulletin);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero), store.DeliverNow(m.Bulletin.ObjectId)!.Date);

        var m2 = MakeCanonical(22);
        Feed(store, m2.Bulletin);
        time.Advance(TimeSpan.FromHours(49));
        var reopened = Open(dir.Path, time);
        Assert.Empty(reopened.Awaiting());
        Assert.Equal(2, reopened.Pending().Count);
    }

    [Fact]
    public void AFullTitleThatWillNotSerialise_KeepsTheRebuiltTextAndOnlyCleansTheTitle()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var m = MakeCanonical(21);
        byte[] copy = Bulletin.TextEncoding.GetBytes("R:261019/0900Z 21@GB7ZZZ.#1.GBR.EURO LinBPQ6.0.25\r\n" + Bulletin.TextEncoding.GetString(m.Original));
        var (_, envelope) = CanonicalForm.ToCanonicalBulletin('B', "G4ABC", "ALL", "WW", m.Canonical.Bid, "Line one\rline two", copy);
        var env = TransferObject.ForEnvelope(envelope, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Feed(store, DirectoryFrames(SignerA, Entry(m, env)));
        Feed(store, env);
        Feed(store, m.Bulletin);
        var delivered = Assert.Single(store.Pending());
        Assert.Equal("Line one line two", delivered.Title);
        Assert.Contains("21@GB7ZZZ", delivered.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAwaitingFileLeftBesideAnOutboxFile_IsDroppedAtOpen()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir.Path, time);
        var m = MakeCanonical(22);
        Feed(store, m.Bulletin);
        string file = Path.Combine(dir.Path, "awaiting", ObjectId.Format(m.Bulletin.ObjectId));
        byte[] kept = File.ReadAllBytes(file);
        store.DeliverNow(m.Bulletin.ObjectId);
        File.WriteAllBytes(file, kept); // a crash between writing the outbox file and removing this one
        var again = Open(dir.Path, time);
        Assert.Empty(again.Awaiting());
        Assert.Single(again.Pending());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void AnEnvelopeHeldUntilItsDirectoryComes_StillDeliversTheWaitingBulletin_AndSaysSoInTheResult()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var m = MakeCanonical(23);
        var env = EnvelopeOf(m, "23@GB7ZZZ.#1.GBR.EURO");
        Assert.Equal(FrameOutcome.AwaitingEnvelope, Feed(store, m.Bulletin).Outcome);
        Assert.Equal(FrameOutcome.Withheld, Feed(store, env).Outcome);
        Assert.Empty(store.Pending());

        var result = Feed(store, DirectoryFrames(SignerA, Entry(m, env)));
        Assert.Equal(FrameOutcome.CompletedDirectory, result.Outcome);
        Assert.Equal(m.Canonical.Bid, Assert.Single(result.Delivered!).Bid);
        Assert.Null(result.Released);
        Assert.Single(store.Pending());
    }
}
