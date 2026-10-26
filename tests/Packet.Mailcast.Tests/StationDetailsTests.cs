using Microsoft.Extensions.Time.Testing;

namespace Packet.Mailcast.Tests;

/// <summary>A station's own frequency, sideband and locator in its directory, and in adverts (issue #179).</summary>
public class StationDetailsTests
{
    private static readonly DateOnly Day = new(2026, 10, 26);
    private static readonly DirectorySigner Rdg = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly DirectorySigner Bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly SlotTimetable HalfPast = new(new TimeOnly(0, 30), 60);

    private static TransferObject Big(int number) => TransferObject.ForBulletin(TestBulletins.Make(number, 9000), ZstdDictionary.Gb7rdg1Id, Compression.Default);

    private static BroadcastDirectory Directory(DateOnly day, DirectorySigner signer, string station, StationDetails? details, IReadOnlyList<StationAdvert>? adverts, TransferObject obj) =>
        new(day, [new DirectoryEntry(obj.ObjectId, obj.DictionaryId, 9000, $"{obj.ObjectId % 100000}_TEST", "Bulletin", Hash: ObjectId.LongHash(obj.DictionaryId, obj.Bytes))],
            HalfPast, "ms110d-wn4", signer, station: station, adverts: adverts, details: details);

    private static AcceptResult Feed(ReceiverStore store, BroadcastDirectory directory)
    {
        var obj = TransferObject.ForDirectory(directory, ZstdDictionary.Gb7rdg1Id, Compression.Default);
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

    private static ReceiverStore Open(TempDirectory dir) => new(dir.Path, Compression.Default, TestStores.Fast with
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero)),
        TrustedKeys = [TrustedKey.Parse("GB7RDG", Rdg.PublicKeyBase64)],
    });

    [Fact]
    public void TheDetails_RoundTrip_AndReleasedReceiversReadTheSameDirectory()
    {
        var advert = new StationAdvert("QO100X", HalfPast, 10_489_750_000, Sideband.Usb, "JO02");
        var directory = Directory(Day, Bpq, "G8BPQ", new StationDetails(7_047_000, Sideband.Lsb, "IO83fb"), [advert], Big(1));
        byte[] bytes = directory.Serialize();
        string first = Bulletin.TextEncoding.GetString(bytes).Split('\n')[2];
        Assert.Contains("\thz=7047000\tsideband=lsb\tlocator=IO83fb\t", first, StringComparison.Ordinal);
        Assert.Contains("\tadvert=QO100X;00:30/60;-;10489750000;usb;JO02\t", first, StringComparison.Ordinal);

        var parsed = BroadcastDirectory.Parse(bytes);
        Assert.Equal(new StationDetails(7_047_000, Sideband.Lsb, "IO83fb"), parsed.Details);
        Assert.Equal(advert, Assert.Single(parsed.Adverts));

        // Released receivers (0.16.0 to 0.18.1 as V0181Directory, and 0.19.0's adverts, which read the first five
        // parts and ignore a sixth): the same entries and timetable, and the signature still checks.
        var old = V0181Directory.Parse(bytes);
        Assert.Equal(parsed.Entries, old.Entries);
        Assert.Equal(parsed.Schedule, old.Schedule);
        Assert.Equal(SignatureState.Trusted, old.Check([TrustedKey.Parse("G8BPQ", Bpq.PublicKeyBase64)]).State);
        Assert.Equal(SignatureState.Trusted, parsed.Check([TrustedKey.Parse("G8BPQ", Bpq.PublicKeyBase64)]).State);
    }

    [Fact]
    public void AnAdvertWithALocator_IsReadByCodeThatStopsAtTheSideband()
    {
        // v0.19.0's reader: five parts or more, the first five used. Reproduced here as the rule it applies.
        var p = new StationAdvert("G8BPQ", HalfPast, 7_047_000, Sideband.Usb, "IO83").ToField()["advert=".Length..].Split(';');
        Assert.True(p.Length >= 5);
        Assert.Equal(["G8BPQ", "00:30/60", "-", "7047000", "usb"], p[..5]);
        Assert.Null(StationAdvert.FromField("advert=G8BPQ;00:30/60;-;7047000;usb")!.Locator);
        Assert.Null(StationAdvert.FromField("advert=G8BPQ;00:30/60;-;7047000;usb;nonsense")!.Locator);
    }

    [Fact]
    public void ADirectoryWithoutDetails_ReadsAsNone_AndWrongOnesAreRefused()
    {
        var plain = BroadcastDirectory.Parse(Directory(Day, Bpq, "G8BPQ", null, null, Big(2)).Serialize());
        Assert.Null(plain.Details);
        Assert.Throws<ArgumentException>(() => Directory(Day, Bpq, "G8BPQ", new StationDetails(0), null, Big(2)));
        Assert.Throws<ArgumentException>(() => Directory(Day, Bpq, "G8BPQ", new StationDetails(Locator: "nope"), null, Big(2)));
        Assert.Null(StationDetails.FromFields(["hz=abc", "sideband=am", "locator=zz"]));
    }

    [Fact]
    public void TheStoreKeepsEachPartFromTheNewestTrustedDirectoryThatGivesIt_AndNotFromUnsignedOnes()
    {
        using var dir = new TempDirectory();
        var store = Open(dir);
        Assert.Null(store.HeardDetails("G8BPQ"));
        Assert.Equal(7_053_800, ReceiverStore.BuiltInDetails.FrequencyHz);

        Assert.Equal(FrameOutcome.CompletedDirectory, Feed(store, Directory(Day, Bpq, "G8BPQ", new StationDetails(7_047_000, Sideband.Usb, "IO83"), null, Big(3))).Outcome);
        Assert.Equal(new StationDetails(7_047_000, Sideband.Usb, "IO83"), store.HeardDetails("G8BPQ"));

        // A later directory that moves the frequency and leaves the locator out: the locator stands.
        Feed(store, Directory(Day.AddDays(1), Bpq, "G8BPQ", new StationDetails(7_048_500), null, Big(4)));
        Assert.Equal(new StationDetails(7_048_500, Sideband.Usb, "IO83"), store.HeardDetails("G8BPQ"));

        // An unsigned directory naming it belongs to no station, so it can move nothing.
        var unsigned = new BroadcastDirectory(Day.AddDays(2), [new DirectoryEntry(5, 1, 9000, "5_T", "T")], HalfPast, "ms110d-wn4", null, station: "G8BPQ", details: new StationDetails(14_000_000));
        Feed(store, unsigned);
        Assert.Equal(7_048_500, store.HeardDetails("G8BPQ")!.FrequencyHz);
        Assert.Null(store.HeardDetails("GB7RDG"));
    }
}
