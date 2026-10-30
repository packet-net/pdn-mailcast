using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace Packet.Mailcast.Tests;

/// <summary>Signed directories: the format, what old receivers make of it, and keys.</summary>
public class SignedDirectoryTests(ITestOutputHelper output)
{
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly DirectorySigner Signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly TrustedKey Trusted = TrustedKey.Parse("TEST", Signer.PublicKeyBase64);

    private static (BroadcastDirectory Unsigned, BroadcastDirectory Signed) Pair(int count, int seed = 1)
    {
        var objects = TestBulletins.Day(seed, count).Select(b => (Bulletin: b, Object: TransferObject.ForBulletin(b, ZstdDictionary.Gb7rdg1Id, Compression.Default))).ToList();
        var timetable = new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);
        var plain = objects.Select(o => new DirectoryEntry(o.Object.ObjectId, o.Object.DictionaryId, o.Bulletin.Serialize().Length, o.Bulletin.Bid, o.Bulletin.Title)).ToList();
        var hashed = objects.Select((o, i) => plain[i] with { Hash = ObjectId.LongHash(o.Object.DictionaryId, o.Object.Bytes) }).ToList();
        return (new BroadcastDirectory(Day, plain, timetable, "ms110d-wn3"), new BroadcastDirectory(Day, hashed, timetable, "ms110d-wn3", Signer));
    }

    [Fact]
    public void Signed_RoundTrips_AndChecksAgainstItsKey()
    {
        var (_, signed) = Pair(5);
        byte[] bytes = signed.Serialize();
        string text = Bulletin.TextEncoding.GetString(bytes);
        string last = text.Split('\n')[^2];
        Assert.Contains($"\tkey={KeyId.Format(Signer.KeyId)}\tsig=", last, StringComparison.Ordinal);
        Assert.All(text.Split('\n')[2..^1], line => Assert.Matches("\thash=[0-9a-f]{16}(\t|$)", line));

        var parsed = BroadcastDirectory.Parse(bytes);
        Assert.Equal(bytes, parsed.Serialize()); // exactly the octets read, so the signature still checks
        Assert.Equal(signed.Entries, parsed.Entries);
        Assert.Equal(signed.Schedule, parsed.Schedule);
        Assert.Equal(signed.Mode, parsed.Mode);
        Assert.True(parsed.IsSigned);
        Assert.Equal(new DirectoryCheck(SignatureState.Trusted, SignedBy: "TEST"), parsed.Check([TrustedKey.Gb7rdg, Trusted]));
        Assert.Equal("signed by TEST", parsed.Check([Trusted]).Describe());
        Assert.Equal(signed.Check([Trusted]), parsed.Check([Trusted]));
    }

    [Fact]
    public void Signing_IsDeterministic_SoTheSameDirectoryIsTheSameObject()
    {
        var (_, a) = Pair(4);
        var (_, b) = Pair(4);
        Assert.Equal(a.Serialize(), b.Serialize());
        Assert.Equal(TransferObject.ForDirectory(a, ZstdDictionary.Gb7rdg1Id, Compression.Default).ObjectId,
            TransferObject.ForDirectory(b, ZstdDictionary.Gb7rdg1Id, Compression.Default).ObjectId);
    }

    [Fact]
    public void Unsigned_IsNotSigned_AndUnchangedInFormat()
    {
        var (plain, _) = Pair(3);
        Assert.False(plain.IsSigned);
        Assert.Equal(DirectoryCheck.NotSigned, BroadcastDirectory.Parse(plain.Serialize()).Check([Trusted]));
        Assert.Equal("not signed", DirectoryCheck.NotSigned.Describe());
        Assert.DoesNotContain("hash=", Bulletin.TextEncoding.GetString(plain.Serialize()), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_HasNowhereForASignature_SoGoesOutUnsigned()
    {
        var empty = new BroadcastDirectory(Day, [], signer: Signer);
        Assert.False(empty.IsSigned);
        Assert.Equal("MAILCAST DIRECTORY 1\n2026-10-15\n", Bulletin.TextEncoding.GetString(empty.Serialize()));
    }

    /// <summary>
    /// Receivers 0.8.0 to 0.13.0, reading through their own code (copied in <see cref="V0130Reader"/>),
    /// get exactly the same entries, timetable and mode from a signed directory as from the same
    /// directory unsigned, and the object is still content type 2 with no metadata block.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(25)]
    public void OldReceivers_ReadASignedDirectory_ExactlyAsTheUnsignedOne(int count)
    {
        var (plain, signed) = Pair(count, seed: 40 + count);
        var plainObject = TransferObject.ForDirectory(plain, ZstdDictionary.Gb7rdg1Id, Compression.Default, 240);
        var signedObject = TransferObject.ForDirectory(signed, ZstdDictionary.Gb7rdg1Id, Compression.Default, 240);
        Assert.Equal((byte)ObjectKind.Directory, signedObject.Bytes[0]); // no metadata bit: the type octet old receivers want

        var old = V0130Reader.ReadDirectoryObject(signedObject.Bytes, signedObject.DictionaryId, Compression.Default);
        var oldPlain = V0130Reader.ReadDirectoryObject(plainObject.Bytes, plainObject.DictionaryId, Compression.Default);
        Assert.Equal(oldPlain.Date, old.Date);
        Assert.Equal(oldPlain.Entries, old.Entries);
        Assert.Equal(plain.Entries, old.Entries);
        Assert.Equal(oldPlain.Schedule, old.Schedule);
        Assert.NotNull(old.Schedule);
        Assert.Equal("ms110d-wn3", old.Mode);

        // And the v0.2.0 reader, the oldest still around, likewise.
        var v020 = V020Reader.ParseDirectory(V020Reader.Unpack(signedObject.Bytes, signedObject.DictionaryId, Compression.Default).Content);
        Assert.Equal(plain.Entries.Select(e => (e.ObjectId, e.DictionaryId, e.Size, e.Bid, e.Title)), v020.Entries);
    }

    [Fact]
    public void ChangedContent_FailsItsCheck()
    {
        var (_, signed) = Pair(3);
        string text = Bulletin.TextEncoding.GetString(signed.Serialize());
        string title = signed.Entries[1].Title;
        var tampered = BroadcastDirectory.Parse(Bulletin.TextEncoding.GetBytes(text.Replace(title, title + "!", StringComparison.Ordinal)));
        var check = tampered.Check([Trusted]);
        Assert.Equal(SignatureState.Rejected, check.State);
        Assert.False(check.Usable);
        Assert.Contains("does not match its content", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherKey_OrAnUnreadableSignature_IsRejected_WithWhy()
    {
        var (_, signed) = Pair(2);
        var unknown = signed.Check([TrustedKey.Gb7rdg]);
        Assert.Equal(SignatureState.Rejected, unknown.State);
        Assert.Equal($"it is signed by key {KeyId.Format(Signer.KeyId)}, which is not a trusted key", unknown.Reason);

        string text = Bulletin.TextEncoding.GetString(signed.Serialize());
        string garbled = text[..text.LastIndexOf("sig=", StringComparison.Ordinal)] + "sig=nonsense\n";
        Assert.Equal("its signature is not readable", BroadcastDirectory.Parse(Bulletin.TextEncoding.GetBytes(garbled)).Check([Trusted]).Reason);
        string keyless = text.Replace("key=", "kex=", StringComparison.Ordinal);
        Assert.Equal("its signature does not say which key made it", BroadcastDirectory.Parse(Bulletin.TextEncoding.GetBytes(keyless)).Check([Trusted]).Reason);
        Assert.StartsWith("rejected: ", unknown.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Gb7rdgsKey_IsBuiltIn_FromItsSubjectPublicKeyInfo()
    {
        Assert.Equal("GB7RDG", TrustedKey.BuiltIn[0].Label);
        Assert.Same(TrustedKey.Gb7rdg, TrustedKey.BuiltIn[0]);
        Assert.Equal("6f39d04c925c01cf", KeyId.Format(TrustedKey.Gb7rdg.KeyId));
        Assert.Equal("e1ZU+I6EO97joFIDmTGCdGkpAi+CNlfiUe/nJJKlIGo=", Convert.ToBase64String(TrustedKey.Gb7rdg.PublicKey));
        // The raw 32 octets, as the head end logs them, are the same key.
        Assert.Equal(TrustedKey.Gb7rdg.KeyId, TrustedKey.Parse("again", "e1ZU+I6EO97joFIDmTGCdGkpAi+CNlfiUe/nJJKlIGo=").KeyId);
    }

    [Theory]
    [InlineData("TEST", "not base64!", "not base64")]
    [InlineData("TEST", "AAAA", "3 octets")]
    [InlineData("", "e1ZU+I6EO97joFIDmTGCdGkpAi+CNlfiUe/nJJKlIGo=", "label")]
    public void TrustedKey_SaysWhatIsWrong(string label, string key, string expected)
    {
        var e = Assert.Throws<FormatException>(() => TrustedKey.Parse(label, key));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Signer_ReadsOpensslsPem_AndSaysWhatIsWrongWithAnythingElse()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "signing-key.pem");
        Assert.Null(DirectorySigner.LoadIfPresent(path));

        File.WriteAllText(path, DirectorySigner.NewKeyPem());
        var signer = DirectorySigner.LoadIfPresent(path)!;
        Assert.Equal(32, signer.PublicKey.Length);
        Assert.Equal(KeyId.Of(signer.PublicKey), signer.KeyId);
        Assert.True(TrustedKey.Parse("me", signer.PublicKeyBase64).Verifies("hello"u8, signer.Sign("hello"u8)));
        Assert.False(TrustedKey.Parse("me", signer.PublicKeyBase64).Verifies("hellO"u8, signer.Sign("hello"u8)));

        File.WriteAllText(path, "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEAe1ZU+I6EO97joFIDmTGCdGkpAi+CNlfiUe/nJJKlIGo=\n-----END PUBLIC KEY-----\n");
        Assert.Contains("not a PEM private key", Assert.Throws<FormatException>(() => DirectorySigner.LoadIfPresent(path)).Message, StringComparison.Ordinal);
        File.WriteAllText(path, "nonsense");
        Assert.Contains(path, Assert.Throws<FormatException>(() => DirectorySigner.LoadIfPresent(path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduler_SignsWithItsKey_AndListsEachLongHash()
    {
        var bulletins = TestBulletins.Day(9, 6).Select(b => new BroadcastBulletin(b, Day)).ToList();
        var unsigned = BroadcastScheduler.Plan(bulletins, Day, 1, Compression.Default);
        var plan = BroadcastScheduler.Plan(bulletins, Day, 1, Compression.Default, new ScheduleOptions { Signer = Signer });
        Assert.False(unsigned.Directory.IsSigned);
        Assert.All(unsigned.Directory.Entries, e => Assert.Null(e.Hash));
        Assert.True(plan.Directory.IsSigned);
        Assert.Equal(SignatureState.Trusted, plan.Directory.Check([Trusted]).State);
        foreach (var o in plan.Objects.Where(o => o.Bid is not null))
        {
            Assert.Equal(ObjectId.LongHash(o.Transfer.DictionaryId, o.Transfer.Bytes), plan.Directory.Find(o.Transfer.ObjectId)!.Hash);
            // Octets 8 to 15 of the SHA-256: the ID, on the same line, is octets 0 to 7.
            Assert.DoesNotContain(ObjectId.Format(o.Transfer.ObjectId), plan.Directory.Find(o.Transfer.ObjectId)!.Hash, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What signing costs on the air, for a typical 25-bulletin rotation at GB7RDG's 240-octet
    /// pieces: the hashes and the signature add about 370 octets after compression, two
    /// more source symbols, and so (with issue #69's floor, about five frames a symbol) about ten
    /// more directory frames a slot. Kept as a guard, so a later change that makes it much worse shows.
    /// </summary>
    [Theory]
    [InlineData(15, 1)]
    [InlineData(25, 2)]
    public void Airtime_SigningAddsLittleToTheDirectory(int count, int mostExtraSymbols)
    {
        var (plain, signed) = Pair(count, seed: 77);
        var a = TransferObject.ForDirectory(plain, ZstdDictionary.Gb7rdg1Id, Compression.Default, 240);
        var b = TransferObject.ForDirectory(signed, ZstdDictionary.Gb7rdg1Id, Compression.Default, 240);
        int floorBefore = Math.Max(a.SourceSymbols + 2, BroadcastScheduler.DirectoryFloor(a.SourceSymbols));
        int floorAfter = Math.Max(b.SourceSymbols + 2, BroadcastScheduler.DirectoryFloor(b.SourceSymbols));
        output.WriteLine($"{count} bulletins: {a.Length} -> {b.Length} octets on the air (+{b.Length - a.Length}), K {a.SourceSymbols} -> {b.SourceSymbols}, directory frames a slot at least {floorBefore} -> {floorAfter}");
        Assert.InRange(b.SourceSymbols - a.SourceSymbols, 0, mostExtraSymbols);
        Assert.InRange(b.Length - a.Length, 64, 20 * count + 120);
    }
}

/// <summary>What a receiver does with signed, unsigned and rejected directories.</summary>
public class SignedDirectoryStoreTests
{
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly DirectorySigner Signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly DirectorySigner Stranger = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly IReadOnlyList<TrustedKey> Keys = [TrustedKey.Gb7rdg, TrustedKey.Parse("TEST", Signer.PublicKeyBase64)];

    private static ReceiverStore Open(string path, FakeTimeProvider time, List<string> log) =>
        new(path, Compression.Default, TestStores.Fast with { Time = time, Log = log.Add, TrustedKeys = Keys });

    private static TransferObject Small(int seed) => TransferObject.ForBulletin(TestBulletins.Make(seed, 400), ZstdDictionary.Gb7rdg1Id, Compression.Default);

    private static TransferObject Big(int seed)
    {
        var obj = TransferObject.ForBulletin(TestBulletins.Make(seed, 9000), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.True(obj.SourceSymbols > 1);
        return obj;
    }

    private static DirectoryEntry Entry(TransferObject o, int i, string? hash = null) =>
        new(o.ObjectId, o.DictionaryId, 9000, $"{i + 1}_TEST", $"Bulletin {i + 1}", Hash: hash ?? ObjectId.LongHash(o.DictionaryId, o.Bytes));

    private static List<MailcastFrame> DirectoryFrames(DirectorySigner? signer, params TransferObject[] listed) =>
        DirectoryFrames(new BroadcastDirectory(Day, listed.Select((o, i) => Entry(o, i)), signer: signer));

    private static List<MailcastFrame> DirectoryFrames(BroadcastDirectory directory)
    {
        var obj = TransferObject.ForDirectory(directory, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        return [.. Enumerable.Range(0, obj.SourceSymbols).Select(i => obj.Frame((uint)i))];
    }

    private static AcceptResult FeedAll(ReceiverStore store, IEnumerable<MailcastFrame> frames)
    {
        AcceptResult last = null!;
        foreach (var f in frames)
        {
            last = store.Accept(f.ToBytes());
        }
        return last;
    }

    private static AcceptResult FeedObject(ReceiverStore store, TransferObject o) =>
        FeedAll(store, Enumerable.Range(0, o.SourceSymbols).Select(i => o.Frame((uint)i)));

    [Fact]
    public void TrustedSigned_DeliversAsUsual_AndSaysSignedBy()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        var store = Open(dir.Path, time, log);
        var a = Small(1);
        var result = FeedAll(store, DirectoryFrames(Signer, a));
        Assert.Equal(FrameOutcome.CompletedDirectory, result.Outcome);
        Assert.Equal("signed by TEST", result.Check!.Describe());
        Assert.Equal(new HeardDirectoryCheck(Day, result.Check), store.LastDirectoryHeard);
        Assert.Equal(SignatureState.Trusted, store.NewestCheck!.State);

        Assert.Equal(FrameOutcome.CompletedBulletin, FeedObject(store, a).Outcome);
        Assert.Single(store.Pending());
        var row = Assert.Single(store.HeldBulletins());
        Assert.Equal("signed by TEST", row.Signature);
        Assert.False(row.Withheld);
    }

    /// <summary>The real default: an unsigned directory is not used (issue #146).</summary>
    private static ReceiverStore OpenStrict(string path, FakeTimeProvider time, List<string> log) =>
        new(path, Compression.Default, new ReceiverStoreOptions { FlushToDisk = false, Time = time, Log = log.Add, TrustedKeys = Keys });

    [Fact]
    public void Unsigned_IsUsedForNothing_SaidOnce_AndWhatOnlyItListsIsHeldUntilASignedOneListsIt()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        var store = OpenStrict(dir.Path, time, log);
        var a = Small(2);
        var timetable = new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);
        var result = FeedAll(store, DirectoryFrames(new BroadcastDirectory(Day, [Entry(a, 0)], schedule: timetable)));
        Assert.Equal(FrameOutcome.RejectedDirectory, result.Outcome);
        Assert.Equal("unsigned, not used", result.Check!.Describe());
        Assert.False(result.Check.Usable);
        Assert.Null(store.Directory);
        Assert.Null(store.HeardSchedule); // the config's slots stand, as with no directory heard
        Assert.Empty(store.StationDirectories());
        Assert.Equal("unsigned, not used", store.LastDirectoryHeard!.Check.Describe());

        // Said once, however many unsigned directories follow.
        FeedAll(store, DirectoryFrames(new BroadcastDirectory(Day.AddDays(1), [Entry(a, 0)], schedule: timetable)));
        var said = log.Where(l => l.Contains("no signature", StringComparison.Ordinal)).ToList();
        Assert.Single(said);
        Assert.All(said, l => Assert.All(l, c => Assert.InRange(c, ' ', '~')));

        // The bulletin only it lists is held, as under a bad signature.
        Assert.Equal(FrameOutcome.Withheld, FeedObject(store, a).Outcome);
        Assert.Empty(store.Pending());

        // A signed directory listing it lets it go.
        time.Advance(TimeSpan.FromMinutes(5));
        var good = FeedAll(store, DirectoryFrames(Signer, a));
        Assert.Equal(FrameOutcome.CompletedDirectory, good.Outcome);
        Assert.Single(good.Released!);
        Assert.Single(store.Pending());
    }

    [Fact]
    public void SignedListing_WithAnotherHash_IsNotDelivered()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        var store = Open(dir.Path, time, log);
        var a = Small(4);
        var other = Small(5);
        // A good signature, but over a hash that is not this object's: as if a forged object had
        // been made to match the short ID.
        var directory = new BroadcastDirectory(Day, [Entry(a, 0, ObjectId.LongHash(other.DictionaryId, other.Bytes))], signer: Signer);
        Assert.Equal(FrameOutcome.CompletedDirectory, FeedAll(store, DirectoryFrames(directory)).Outcome);

        var result = FeedObject(store, a);
        Assert.Equal(FrameOutcome.Rejected, result.Outcome);
        Assert.Contains("does not match the hash its signed directory lists for it", result.Detail, StringComparison.Ordinal);
        Assert.Empty(store.Pending());
        Assert.False(store.IsComplete(a.ObjectId));
        Assert.Contains(log, l => l.Contains("not delivered", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rejected_IsUsedForNothing_AndWhatOnlyItListsIsHeldUntilAGoodDirectoryListsIt(bool goodIsSigned)
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        var store = Open(dir.Path, time, log);
        var a = Small(6);
        var partial = Big(7);

        // A part-received bulletin, heard before the stranger's directory, which leaves it out.
        store.Accept(partial.Frame(0).ToBytes());
        time.Advance(TimeSpan.FromMinutes(5));
        var rejected = FeedAll(store, DirectoryFrames(Stranger, a));
        Assert.Equal(FrameOutcome.RejectedDirectory, rejected.Outcome);
        Assert.Null(rejected.Directory);
        Assert.Null(rejected.NoLongerSent); // the give-up rule ignores it
        Assert.Equal(SignatureState.Rejected, rejected.Check!.State);
        Assert.Null(store.Directory); // not the newest directory
        Assert.Equal(SignatureState.Rejected, store.LastDirectoryHeard!.Check.State);
        Assert.Contains($"which is not a trusted key", store.LastDirectoryHeard.Check.Reason, StringComparison.Ordinal);
        Assert.Single(log, l => l.Contains("not used: it is signed by key " + KeyId.Format(Stranger.KeyId), StringComparison.Ordinal));

        // Its repeats are said nothing more about, and still used for nothing.
        var repeat = FeedAll(store, DirectoryFrames(Stranger, a));
        Assert.Equal(FrameOutcome.AlreadyComplete, repeat.Outcome);
        Assert.Null(repeat.Directory);
        Assert.Null(repeat.NoLongerSent);
        Assert.Single(log, l => l.Contains("not used:", StringComparison.Ordinal));

        // The bulletin only it lists is held, complete, not delivered, and not named by it.
        var held = FeedObject(store, a);
        Assert.Equal(FrameOutcome.Withheld, held.Outcome);
        Assert.Empty(store.Pending());
        Assert.False(store.IsComplete(a.ObjectId));
        var row = Assert.Single(store.HeldBulletins(), h => h.ObjectId == a.ObjectId);
        Assert.True(row.Withheld);
        Assert.Null(row.Bid);
        Assert.Null(row.Signature);
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(a.Frame(5).ToBytes()).Outcome);
        Assert.Single(log, l => l.Contains("held, not delivered", StringComparison.Ordinal));

        // Held across a restart too.
        store = Open(dir.Path, time, log);
        Assert.Empty(store.Pending());
        Assert.Null(store.Directory);
        Assert.True(Assert.Single(store.HeldBulletins(), h => h.ObjectId == a.ObjectId).Withheld);

        // A good directory listing it lets it go.
        time.Advance(TimeSpan.FromMinutes(5));
        var good = FeedAll(store, DirectoryFrames(goodIsSigned ? Signer : null, a, partial));
        Assert.Equal(FrameOutcome.CompletedDirectory, good.Outcome);
        Assert.Equal(TestBulletins.Make(6, 400).Bid, Assert.Single(good.Released!).Bid);
        Assert.Single(store.Pending());
        Assert.True(store.IsComplete(a.ObjectId));
        Assert.Contains(log, l => l.Contains("held back, is now listed by a good directory: delivered", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejected_NeverMarksAnythingNoLongerSent_ButAGoodOneStillDoes()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var store = Open(dir.Path, time, []);
        var partial = Big(8);
        var other = Small(9);
        FeedAll(store, DirectoryFrames(Signer, partial));
        time.Advance(TimeSpan.FromMinutes(1));
        store.Accept(partial.Frame(0).ToBytes());
        time.Advance(TimeSpan.FromHours(1));

        // A forged directory of the same day, signed with a stranger's key, and one whose
        // signature was made by the trusted key but whose content was changed, both leave it out.
        Assert.Null(FeedAll(store, DirectoryFrames(Stranger, other)).NoLongerSent);
        string text = Bulletin.TextEncoding.GetString(new BroadcastDirectory(Day, [Entry(other, 0)], signer: Signer).Serialize());
        var tampered = BroadcastDirectory.Parse(Bulletin.TextEncoding.GetBytes(text.Replace("Bulletin 1", "Bulletin 2", StringComparison.Ordinal)));
        var result = FeedAll(store, DirectoryFrames(tampered));
        Assert.Equal(FrameOutcome.RejectedDirectory, result.Outcome);
        Assert.Contains("does not match its content", result.Check!.Reason, StringComparison.Ordinal);
        Assert.Null(result.NoLongerSent);
        Assert.All(store.HeldBulletins(), h => Assert.Null(h.NoLongerSentSince));

        // The trusted head end's own directory leaving it out does.
        Assert.Equal([partial.ObjectId], FeedAll(store, DirectoryFrames(Signer, other)).NoLongerSent);
    }

    [Fact]
    public void AKeyAddedLater_TrustsWhatWasRejected_AfterARestart()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var store = Open(dir.Path, time, []);
        var a = Small(10);
        Assert.Equal(FrameOutcome.RejectedDirectory, FeedAll(store, DirectoryFrames(Stranger, a)).Outcome);
        Assert.Equal(FrameOutcome.Withheld, FeedObject(store, a).Outcome);

        var trusting = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with
        {
            Time = time,
            TrustedKeys = [.. Keys, TrustedKey.Parse("FRIEND", Stranger.PublicKeyBase64)],
        });
        Assert.Single(trusting.Pending());
        Assert.Equal("signed by FRIEND", Assert.Single(trusting.HeldBulletins()).Signature);
    }

    /// <summary>
    /// A bulletin rebuilt before any directory listed it is delivered unchecked, as always. A
    /// trusted signed directory heard later only makes it "signed by" if its own kept hash matches:
    /// an object made to share only the 8-octet ID with a listed one says plainly that it does not.
    /// </summary>
    [Fact]
    public void SignedBy_IsOnlySaidOfABulletinWhoseOwnHashMatched_EvenOneDeliveredBeforeTheList()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var log = new List<string>();
        var store = Open(dir.Path, time, log);
        var good = Small(20);
        var forged = Small(21);
        Assert.Equal(FrameOutcome.CompletedBulletin, FeedObject(store, good).Outcome);
        Assert.Equal(FrameOutcome.CompletedBulletin, FeedObject(store, forged).Outcome);
        Assert.All(store.HeldBulletins(), h => Assert.Null(h.Signature)); // no directory names them yet

        // The signed list names both IDs, but gives "forged" the hash of another object, as if
        // it were the other half of a pair made to share an ID.
        var other = Small(22);
        var partial = Big(23);
        store.Accept(partial.Frame(0).ToBytes());
        var directory = new BroadcastDirectory(Day, [Entry(good, 0), Entry(forged, 1, ObjectId.LongHash(other.DictionaryId, other.Bytes)), Entry(partial, 2)], signer: Signer);
        FeedAll(store, DirectoryFrames(directory));
        FeedAll(store, DirectoryFrames(directory)); // a repeat says nothing more

        var rows = store.HeldBulletins().ToDictionary(h => h.ObjectId);
        Assert.Equal("signed by TEST", rows[good.ObjectId].Signature);
        Assert.Equal("TEST", rows[good.ObjectId].SignedBy);
        Assert.Equal("does not match TEST's signed list", rows[forged.ObjectId].Signature);
        Assert.Null(rows[forged.ObjectId].SignedBy);
        Assert.Single(log, l => l.Contains(ObjectId.Format(forged.ObjectId), StringComparison.Ordinal) && l.Contains("does not match the hash", StringComparison.Ordinal));

        // The hash is kept with the done marker, so a restart says the same.
        rows = Open(dir.Path, time, []).HeldBulletins().ToDictionary(h => h.ObjectId);
        Assert.Equal("signed by TEST", rows[good.ObjectId].Signature);
        Assert.Equal("does not match TEST's signed list", rows[forged.ObjectId].Signature);
        Assert.Equal("in TEST's signed list", Assert.Single(rows.Values, h => !h.Complete).Signature);

        // One rebuilt by a receiver from before signing has no hash kept: never "signed by".
        File.WriteAllText(Path.Combine(dir.Path, "done", ObjectId.Format(good.ObjectId)), time.GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("in TEST's signed list", Open(dir.Path, time, []).HeldBulletins().Single(h => h.ObjectId == good.ObjectId).Signature);
    }

    /// <summary>
    /// A held bulletin stays held across a restart even once the rejected directory that listed
    /// it is forgotten, and a good directory heard after the restart releases it with no new piece.
    /// </summary>
    [Fact]
    public void Held_SurvivesARestart_AndTheRejectedDirectoryBeingForgotten()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var store = Open(dir.Path, time, []);
        var a = Small(30);
        Assert.Equal(FrameOutcome.RejectedDirectory, FeedAll(store, DirectoryFrames(Stranger, a)).Outcome);
        Assert.Equal(FrameOutcome.Withheld, FeedObject(store, a).Outcome);
        Assert.True(File.Exists(Path.Combine(dir.Path, "held", ObjectId.Format(a.ObjectId))));

        time.Advance(ReceiverStore.KnownDirectoriesRetention + TimeSpan.FromHours(1));
        store = Open(dir.Path, time, []);
        Assert.Empty(store.Pending());
        Assert.True(Assert.Single(store.HeldBulletins()).Withheld);

        var released = FeedAll(store, DirectoryFrames(null, a));
        Assert.Equal(TestBulletins.Make(30, 400).Bid, Assert.Single(released.Released!).Bid);
        Assert.Single(store.Pending());
        Assert.False(File.Exists(Path.Combine(dir.Path, "held", ObjectId.Format(a.ObjectId))));
    }
}
