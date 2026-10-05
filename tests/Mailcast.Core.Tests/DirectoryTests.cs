using System.Text;

namespace Mailcast.Core.Tests;

public class DirectoryTests
{
    [Fact]
    public void Serialize_IsTheDocumentedLayout()
    {
        var d = new BroadcastDirectory(new DateOnly(2026, 10, 4),
        [
            new DirectoryEntry(0x0123456789abcdef, 1, 2345, "12345_GB7RDG", "Title with  spaces"),
            new DirectoryEntry(0x0000000000000001, 0, 7, "1_X", ""),
        ]);
        Assert.Equal(
            "MAILCAST DIRECTORY 1\n2026-10-04\n0123456789abcdef\t1\t2345\t12345_GB7RDG\tTitle with  spaces\n0000000000000001\t0\t7\t1_X\t\n",
            Encoding.Latin1.GetString(d.Serialize()));
        var back = BroadcastDirectory.Parse(d.Serialize());
        Assert.Equal(d.Date, back.Date);
        Assert.Equal(d.Entries, back.Entries);
    }

    [Fact]
    public void Parse_IgnoresFieldsAfterTheTitle()
    {
        var text = "MAILCAST DIRECTORY 1\n2026-10-04\n0123456789abcdef\t1\t2345\t12345_GB7RDG\tA title\tsomething new\t42\n";
        var d = BroadcastDirectory.Parse(Encoding.Latin1.GetBytes(text));
        Assert.Equal(new DirectoryEntry(0x0123456789abcdef, 1, 2345, "12345_GB7RDG", "A title"), Assert.Single(d.Entries));
    }

    [Fact]
    public void TabInATitle_BecomesASpace()
    {
        var d = new BroadcastDirectory(new DateOnly(2026, 10, 4), [new DirectoryEntry(1, 1, 1, "1_X", "a\tb")]);
        Assert.Equal("a b", BroadcastDirectory.Parse(d.Serialize()).Entries[0].Title);
    }

    [Fact]
    public void Empty_RoundTrips()
    {
        var d = new BroadcastDirectory(new DateOnly(2026, 10, 4), []);
        Assert.Empty(BroadcastDirectory.Parse(d.Serialize()).Entries);
    }

    [Fact]
    public void DirectoriesWithDifferentContents_HaveDifferentIds()
    {
        var day = new DateOnly(2026, 10, 4);
        var a = TransferObject.ForDirectory(new BroadcastDirectory(day, [new DirectoryEntry(1, 1, 1, "1_X", "a")]), 1, Compression.Default);
        var b = TransferObject.ForDirectory(new BroadcastDirectory(day, [new DirectoryEntry(2, 1, 1, "2_X", "b")]), 1, Compression.Default);
        Assert.NotEqual(a.ObjectId, b.ObjectId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MAILCAST DIRECTORY 1\n2026-10-04")]
    [InlineData("2026-10-04\n")]
    [InlineData("MAILCAST DIRECTORY 2\n2026-10-04\n")]
    [InlineData("MAILCAST DIRECTORY 1\n04/10/2026\n")]
    [InlineData("MAILCAST DIRECTORY 1\n2026-10-04\n0123456789abcdef\t1\t2345\t1_X\n")]
    [InlineData("MAILCAST DIRECTORY 1\n2026-10-04\nzzzzzzzzzzzzzzzz\t1\t2345\t1_X\tt\n")]
    [InlineData("MAILCAST DIRECTORY 1\n2026-10-04\n01234567\t1\t2345\t1_X\tt\n")]
    public void Parse_RejectsMalformed(string text)
    {
        Assert.Throws<FormatException>(() => BroadcastDirectory.Parse(Encoding.Latin1.GetBytes(text)));
    }
}
