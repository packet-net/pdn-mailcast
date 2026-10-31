using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Relay;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Tests;

/// <summary>Issue #218, part 2: the head end relays what the receivers on its LAN hear from the advertised stations.</summary>
public class RelayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Slot = new(2026, 10, 31, 13, 0, 0, TimeSpan.Zero);
    private static readonly SlotTimetable Timetable = new(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);
    private static readonly StationAdvert Other = new("GB7RDG", Timetable, 7053800, Sideband.Usb);

    private static ScheduleOptions Options(bool signed = true) => ScheduleOptions.HourlyBudget with
    {
        Station = "M0LTE",
        Adverts = [Other],
        Signer = signed ? DirectorySigner.FromPem(DirectorySigner.NewKeyPem()) : null,
        Timetable = null,
    };

    // A slot of this many frames, any airtime of the directory included.
    private static SlotBudget Frames(int n) => new(TimeSpan.FromSeconds(n), lengths => TimeSpan.FromSeconds(lengths.Count));

    private static TransferObject Remote(int seed, int size = 2500) =>
        TransferObject.ForBulletin(Bulletins.Make(seed, size), ScheduleOptions.HourlyBudget.DictionaryId, Compression.Default, MailcastFrame.FixedSymbolSize, MailcastFrame.StandardAlignment);

    private sealed class Fixture
    {
        public FakeTimeProvider Time { get; } = new(Now);
        public ObjectCatalogue Catalogue { get; }
        public MemoryJournal Journal { get; } = new();
        public Fixture() => Catalogue = new ObjectCatalogue(Time);

        /// <summary>Puts pieces in the catalogue as a receiver would give them, with the listing a trusted directory would give.</summary>
        public void Hear(TransferObject o, IEnumerable<uint> esis, bool onAir = true, string heardFrom = "GB7RDG", string? listedBy = "GB7RDG", string? hash = "same", bool complete = false)
        {
            long cursor = 1;
            foreach (uint esi in esis)
            {
                Catalogue.AddPiece(o.ObjectId, new RemotePiece(esi, o.Frame(esi).ToBytes(), Now, onAir, onAir ? heardFrom : null, cursor++));
            }
            var listings = listedBy is null ? [] : new List<ObjectListing> { new(listedBy, hash == "same" ? ObjectId.LongHash(o.DictionaryId, o.Bytes) : hash) };
            Catalogue.Note(new RemoteObject(o.ObjectId, (byte)o.Kind, o.DictionaryId, o.Oti, o.SourceSymbols, 0, complete, listings, cursor));
            if (complete)
            {
                Catalogue.SetData(o.ObjectId, o.DictionaryId, o.Bytes.ToArray());
            }
        }

        public RelaySelector Selector(ScheduleOptions options) => new(Catalogue, Compression.Default, options.Station, options.Adverts, Time, Journal);
    }

    private static SlotBroadcast Plan(HeadEndStore store, RelaySelector selector, ScheduleOptions options, int frames = 60, DateTimeOffset? slot = null)
    {
        var at = slot ?? Slot;
        var relayed = selector.Select(store.GetRelayProgress, store.CarriesObject);
        return BroadcastScheduler.Plan(store.InRotation(at), at, 1, Compression.Default, options, store.DirectoryNextEsi, Frames(frames), "ms110d-wn4", null, null, relayed);
    }

    [Fact]
    public void AnObjectHeardOnAir_IsRelayedUnchanged_AndNeverFromThoseHandedIn()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(1);
        int k = obj.SourceSymbols;
        Assert.True(k > 4);
        f.Hear(obj, [0u, 1u, 2u]);
        f.Hear(obj, [100u, 101u], onAir: false); // handed in through the API: never relayed
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        var plan = Plan(store, f.Selector(options), options);

        var relay = Assert.Single(plan.Relayed);
        Assert.Equal(obj.ObjectId, relay.ObjectId);
        Assert.False(relay.Fresh);
        Assert.Equal([0u, 1u, 2u], relay.Frames.Select(x => x.EncodingSymbolId).Order());
        Assert.All(relay.Frames, x => Assert.Equal(obj.Frame(x.EncodingSymbolId).ToBytes(), x.ToBytes())); // unchanged
        Assert.Equal(relay.Frames.Count, plan.RelayedFrames);
        // Incomplete: sent but not listed.
        Assert.DoesNotContain(plan.Directory.Entries, e => e.ObjectId == obj.ObjectId);
    }

    [Fact]
    public void AnObjectOnlyHandedIn_HasNothingToRelay()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(2);
        f.Hear(obj, [0u, 1u, 2u, 3u], onAir: false);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        var plan = Plan(store, f.Selector(options), options);

        Assert.Empty(plan.Relayed);
        Assert.Equal(0, plan.RelayedFrames);
    }

    [Fact]
    public void ACompleteObject_GetsFreshPiecesInThisStationsOrder_IsListedWithAComputedHash_AndUnchangedOnesAreNotNeeded()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(3);
        f.Hear(obj, [0u, 1u, 2u], complete: true);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        var plan = Plan(store, f.Selector(options), options);

        var relay = Assert.Single(plan.Relayed);
        Assert.True(relay.Fresh);
        Assert.True(relay.Listed);
        var shuffle = EsiShuffle.For("M0LTE", obj.ObjectId);
        Assert.Equal(Enumerable.Range(0, relay.Frames.Count).Select(i => shuffle.Map((uint)i)), relay.Frames.Select(x => x.EncodingSymbolId).OrderBy(e => shuffle.Unmap(e)));
        Assert.All(relay.Frames, x => Assert.Equal(obj.Frame(x.EncodingSymbolId).ToBytes(), x.ToBytes()));
        var entry = Assert.Single(plan.Directory.Entries, e => e.ObjectId == obj.ObjectId);
        Assert.Equal(ObjectId.LongHash(obj.DictionaryId, obj.Bytes), entry.Hash);
        Assert.Equal((byte)ObjectKind.Bulletin, entry.Type);
        Assert.Contains("Bulletin 3", entry.Title, StringComparison.Ordinal);
        Assert.StartsWith("1003_", entry.Bid, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadPieceHeardOnAir_ForAnIncompleteObject_IsNotRelayedIfItDoesNotParse_AndAWrongHashLeavesTheObjectOut()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(4);
        var good = Remote(5);
        f.Hear(obj, [0u, 1u], complete: true, hash: "0123456789abcdef"); // a trusted directory lists another hash for these octets
        f.Hear(good, [0u, 1u]);
        var corrupt = good.Frame(7).ToBytes();
        corrupt[40] ^= 0xFF; // fails its CRC
        Assert.False(f.Catalogue.AddPiece(good.ObjectId, new RemotePiece(7, corrupt, Now, true, "GB7RDG", 99)));
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var selector = f.Selector(options);

        var plan = Plan(store, selector, options);

        Assert.DoesNotContain(plan.Relayed, r => r.ObjectId == obj.ObjectId);
        Assert.DoesNotContain(plan.Directory.Entries, e => e.ObjectId == obj.ObjectId);
        var sent = Assert.Single(plan.Relayed);
        Assert.Equal(good.ObjectId, sent.ObjectId);
        Assert.Equal([0u, 1u], sent.Frames.Select(x => x.EncodingSymbolId).Order());
        Assert.Single(f.Journal.Lines, l => l.StartsWith("relay: ", StringComparison.Ordinal) && l.Contains("do not match the hash GB7RDG lists", StringComparison.Ordinal));
        Assert.Equal(1, selector.Summary.Dropped);
    }

    [Fact]
    public void OnlyObjectsListedByAnAdvertisedStationOrThisOne_AndNotCarriedHere_AreRelayed()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var advertised = Remote(6);
        var own = Remote(7);
        var stranger = Remote(8);
        var unlisted = Remote(9);
        f.Hear(advertised, [0u, 1u]);
        f.Hear(own, [0u, 1u], listedBy: "M0LTE");
        f.Hear(stranger, [0u, 1u], listedBy: "G9XYZ");
        f.Hear(unlisted, [0u, 1u], listedBy: null);
        // An object this head end carries itself, also heard back on air.
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var carried = store.Offer(Bulletins.Make(11, 2500), new DateOnly(2026, 10, 31))!;
        f.Hear(carried.Transfer, [0u, 1u, 2u], listedBy: "M0LTE");

        var plan = Plan(store, f.Selector(options), options);

        Assert.Equal(new[] { advertised.ObjectId, own.ObjectId }.Order(), plan.Relayed.Select(r => r.ObjectId).Order());
    }

    [Fact]
    public void RankedLeastCoveredFirst_WithOwnMailFirst_AndCoverageCountingEveryStationHeard()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var mine = store.Offer(Bulletins.Make(20, 9000), new DateOnly(2026, 10, 31))!;
        var heavy = Remote(21, 9000); // plenty heard from two stations already
        var light = Remote(22, 9000); // hardly heard
        int k = heavy.SourceSymbols;
        f.Hear(heavy, Enumerable.Range(0, k * 3).Select(i => (uint)(2000 + i)), heardFrom: "GB7RDG");
        f.Hear(light, [5u], heardFrom: "GB7RDG");
        // Make the heavy one complete so that it can give as much as is asked.
        f.Hear(heavy, [], complete: true);
        f.Hear(light, [], complete: true);

        var plan = Plan(store, f.Selector(options), options, frames: 24);

        int Sent(ulong id) => plan.Frames.Count(x => x.ObjectId == id);
        Assert.True(Sent(mine.Transfer.ObjectId) > 0);
        Assert.True(Sent(light.ObjectId) > Sent(heavy.ObjectId), $"light {Sent(light.ObjectId)} heavy {Sent(heavy.ObjectId)}");
        Assert.True(Sent(mine.Transfer.ObjectId) >= Sent(light.ObjectId));
        // With room for more, the heavy one gets its turn too, but never past the per-slot cap.
        var roomy = Plan(store, f.Selector(options), options, frames: 60);
        Assert.Contains(roomy.Frames, x => x.ObjectId == heavy.ObjectId);
        Assert.True(roomy.Frames.Count(x => x.ObjectId == heavy.ObjectId) <= BroadcastScheduler.CapSymbols(k, options.Budget!));
        Assert.True(Sent(heavy.ObjectId) <= BroadcastScheduler.CapSymbols(k, options.Budget!));
    }

    [Fact]
    public void AnObjectHeardTenTimesOver_IsRetired_AndCoverageIncludesWhatWeSent()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var done = Remote(30);
        int k = done.SourceSymbols;
        f.Hear(done, Enumerable.Range(0, (k * 10) + 1).Select(i => (uint)(3000 + i)), complete: true);

        var plan = Plan(store, f.Selector(options), options);

        Assert.Empty(plan.Relayed);
        Assert.DoesNotContain(plan.Directory.Entries, e => e.ObjectId == done.ObjectId);
    }

    [Fact]
    public void WhatWasSent_SurvivesARestart_SoNoEsiOfOursIsRepeated_AndFirstSlotIsKept()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(40);
        f.Hear(obj, [0u, 1u, 2u, 3u], complete: true);
        var sent = new HashSet<uint>();
        for (int round = 0; round < 4; round++)
        {
            var store = new HeadEndStore(dir.Path, Compression.Default, options); // a restart every round
            var plan = Plan(store, f.Selector(options), options, slot: Slot.AddHours(round));
            var relay = Assert.Single(plan.Relayed);
            foreach (var frame in relay.Frames)
            {
                Assert.True(sent.Add(frame.EncodingSymbolId), $"ESI {frame.EncodingSymbolId} sent twice");
            }
            store.Commit(plan);
        }
        var after = new HeadEndStore(dir.Path, Compression.Default, options).GetRelayProgress(obj.ObjectId);
        Assert.Equal(sent.Order(), after.Sent.Order());
        Assert.Equal(Slot, after.FirstSlot);
    }

    [Fact]
    public void UnchangedPieces_AreNeverRepeatedByUs_AndAreFollowedByFreshOnesOnceComplete()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(41);
        f.Hear(obj, [0u, 1u]);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var first = Plan(store, f.Selector(options), options);
        store.Commit(first);
        Assert.Equal([0u, 1u], first.Relayed.Single().Frames.Select(x => x.EncodingSymbolId).Order());

        // Nothing new heard: nothing unchanged left to send.
        var second = Plan(store, f.Selector(options), options, slot: Slot.AddHours(1));
        Assert.Empty(second.Relayed);

        // Complete now: fresh pieces, and none of ours repeated.
        f.Hear(obj, [], complete: true);
        var third = Plan(store, f.Selector(options), options, slot: Slot.AddHours(2));
        var relay = Assert.Single(third.Relayed);
        Assert.True(relay.Fresh);
        Assert.DoesNotContain(relay.Frames, x => x.EncodingSymbolId is 0 or 1);
    }

    [Fact]
    public void WithNothingToRelay_ThePlanIsExactlyTheOwnBulletinsPlan()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        store.Offer(Bulletins.Make(50, 2500), new DateOnly(2026, 10, 31));
        store.Offer(Bulletins.Make(51, 4000), new DateOnly(2026, 10, 31));
        var without = BroadcastScheduler.Plan(store.InRotation(Slot), Slot, 1, Compression.Default, options, store.DirectoryNextEsi, Frames(40), "ms110d-wn4");
        var empty = BroadcastScheduler.Plan(store.InRotation(Slot), Slot, 1, Compression.Default, options, store.DirectoryNextEsi, Frames(40), "ms110d-wn4", null, null, []);
        Assert.Equal(without.Frames.Select(x => x.ToBytes()), empty.Frames.Select(x => x.ToBytes()));
        Assert.Equal(without.Directory.Serialize(), empty.Directory.Serialize());
    }

    [Fact]
    public void ARelayedObjectsFrames_JoinAnOtherwiseEmptySlot_AndTheSlotLineIsOneShortAsciiLine()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(60);
        f.Hear(obj, [0u, 1u, 2u]);
        var selector = f.Selector(options);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        var plan = Plan(store, selector, options);

        Assert.True(plan.BulletinFrames > 0);
        string line = RelaySelector.Line(plan.Relayed.Count, plan.RelayedFrames, selector.Summary);
        Assert.Equal("relay: 1 objects, 3 frames (1 heard on the LAN, 0 complete)", line);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void TheRotationStore_PlansWithTheRelay_AndRemembersWhatItSent()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(70);
        f.Hear(obj, [0u, 1u, 2u]);
        var store = new RotationStore(dir.Path, Compression.Default, options, new MemoryJournal());

        var plan = store.Plan(Slot, 1, Compression.Default, options, Frames(60), "ms110d-wn4", null, null, f.Selector(options));
        Assert.Single(plan.Relayed);
        store.Commit(plan, plan.Frames.Count);

        Assert.Equal(3, store.GetRelayProgress(obj.ObjectId).Sent.Count);
        Assert.False(store.CarriesObject(obj.ObjectId));
    }

    [Fact]
    public void TheStatusPage_ListsRelayedObjectsBriefly()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(80);
        f.Hear(obj, [0u, 1u]);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var selector = f.Selector(options);
        var status = new Mailcast.HeadEnd.Status.StatusStore(null, f.Time) { Relay = () => selector.StatusView(store.GetRelayProgress, store.CarriesObject) };

        using var doc = System.Text.Json.JsonDocument.Parse(status.Render());

        var relay = doc.RootElement.GetProperty("relay");
        Assert.Equal(1, relay.GetProperty("candidates").GetInt32());
        var listed = Assert.Single(relay.GetProperty("objects").EnumerateArray());
        Assert.Equal(ObjectId.Format(obj.ObjectId), listed.GetProperty("id").GetString());
        Assert.Equal(2, listed.GetProperty("heardOnAir").GetInt32());
        Assert.False(listed.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void ARelayedBulletinsEnvelope_TravelsWithItAndIsLinkedFromItsListing()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var obj = Remote(90);
        var envelope = TransferObject.ForEnvelope(new BulletinEnvelope("R:1\r\n"u8, ReadOnlySpan<byte>.Empty, "A longer title", LineEnding.CrLf), options.DictionaryId, Compression.Default);
        var relayed = new RelayedObject
        {
            ObjectId = obj.ObjectId,
            DictionaryId = obj.DictionaryId,
            Type = (byte)ObjectKind.Bulletin,
            Oti = obj.Oti,
            Transfer = obj,
            Listing = new RelayListing("1090_GB7XYZ", "Bulletin 90", 2500),
            Envelope = envelope,
        };
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        var plan = BroadcastScheduler.Plan(store.InRotation(Slot), Slot, 1, Compression.Default, options, store.DirectoryNextEsi, Frames(60), "ms110d-wn4", null, null, [relayed]);

        var entry = Assert.Single(plan.Directory.Entries);
        Assert.Equal(envelope.ObjectId, entry.Envelope!.ObjectId);
        Assert.Equal(ObjectId.LongHash(envelope.DictionaryId, envelope.Bytes), entry.Envelope.Hash);
        Assert.Equal(envelope.SourceSymbols, plan.Frames.Count(x => x.ObjectId == envelope.ObjectId));
        store.Commit(plan);
        Assert.Equal((uint)envelope.SourceSymbols, store.GetRelayProgress(obj.ObjectId).EnvelopeNextEsi);
    }
}
