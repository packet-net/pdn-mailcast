using Mailcast.RaptorQ;
using Microsoft.Extensions.Time.Testing;

namespace Packet.Mailcast.Tests;

/// <summary>Content type 5, the envelope object, its link from the signed directory (<c>env=</c>) and how a receiver keeps it (issue #142).</summary>
public class EnvelopeObjectTests
{
    private static readonly DateOnly Day = new(2026, 10, 30);
    private static readonly DirectorySigner Signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly DirectorySigner Stranger = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly TrustedKey Trusted = TrustedKey.Parse("TEST", Signer.PublicKeyBase64);

    private static BulletinEnvelope MakeEnvelope(int seed = 1) =>
        CanonicalForm.ToCanonicalBulletin('B', "G4ABC", "ALL", "WW", $"{seed}_TEST", "A title for envelope " + seed,
            Bulletin.TextEncoding.GetBytes($"R:261019/0850Z {seed}@GB7AAA.#1.GBR.EURO LinBPQ6.0.25\r\n\r\nBody {seed}.\r\n")).Envelope;

    private static TransferObject Small(int seed) => TransferObject.ForBulletin(TestBulletins.Make(seed, 400), ZstdDictionary.Gb7rdg1Id, Compression.Default);

    private static DirectoryEntry Entry(TransferObject bulletin, TransferObject? envelope) =>
        new(bulletin.ObjectId, bulletin.DictionaryId, 400, $"{bulletin.ObjectId % 100000}_TEST", "Title",
            Hash: ObjectId.LongHash(bulletin.DictionaryId, bulletin.Bytes),
            Envelope: envelope is null ? null : new EnvelopeLink(envelope.ObjectId, ObjectId.LongHash(envelope.DictionaryId, envelope.Bytes)));

    private static List<MailcastFrame> DirectoryFrames(DirectorySigner? signer, params DirectoryEntry[] entries)
    {
        var obj = TransferObject.ForDirectory(new BroadcastDirectory(Day, entries, signer: signer), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        return [.. Enumerable.Range(0, obj.SourceSymbols).Select(i => obj.Frame((uint)i))];
    }

    private static AcceptResult Feed(ReceiverStore store, IEnumerable<MailcastFrame> frames)
    {
        AcceptResult last = null!;
        foreach (var f in frames)
        {
            last = store.Accept(f.ToBytes());
        }
        return last;
    }

    private static AcceptResult Feed(ReceiverStore store, TransferObject o) => Feed(store, Enumerable.Range(0, o.SourceSymbols).Select(i => o.Frame((uint)i)));

    private static ReceiverStore Open(string path, FakeTimeProvider time, List<string>? log = null) =>
        new(path, Compression.Default, TestStores.Fast with { Time = time, TrustedKeys = [Trusted], Log = log is null ? null : log.Add });

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));

    // ---- The object ----

    [Fact]
    public void ForEnvelope_IsTypeFive_AtTheFixedPieceSize_AndUnpacksToTheEnvelope()
    {
        Assert.Equal(240, MailcastFrame.FixedSymbolSize);
        Assert.Equal(0, MailcastFrame.FixedSymbolSize % MailcastFrame.StandardAlignment);
        var envelope = MakeEnvelope();
        var obj = TransferObject.ForEnvelope(envelope, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(ObjectKind.Envelope, obj.Kind);
        Assert.Equal((byte)5, obj.Bytes[0]);
        Assert.Equal(MailcastFrame.FixedSymbolSize, obj.Oti.SymbolSize);
        Assert.Equal(ZstdDictionary.Gb7rdg1Id, obj.DictionaryId);
        Assert.True(ContentType.IsKnown(5));

        var (kind, content) = TransferObject.Unpack(obj.Bytes, obj.DictionaryId, Compression.Default);
        Assert.Equal(ObjectKind.Envelope, kind);
        Assert.Equal(envelope, BulletinEnvelope.Parse(content));
        Assert.Equal(envelope.Serialise(), content);

        // Deterministic: the same envelope is the same object.
        Assert.Equal(obj.ObjectId, TransferObject.ForEnvelope(envelope, ZstdDictionary.Gb7rdg1Id, Compression.Default).ObjectId);
    }

    [Fact]
    public void ForEnvelope_IgnoresAnyOtherSymbolSize_ItHasNoParameterForOne()
    {
        var obj = TransferObject.ForEnvelope(MakeEnvelope(), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.NotEqual(MailcastFrame.StandardSymbolSize, obj.Oti.SymbolSize);
    }

    // ---- The directory field ----

    [Fact]
    public void EnvField_IsOneFieldAfterTheHash_InsideTheSignature_AndRoundTrips()
    {
        var bulletin = Small(1);
        var envelope = TransferObject.ForEnvelope(MakeEnvelope(), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var entry = Entry(bulletin, envelope);
        var signed = new BroadcastDirectory(Day, [entry], signer: Signer);
        byte[] bytes = signed.Serialize();
        string line = Bulletin.TextEncoding.GetString(bytes).Split('\n')[2];
        string link = $"\tenv={ObjectId.Format(envelope.ObjectId)};{entry.Envelope!.Hash}\t";
        Assert.Contains($"\thash={entry.Hash}{link}key=", line, StringComparison.Ordinal);
        Assert.Matches("\tenv=[0-9a-f]{16};[0-9a-f]{16}\t", line);

        var parsed = BroadcastDirectory.Parse(bytes);
        Assert.Equal(entry, Assert.Single(parsed.Entries));
        Assert.Equal(bytes, parsed.Serialize());
        Assert.Equal(SignatureState.Trusted, parsed.Check([Trusted]).State);

        // Inside the signature: changing the link breaks it.
        string other = ObjectId.Format(envelope.ObjectId ^ 1);
        var tampered = Bulletin.TextEncoding.GetBytes(Bulletin.TextEncoding.GetString(bytes).Replace("env=" + ObjectId.Format(envelope.ObjectId), "env=" + other, StringComparison.Ordinal));
        Assert.Equal(SignatureState.Rejected, BroadcastDirectory.Parse(tampered).Check([Trusted]).State);
    }

    private static byte[] WithField(string directoryText, string field)
    {
        var lines = directoryText.Split('\n');
        lines[2] += "\t" + field;
        return Bulletin.TextEncoding.GetBytes(string.Join('\n', lines));
    }

    [Fact]
    public void AMalformedEnvField_IsIgnored_AndAnEntryWithoutOneIsUnchanged()
    {
        var bulletin = Small(2);
        var plain = new BroadcastDirectory(Day, [Entry(bulletin, null)]);
        string text = Bulletin.TextEncoding.GetString(plain.Serialize());
        Assert.DoesNotContain("env=", text, StringComparison.Ordinal);
        string good = new EnvelopeLink(0x0123456789abcdef, "fedcba9876543210").ToField();
        foreach (var bad in new[] { "env=", "env=0123456789abcdef", "env=0123456789abcdef;", "env=0123456789ABCDEF;fedcba9876543210", "env=0123456789abcdef;fedcba987654321", "env=0123456789abcdef;fedcba9876543210;x", "env=zzzzzzzzzzzzzzzz;fedcba9876543210" })
        {
            var parsed = BroadcastDirectory.Parse(WithField(text, bad));
            Assert.Null(Assert.Single(parsed.Entries).Envelope);
        }

        var ok = BroadcastDirectory.Parse(WithField(text, good));
        Assert.Equal(new EnvelopeLink(0x0123456789abcdef, "fedcba9876543210"), Assert.Single(ok.Entries).Envelope);
        Assert.Throws<ArgumentException>(() => new BroadcastDirectory(Day, [Entry(bulletin, null) with { Envelope = new EnvelopeLink(1, "nope") }]));
    }

    [Fact]
    public void ReleasedReaders_IgnoreEnv_AndTheSignatureStillChecks()
    {
        var a = Small(3);
        var b = Small(4);
        var ea = TransferObject.ForEnvelope(MakeEnvelope(3), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var eb = TransferObject.ForEnvelope(MakeEnvelope(4), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var timetable = new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);
        var directory = new BroadcastDirectory(Day, [Entry(a, ea), Entry(b, eb)], timetable, "ms110d-wn3", Signer);
        byte[] bytes = directory.Serialize();
        Assert.Contains("env=", Bulletin.TextEncoding.GetString(bytes), StringComparison.Ordinal);

        // v0.18.1's reader (V0181Directory, issue #185): the same entries without the link, the timetable, and the signature checks.
        var old = V0181Directory.Parse(bytes);
        var parsed = BroadcastDirectory.Parse(bytes);
        Assert.Equal(parsed.Entries.Select(e => e with { Envelope = null }), old.Entries);
        Assert.Equal(parsed.Schedule, old.Schedule);
        Assert.Equal(SignatureState.Trusted, old.Check([Trusted]).State);
        Assert.Equal(SignatureState.Trusted, parsed.Check([Trusted]).State);
    }

    // ---- The receiver ----

    [Fact]
    public void ATrustedDirectoryThatListsTheEnvelope_LetsTheReceiverKeepIt_AndNothingGoesToTheBbs()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir.Path, time);
        var bulletin = Small(5);
        var envelope = MakeEnvelope(5);
        var eo = TransferObject.ForEnvelope(envelope, ZstdDictionary.Gb7rdg1Id, Compression.Default);

        Assert.Equal(FrameOutcome.CompletedDirectory, Feed(store, DirectoryFrames(Signer, Entry(bulletin, eo))).Outcome);
        var result = Feed(store, eo);
        Assert.Equal(FrameOutcome.CompletedEnvelope, result.Outcome);
        Assert.Equal((byte)5, result.ContentType);
        Assert.Equal(envelope, store.EnvelopeFor(bulletin.ObjectId));
        Assert.Null(store.EnvelopeFor(bulletin.ObjectId ^ 1));
        Assert.True(store.IsComplete(eo.ObjectId));
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(eo.Frame(0).ToBytes()).Outcome);
        Assert.Empty(store.Mail.NewestFirst);
        Assert.DoesNotContain(store.HeldBulletins(), h => h.ObjectId == eo.ObjectId);

        // Durable: a reopened store still has it, and the bulletin still delivers as always.
        var again = Open(dir.Path, time);
        Assert.Equal(envelope, again.EnvelopeFor(bulletin.ObjectId));
        Assert.Equal(FrameOutcome.CompletedBulletin, Feed(again, bulletin).Outcome);
    }

    [Fact]
    public void AnEnvelopeHeardBeforeItsDirectory_IsHeldThenKept_AndNotShownAsABulletin()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = Open(dir.Path, Clock(), log);
        var bulletin = Small(6);
        var eo = TransferObject.ForEnvelope(MakeEnvelope(6), ZstdDictionary.Gb7rdg1Id, Compression.Default);

        Assert.Equal(FrameOutcome.Withheld, Feed(store, eo).Outcome);
        Assert.Null(store.EnvelopeFor(bulletin.ObjectId));
        Assert.DoesNotContain(store.HeldBulletins(), h => h.ObjectId == eo.ObjectId);
        Assert.Single(log, l => l.StartsWith("envelope ", StringComparison.Ordinal) && l.Contains("held", StringComparison.Ordinal));

        var released = Feed(store, DirectoryFrames(Signer, Entry(bulletin, eo)));
        Assert.Equal(FrameOutcome.CompletedDirectory, released.Outcome);
        Assert.NotNull(store.EnvelopeFor(bulletin.ObjectId));
        Assert.True(store.IsComplete(eo.ObjectId));
        Assert.All(log, l => Assert.True(l.All(c => c < 128), l));
    }

    [Fact]
    public void AnEnvelopeWithTheWrongHash_OrListedOnlyByAnUntrustedOrUnsignedDirectory_IsNotKept()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var bulletin = Small(7);
        var eo = TransferObject.ForEnvelope(MakeEnvelope(7), ZstdDictionary.Gb7rdg1Id, Compression.Default);

        // Listed by a trusted directory with a different hash: refused.
        var wrong = Entry(bulletin, eo) with { Envelope = new EnvelopeLink(eo.ObjectId, "0000000000000000") };
        Feed(store, DirectoryFrames(Signer, wrong));
        Assert.Equal(FrameOutcome.Rejected, Feed(store, eo).Outcome);
        Assert.Null(store.EnvelopeFor(bulletin.ObjectId));

        // Listed only by an untrusted key's directory, or an unsigned one: held, never kept.
        using var dir2 = new TempDirectory();
        var store2 = Open(dir2.Path, Clock());
        Feed(store2, DirectoryFrames(Stranger, Entry(bulletin, eo)));
        Assert.Equal(FrameOutcome.Withheld, Feed(store2, eo).Outcome);
        Assert.Null(store2.EnvelopeFor(bulletin.ObjectId));
        Feed(store2, DirectoryFrames(null, Entry(bulletin, eo)));
        Assert.Null(store2.EnvelopeFor(bulletin.ObjectId));
        Assert.False(store2.IsComplete(eo.ObjectId));
    }

    [Fact]
    public void KeptEnvelopes_AreRemovedWithTheArchiveRetention()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir.Path, time);
        var bulletin = Small(8);
        var eo = TransferObject.ForEnvelope(MakeEnvelope(8), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Feed(store, DirectoryFrames(Signer, Entry(bulletin, eo)));
        Feed(store, eo);
        Assert.NotNull(store.EnvelopeFor(bulletin.ObjectId));

        time.Advance(TimeSpan.FromDays(29));
        store.Expire();
        Assert.NotNull(store.EnvelopeFor(bulletin.ObjectId));
        time.Advance(TimeSpan.FromDays(2));
        store.Expire();
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "envelopes")));
    }
}
