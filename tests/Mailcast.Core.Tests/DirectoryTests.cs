using System.Text;

namespace Mailcast.Core.Tests;

public class DirectoryTests
{
    [Fact]
    public void Serialize_IsTheDocumentedLayout()
    {
        var d = new BroadcastDirectory(new DateOnly(2026, 10, 4),
        [
            new DirectoryEntry(0x1a2b3c4d, 1, 2345, "0123456789abcdef", "12345_GB7RDG", "Title with  spaces"),
            new DirectoryEntry(0x00000001, 0, 7, "fedcba9876543210", "1_X", ""),
        ]);
        Assert.Equal(
            "2026-10-04\n1a2b3c4d 1 2345 0123456789abcdef 12345_GB7RDG Title with  spaces\n00000001 0 7 fedcba9876543210 1_X \n",
            Encoding.Latin1.GetString(d.Serialize()));
        var back = BroadcastDirectory.Parse(d.Serialize());
        Assert.Equal(d.Date, back.Date);
        Assert.Equal(d.Entries, back.Entries);
        Assert.Equal(ObjectId.ForDirectory(d.Date), d.ObjectId);
    }

    [Fact]
    public void Empty_RoundTrips()
    {
        var d = new BroadcastDirectory(new DateOnly(2026, 10, 4), []);
        Assert.Empty(BroadcastDirectory.Parse(d.Serialize()).Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-10-04")]
    [InlineData("04/10/2026\n")]
    [InlineData("2026-10-04\n1a2b3c4d 1 2345 0123456789abcdef\n")]
    [InlineData("2026-10-04\nzzzzzzzz 1 2345 0123456789abcdef 1_X t\n")]
    [InlineData("2026-10-04\n1a2b3c4d 1 2345 0123456789ABCDEF 1_X t\n")]
    public void Parse_RejectsMalformed(string text)
    {
        Assert.Throws<FormatException>(() => BroadcastDirectory.Parse(Encoding.Latin1.GetBytes(text)));
    }
}
