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
    private static readonly SlotTimetable Timetable = new(TimeOnly.MinValue, 60, StationFixture.Daylight);
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
        public void Hear(TransferObject o, IEnumerable<uint> esis, bool onAir = true, string heardFrom = "GB7RDG", string? listedBy = "GB7RDG", string? hash = "same", bool complete = false, EnvelopeLink? envelope = null, DateTimeOffset? heardAt = null, AirHeard? air = null, bool noAirCount = false)
        {
            long cursor = 1;
            var list = esis.ToList();
            if (air is null && onAir && !noAirCount && list.Count > 0)
            {
                // A receiver that counts what it hears on air, as the current ones do.
                air = new AirHeard(list.Count, new Dictionary<string, int> { [heardFrom] = list.Count }, heardAt ?? Now, heardAt ?? Now);
            }
            foreach (uint esi in list)
            {
                Catalogue.AddPiece(o.ObjectId, new RemotePiece(esi, o.Frame(esi).ToBytes(), heardAt ?? Now, onAir, onAir ? heardFrom : null, cursor++));
            }
            var listings = listedBy is null ? [] : new List<ObjectListing> { new(listedBy, hash == "same" ? ObjectId.LongHash(o.DictionaryId, o.Bytes) : hash, envelope) };
            Catalogue.Note(new RemoteObject(o.ObjectId, (byte)o.Kind, o.DictionaryId, o.Oti, o.SourceSymbols, 0, complete, listings, cursor, air));
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
        Assert.Equal("relay: 1 objects, 3 frames (1 candidates, 0 complete, 0 listed)", line);
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
        selector.Select(store.GetRelayProgress, store.CarriesObject);
        var status = new Mailcast.HeadEnd.Status.StatusStore(null, f.Time) { Relay = selector.StatusView };

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
        Assert.Equal(envelope.SourceSymbols, store.GetRelayProgress(envelope.ObjectId).Sent.Count);
    }

    private static TransferObject EnvelopeObject(ScheduleOptions options) =>
        TransferObject.ForEnvelope(new BulletinEnvelope("R:1\r\n"u8, ReadOnlySpan<byte>.Empty, "A longer title", LineEnding.CrLf), options.DictionaryId, Compression.Default);

    [Fact]
    public void ABulletinsEnvelope_IsRelayedWithItWhenHeldCheckedAndMatchingTheListedLink_AndNeverOnItsOwn()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(100);
        var envelope = EnvelopeObject(options);
        var link = new EnvelopeLink(envelope.ObjectId, ObjectId.LongHash(envelope.DictionaryId, envelope.Bytes));
        f.Hear(obj, [0u, 1u], complete: true, envelope: link);
        f.Hear(envelope, [0u], complete: true);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        var plan = Plan(store, f.Selector(options), options);

        var relay = Assert.Single(plan.Relayed);
        Assert.Equal(obj.ObjectId, relay.ObjectId);
        var entry = Assert.Single(plan.Directory.Entries);
        Assert.Equal(link, entry.Envelope);
        Assert.Equal(envelope.SourceSymbols, plan.Frames.Count(x => x.ObjectId == envelope.ObjectId));
        Assert.Equal(envelope.SourceSymbols, relay.EnvelopeFrames!.Count);
    }

    [Fact]
    public void AnEnvelopeThatDoesNotMatchTheListedHash_IsNotRelayed_ButTheBulletinStillIs()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(101);
        var envelope = EnvelopeObject(options);
        f.Hear(obj, [0u, 1u], complete: true, envelope: new EnvelopeLink(envelope.ObjectId, "0123456789abcdef"));
        f.Hear(envelope, [0u], complete: true);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        var plan = Plan(store, f.Selector(options), options);

        Assert.Single(plan.Relayed);
        Assert.Null(Assert.Single(plan.Directory.Entries).Envelope);
        Assert.DoesNotContain(plan.Frames, x => x.ObjectId == envelope.ObjectId);
    }

    [Fact]
    public void RelayState_IsKeptPastTheRememberingDays_WhileAnythingStillOffersTheObject()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(102);
        f.Hear(obj, [0u, 1u, 2u], complete: true);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        store.Commit(Plan(store, f.Selector(options), options));
        int sent = store.GetRelayProgress(obj.ObjectId).Sent.Count;
        Assert.True(sent > 0);
        var later = DateOnly.FromDateTime(Slot.UtcDateTime).AddDays(options.RememberDays + 5);

        store.Expire(later, id => f.Catalogue.Get(id) is not null);
        Assert.Equal(sent, store.GetRelayProgress(obj.ObjectId).Sent.Count);
        Assert.Equal(sent, new HeadEndStore(dir.Path, Compression.Default, options).GetRelayProgress(obj.ObjectId).Sent.Count);

        store.Expire(later, _ => false);
        Assert.Empty(store.GetRelayProgress(obj.ObjectId).Sent);
    }

    [Fact]
    public void ACompleteObjectHeardManyTimesOver_IsCoveredByTheReceiversFrameCount_NotByTheKPiecesKept()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var busy = Remote(110, 9000);
        var quiet = Remote(111, 9000);
        int k = busy.SourceSymbols;
        f.Hear(busy, Enumerable.Range(0, k).Select(i => (uint)i), complete: true, air: new AirHeard(9 * k, new Dictionary<string, int> { ["GB7RDG"] = 9 * k }, Now, Now));
        f.Hear(quiet, Enumerable.Range(0, k).Select(i => (uint)i), complete: true, air: new AirHeard(k, new Dictionary<string, int> { ["GB7RDG"] = k }, Now, Now));
        var selector = f.Selector(options);

        var plan = Plan(store, selector, options, frames: 18);

        int Sent(ulong id) => plan.Frames.Count(x => x.ObjectId == id);
        Assert.True(Sent(quiet.ObjectId) > Sent(busy.ObjectId), $"quiet {Sent(quiet.ObjectId)} busy {Sent(busy.ObjectId)}");
        var chosen = selector.Select(store.GetRelayProgress, store.CarriesObject);
        Assert.Equal(9 * k, chosen.Single(r => r.ObjectId == busy.ObjectId).HeardOnAir);
        // Our own station's frames heard back are not counted again.
        f.Hear(quiet, [], air: new AirHeard(k + 5, new Dictionary<string, int> { ["GB7RDG"] = k, ["M0LTE"] = 5 }, Now, Now));
        Assert.Equal(k, selector.Select(store.GetRelayProgress, store.CarriesObject).Single(r => r.ObjectId == quiet.ObjectId).HeardOnAir);
    }

    [Fact]
    public void ACompleteObjectFromAReceiverWithoutTheFrameCount_StillListed_CountsAsCoveredToTheLeftoverMark()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(112, 9000);
        f.Hear(obj, Enumerable.Range(0, obj.SourceSymbols).Select(i => (uint)i), complete: true, noAirCount: true);
        var chosen = f.Selector(options).Select(new HeadEndStore(dir.Path, Compression.Default, options).GetRelayProgress, _ => false);
        Assert.True(Assert.Single(chosen).HeardOnAir >= BroadcastScheduler.LeftoverSymbols(obj.SourceSymbols, options.Budget!));
    }

    [Fact]
    public void AnOwnBulletinsCoverage_IncludesPiecesOfItHeardFromOtherStations()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var mine = store.Offer(Bulletins.Make(120, 9000), new DateOnly(2026, 10, 31))!;
        var other = store.Offer(Bulletins.Make(121, 9000), new DateOnly(2026, 10, 31))!;
        int k = mine.Transfer.SourceSymbols;
        f.Hear(mine.Transfer, [3000u], listedBy: "M0LTE", air: new AirHeard(3 * k, new Dictionary<string, int> { ["GB7RDG"] = 3 * k }, Now, Now));
        var selector = f.Selector(options);
        var plan = BroadcastScheduler.Plan(store.InRotation(Slot), Slot, 1, Compression.Default, options, store.DirectoryNextEsi, Frames(26), "ms110d-wn4", null, null, [], selector.HeardFromOthers);

        Assert.Equal(3 * k, selector.HeardFromOthers(mine.Transfer.ObjectId));
        Assert.True(plan.Objects.Single(o => o.Bid == other.Bid).Count > plan.Objects.Single(o => o.Bid == mine.Bid).Count);
    }

    private static TransferObject OwnObject(ScheduleOptions options, Bulletin b)
    {
        using var scratch = new TempDirectory();
        return new HeadEndStore(scratch.Path, Compression.Default, options).Offer(b, new DateOnly(2026, 10, 31))!.Transfer;
    }

    [Fact]
    public void AnObjectThatBecomesOwnAfterRelaying_CarriesOnPastEveryRelayedEsi()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var bulletin = Bulletins.Make(130, 4000);
        var obj = OwnObject(options, bulletin);
        var f = new Fixture();
        f.Hear(obj, [0u, 1u], complete: true);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var relayedPlan = Plan(store, f.Selector(options), options);
        store.Commit(relayedPlan);
        var relayedEsis = relayedPlan.Relayed.Single().Frames.Select(x => x.EncodingSymbolId).ToHashSet();
        var slotOne = store.GetRelayProgress(obj.ObjectId).FirstSlot;

        var carried = store.Offer(bulletin, new DateOnly(2026, 10, 31))!;

        Assert.Equal(obj.ObjectId, carried.Transfer.ObjectId);
        Assert.Equal(slotOne, carried.FirstSlot);
        var own = Enumerable.Range(0, 200).Select(n => carried.EsiOf((uint)n)).Skip((int)carried.NextEsi);
        Assert.Empty(own.Take(50).Intersect(relayedEsis));
        Assert.All(relayedEsis, e => Assert.True(carried.Shuffle!.Unmap(e) < carried.NextEsi));
        Assert.Empty(store.GetRelayProgress(obj.ObjectId).Sent); // now in the bulletin's own state
        Assert.True(store.CarriesObject(obj.ObjectId));
    }

    [Fact]
    public void AnOwnBulletinForgotten_LeavesItsSentEsisSoItNeverStartsFromZeroAgain()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var bulletin = Bulletins.Make(131, 4000);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var day = new DateOnly(2026, 10, 31);
        store.Offer(bulletin, day);
        var plan = BroadcastScheduler.Plan(store.InRotation(Slot), Slot, 1, Compression.Default, options, store.DirectoryNextEsi, Frames(30), "ms110d-wn4");
        store.Commit(plan);
        var sent = plan.Frames.Where(x => plan.Objects.Any(o => o.Bid == bulletin.Bid && o.Transfer.ObjectId == x.ObjectId)).Select(x => (x.ObjectId, x.EncodingSymbolId)).ToList();
        Assert.NotEmpty(sent);

        store.Expire(day.AddDays(options.RememberDays + 1));
        Assert.Equal(0, store.Count);
        var restarted = new HeadEndStore(dir.Path, Compression.Default, options);
        Assert.Equal(sent.Count, restarted.GetRelayProgress(sent[0].ObjectId).Sent.Count);

        var back = restarted.Offer(bulletin, day.AddDays(options.RememberDays + 1))!;
        Assert.True(back.NextEsi >= (uint)sent.Count);
        Assert.All(sent, x => Assert.True(back.Shuffle!.Unmap(x.EncodingSymbolId) < back.NextEsi));
    }

    [Fact]
    public void AnObjectNotHeardOnAirForTwoDays_IsNotRelayed_WhateverWhenTheCatalogueGotIt()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(140);
        f.Hear(obj, [0u, 1u, 2u], heardAt: Now.AddHours(-49));
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        Assert.Empty(Plan(store, f.Selector(options), options).Relayed);

        // First heard 49 hours ago, it is past the retiring age however recently it was heard again.
        f.Hear(obj, [5u], heardAt: Now.AddHours(-1));
        Assert.Empty(Plan(store, f.Selector(options), options).Relayed);

        var recent = Remote(141);
        f.Hear(recent, [0u, 1u], heardAt: Now.AddHours(-10));
        f.Hear(recent, [5u], heardAt: Now.AddHours(-1));
        Assert.Equal(recent.ObjectId, Assert.Single(Plan(store, f.Selector(options), options).Relayed).ObjectId);
    }

    [Fact]
    public void TheCodingIsOnlyEverTakenFromFramesOnAir_AndAnObjectWhoseFramesDisagreeIsLeftOut()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(150);
        var wrong = Remote(151, 6000);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        // A piece handed in with a different coding does not set the object's coding, and the on-air frames' stands.
        f.Catalogue.AddPiece(obj.ObjectId, new RemotePiece(9, new MailcastFrame(obj.ObjectId, obj.DictionaryId, wrong.Oti, 9, obj.Frame(9).Symbol).ToBytes(), Now, false, null, 1));
        f.Hear(obj, [0u, 1u]);
        var selector = f.Selector(options);
        Assert.Equal(obj.Oti, Assert.Single(selector.Select(store.GetRelayProgress, store.CarriesObject)).Oti);

        // Two on-air frames that disagree: left out, said once.
        var bad = Remote(152);
        f.Hear(bad, [0u]);
        f.Catalogue.AddPiece(bad.ObjectId, new RemotePiece(4, new MailcastFrame(bad.ObjectId, bad.DictionaryId, wrong.Oti, 4, bad.Frame(4).Symbol).ToBytes(), Now, true, "GB7RDG", 2));
        Assert.DoesNotContain(selector.Select(store.GetRelayProgress, store.CarriesObject), r => r.ObjectId == bad.ObjectId);
        selector.Select(store.GetRelayProgress, store.CarriesObject);
        Assert.Single(f.Journal.Lines, l => l.Contains("coding is not certain", StringComparison.Ordinal));
    }

    [Fact]
    public void FreshPieces_AvoidWhatOthersAlreadySent()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(160);
        var shuffle = EsiShuffle.For("M0LTE", obj.ObjectId);
        var firstThree = Enumerable.Range(0, 3).Select(n => shuffle.Map((uint)n)).ToList();
        f.Hear(obj, firstThree, complete: true);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        var relay = Assert.Single(Plan(store, f.Selector(options), options).Relayed);

        Assert.Empty(relay.Frames.Select(x => x.EncodingSymbolId).Intersect(firstThree));
    }

    [Fact]
    public void TwoBulletinsSharingAnEnvelope_SendItOnce_InThisStationsOrder_WithOneState()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var a = Remote(170);
        var b = Remote(171);
        var envelope = EnvelopeObject(options);
        var link = new EnvelopeLink(envelope.ObjectId, ObjectId.LongHash(envelope.DictionaryId, envelope.Bytes));
        f.Hear(a, [0u], complete: true, envelope: link);
        f.Hear(b, [0u], complete: true, envelope: link);
        f.Hear(envelope, [0u], complete: true);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        var plan = Plan(store, f.Selector(options), options);
        store.Commit(plan);

        var sent = plan.Frames.Where(x => x.ObjectId == envelope.ObjectId).Select(x => x.EncodingSymbolId).ToList();
        Assert.Equal(envelope.SourceSymbols, sent.Count);
        Assert.Equal(EsiShuffle.For("M0LTE", envelope.ObjectId).Map(0), sent[0]);
        Assert.Equal(sent.Order(), store.GetRelayProgress(envelope.ObjectId).Sent.Order());
    }

    [Fact]
    public void AnEnvelopeObjectIsNeverACandidateOnItsOwn()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var envelope = EnvelopeObject(options);
        f.Hear(envelope, [0u], complete: true); // listed by GB7RDG, but linked from no bulletin we know
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        Assert.Empty(Plan(store, f.Selector(options), options).Relayed);
    }

    [Fact]
    public void TheCatalogueSaysWhenItHasBeenReadAtAll()
    {
        var f = new Fixture();
        Assert.False(f.Catalogue.EverHeld);
        f.Hear(Remote(180), [0u]);
        Assert.True(f.Catalogue.EverHeld);
    }

    [Fact]
    public void TheDirectoryCountsListedRelayedObjectsApart()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        f.Hear(Remote(190), [0u, 1u], complete: true);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        store.Offer(Bulletins.Make(191, 2000), new DateOnly(2026, 10, 31));

        var plan = Plan(store, f.Selector(options), options);

        Assert.Equal(1, plan.RelayListed);
        Assert.Equal(2, plan.Directory.Entries.Count);
    }

    [Fact]
    public void UnchangedPiecesRelayedBeforeTheObjectBecomesOwn_DoNotRetireTheBulletin()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var bulletin = Bulletins.Make(200, 4000);
        var obj = OwnObject(options, bulletin);
        var f = new Fixture();
        f.Hear(obj, [(uint)obj.SourceSymbols + 100u, 16_000_000u, 9_000_000u]); // another station's ESIs, anywhere in the 24-bit space
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var relayedPlan = Plan(store, f.Selector(options), options);
        Assert.Equal(3, relayedPlan.RelayedFrames);
        store.Commit(relayedPlan);

        var carried = store.Offer(bulletin, new DateOnly(2026, 10, 31))!;

        Assert.Equal(0u, carried.NextEsi);
        var plan = BroadcastScheduler.Plan(store.InRotation(Slot), Slot, 1, Compression.Default, options, store.DirectoryNextEsi, Frames(40), "ms110d-wn4");
        Assert.Contains(plan.Directory.Entries, e => e.ObjectId == obj.ObjectId);
        Assert.True(plan.Objects.Single(o => o.Bid == bulletin.Bid).Count > 0);

        // Expiry afterwards does not build a huge set either.
        store.Expire(new DateOnly(2026, 10, 31).AddDays(options.RememberDays + 1));
        Assert.True(store.GetRelayProgress(obj.ObjectId).Sent.Count < 100);
    }

    [Fact]
    public void ARelayedEnvelope_ThenOwn_StartsPastNoneOfItsEsisAndStaysInRange()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var bulletin = Bulletins.Make(201, 4000);
        using var scratch = new TempDirectory();
        var own = new HeadEndStore(scratch.Path, Compression.Default, options).Offer(bulletin, new DateOnly(2026, 10, 31))!;
        var envelope = own.Envelope!;
        var f = new Fixture();
        var link = new EnvelopeLink(envelope.ObjectId, ObjectId.LongHash(envelope.DictionaryId, envelope.Bytes));
        f.Hear(own.Transfer, [0u], complete: true, envelope: link);
        f.Hear(envelope, [0u], complete: true);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var relayedPlan = Plan(store, f.Selector(options), options);
        store.Commit(relayedPlan);
        var sent = store.GetRelayProgress(envelope.ObjectId).Sent;
        Assert.NotEmpty(sent);

        var carried = store.Offer(bulletin, new DateOnly(2026, 10, 31))!;

        Assert.True(carried.EnvelopeNextEsi + (uint)envelope.SourceSymbols <= 0xFFFFFF);
        Assert.Empty(Enumerable.Range(0, envelope.SourceSymbols).Select(i => carried.EnvelopeNextEsi + (uint)i).Intersect(sent));
    }

    [Fact]
    public void RelayState_GoesThirtyDaysAfterTheLastPieceSent_WhateverIsOffered()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var obj = Remote(202);
        f.Hear(obj, [0u, 1u, 2u], complete: true);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        store.Commit(Plan(store, f.Selector(options), options));
        var today = DateOnly.FromDateTime(Slot.UtcDateTime);

        store.Expire(today.AddDays(HeadEndStore.RelayStateMaxDays - 1), _ => true);
        Assert.NotEmpty(store.GetRelayProgress(obj.ObjectId).Sent);
        store.Expire(today.AddDays(HeadEndStore.RelayStateMaxDays), _ => true);
        Assert.Empty(store.GetRelayProgress(obj.ObjectId).Sent);
    }

    [Fact]
    public void FramesWithNoStationLabel_DoNotCountForAnOwnBulletin_ButDoForARelayedObject()
    {
        using var dir = new TempDirectory();
        var options = Options();
        var f = new Fixture();
        var mine = Remote(203);
        f.Hear(mine, [3000u], listedBy: "M0LTE", air: new AirHeard(50, new Dictionary<string, int> { ["gb7rdg"] = 10 }, Now, Now));
        var theirs = Remote(204);
        f.Hear(theirs, [3000u], air: new AirHeard(50, new Dictionary<string, int> { ["m0lte"] = 10 }, Now, Now));
        var selector = f.Selector(options);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);

        Assert.Equal(10, selector.HeardFromOthers(mine.ObjectId)); // the 40 with no label are not counted
        Assert.Equal(40, selector.Select(store.GetRelayProgress, store.CarriesObject).Single(r => r.ObjectId == theirs.ObjectId).HeardOnAir); // ours, any case, taken off
    }
}
