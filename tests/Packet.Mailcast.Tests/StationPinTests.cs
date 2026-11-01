using Microsoft.Extensions.Time.Testing;

namespace Packet.Mailcast.Tests;

/// <summary>
/// Stations naming themselves, keys pinned on first hearing, adverts, and a directory and
/// give-up per station (issues #178 and #144).
/// </summary>
public class StationPinTests
{
    private static readonly DateOnly Day = new(2026, 10, 25);

    // Throwaway keys: never real ones.
    private static readonly DirectorySigner Rdg = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly DirectorySigner Bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly DirectorySigner Impostor = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());

    private static readonly SlotTimetable OnTheHour = new(TimeOnly.MinValue, 60);
    private static readonly SlotTimetable HalfPast = new(new TimeOnly(0, 30), 60);
    private static readonly StationAdvert BpqAdvert = new("G8BPQ", HalfPast, 7_053_800, Sideband.Usb);

    private static TransferObject Big(int number)
    {
        var obj = TransferObject.ForBulletin(TestBulletins.Make(number, 9000), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.True(obj.SourceSymbols > 1);
        return obj;
    }

    private static BroadcastDirectory Directory(SlotTimetable timetable, DirectorySigner? signer, string? station, IReadOnlyList<StationAdvert>? adverts, params TransferObject[] listed) =>
        new(Day, listed.Select(o => new DirectoryEntry(o.ObjectId, o.DictionaryId, 9000, $"{o.ObjectId % 100000}_TEST", "Bulletin", Hash: ObjectId.LongHash(o.DictionaryId, o.Bytes))),
            timetable, "ms110d-wn4", signer, station: station, adverts: adverts);

    private static AcceptResult Feed(ReceiverStore store, TransferObject obj)
    {
        AcceptResult result = null!;
        for (uint esi = 0; esi < obj.SourceSymbols + 4; esi++)
        {
            result = store.Accept(obj.Frame(esi).ToBytes());
            if (result.Outcome != FrameOutcome.Stored)
            {
                return result;
            }
        }
        return result;
    }

    private static AcceptResult Feed(ReceiverStore store, BroadcastDirectory directory) =>
        Feed(store, TransferObject.ForDirectory(directory, ZstdDictionary.Gb7rdg1Id, Compression.Default));

    /// <summary>GB7RDG built in as <see cref="Rdg"/>, so tests can sign as it.</summary>
    private static (ReceiverStore Store, FakeTimeProvider Time, TempDirectory Dir) Open(TempDirectory? reuse = null)
    {
        var dir = reuse ?? new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with
        {
            Time = time,
            TrustedKeys = [TrustedKey.Parse("GB7RDG", Rdg.PublicKeyBase64)],
        });
        return (store, time, dir);
    }

    [Fact]
    public void NewFields_RoundTrip_AndReleasedReceiversReadTheSameDirectory()
    {
        var directory = Directory(HalfPast, Bpq, "G8BPQ", [BpqAdvert with { Callsign = "GB7RDG", Timetable = new SlotTimetable(TimeOnly.MinValue, 60, StationFixture.Daylight) }, new("QO100X", OnTheHour, 10_489_750_000, Sideband.Usb)], Big(1), Big(2));
        byte[] bytes = directory.Serialize();
        string first = Bulletin.TextEncoding.GetString(bytes).Split('\n')[2];
        Assert.Contains("\tstation=G8BPQ\tpubkey=" + Bpq.PublicKeyBase64, first, StringComparison.Ordinal);
        Assert.Contains("\tadvert=GB7RDG;00:00/60;IO91lk/120/30;7053800;usb\tadvert=QO100X;00:00/60;-;10489750000;usb", first, StringComparison.Ordinal);

        var parsed = BroadcastDirectory.Parse(bytes);
        Assert.Equal("G8BPQ", parsed.Station);
        Assert.Equal(Bpq.PublicKey, parsed.PublicKey);
        Assert.Equal(directory.Adverts, parsed.Adverts);

        // v0.18.1's own parser: the same entries, timetable, mode and reports, and the signature checks.
        var old = V0181Directory.Parse(bytes);
        Assert.Equal(parsed.Entries, old.Entries);
        Assert.Equal(parsed.Schedule, old.Schedule);
        Assert.Equal(parsed.Mode, old.Mode);
        var bpqKey = TrustedKey.Parse("G8BPQ", Bpq.PublicKeyBase64);
        Assert.Equal(SignatureState.Trusted, old.Check([bpqKey]).State);
        Assert.Equal(SignatureState.Trusted, parsed.Check([bpqKey]).State);
    }

    [Fact]
    public void ANewStationsKey_IsPinnedOnFirstHearing_AndItsBulletinsDeliveredAtOnce()
    {
        var (store, _, dir) = Open();
        using var _d = dir;
        var obj = Big(10);
        var result = Feed(store, Directory(HalfPast, Bpq, "G8BPQ", null, obj));
        Assert.Equal(FrameOutcome.CompletedDirectory, result.Outcome);
        Assert.Equal("G8BPQ", store.StationOf(result.Directory!, result.Check!));
        Assert.Contains(store.Pins, p => p.Station == "G8BPQ" && p.Key.KeyId == Bpq.KeyId && p.PinnedAt is not null);
        Assert.Equal(FrameOutcome.CompletedBulletin, Feed(store, obj).Outcome);
        Assert.Equal(HalfPast, store.HeardTimetable("G8BPQ"));
        Assert.Empty(store.HeardSchedules); // not a station of the stations file

        // Kept across a restart.
        var (reopened, _, _) = Open(dir);
        Assert.Contains(reopened.Pins, p => p.Station == "G8BPQ" && p.Key.KeyId == Bpq.KeyId);
    }

    [Fact]
    public void TwoTrustedKeysWithOneLabel_AreBothTrusted_AsOneStation()
    {
        // A key rotation: the old and the new GB7RDG key both in trustedKeys.
        var newer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with
        {
            Stations = StationsFile.Empty,
            TrustedKeys = [TrustedKey.Parse("GB7RDG", Rdg.PublicKeyBase64), TrustedKey.Parse("GB7RDG", newer.PublicKeyBase64)],
        });
        var result = Feed(store, Directory(OnTheHour, newer, "M0LTE", null, Big(90)));
        Assert.Equal(FrameOutcome.CompletedDirectory, result.Outcome);
        Assert.Equal(StationFixture.Callsign, store.StationOf(result.Directory!, result.Check!));
        Assert.Equal(2, store.Pins.Count(p => p.Station == StationFixture.Callsign));
    }

    [Fact]
    public void ATrustedKeysLabelThatIsNotACallsign_TakesTheCallsignItsDirectoryNames()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        File.WriteAllText(Path.Combine(dir.Path, ReceiverStore.PinsFile), "G4XYZ\tnot a key\t2026-10-25T00:00:00Z\nG4ABC\t" + Impostor.PublicKeyBase64 + "\tno date\n");
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with
        {
            TrustedKeys = [TrustedKey.Parse("GB7RDG", Rdg.PublicKeyBase64), TrustedKey.Parse("Bob's station", Bpq.PublicKeyBase64)],
            Log = log.Add,
        });
        Assert.Equal(2, log.Count(l => l.Contains("skipped an unreadable line", StringComparison.Ordinal)));
        var result = Feed(store, Directory(HalfPast, Bpq, "G8BPQ", null, Big(91)));
        Assert.Equal("G8BPQ", store.StationOf(result.Directory!, result.Check!));
        Assert.Equal(["GB7RDG", "G8BPQ"], store.StationDirectories().Select(d => d.Station).Where(s => s != StationFixture.Callsign).Prepend("GB7RDG"));
        Assert.DoesNotContain(store.Pins, p => p.Station.Contains("BOB", StringComparison.Ordinal));
    }

    [Fact]
    public void ADirectoryNamingAPinnedStation_UnderAnotherKey_IsRefusedAndShown()
    {
        var (store, _, dir) = Open();
        using var _d = dir;
        Feed(store, Directory(HalfPast, Bpq, "G8BPQ", null, Big(20)));
        var obj = Big(21);
        var forged = Feed(store, Directory(HalfPast, Impostor, "G8BPQ", null, obj));
        Assert.Equal(FrameOutcome.RejectedDirectory, forged.Outcome);
        Assert.Contains("whose key is pinned as", forged.Detail, StringComparison.Ordinal);
        var conflict = Assert.Single(store.KeyConflicts);
        Assert.Equal(("G8BPQ", Bpq.KeyId, Impostor.KeyId), (conflict.Station, conflict.PinnedKeyId, conflict.HeardKeyId));
        Assert.Equal(FrameOutcome.Withheld, Feed(store, obj).Outcome);

        // GB7RDG's built-in key cannot be replaced either.
        Assert.Equal(FrameOutcome.RejectedDirectory, Feed(store, Directory(OnTheHour, Impostor, "GB7RDG", null, Big(22))).Outcome);
    }

    [Fact]
    public void APinnedKey_KeepsItsName_WhateverCallsignItsDirectoryGives()
    {
        var (store, _, dir) = Open();
        using var _d = dir;
        // GB7RDG's head end sends from M0LTE, signed with GB7RDG's key.
        var result = Feed(store, Directory(OnTheHour, Rdg, "M0LTE", null, Big(30)));
        Assert.Equal(StationFixture.Callsign, store.StationOf(result.Directory!, result.Check!));
        Assert.DoesNotContain(store.Pins, p => p.Station == "M0LTE");
        Assert.Equal(OnTheHour, store.HeardTimetable(StationFixture.Callsign));
    }

    [Fact]
    public void UnsignedDirectories_AreGb7rdgsAsBefore_UnlessTheyNameAnotherStation()
    {
        var (store, _, dir) = Open();
        using var _d = dir;
        var plain = Feed(store, Directory(HalfPast, null, null, null, Big(40)));
        Assert.Equal(StationFixture.Callsign, store.StationOf(plain.Directory!, plain.Check!));
        Assert.Equal(HalfPast, store.HeardTimetable(StationFixture.Callsign));
        var claims = Feed(store, Directory(OnTheHour, null, "G8BPQ", [BpqAdvert], Big(41)));
        Assert.Equal(FrameOutcome.CompletedDirectory, claims.Outcome);
        Assert.Null(store.StationOf(claims.Directory!, claims.Check!));
        Assert.Equal(HalfPast, store.HeardTimetable(StationFixture.Callsign));
        Assert.Empty(store.Adverts()); // adverts only from trusted signed directories
    }

    [Fact]
    public void Adverts_ComeFromTrustedDirectories_NewestPerStation_AndTrustNothing()
    {
        var (store, _, dir) = Open();
        using var _d = dir;
        Feed(store, Directory(OnTheHour, Rdg, "M0LTE", [BpqAdvert], Big(50)));
        Assert.Equal([BpqAdvert], store.Adverts());
        Assert.DoesNotContain(store.Pins, p => p.Station == "G8BPQ");
        var moved = BpqAdvert with { Timetable = new SlotTimetable(new TimeOnly(0, 45), 60) };
        var qo100 = BpqAdvert with { FrequencyHz = 10_489_750_000 };

        // Another directory the same day that disagrees does not move it (no flip-flopping)...
        Feed(store, Directory(OnTheHour, Rdg, "M0LTE", [moved, qo100], Big(51)));
        Assert.Equal([BpqAdvert, qo100], store.Adverts()); // one advert per frequency: HF and QO-100
        // ...a later one does.
        var tomorrow = new BroadcastDirectory(Day.AddDays(1), [new DirectoryEntry(Big(52).ObjectId, ZstdDictionary.Gb7rdg1Id, 9000, "52_TEST", "B")], OnTheHour, signer: Rdg, station: "M0LTE", adverts: [moved]);
        Feed(store, tomorrow);
        Assert.Equal([moved, qo100], store.Adverts());
    }

    [Fact]
    public void AnAdvert_NoDirectoryRepeatsForTwoDays_Expires()
    {
        var (store, time, dir) = Open();
        using var _d = dir;
        Feed(store, Directory(OnTheHour, Rdg, "M0LTE", [BpqAdvert], Big(53)));
        time.Advance(TimeSpan.FromDays(2));
        Assert.Equal([BpqAdvert], store.Adverts());
        Assert.NotNull(store.AdvertFirstSeen("G8BPQ"));
        time.Advance(TimeSpan.FromDays(1));
        Assert.Empty(store.Adverts());
        Assert.Null(store.AdvertFirstSeen("G8BPQ"));
        // Heard again, it is back, as new.
        var again = new BroadcastDirectory(Day.AddDays(3), [new DirectoryEntry(Big(54).ObjectId, ZstdDictionary.Gb7rdg1Id, 9000, "54_TEST", "B")], OnTheHour, signer: Rdg, station: "M0LTE", adverts: [BpqAdvert]);
        Feed(store, again);
        Assert.Equal([BpqAdvert], store.Adverts());
        Assert.Equal(Day.AddDays(3), store.AdvertFirstSeen("G8BPQ"));
    }

    [Fact]
    public void ATrustedKeysKeyNamingAPinnedStation_IsRefusedAsAKeyConflict()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with
        {
            Stations = StationsFile.Empty,
            TrustedKeys = [TrustedKey.Parse("GB7RDG", Rdg.PublicKeyBase64), TrustedKey.Parse("Bob's station", Impostor.PublicKeyBase64)],
        });
        var result = Feed(store, Directory(OnTheHour, Impostor, "GB7RDG", null, Big(92)));
        Assert.Equal(FrameOutcome.RejectedDirectory, result.Outcome);
        var conflict = Assert.Single(store.KeyConflicts);
        Assert.Equal(("GB7RDG", Rdg.KeyId, Impostor.KeyId), (conflict.Station, conflict.PinnedKeyId, conflict.HeardKeyId));
        Assert.DoesNotContain(store.Pins, p => p.Key.KeyId == Impostor.KeyId);
    }

    [Fact]
    public void AKeyConflictFromTrustedKeys_StaysRefused_WhenANewStationIsLearntLater()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with
        {
            TrustedKeys = [TrustedKey.Parse("GB7RDG", Rdg.PublicKeyBase64), TrustedKey.Parse("Bob's station", Impostor.PublicKeyBase64)],
        });
        var obj = Big(94);
        Assert.Equal(FrameOutcome.RejectedDirectory, Feed(store, Directory(OnTheHour, Impostor, "GB7RDG", null, obj)).Outcome);
        Assert.Equal(FrameOutcome.Withheld, Feed(store, obj).Outcome);
        var pinned = Feed(store, Directory(HalfPast, Bpq, "G8BPQ", null, Big(95)));
        Assert.Contains(store.Pins, p => p.Station == "G8BPQ");
        Assert.DoesNotContain(pinned.Released ?? [], b => b.Bid == TestBulletins.Make(94, 9000).Bid);
        Assert.Single(store.KeyConflicts);
    }

    [Fact]
    public void RemovingAHandAddedKeyFromTrustedKeys_RemovesItsPin()
    {
        using var dir = new TempDirectory();
        TrustedKey[] With() => [TrustedKey.Parse("GB7RDG", Rdg.PublicKeyBase64), TrustedKey.Parse("Bob's station", Bpq.PublicKeyBase64)];
        var first = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { TrustedKeys = With() });
        Feed(first, Directory(HalfPast, Bpq, "G8BPQ", null, Big(93)));
        Assert.Contains(first.Pins, p => p.Station == "G8BPQ");

        // Still in trustedKeys after a restart: still pinned.
        var kept = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { TrustedKeys = With() });
        Assert.Contains(kept.Pins, p => p.Station == "G8BPQ");

        // Taken out: the pin goes (the station is learnt afresh like any other if its directory is heard again).
        var log = new List<string>();
        var removed = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { TrustedKeys = [TrustedKey.Parse("GB7RDG", Rdg.PublicKeyBase64)], Log = log.Add });
        Assert.Contains(log, l => l.Contains("no longer in trustedKeys", StringComparison.Ordinal));
        Assert.DoesNotContain(removed.Pins, p => p.FromTrustedKeys);
        Assert.DoesNotContain("trusted", File.ReadAllText(Path.Combine(dir.Path, ReceiverStore.PinsFile)), StringComparison.Ordinal);
    }

    [Fact]
    public void ABulletinHeldForAnUnknownKey_IsReleased_WhenItsStationIsPinned()
    {
        var (store, _, dir) = Open();
        using var _d = dir;
        var obj = Big(60);
        // An old head end's signed directory names no station or key: refused, so its bulletin is held.
        Assert.Equal(FrameOutcome.RejectedDirectory, Feed(store, Directory(HalfPast, Bpq, null, null, obj)).Outcome);
        Assert.Equal(FrameOutcome.Withheld, Feed(store, obj).Outcome);
        var pinned = Feed(store, Directory(HalfPast, Bpq, "G8BPQ", null, Big(61)));
        Assert.Contains(pinned.Released!, b => b.Bid == TestBulletins.Make(60, 9000).Bid);
    }

    [Fact]
    public void ABulletin_IsGivenUpOnlyWhenNoStationListsItAnyMore()
    {
        var (store, time, dir) = Open();
        using var _d = dir;
        var shared = Big(70);
        var other = Big(71);
        Feed(store, Directory(OnTheHour, Rdg, "M0LTE", null, shared, other));
        Feed(store, Directory(HalfPast, Bpq, "G8BPQ", null, shared));
        time.Advance(TimeSpan.FromMinutes(5));
        store.Accept(shared.Frame(0).ToBytes());
        store.Accept(other.Frame(0).ToBytes());

        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal([other.ObjectId], Feed(store, Directory(OnTheHour, Rdg, "M0LTE", null, Big(72))).NoLongerSent);
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal([shared.ObjectId], Feed(store, Directory(HalfPast, Bpq, "G8BPQ", null, Big(73))).NoLongerSent);
        Assert.Equal(["G8BPQ", StationFixture.Callsign], store.StationDirectories().Select(d => d.Station)); // by callsign
    }

    [Fact]
    public void ADirectoryOfNoStation_GivesNothingUp()
    {
        var (store, time, dir) = Open();
        using var _d = dir;
        var obj = Big(80);
        Feed(store, Directory(HalfPast, Bpq, "G8BPQ", null, obj));
        time.Advance(TimeSpan.FromMinutes(5));
        store.Accept(obj.Frame(0).ToBytes());
        time.Advance(TimeSpan.FromHours(1));
        Assert.Null(Feed(store, Directory(HalfPast, null, "G8BPQ", null, Big(81))).NoLongerSent);
    }
}
