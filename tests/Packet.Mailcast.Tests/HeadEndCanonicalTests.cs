using System.Text.Json.Nodes;
using Mailcast.ModelBVectors;

namespace Packet.Mailcast.Tests;

/// <summary>
/// Issue #142, PR 4: a bulletin a head end takes in is sent canonical, with its envelope, at 240-octet pieces.
/// The vector inputs go through HeadEndStore and BroadcastScheduler as an intake would, so the vector file
/// pins what goes on air, not only what the library can build.
/// </summary>
public class HeadEndCanonicalTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 10, 30);
    private readonly string _root = Directory.CreateTempSubdirectory("headend-canonical-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static JsonArray Cases => ((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "model-b-v1.json")))!)["cases"]!.AsArray();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases)
        {
            data.Add((string)c!["name"]!);
        }

        return data;
    }

    /// <summary>A station's copy as an intake makes it: the BBS's fields and its exact message text.</summary>
    private static Bulletin Intake(VectorInput i) =>
        Bulletin.FromMessageText(i.Type, i.From, i.To, i.At, i.Bid, i.TitleText, new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), Bulletin.TextEncoding.GetString(i.MessageText));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void VectorInput_GoesOnAirAsTheFileSays(string name)
    {
        var node = Cases.Single(c => (string)c!["name"]! == name)!;
        var expected = node["expected"]!;
        var input = VectorInput.FromJson(node);
        // Signed, as GB7RDG is: the vector line carries hash=. The file has a body over 128 KB.
        var options = new ScheduleOptions { MaxBulletinSize = int.MaxValue, Signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem()) };
        var store = new HeadEndStore(_root, Compression.Default, options);
        var carried = store.Offer(Intake(input), Day);
        Assert.NotNull(carried);

        Assert.Equal((string)expected["bulletinDictionary1"]!["objectHex"]!, Convert.ToHexStringLower(carried.Transfer.Bytes));
        Assert.NotNull(carried.Envelope);
        Assert.Equal((string)expected["envelopeObjectDictionary1"]!["objectHex"]!, Convert.ToHexStringLower(carried.Envelope.Bytes));
        Assert.Equal((string)expected["bulletinDictionary1"]!["otiHex"]!, Convert.ToHexStringLower(carried.Transfer.Oti.ToBytes()));

        var plan = BroadcastScheduler.Plan(store.InRotation(Day), Day, 1, Compression.Default, options, store.DirectoryNextEsi);
        string line = Bulletin.TextEncoding.GetString(plan.Directory.Serialize()).Split('\n')[2];
        // On air the first entry line also carries the station, slots and similar fields, so the literal vector line
        // applies to later entries; here it is the only entry of a signed directory, with just key= and sig= after it.
        int key = line.IndexOf("\tkey=", StringComparison.Ordinal);
        Assert.True(key > 0, "the signed line has key=");
        Assert.Equal((string)expected["directoryEntryLine"]!, line[..key]);
        var after = line[(key + 1)..].Split('\t');
        Assert.Equal(2, after.Length);
        Assert.StartsWith("key=", after[0], StringComparison.Ordinal);
        Assert.StartsWith("sig=", after[1], StringComparison.Ordinal);

        // The frames on air: the envelope's first frame is the file's, and so is the bulletin's if it is sent.
        var envelopeFrame = plan.Frames.Single(f => f.ObjectId == carried.Envelope.ObjectId && f.EncodingSymbolId == 0);
        Assert.Equal(FileFrame(expected["envelopeObjectDictionary1"]!, 0), Convert.ToHexStringLower(envelopeFrame.ToBytes()));
        var first = plan.Frames.Single(f => f.ObjectId == carried.Transfer.ObjectId && f.EncodingSymbolId == 0);
        Assert.Equal(FileFrame(expected["bulletinDictionary1"]!, 0), Convert.ToHexStringLower(first.ToBytes()));
    }

    private static string FileFrame(JsonNode object_, uint esi) =>
        object_["frames"]!.AsArray().Select(f => (string)f!).Single(f => f.StartsWith($"{esi}:", StringComparison.Ordinal))[(esi.ToString().Length + 1)..];

    [Fact]
    public void EveryObject_IsCutAtTheFixedPieceSize_TheDirectoryToo()
    {
        var store = new HeadEndStore(_root, Compression.Default, new ScheduleOptions());
        var carried = store.Offer(TestBulletins.Make(1, 3000), Day)!;
        var plan = BroadcastScheduler.Plan(store.InRotation(Day), Day, 1, Compression.Default, new ScheduleOptions(), store.DirectoryNextEsi);
        Assert.All(plan.Objects, o => Assert.Equal(MailcastFrame.FixedSymbolSize, o.Transfer.Oti.SymbolSize));
        Assert.All(plan.Objects, o => Assert.Equal(MailcastFrame.StandardAlignment, o.Transfer.Oti.Alignment));
        Assert.Equal(MailcastFrame.FixedSymbolSize, carried.Envelope!.Oti.SymbolSize);
    }

    [Fact]
    public void TheEnvelope_IsSentInEverySlotThatCarriesItsBulletin_WithFreshEsis()
    {
        var options = ScheduleOptions.Hourly;
        var store = new HeadEndStore(_root, Compression.Default, options);
        var carried = store.Offer(TestBulletins.Make(2, 2000), Day)!;
        var seen = new HashSet<uint>();
        int slotsWithBulletin = 0;
        for (int h = 0; h < 12; h++)
        {
            var slot = BroadcastScheduler.Midnight(Day).AddHours(9 + h);
            var plan = BroadcastScheduler.Plan(store.InRotation(slot), slot, 7, Compression.Default, options, store.DirectoryNextEsi);
            bool carries = plan.Frames.Any(f => f.ObjectId == carried.Transfer.ObjectId);
            var envelopeFrames = plan.Frames.Where(f => f.ObjectId == carried.Envelope!.ObjectId).ToList();
            if (carries)
            {
                slotsWithBulletin++;
                Assert.Equal(carried.Envelope!.SourceSymbols, envelopeFrames.Count);
            }
            else
            {
                Assert.Empty(envelopeFrames);
            }
            Assert.All(envelopeFrames, f => Assert.True(seen.Add(f.EncodingSymbolId), "an envelope ESI was repeated"));
            Assert.Equal(carries ? carried.Envelope!.SourceSymbols : 0, plan.BulletinFrames == 0 ? 0 : envelopeFrames.Count);
            store.Commit(plan);
        }
        Assert.True(slotsWithBulletin >= 2);
        Assert.Equal(slotsWithBulletin * carried.Envelope!.SourceSymbols, seen.Count);
    }

    [Fact]
    public void EnvelopeAndItsNextEsi_SurviveARestart_AndAHeldBulletinWithoutOneStaysSo()
    {
        var options = new ScheduleOptions();
        var store = new HeadEndStore(_root, Compression.Default, options);
        var first = store.Offer(TestBulletins.Make(3, 1500), Day)!;
        var plan = BroadcastScheduler.Plan(store.InRotation(Day), Day, 1, Compression.Default, options, store.DirectoryNextEsi);
        store.Commit(plan);

        // A bulletin held from before: object only, no envelope, its encoding as first made (here at 940).
        var old = TestBulletins.Make(4, 1500);
        var oldObject = TransferObject.ForBulletin(old, options.DictionaryId, Compression.Default, MailcastFrame.StandardSymbolSize);
        string oldDir = Path.Combine(_root, "bulletins", "00000000000000aa");
        Directory.CreateDirectory(oldDir);
        File.WriteAllBytes(Path.Combine(oldDir, "object.bin"), oldObject.Bytes.ToArray());
        File.WriteAllText(Path.Combine(oldDir, "state.txt"),
            $"Bid: {old.Bid}\nTitle: {old.Title}\nSize: 100\nFirstSeen: 2026-10-30\nObject: {ObjectId.Format(oldObject.ObjectId)}\nDictionary: {oldObject.DictionaryId}\nOti: {Convert.ToHexStringLower(oldObject.Oti.ToBytes())}\nNextEsi: 0\n");

        var reopened = new HeadEndStore(_root, Compression.Default, options);
        var held = reopened.InRotation(Day);
        var again = held.Single(c => c.Bid == first.Bid);
        Assert.Equal(first.Envelope!.ObjectId, again.Envelope!.ObjectId);
        Assert.Equal((uint)first.Envelope.SourceSymbols, again.EnvelopeNextEsi);
        var legacy = held.Single(c => c.Bid == old.Bid);
        Assert.Null(legacy.Envelope);
        Assert.Equal(MailcastFrame.StandardSymbolSize, legacy.Transfer.Oti.SymbolSize);

        var tomorrow = Day.AddDays(1);
        var plan2 = BroadcastScheduler.Plan(reopened.InRotation(tomorrow), tomorrow, 1, Compression.Default, options, reopened.DirectoryNextEsi);
        Assert.DoesNotContain(plan2.Objects, o => o.EnvelopeFor == old.Bid);
        Assert.Contains(plan2.Objects, o => o.EnvelopeFor == first.Bid && o.FirstEsi == again.EnvelopeNextEsi);
    }

    [Fact]
    public void BudgetRule_CountsEnvelopeFramesInTheAirtime()
    {
        var options = ScheduleOptions.HourlyBudget;
        var store = new HeadEndStore(_root, Compression.Default, options);
        for (int i = 0; i < 6; i++)
        {
            store.Offer(TestBulletins.Make(10 + i, 2500), Day);
        }
        var slot = BroadcastScheduler.Midnight(Day).AddHours(10);
        var budget = new SlotBudget(TimeSpan.FromSeconds(40), lengths => TimeSpan.FromSeconds(lengths.Count));
        var plan = BroadcastScheduler.Plan(store.InRotation(slot), slot, 3, Compression.Default, options, store.DirectoryNextEsi, budget);
        Assert.True(plan.Frames.Count <= 40, $"{plan.Frames.Count} frames in a 40 frame budget");
        int envelopes = plan.Objects.Count(o => o.EnvelopeFor is not null);
        Assert.Equal(plan.Objects.Count(o => o.Bid is not null && o.Count > 0), envelopes);
        Assert.Equal(plan.Frames.Count, plan.Objects.Sum(o => o.Count));
    }

    [Fact]
    public void TwoBulletinsWithTheSameEnvelope_ShareItsObject_SentOncePerSlot_AndNoEsiTwice()
    {
        var options = new ScheduleOptions();
        var store = new HeadEndStore(_root, Compression.Default, options);
        Bulletin Same(int n) => Bulletin.FromMessageText('B', "G4ABC", "ALL", "WW", $"{n}_TEST", "Same title", new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero),
            $"R:261019/0850Z 1@GB7AAA.#1.GBR.EURO LinBPQ6.0.25\r\n\r\nBody number {n}.\r\n");
        var a = store.Offer(Same(1), Day)!;
        var b = store.Offer(Same(2), Day)!;
        Assert.Equal(a.Envelope!.ObjectId, b.Envelope!.ObjectId);

        var seen = new HashSet<uint>();
        for (int day = 0; day < 3; day++)
        {
            var today = Day.AddDays(day);
            var plan = BroadcastScheduler.Plan(store.InRotation(today), today, 5, Compression.Default, options, store.DirectoryNextEsi);
            Assert.Single(plan.Objects, o => o.EnvelopeFor is not null);
            foreach (var f in plan.Frames.Where(f => f.ObjectId == a.Envelope.ObjectId))
            {
                Assert.True(seen.Add(f.EncodingSymbolId), "an envelope ESI was sent twice");
            }
            store.Commit(plan);
            var held = store.InRotation(today);
            Assert.Equal(held[0].EnvelopeNextEsi, held[1].EnvelopeNextEsi);
        }
        Assert.Equal(3 * a.Envelope.SourceSymbols, seen.Count);
        Assert.Equal((uint)seen.Count, new HeadEndStore(_root, Compression.Default, options).InRotation(Day.AddDays(2))[0].EnvelopeNextEsi);
    }

    [Fact]
    public void AStateNamingAnEnvelopeWhoseFileIsMissing_IsSentWithoutIt_AndSaysSo()
    {
        var options = new ScheduleOptions();
        var log = new List<string>();
        var carried = new HeadEndStore(_root, Compression.Default, options).Offer(TestBulletins.Make(6, 1500), Day)!;
        File.Delete(Directory.EnumerateFiles(_root, "envelope.bin", SearchOption.AllDirectories).Single());
        var again = new HeadEndStore(_root, Compression.Default, options, log.Add).InRotation(Day).Single();
        Assert.Null(again.Envelope);
        Assert.Equal(carried.Transfer.ObjectId, again.Transfer.ObjectId);
        Assert.Contains(log, l => l.Contains("envelope of", StringComparison.Ordinal));
    }

    [Fact]
    public void ASignedHeadEnd_ToAReceiver_DeliversTheStationsCopyUnderTheReceiversOwnRoutingLine()
    {
        var signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        var options = new ScheduleOptions { Signer = signer, Station = "ZZ1AAA" };
        var store = new HeadEndStore(_root, Compression.Default, options);
        var bulletins = new[] { TestBulletins.Make(31, 1200), TestBulletins.Make(32, 2500), TestBulletins.Make(33, 90) };
        foreach (var b in bulletins)
        {
            store.Offer(b, Day);
        }
        var plan = BroadcastScheduler.Plan(store.InRotation(Day), Day, 1, Compression.Default, options, store.DirectoryNextEsi);
        using var rx = new TempDirectory();
        var receiver = new ReceiverStore(rx.Path, Compression.Default,
            TestStores.Fast with { TrustedKeys = [TrustedKey.Parse("ZZ1AAA", signer.PublicKeyBase64)], DeliveryCall = () => "M0LTE-1" });
        foreach (var frame in plan.Frames)
        {
            receiver.Accept(frame.ToBytes(), "ZZ1AAA");
        }
        var delivered = receiver.Pending().OrderBy(d => d.Bid).ToList();
        Assert.Equal(bulletins.Length, delivered.Count);
        foreach (var (got, original) in delivered.Zip(bulletins.OrderBy(b => b.Bid)))
        {
            Assert.Equal(original.Title, got.Title);
            Assert.Matches(@"^R:\d{6}/\d{4}Z @:M0LTE-1 \[pdn-mailcast via ZZ1AAA\]\r\n", got.MessageText);
            Assert.Equal(original.MessageText, got.MessageText[(got.MessageText.IndexOf("\r\n", StringComparison.Ordinal) + 2)..]);
        }
    }
}
