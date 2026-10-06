using System.Text;
using Mailcast.RaptorQ;

namespace Packet.Mailcast.Tests;

/// <summary>
/// The content type octet and the metadata block, read by this version and by v0.2.0
/// (<see cref="V020Reader"/>), and the directory's new fields read both ways.
/// </summary>
public class ContentTypeTests
{
    private static readonly Bulletin Sample = TestBulletins.Day(4, 1)[0];

    [Fact]
    public void BulletinsAndDirectories_AreExactlyWhatV020Sent_AndV020ReadsThem()
    {
        var obj = TransferObject.ForBulletin(Sample, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var compressed = Compression.Default.Compress(Sample.Serialize(), ZstdDictionary.Gb7rdg1Id);
        Assert.Equal([(byte)1, .. compressed], obj.Bytes.ToArray());
        var (kind, content) = V020Reader.Unpack(obj.Bytes, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(1, kind);
        Assert.Equal(Sample, Bulletin.Parse(content));

        var directory = new BroadcastDirectory(new DateOnly(2026, 10, 5), [new DirectoryEntry(obj.ObjectId, obj.DictionaryId, 100, Sample.Bid, Sample.Title)], new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg));
        var dirObj = TransferObject.ForDirectory(directory, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(2, dirObj.Bytes[0]);
        var (dirKind, dirContent) = V020Reader.Unpack(dirObj.Bytes, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(2, dirKind);
        var old = V020Reader.ParseDirectory(dirContent);
        Assert.Equal(directory.Date, old.Date);
        Assert.Equal((obj.ObjectId, obj.DictionaryId, 100, Sample.Bid, Sample.Title), Assert.Single(old.Entries));
    }

    [Fact]
    public void ThisVersion_ReadsWhatV020Sent()
    {
        // A v0.2.0 head end's objects: the kind octet then zstd, and directory lines of five fields.
        var compressed = Compression.Default.Compress(Sample.Serialize(), ZstdDictionary.Gb7rdg1Id);
        var (kind, content) = TransferObject.Unpack([1, .. compressed], ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(ObjectKind.Bulletin, kind);
        Assert.Equal(Sample, Bulletin.Parse(content));
        var d = BroadcastDirectory.Parse(Encoding.Latin1.GetBytes("MAILCAST DIRECTORY 1\n2026-10-04\n0123456789abcdef\t1\t2345\t12345_GB7RDG\tA title\n"));
        Assert.Null(d.Schedule);
        Assert.Equal((byte)ObjectKind.Bulletin, Assert.Single(d.Entries).Type);
    }

    [Fact]
    public void Directory_CarriesEachEntrysType_AndTheTimetableOnTheFirstLine()
    {
        var timetable = new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);
        var d = new BroadcastDirectory(new DateOnly(2026, 10, 5),
        [
            new DirectoryEntry(1, 1, 10, "1_A", "First"),
            new DirectoryEntry(2, 1, 20, "2_B", "Second", (byte)ObjectKind.DappsMessage),
        ], timetable);
        Assert.Equal(
            "MAILCAST DIRECTORY 1\n2026-10-05\n0000000000000001\t1\t10\t1_A\tFirst\ttype=1\tslots=00:00/60\tdaylight=IO91lk/120/30\n0000000000000002\t1\t20\t2_B\tSecond\ttype=3\n",
            Encoding.Latin1.GetString(d.Serialize()));
        var back = BroadcastDirectory.Parse(d.Serialize());
        Assert.Equal(d.Entries, back.Entries);
        Assert.Equal(timetable, back.Schedule);

        // v0.2.0 sees the same two entries and nothing else.
        var old = V020Reader.ParseDirectory(d.Serialize());
        Assert.Equal([(1UL, (ushort)1, 10, "1_A", "First"), (2UL, (ushort)1, 20, "2_B", "Second")], old.Entries);

        // No entries, nowhere to carry it; and a head end without one sends none.
        Assert.Null(BroadcastDirectory.Parse(new BroadcastDirectory(d.Date, [], timetable).Serialize()).Schedule);
        Assert.Null(BroadcastDirectory.Parse(new BroadcastDirectory(d.Date, d.Entries).Serialize()).Schedule);
    }

    [Fact]
    public void Directory_IgnoresATimetableItCannotRead_AndOddTypes()
    {
        var d = BroadcastDirectory.Parse(Encoding.Latin1.GetBytes(
            "MAILCAST DIRECTORY 1\n2026-10-05\n0000000000000001\t1\t10\t1_A\tFirst\ttype=200\tslots=00:00/7\n0000000000000002\t1\t20\t2_B\tSecond\ttype=x\n"));
        Assert.Null(d.Schedule);
        Assert.All(d.Entries, e => Assert.Equal((byte)ObjectKind.Bulletin, e.Type));
    }

    [Fact]
    public void MetadataBlock_IsSkipped_ByThisVersion()
    {
        byte[] meta = Encoding.ASCII.GetBytes("X-Wrapper: test\n");
        var obj = TransferObject.ForContent((byte)ObjectKind.Bulletin, Sample.Serialize(), meta, withMetadata: true, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(0x81, obj.Bytes[0]);
        Assert.Equal(ObjectKind.Bulletin, obj.Kind);
        Assert.True(ContentType.TryRead(obj.Bytes, out byte type, out var metadata, out _));
        Assert.Equal(1, type);
        Assert.Equal(meta, metadata.ToArray());
        var (kind, content) = TransferObject.Unpack(obj.Bytes, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(ObjectKind.Bulletin, kind);
        Assert.Equal(Sample, Bulletin.Parse(content));

        // An empty block is allowed too.
        var empty = TransferObject.ForContent((byte)ObjectKind.Bulletin, Sample.Serialize(), [], withMetadata: true, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(Sample, Bulletin.Parse(TransferObject.Unpack(empty.Bytes, ZstdDictionary.Gb7rdg1Id, Compression.Default).Content));
    }

    [Fact]
    public void V020_RefusesAnythingButTypesOneAndTwo_WithoutMetadata()
    {
        // So a receiver already in the field never hands a DAPPS message, a wrapped bulletin or an
        // experiment to its BBS: it logs the object as one it cannot use and drops it.
        foreach (var (type, meta) in new (byte, bool)[] { (3, false), (0x70, false), (1, true) })
        {
            var obj = TransferObject.ForContent(type, Sample.Serialize(), [1, 2, 3], meta, ZstdDictionary.Gb7rdg1Id, Compression.Default);
            Assert.Throws<InvalidDataException>(() => V020Reader.Unpack(obj.Bytes, ZstdDictionary.Gb7rdg1Id, Compression.Default));
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x81 }, false)]
    [InlineData(new byte[] { 0x81, 0x00 }, false)]
    [InlineData(new byte[] { 0x81, 0x00, 0x05, 1, 2, 3, 4, 5 }, false)]
    [InlineData(new byte[] { 0x81, 0x00, 0x00, 0x28 }, true)]
    [InlineData(new byte[] { 0x00, 0x28 }, false)]
    [InlineData(new byte[] { 0x7f, 0x28 }, true)]
    public void TryRead_RefusesAHeaderCutShort(byte[] data, bool ok)
    {
        Assert.Equal(ok, ContentType.TryRead(data, out _, out _, out _));
    }

    [Fact]
    public void Registry_InWords()
    {
        Assert.Equal("packet mail bulletin (FBB/BPQ message format)", ContentType.Describe(1));
        Assert.Equal("directory", ContentType.Describe(2));
        Assert.Equal("DAPPS message", ContentType.Describe(3));
        Assert.Equal("ionosonde reading", ContentType.Describe(4));
        Assert.Equal("unassigned content type 5", ContentType.Describe(5));
        Assert.Equal("experimental content type 112", ContentType.Describe(0x70));
        Assert.True(ContentType.IsKnown(3));
        Assert.False(ContentType.IsKnown(0x70));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransferObject.ForContent(0x80, [1], [], false, 0, Compression.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransferObject.ForContent(0, [1], [], false, 0, Compression.Default));
    }

    private static AcceptResult FeedUntilDone(ReceiverStore store, TransferObject obj)
    {
        AcceptResult result = new(FrameOutcome.Stored);
        uint esi = 0;
        while (result.Outcome is FrameOutcome.Stored)
        {
            result = store.Accept(obj.Frame(esi++).ToBytes());
        }
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(obj.Frame(esi).ToBytes()).Outcome);
        return result;
    }

    [Fact]
    public void Receiver_KeepsADappsMessageOutOfTheBbs_AndIgnoresAnUnknownType_AndDeliversAWrappedBulletin()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });

        var dapps = TransferObject.ForContent((byte)ObjectKind.DappsMessage, Encoding.ASCII.GetBytes("a DAPPS message"), [], false, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var result = FeedUntilDone(store, dapps);
        Assert.Equal(FrameOutcome.CompletedUnhandled, result.Outcome);
        Assert.Equal((byte)3, result.ContentType);

        var experiment = TransferObject.ForContent(0x70, Encoding.ASCII.GetBytes("an experiment"), [9, 9], true, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.Equal(FrameOutcome.CompletedUnknown, FeedUntilDone(store, experiment).Outcome);

        // An experiment that is not even zstd inside is no error either: it is never decompressed.
        var junk = new byte[500];
        new Random(1).NextBytes(junk);
        junk[0] = 0x71;
        var raw = TransferObject.FromStored(junk, ZstdDictionary.Gb7rdg1Id, new ObjectTransmissionInformation(junk.Length, MailcastFrame.StandardSymbolSize, 1, 1, MailcastFrame.StandardAlignment));
        Assert.Equal(FrameOutcome.CompletedUnknown, FeedUntilDone(store, raw).Outcome);

        Assert.Empty(store.Pending());

        var wrapped = TransferObject.ForContent((byte)ObjectKind.Bulletin, Sample.Serialize(), Encoding.ASCII.GetBytes("meta"), true, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var delivered = FeedUntilDone(store, wrapped);
        Assert.Equal(FrameOutcome.CompletedBulletin, delivered.Outcome);
        Assert.Equal(Sample, Assert.Single(store.Pending()));
        Assert.DoesNotContain(log, l => l.Contains("unusable", StringComparison.OrdinalIgnoreCase) || l.Contains("does not decompress", StringComparison.Ordinal));
    }
}
