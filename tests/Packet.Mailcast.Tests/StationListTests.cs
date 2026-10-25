namespace Packet.Mailcast.Tests;

/// <summary>The signed station list (issue #178): its format, its signature, its object, and the receiver's store.</summary>
public class StationListTests
{
    // Throwaway keys: never a real list key.
    private static readonly DirectorySigner ListSigner = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly TrustedKey ListKey = TrustedKey.Parse("LIST", ListSigner.PublicKeyBase64);
    private static readonly DirectorySigner OtherSigner = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly DirectorySigner BpqSigner = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());

    private static StationEntry Gb7rdg => StationEntry.Gb7rdg(new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg));

    private static StationEntry G8bpq => new("G8BPQ", "IO93fb", 7_047_000, Sideband.Usb, new SlotTimetable(new TimeOnly(0, 30), 60), ["ms110d-wn4"], TrustedKey.Parse("G8BPQ", BpqSigner.PublicKeyBase64));

    private static StationList Signed(long serial, DirectorySigner? by = null, params StationEntry[] stations) =>
        StationList.Sign(serial, stations.Length == 0 ? [Gb7rdg, G8bpq] : stations, by ?? ListSigner);

    [Fact]
    public void Signed_RoundTrips_AndChecksAgainstTheListKey()
    {
        var list = Signed(2026102501);
        string text = Bulletin.TextEncoding.GetString(list.Serialize());
        var lines = text.Split('\n');
        Assert.Equal(StationList.VersionLine, lines[0]);
        Assert.Equal("serial=2026102501", lines[1]);
        Assert.StartsWith("GB7RDG\tlocator=IO91lk\thz=7053800\tsideband=usb\tslots=00:00/60\tdaylight=IO91lk/120/30\tmodes=ms110d-wn3,ms110d-wn4\tkey=", lines[2], StringComparison.Ordinal);
        Assert.StartsWith($"listkey={KeyId.Format(ListSigner.KeyId)}\tsig=", lines[4], StringComparison.Ordinal);
        Assert.All(text, c => Assert.InRange(c, '\t', '~'));

        var parsed = StationList.Parse(list.Serialize());
        Assert.Equal(list.Serialize(), parsed.Serialize());
        Assert.Equal(2026102501, parsed.Serial);
        Assert.Equal(["GB7RDG", "G8BPQ"], parsed.Stations.Select(s => s.Callsign));
        var bpq = parsed.Stations[1];
        Assert.Equal(G8bpq.Timetable, bpq.Timetable);
        Assert.Equal(7_047_000, bpq.FrequencyHz);
        Assert.Equal(BpqSigner.KeyId, bpq.Key.KeyId);
        Assert.Equal(TrustedKey.Gb7rdg.KeyId, parsed.Stations[0].Key.KeyId);
        Assert.Empty(parsed.Skipped);
        Assert.Equal(SignatureState.Trusted, parsed.Check([ListKey]).State);
    }

    [Fact]
    public void Signing_IsDeterministic_SoEveryHeadEndSendsTheSameObject()
    {
        var a = TransferObject.ForStationList(Signed(5));
        var b = TransferObject.ForStationList(StationList.Parse(Signed(5).Serialize()));
        Assert.Equal(a.ObjectId, b.ObjectId);
        Assert.Equal(a.Bytes.ToArray(), b.Bytes.ToArray());
        Assert.Equal(ObjectKind.StationList, a.Kind);
        Assert.Equal(Compression.NoDictionary, a.DictionaryId);
    }

    [Fact]
    public void Check_RefusesAnotherKey_ATamperedList_AndThePlaceholder()
    {
        var list = Signed(3);
        Assert.Equal(SignatureState.Rejected, Signed(3, OtherSigner).Check([ListKey]).State);
        Assert.Contains("not the station list key", Signed(3, OtherSigner).Check([ListKey]).Reason, StringComparison.Ordinal);

        byte[] bytes = list.Serialize();
        string text = Bulletin.TextEncoding.GetString(bytes).Replace("hz=7047000", "hz=7047100", StringComparison.Ordinal);
        var tampered = StationList.Parse(Bulletin.TextEncoding.GetBytes(text));
        Assert.Equal(SignatureState.Rejected, tampered.Check([ListKey]).State);
        Assert.Contains("does not match", tampered.Check([ListKey]).Reason, StringComparison.Ordinal);

        // Until Tom makes the real list key, none is built in, and every list is refused.
        Assert.True(StationListKey.IsPlaceholder);
        Assert.Empty(StationListKey.BuiltIn);
        Assert.Equal("no station list key is built in yet", list.Check(StationListKey.BuiltIn).Reason);
    }

    [Fact]
    public void Parse_IgnoresUnknownFields_AndSkipsStationLinesItCannotUse()
    {
        var bytes = Signed(9).Serialize();
        string text = Bulletin.TextEncoding.GetString(bytes);
        var lines = text.Split('\n').ToList();
        lines[2] += "\tplace=Reading";
        lines.Insert(3, "M0XYZ\tlocator=IO91\thz=7000000\tsideband=usb\tmodes=ms110d-wn4\tkey=AAAA");
        var parsed = StationList.Parse(Bulletin.TextEncoding.GetBytes(string.Join('\n', lines)));
        Assert.Equal(["GB7RDG", "G8BPQ"], parsed.Stations.Select(s => s.Callsign));
        Assert.Single(parsed.Skipped);
        Assert.StartsWith("M0XYZ: no readable slots", parsed.Skipped[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MAILCAST STATIONS 2\nserial=1\nlistkey=0000000000000000\tsig=AA==\n")]
    [InlineData("MAILCAST STATIONS 1\nserial=0\nlistkey=0000000000000000\tsig=AA==\n")]
    [InlineData("MAILCAST STATIONS 1\nserial=1\nGB7RDG\tlocator=IO91lk\n")]
    [InlineData("MAILCAST STATIONS 1\nserial=1\nlistkey=0000000000000000\tsig=AA==")]
    public void Parse_RefusesWhatIsNotAList(string text) =>
        Assert.Throws<FormatException>(() => StationList.Parse(Bulletin.TextEncoding.GetBytes(text)));

    [Fact]
    public void Sign_RefusesBadStations()
    {
        Assert.Throws<ArgumentException>(() => Signed(1, null, Gb7rdg, Gb7rdg));
        Assert.Throws<ArgumentException>(() => Signed(1, null, G8bpq with { Callsign = "g8bpq" }));
        Assert.Throws<ArgumentException>(() => Signed(1, null, G8bpq with { Locator = "XX" }));
        Assert.Throws<ArgumentException>(() => Signed(1, null, G8bpq with { Modes = [] }));
        Assert.Throws<ArgumentException>(() => Signed(1, null, G8bpq with { Key = TrustedKey.Gb7rdg }, Gb7rdg));
    }

    [Fact]
    public void Object_OfAShortList_IsOneSymbol_AndAnyFrameRebuildsIt()
    {
        var obj = TransferObject.ForStationList(Signed(4));
        Assert.Equal(1, obj.SourceSymbols);
        Assert.True(obj.Oti.SymbolSize < MailcastFrame.StandardSymbolSize);
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { ListKeys = [ListKey] });
        var result = store.Accept(obj.Frame(0).ToBytes());
        Assert.Equal(FrameOutcome.CompletedStationList, result.Outcome);
        Assert.Null(result.Detail);
        Assert.Equal(4, store.Stations!.Serial);
    }

    [Fact]
    public void Object_OfALongList_UsesStandardSymbols()
    {
        var many = Enumerable.Range(0, 8).Select(i => G8bpq with
        {
            Callsign = $"G{i}ABC",
            Key = TrustedKey.Parse($"G{i}ABC", DirectorySigner.FromPem(DirectorySigner.NewKeyPem()).PublicKeyBase64),
        }).ToArray();
        var obj = TransferObject.ForStationList(Signed(4, null, many));
        Assert.Equal(MailcastFrame.StandardSymbolSize, obj.Oti.SymbolSize);
        Assert.True(obj.SourceSymbols > 1);
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { ListKeys = [ListKey] });
        AcceptResult? last = null;
        for (uint esi = 0; esi < obj.SourceSymbols + 2 && store.Stations is null; esi++)
        {
            last = store.Accept(obj.Frame(esi).ToBytes());
        }
        Assert.Equal(FrameOutcome.CompletedStationList, last!.Outcome);
        Assert.Equal(8, store.Stations!.Stations.Count);
    }

    [Fact]
    public void Store_KeepsTheNewest_AcrossRestarts_AndNeverGoesBack()
    {
        using var dir = new TempDirectory();
        var options = TestStores.Fast with { ListKeys = [ListKey] };
        var store = new ReceiverStore(dir.Path, Compression.Default, options);
        Assert.Null(store.Stations);
        Assert.Equal(TrustedKey.BuiltIn.Select(k => k.KeyId), store.DirectoryKeys.Select(k => k.KeyId));

        store.Accept(TransferObject.ForStationList(Signed(10)).Frame(0).ToBytes());
        Assert.Equal(10, store.Stations!.Serial);

        var older = store.Accept(TransferObject.ForStationList(Signed(9, null, Gb7rdg)).Frame(0).ToBytes());
        Assert.Equal(FrameOutcome.CompletedStationList, older.Outcome);
        Assert.Contains("older than the list held", older.Detail, StringComparison.Ordinal);
        Assert.Equal(10, store.Stations.Serial);

        var sameSerial = store.Accept(TransferObject.ForStationList(Signed(10, null, Gb7rdg)).Frame(0).ToBytes());
        Assert.Contains("same serial", sameSerial.Detail, StringComparison.Ordinal);
        Assert.Equal(2, store.Stations.Stations.Count);

        var untrusted = store.Accept(TransferObject.ForStationList(Signed(11, OtherSigner)).Frame(0).ToBytes());
        Assert.NotNull(untrusted.Detail);
        Assert.Equal(10, store.Stations.Serial);

        var reopened = new ReceiverStore(dir.Path, Compression.Default, options);
        Assert.Equal(10, reopened.Stations!.Serial);
        Assert.Contains(reopened.DirectoryKeys, k => k.KeyId == BpqSigner.KeyId);

        // A receiver without the key (the placeholder) does not use the file it finds.
        Assert.Null(new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast).Stations);
    }

    [Fact]
    public void Store_WithThePlaceholderKey_RefusesEveryList_AndCarriesOn()
    {
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        var result = store.Accept(TransferObject.ForStationList(Signed(1)).Frame(0).ToBytes());
        Assert.Equal(FrameOutcome.CompletedStationList, result.Outcome);
        Assert.Contains("no station list key is built in yet", result.Detail, StringComparison.Ordinal);
        Assert.Null(store.Stations);
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(TransferObject.ForStationList(Signed(1)).Frame(1).ToBytes()).Outcome);
    }

    [Fact]
    public void ListedStationsKeys_AreTrustedForTheirDirectories()
    {
        var bulletin = TestBulletins.Day(1, 1)[0];
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var entry = new DirectoryEntry(obj.ObjectId, obj.DictionaryId, bulletin.Serialize().Length, bulletin.Bid, bulletin.Title, Hash: ObjectId.LongHash(obj.DictionaryId, obj.Bytes));
        var directory = new BroadcastDirectory(new DateOnly(2026, 10, 25), [entry], new SlotTimetable(new TimeOnly(0, 30), 60), signer: BpqSigner);
        var directoryObject = TransferObject.ForDirectory(directory, ZstdDictionary.Gb7rdg1Id, Compression.Default);

        using var before = new TempDirectory();
        var unlisted = new ReceiverStore(before.Path, Compression.Default, TestStores.Fast with { ListKeys = [ListKey] });
        Assert.Equal(FrameOutcome.RejectedDirectory, FeedAll(unlisted, directoryObject).Outcome);

        using var after = new TempDirectory();
        var listed = new ReceiverStore(after.Path, Compression.Default, TestStores.Fast with { ListKeys = [ListKey] });
        listed.Accept(TransferObject.ForStationList(Signed(1)).Frame(0).ToBytes());
        var result = FeedAll(listed, directoryObject);
        Assert.Equal(FrameOutcome.CompletedDirectory, result.Outcome);
        Assert.Equal("signed by G8BPQ", result.Check!.Describe());
    }

    [Fact]
    public void ANewList_TrustsADirectoryHeardBefore_AndReleasesWhatItHeld()
    {
        var bulletin = TestBulletins.Day(2, 1)[0];
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var entry = new DirectoryEntry(obj.ObjectId, obj.DictionaryId, bulletin.Serialize().Length, bulletin.Bid, bulletin.Title, Hash: ObjectId.LongHash(obj.DictionaryId, obj.Bytes));
        var directory = new BroadcastDirectory(new DateOnly(2026, 10, 25), [entry], new SlotTimetable(new TimeOnly(0, 30), 60), signer: BpqSigner);
        using var dir = new TempDirectory();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { ListKeys = [ListKey] });
        Assert.Equal(FrameOutcome.RejectedDirectory, FeedAll(store, TransferObject.ForDirectory(directory, ZstdDictionary.Gb7rdg1Id, Compression.Default)).Outcome);
        Assert.Equal(FrameOutcome.Withheld, FeedAll(store, obj).Outcome);
        Assert.Empty(store.Pending());

        var result = store.Accept(TransferObject.ForStationList(Signed(1)).Frame(0).ToBytes());
        Assert.Equal(bulletin.Bid, Assert.Single(result.Released!).Bid);
        Assert.Single(store.Pending());
        Assert.Equal(directory.Date, store.Directory!.Date);
    }

    private static AcceptResult FeedAll(ReceiverStore store, TransferObject obj)
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
}
