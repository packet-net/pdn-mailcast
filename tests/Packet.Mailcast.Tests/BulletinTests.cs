using System.Text;

namespace Packet.Mailcast.Tests;

public class BulletinTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 1, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Serialize_IsTheDocumentedLayout()
    {
        var b = Bulletin.FromMessageText('B', "G4ABC", "ALL", "WW", "12345_GB7RDG", "A title", When,
            "R:261001/1234Z 156@GB7RDG.#42.GBR.EURO BPQ6.0.25\r\nR:261001/1200Z 99@M0XYZ BPQ\r\n\r\nHello.\r\n");
        Assert.Equal(2, b.RoutingLines.Count);
        Assert.Equal("\r\nHello.\r\n", b.Body);
        var expected = "Type: B\nFrom: G4ABC\nTo: ALL\nAt: WW\nBid: 12345_GB7RDG\nDate: 2026-10-01T12:34:56Z\nTitle: A title\n\n"
            + "R:261001/1234Z 156@GB7RDG.#42.GBR.EURO BPQ6.0.25\r\nR:261001/1200Z 99@M0XYZ BPQ\r\n\r\nHello.\r\n";
        Assert.Equal(Encoding.Latin1.GetBytes(expected), b.Serialize());
    }

    [Theory]
    [InlineData("")]
    [InlineData("no routing lines\r\n")]
    [InlineData("R:only a routing line\r\n")]
    [InlineData("R:not ended by CR LF")]
    [InlineData("R:ended by LF only\nbody")]
    [InlineData("R:a\r\nR:b\rbare CR\r\n")]
    [InlineData("R:a\r\n\r\nR: a line later in the body\r\n")]
    [InlineData("lines\nended\rdifferently\r\n\n\r")]
    public void RoundTrip_IsExact(string text)
    {
        var b = Bulletin.FromMessageText('B', "G4ABC", "ALL", "", "1_GB7RDG", "", When, text);
        Assert.Equal(text, b.MessageText);
        var bytes = b.Serialize();
        var back = Bulletin.Parse(bytes);
        Assert.Equal(b, back);
        Assert.Equal(bytes, back.Serialize());
    }

    [Fact]
    public void RoundTrip_KeepsEveryOctet()
    {
        // BBS mail is octets: UTF-8, DOS code pages, 7plus. All 256 values must survive.
        var octets = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var text = "R:261001/1234Z 1@GB7RDG\r\n" + Encoding.Latin1.GetString(octets);
        var b = Bulletin.FromMessageText('B', "G4ABC", "ALL", "WW", "2_GB7RDG", "Café ÿ", When, text);
        var bytes = b.Serialize();
        Assert.Equal(octets, bytes[^256..]);
        Assert.Equal(b, Bulletin.Parse(bytes));
        Assert.Equal(bytes, Bulletin.Parse(bytes).Serialize());
    }

    [Fact]
    public void RoundTrip_ManyMadeUpBulletins()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            var b = TestBulletins.Make(seed, seed * 20);
            var bytes = b.Serialize();
            Assert.Equal(bytes, Bulletin.Parse(bytes).Serialize());
            Assert.Equal(b, Bulletin.Parse(bytes));
        }
    }

    [Fact]
    public void Constructor_RejectsWhatCannotRoundTrip()
    {
        Assert.Throws<ArgumentException>(() => new Bulletin('b', "G4ABC", "ALL", "WW", "1_X", "t", When, [], ""));
        Assert.Throws<ArgumentException>(() => new Bulletin('B', "G4 ABC", "ALL", "WW", "1_X", "t", When, [], ""));
        Assert.Throws<ArgumentException>(() => new Bulletin('B', "G4ABC", "", "WW", "1_X", "t", When, [], ""));
        Assert.Throws<ArgumentException>(() => new Bulletin('B', "G4ABC", "ALL", "WW", "1_X", "two\nlines", When, [], ""));
        Assert.Throws<ArgumentException>(() => new Bulletin('B', "G4ABC", "ALL", "WW", "1_X", "t", When.AddMilliseconds(1), [], ""));
        Assert.Throws<ArgumentException>(() => new Bulletin('B', "G4ABC", "ALL", "WW", "1_X", "t", When, ["not R"], ""));
        Assert.Throws<ArgumentException>(() => new Bulletin('B', "G4ABC", "ALL", "WW", "1_X", "t", When, [], "R:x\r\nbody"));
        Assert.Throws<ArgumentException>(() => new Bulletin('B', "G4ABC", "ALL", "WW", "1_X", "€", When, [], ""));
    }

    private const string Good = "Type: B\nFrom: G4ABC\nTo: ALL\nAt: WW\nBid: 1_X\nDate: 2026-10-01T12:34:56Z\nTitle: t\n\n";

    [Theory]
    [InlineData("")]
    [InlineData("Type: B\nFrom: G4ABC\nTo: ALL\nAt: WW\nBid: 1_X\nDate: 2026-10-01T12:34:56Z\nTitle: t\n")] // no blank line
    [InlineData("Type: B\nFrom: G4ABC\nTo: ALL\nAt: WW\nBid: 1_X\nDate: 2026-10-01T12:34:56Z\n\n")] // no title
    [InlineData("Type: BB\nFrom: G4ABC\nTo: ALL\nAt: WW\nBid: 1_X\nDate: 2026-10-01T12:34:56Z\nTitle: t\n\n")]
    [InlineData("Type: B\nFrom: G4ABC\nTo: ALL\nAt: WW\nBid: 1_X\nDate: 2026-10-01 12:34\nTitle: t\n\n")]
    [InlineData("Type: B\nFrom: G4 ABC\nTo: ALL\nAt: WW\nBid: 1_X\nDate: 2026-10-01T12:34:56Z\nTitle: t\n\n")]
    [InlineData("Type: B\nFrom: G4ABC\nFrom: G4ABC\nTo: ALL\nAt: WW\nBid: 1_X\nDate: 2026-10-01T12:34:56Z\nTitle: t\n\n")]
    [InlineData("Type: B\nFrom G4ABC\nTo: ALL\nAt: WW\nBid: 1_X\nDate: 2026-10-01T12:34:56Z\nTitle: t\n\n")]
    public void Parse_RejectsMalformed(string text)
    {
        Assert.Throws<FormatException>(() => Bulletin.Parse(Encoding.Latin1.GetBytes(text)));
    }

    [Fact]
    public void Parse_IgnoresUnknownKeysInAnyOrder()
    {
        var text = "Title: t: with a colon\nX-Later: something new\nType: B\nFrom: G4ABC\nTo: ALL\nAt: \nBid: 1_X\nDate: 2026-10-01T12:34:56Z\n\nbody\r\n";
        var b = Bulletin.Parse(Encoding.Latin1.GetBytes(text));
        Assert.Equal("t: with a colon", b.Title);
        Assert.Equal("", b.At);
        Assert.Equal("body\r\n", b.Body);
        Assert.Equal(b, Bulletin.Parse(b.Serialize()));
        Assert.True(Bulletin.Parse(Encoding.Latin1.GetBytes(Good)).Body.Length == 0);
    }

    [Fact]
    public void Parse_AcceptsAKeyWithNoSpaceAfterTheColon()
    {
        var text = "Type:B\nFrom:G4ABC\nTo: ALL\nAt:\nBid:1_X\nDate:2026-10-01T12:34:56Z\nTitle:  two spaces\n\nbody";
        var b = Bulletin.Parse(Encoding.Latin1.GetBytes(text));
        Assert.Equal("G4ABC", b.From);
        Assert.Equal("", b.At);
        Assert.Equal(" two spaces", b.Title); // one space after the colon is the separator; the rest is the value
        Assert.Equal(b, Bulletin.Parse(b.Serialize()));
    }

    [Fact]
    public void Date_IsUtc()
    {
        var local = new DateTimeOffset(2026, 10, 1, 13, 34, 56, TimeSpan.FromHours(1));
        var b = new Bulletin('B', "G4ABC", "ALL", "WW", "1_X", "t", local, [], "");
        Assert.Equal(TimeSpan.Zero, b.Date.Offset);
        Assert.Equal(When, Bulletin.Parse(b.Serialize()).Date);
    }
}
