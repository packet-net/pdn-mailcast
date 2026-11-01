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

    private static TransferObject Small(int seed) => TransferObject.ForBulletin(TestBulletins.Make(seed, 400), ZstdDictionary.Dictionary1Id, Compression.Default);

    private static DirectoryEntry Entry(TransferObject bulletin, TransferObject? envelope) =>
        new(bulletin.ObjectId, bulletin.DictionaryId, 400, $"{bulletin.ObjectId % 100000}_TEST", "Title",
            Hash: ObjectId.LongHash(bulletin.DictionaryId, bulletin.Bytes),
            Envelope: envelope is null ? null : new EnvelopeLink(envelope.ObjectId, ObjectId.LongHash(envelope.DictionaryId, envelope.Bytes)));

    private static List<MailcastFrame> DirectoryFrames(DirectorySigner? signer, params DirectoryEntry[] entries)
    {
        var obj = TransferObject.ForDirectory(new BroadcastDirectory(Day, entries, signer: signer), ZstdDictionary.Dictionary1Id, Compression.Default);
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
        var obj = TransferObject.ForEnvelope(envelope, ZstdDictionary.Dictionary1Id, Compression.Default);
        Assert.Equal(ObjectKind.Envelope, obj.Kind);
        Assert.Equal((byte)5, obj.Bytes[0]);
        Assert.Equal(MailcastFrame.FixedSymbolSize, obj.Oti.SymbolSize);
        Assert.Equal(ZstdDictionary.Dictionary1Id, obj.DictionaryId);
        Assert.True(ContentType.IsKnown(5));

        var (kind, content) = TransferObject.Unpack(obj.Bytes, obj.DictionaryId, Compression.Default);
        Assert.Equal(ObjectKind.Envelope, kind);
        Assert.Equal(envelope, BulletinEnvelope.Parse(content));
        Assert.Equal(envelope.Serialise(), content);

        // Deterministic: the same envelope is the same object.
        Assert.Equal(obj.ObjectId, TransferObject.ForEnvelope(envelope, ZstdDictionary.Dictionary1Id, Compression.Default).ObjectId);
    }

    [Fact]
    public void ForEnvelope_IgnoresAnyOtherSymbolSize_ItHasNoParameterForOne()
    {
        var obj = TransferObject.ForEnvelope(MakeEnvelope(), ZstdDictionary.Dictionary1Id, Compression.Default);
        Assert.NotEqual(MailcastFrame.StandardSymbolSize, obj.Oti.SymbolSize);
    }

    // ---- The directory field ----

    [Fact]
    public void EnvField_IsOneFieldAfterTheHash_InsideTheSignature_AndRoundTrips()
    {
        var bulletin = Small(1);
        var envelope = TransferObject.ForEnvelope(MakeEnvelope(), ZstdDictionary.Dictionary1Id, Compression.Default);
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
        var ea = TransferObject.ForEnvelope(MakeEnvelope(3), ZstdDictionary.Dictionary1Id, Compression.Default);
        var eb = TransferObject.ForEnvelope(MakeEnvelope(4), ZstdDictionary.Dictionary1Id, Compression.Default);
        var timetable = new SlotTimetable(TimeOnly.MinValue, 60, StationFixture.Daylight);
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
        var eo = TransferObject.ForEnvelope(envelope, ZstdDictionary.Dictionary1Id, Compression.Default);

        Assert.Equal(FrameOutcome.CompletedDirectory, Feed(store, DirectoryFrames(Signer, Entry(bulletin, eo))).Outcome);
        var result = Feed(store, eo);
        Assert.Equal(FrameOutcome.CompletedEnvelope, result.Outcome);
        Assert.Equal((byte)5, result.ContentType);
        Assert.Equal(envelope, Assert.Single(store.EnvelopesFor(bulletin.ObjectId)).Envelope);
        Assert.Empty(store.EnvelopesFor(bulletin.ObjectId ^ 1));
        Assert.True(store.IsComplete(eo.ObjectId));
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(eo.Frame(0).ToBytes()).Outcome);
        Assert.Empty(store.Mail.NewestFirst);
        Assert.DoesNotContain(store.HeldBulletins(), h => h.ObjectId == eo.ObjectId);

        // Durable: a reopened store still has it, and the bulletin still delivers as always.
        var again = Open(dir.Path, time);
        Assert.Equal(envelope, Assert.Single(again.EnvelopesFor(bulletin.ObjectId)).Envelope);
        Assert.Equal(FrameOutcome.CompletedBulletin, Feed(again, bulletin).Outcome);
    }

    [Fact]
    public void AnEnvelopeHeardBeforeItsDirectory_IsHeldThenKept_AndNotShownAsABulletin()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = Open(dir.Path, Clock(), log);
        var bulletin = Small(6);
        var eo = TransferObject.ForEnvelope(MakeEnvelope(6), ZstdDictionary.Dictionary1Id, Compression.Default);

        Assert.Equal(FrameOutcome.Withheld, Feed(store, eo).Outcome);
        Assert.Empty(store.EnvelopesFor(bulletin.ObjectId));
        Assert.DoesNotContain(store.HeldBulletins(), h => h.ObjectId == eo.ObjectId);
        Assert.Single(log, l => l.StartsWith("envelope ", StringComparison.Ordinal) && l.Contains("held", StringComparison.Ordinal));

        var released = Feed(store, DirectoryFrames(Signer, Entry(bulletin, eo)));
        Assert.Equal(FrameOutcome.CompletedDirectory, released.Outcome);
        Assert.NotEmpty(store.EnvelopesFor(bulletin.ObjectId));
        Assert.True(store.IsComplete(eo.ObjectId));
        Assert.All(log, l => Assert.True(l.All(c => c < 128), l));
    }

    [Fact]
    public void AnEnvelopeWithTheWrongHash_OrListedOnlyByAnUntrustedOrUnsignedDirectory_IsNotKept()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var bulletin = Small(7);
        var eo = TransferObject.ForEnvelope(MakeEnvelope(7), ZstdDictionary.Dictionary1Id, Compression.Default);

        // Listed by a trusted directory with a different hash: refused.
        var wrong = Entry(bulletin, eo) with { Envelope = new EnvelopeLink(eo.ObjectId, "0000000000000000") };
        Feed(store, DirectoryFrames(Signer, wrong));
        Assert.Equal(FrameOutcome.Rejected, Feed(store, eo).Outcome);
        Assert.Empty(store.EnvelopesFor(bulletin.ObjectId));

        // Listed only by an untrusted key's directory, or an unsigned one: held, never kept.
        using var dir2 = new TempDirectory();
        var store2 = Open(dir2.Path, Clock());
        Feed(store2, DirectoryFrames(Stranger, Entry(bulletin, eo)));
        Assert.Equal(FrameOutcome.Withheld, Feed(store2, eo).Outcome);
        Assert.Empty(store2.EnvelopesFor(bulletin.ObjectId));
        Feed(store2, DirectoryFrames(null, Entry(bulletin, eo)));
        Assert.Empty(store2.EnvelopesFor(bulletin.ObjectId));
        Assert.False(store2.IsComplete(eo.ObjectId));
    }

    [Fact]
    public void KeptEnvelopes_AreRemovedAfterTheLongestRetention_AndCanComeBack()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = Open(dir.Path, time);
        var bulletin = Small(8);
        var eo = TransferObject.ForEnvelope(MakeEnvelope(8), ZstdDictionary.Dictionary1Id, Compression.Default);
        Feed(store, DirectoryFrames(Signer, Entry(bulletin, eo)));
        Feed(store, eo);
        Assert.NotEmpty(store.EnvelopesFor(bulletin.ObjectId));

        time.Advance(TimeSpan.FromDays(29));
        store.Expire();
        Assert.NotEmpty(store.EnvelopesFor(bulletin.ObjectId));
        time.Advance(TimeSpan.FromDays(2));
        store.Expire();
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "envelopes")));
        Assert.False(store.IsComplete(eo.ObjectId));
        Assert.Equal(FrameOutcome.CompletedEnvelope, Feed(store, eo).Outcome);
        Assert.NotEmpty(store.EnvelopesFor(bulletin.ObjectId));
    }

    [Fact]
    public void WithArchiveDaysZero_EnvelopesAreKeptForTheDoneRetention_ThenForgottenAndResendable()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time, TrustedKeys = [Trusted], ArchiveRetention = TimeSpan.Zero });
        var bulletin = Small(9);
        var eo = TransferObject.ForEnvelope(MakeEnvelope(9), ZstdDictionary.Dictionary1Id, Compression.Default);
        Feed(store, DirectoryFrames(Signer, Entry(bulletin, eo)));
        Feed(store, eo);

        time.Advance(TimeSpan.FromMinutes(61));
        store.Expire();
        Assert.NotEmpty(store.EnvelopesFor(bulletin.ObjectId));
        Assert.True(store.IsComplete(eo.ObjectId));

        time.Advance(TimeSpan.FromDays(15));
        store.Expire();
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "envelopes")));
        Assert.False(store.IsComplete(eo.ObjectId));
        Feed(store, DirectoryFrames(Signer, Entry(bulletin, eo)));
        Assert.Equal(FrameOutcome.CompletedEnvelope, Feed(store, eo).Outcome);
        Assert.NotEmpty(store.EnvelopesFor(bulletin.ObjectId));
    }

    [Fact]
    public void EnvelopesFor_ReturnsEveryHeldEnvelope_WithTheStationThatLinkedIt()
    {
        using var dir = new TempDirectory();
        var time = Clock();
        var signer2 = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Time = time, TrustedKeys = [Trusted, TrustedKey.Parse("TESTB", signer2.PublicKeyBase64)] });
        var bulletin = Small(10);
        var ea = TransferObject.ForEnvelope(MakeEnvelope(10), ZstdDictionary.Dictionary1Id, Compression.Default);
        var eb = TransferObject.ForEnvelope(MakeEnvelope(11), ZstdDictionary.Dictionary1Id, Compression.Default);
        Feed(store, DirectoryFrames(Signer, Entry(bulletin, ea)));
        Feed(store, DirectoryFrames(signer2, Entry(bulletin, eb)));

        Assert.Equal(FrameOutcome.CompletedEnvelope, Feed(store, eb).Outcome);
        var only = Assert.Single(store.EnvelopesFor(bulletin.ObjectId));
        Assert.Equal("TESTB", only.Station);
        Assert.Equal(MakeEnvelope(11), only.Envelope);

        Assert.Equal(FrameOutcome.CompletedEnvelope, Feed(store, ea).Outcome);
        var both = store.EnvelopesFor(bulletin.ObjectId);
        Assert.Equal(["TEST", "TESTB"], both.Select(b => b.Station));
        Assert.Equal([MakeEnvelope(10), MakeEnvelope(11)], both.Select(b => b.Envelope));
    }

    [Fact]
    public void AnEnvelopeRebuiltFromRepairPiecesAlone_IsNotListedAsABulletin()
    {
        using var dir = new TempDirectory();
        var store = Open(dir.Path, Clock());
        var eo = TransferObject.ForEnvelope(MakeEnvelope(12), ZstdDictionary.Dictionary1Id, Compression.Default);
        AcceptResult last = null!;
        for (int i = 0; i < eo.SourceSymbols + 3 && last?.Outcome != FrameOutcome.Withheld; i++)
        {
            last = store.Accept(eo.Frame((uint)(eo.SourceSymbols + 5 + i)).ToBytes());
        }
        Assert.True(last.Outcome == FrameOutcome.Withheld, last.Outcome.ToString());
        Assert.DoesNotContain(store.HeldBulletins(), h => h.ObjectId == eo.ObjectId);
    }

    [Fact]
    public void ReleasedReceiver_ReadsACanonicalBulletin_WithTheSameFields()
    {
        var (bulletin, _) = CanonicalForm.ToCanonicalBulletin('B', "G4ABC", "ALL", "WW", "13_TEST", "A title",
            Bulletin.TextEncoding.GetBytes("R:261019/0850Z 13@GB7AAA.#1.GBR.EURO LinBPQ6.0.25\r\n\r\nBody.\r\n"));
        byte[] bytes = bulletin.Serialize();
        Assert.Contains("Canonical: 1\n", Bulletin.TextEncoding.GetString(bytes), StringComparison.Ordinal);

        var (fields, text) = V0230BulletinParse(bytes);
        Assert.Equal(("B", "G4ABC", "ALL", "WW", "13_TEST", "A title"), (fields["Type"], fields["From"], fields["To"], fields["At"], fields["Bid"], fields["Title"]));
        Assert.Equal(bulletin.Date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), fields["Date"]);
        Assert.Equal(bulletin.MessageText, text);
    }

    // The header reading of v0.23.0's Bulletin.Parse, copied: it keeps every key, knows nothing of Canonical, and takes the rest as the message text.
    private static (Dictionary<string, string> Fields, string Text) V0230BulletinParse(ReadOnlySpan<byte> serialized)
    {
        string text = Bulletin.TextEncoding.GetString(serialized);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        int position = 0;
        while (true)
        {
            int end = text.IndexOf('\n', position);
            Assert.True(end >= 0, "The bulletin header does not end with an empty line.");
            string line = text[position..end];
            position = end + 1;
            if (line.Length == 0)
            {
                break;
            }
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            Assert.True(colon > 0, "Not a header line: " + line);
            int valueStart = colon + 1 < line.Length && line[colon + 1] == ' ' ? colon + 2 : colon + 1;
            Assert.True(fields.TryAdd(line[..colon], line[valueStart..]), "The header has a key twice.");
        }
        foreach (var key in new[] { "Type", "From", "To", "At", "Bid", "Date", "Title" })
        {
            Assert.True(fields.ContainsKey(key), "The header has no " + key);
        }
        return (fields, text[position..]);
    }
}
