using Packet.Mailcast.Feedback;
using Xunit.Abstractions;

namespace Packet.Mailcast.Tests;

/// <summary>
/// Issue #136: the directory lists the listeners whose daily reports reached GB7RDG lately,
/// inside what the signature covers, and older receivers read it exactly as before.
/// </summary>
public class ReportReceiptsTests(ITestOutputHelper output)
{
    private static readonly DateOnly Day = new(2026, 10, 16);
    private static readonly DirectorySigner Signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
    private static readonly TrustedKey Trusted = TrustedKey.Parse("TEST", Signer.PublicKeyBase64);

    private static readonly ReportReceipts Some = new([
        new("G8BPQ", Day.AddDays(-1)),
        new("GM8BPQ", Day.AddDays(-1)),
        new("M9TWM", Day.AddDays(-2)),
        new("G4ABC", Day.AddDays(-2)),
        new("G4ABC", Day.AddDays(-1)), // the newer wins
    ]);

    private static BroadcastDirectory Directory(int count, ReportReceipts? reports, bool signed = true)
    {
        var objects = TestBulletins.Day(3, count).Select(b => (Bulletin: b, Object: TransferObject.ForBulletin(b, ZstdDictionary.Gb7rdg1Id, Compression.Default))).ToList();
        var entries = objects.Select(o => new DirectoryEntry(o.Object.ObjectId, o.Object.DictionaryId, o.Bulletin.Serialize().Length, o.Bulletin.Bid, o.Bulletin.Title,
            Hash: signed ? ObjectId.LongHash(o.Object.DictionaryId, o.Object.Bytes) : null)).ToList();
        return new BroadcastDirectory(Day, entries, new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg), "ms110d-wn4", signed ? Signer : null, reports);
    }

    [Fact]
    public void Field_GroupsCallsignsByDay_NewestFirst_AndReadsBack()
    {
        Assert.Equal("reports=2026-10-15:G4ABC,G8BPQ,GM8BPQ;2026-10-14:M9TWM", Some.ToField());
        var back = ReportReceipts.FromFields(["type=1", "slots=00:00/60", Some.ToField()])!;
        Assert.Equal(Some.Newest, back.Newest);
        Assert.Equal(Day.AddDays(-1), back.For("g4abc"));
        Assert.Null(back.For("GM4CXF"));

        Assert.Equal("reports=", ReportReceipts.None.ToField());
        Assert.Empty(ReportReceipts.FromFields(["reports="])!.Newest);
        Assert.Null(ReportReceipts.FromFields(["type=1"]));
        Assert.Null(ReportReceipts.FromFields(["reports=yesterday:G4ABC"]));
        Assert.Null(ReportReceipts.FromFields(["reports=2026-10-15:G4 ABC"]));
        Assert.Throws<ArgumentException>(() => new ReportReceipts([new("G4ABC-1", Day)]));
    }

    [Fact]
    public void ForDay_ListsTheLastTwoDays()
    {
        var listed = ReportReceipts.ForDay(Day, [new("G4ABC", Day), new("G4DEF", Day.AddDays(-2)), new("G4GHI", Day.AddDays(-3)), new("G4JKL", Day.AddDays(1))]);
        Assert.Equal(["G4ABC", "G4DEF"], listed.Newest.Keys);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Directory_CarriesTheList_OnItsFirstLine_InsideTheSignature(int count)
    {
        var directory = Directory(count, Some);
        byte[] bytes = directory.Serialize();
        string[] lines = Bulletin.TextEncoding.GetString(bytes).Split('\n');
        Assert.Contains("\tmode=ms110d-wn4\t" + Some.ToField() + "\t", lines[2], StringComparison.Ordinal);
        Assert.Single(lines, l => l.Contains("reports=", StringComparison.Ordinal));

        var parsed = BroadcastDirectory.Parse(bytes);
        Assert.Equal(Some.Newest, parsed.Reports!.Newest);
        Assert.Equal(SignatureState.Trusted, parsed.Check([Trusted]).State);

        // The list is covered: change a callsign in it and the signature no longer checks.
        byte[] forged = Bulletin.TextEncoding.GetBytes(Bulletin.TextEncoding.GetString(bytes).Replace("M9TWM", "M9TWN", StringComparison.Ordinal));
        Assert.Equal(SignatureState.Rejected, BroadcastDirectory.Parse(forged).Check([Trusted]).State);

        Assert.Null(BroadcastDirectory.Parse(Directory(count, null).Serialize()).Reports);
        Assert.Empty(BroadcastDirectory.Parse(Directory(count, ReportReceipts.None).Serialize()).Reports!.Newest);
    }

    [Fact]
    public void Empty_HasNowhereForTheList()
    {
        var empty = new BroadcastDirectory(Day, [], reports: Some);
        Assert.Equal("MAILCAST DIRECTORY 1\n2026-10-16\n", Bulletin.TextEncoding.GetString(empty.Serialize()));
        Assert.Null(BroadcastDirectory.Parse(empty.Serialize()).Reports);
    }

    /// <summary>
    /// Receivers 0.14.0 and 0.15.0 (<see cref="V0140Directory"/>) read the same entries, timetable
    /// and mode from a directory with the list as from the same directory without it, and its
    /// signature checks; receivers 0.8.0 to 0.13.0 (<see cref="V0130Reader"/>) likewise.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(25, true)]
    [InlineData(4, false)]
    public void OlderReceivers_ReadADirectoryWithTheList_ExactlyAsOneWithout(int count, bool withSignature)
    {
        var with = Directory(count, Some, withSignature);
        var without = Directory(count, null, withSignature);

        var old = V0140Directory.Parse(with.Serialize());
        var oldWithout = V0140Directory.Parse(without.Serialize());
        Assert.Equal(oldWithout.Entries, old.Entries);
        Assert.Equal(oldWithout.Schedule, old.Schedule);
        Assert.Equal(oldWithout.Mode, old.Mode);
        Assert.Equal(with.Entries, old.Entries);
        Assert.Equal(withSignature ? SignatureState.Trusted : SignatureState.None, old.Check([Trusted]).State);

        var withObject = TransferObject.ForDirectory(with, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var older = V0130Reader.ReadDirectoryObject(withObject.Bytes, withObject.DictionaryId, Compression.Default);
        Assert.Equal(oldWithout.Entries.Select(e => e with { Hash = null }), older.Entries);
        Assert.Equal(oldWithout.Schedule, older.Schedule);
        Assert.Equal(oldWithout.Mode, older.Mode);
    }

    /// <summary>
    /// What the list costs on the air: the directory's compressed octets and source symbols at
    /// GB7RDG's 240-octet frames, with 30 bulletins in rotation (a busy day), without the list and
    /// with 5, 10 and 20 listeners in it. Printed for the record; the bound is loose on purpose.
    /// </summary>
    [Fact]
    public void Airtime_TheListAddsLittleToTheDirectory()
    {
        TransferObject Object(ReportReceipts? reports) =>
            TransferObject.ForDirectory(Directory(30, reports), ZstdDictionary.Gb7rdg1Id, Compression.Default, 240, MailcastFrame.StandardAlignment);
        string[] calls = ["G8BPQ", "GM8BPQ", "G7TAJ", "M9TWM", "GM4CXF", "G4ABC", "M0XYZ", "2E0ABC", "G0DEF", "MW1GHI",
            "GI4JKL", "M7MNO", "G3PQR", "EI2STU", "G6VWX", "M1YZA", "GW0BCD", "G4EFG", "M5HIJ", "2M0KLM"];
        ReportReceipts Listeners(int n) => new(calls.Take(n).Select((c, i) => new KeyValuePair<string, DateOnly>(c, Day.AddDays(-1 - (i % 4 == 3 ? 1 : 0)))));
        var none = Object(null);
        output.WriteLine($"directory, 30 bulletins, 240-octet symbols: {Directory(30, null).Serialize().Length} octets, {none.Bytes.Length} compressed, {none.SourceSymbols} symbols without the list");
        foreach (int n in new[] { 5, 10, 20 })
        {
            var list = Listeners(n);
            var with = Object(list);
            output.WriteLine($"  {n} listeners: field {list.ToField().Length + 1} octets, {with.Bytes.Length - none.Bytes.Length:+0;-0} compressed, {with.SourceSymbols} symbols (+{with.SourceSymbols - none.SourceSymbols})");
            Assert.InRange(with.SourceSymbols - none.SourceSymbols, 0, n <= 10 ? 1 : 2);
        }
    }
}
