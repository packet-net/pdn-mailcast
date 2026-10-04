// Copied from packet-net/pdn-bbs (AGPL-3.0) at 92a54cafc1eb7f05779c0d740c67a088eacc3e1a, tests/Bbs.Fbb.Tests/HeaderEncodingTests.cs.
// Changed only to replace dashes in comments with hyphens; see this project's csproj.
using System.Text;

namespace Bbs.Fbb.Tests;

/// <summary>
/// Round-trip tests for non-ASCII subject/title text through the real B1 SOH
/// title path (<see cref="BlockFraming"/>/<see cref="FbbBlockReader"/>) and the
/// B2F <c>Subject:</c> header path (<see cref="B2Message"/>). The headers are
/// stored as decoded strings, so the encoding must be correct at ingest and
/// byte-faithful at egress. ASCII MUST stay byte-identical on the wire.
/// </summary>
public sealed class HeaderEncodingTests
{
    private const string Utf8Subject = "He said “hello” to José"; // smart quotes + é
    private const string Latin1PoundSubject = "Cost £5";

    // --- B1 SOH title ---------------------------------------------------------

    [Fact]
    public void B1Title_AsciiTitle_IsByteIdenticalToLatin1OnTheWire()
    {
        // Egress for an ASCII title must not change vs the old Latin-1 emit.
        const string title = "Hello there";
        var header = BlockFraming.EncodeHeader(title, 0);

        var titleBytes = header[2..(2 + title.Length)];
        Assert.Equal(Encoding.Latin1.GetBytes(title), titleBytes);
        Assert.Equal(Encoding.ASCII.GetBytes(title), titleBytes);
    }

    [Fact]
    public void B1Title_Utf8Title_RoundTripsThroughBlockReader()
    {
        var framed = BlockFraming.EncodeMessage(Utf8Subject, 0, Encoding.ASCII.GetBytes("body"));

        var reader = new FbbBlockReader();
        Assert.Equal(FbbBlockReaderStatus.Complete, reader.Feed(framed, out _));
        Assert.Equal(Utf8Subject, reader.Title);
    }

    [Fact]
    public void B1Title_Utf8Title_HeaderLengthCountsEncodedBytes()
    {
        // The SOH length byte and the title NUL position must use the ENCODED
        // (UTF-8) byte length, not the char count.
        var header = BlockFraming.EncodeHeader(Utf8Subject, 0);
        var titleBytes = Encoding.UTF8.GetBytes(Utf8Subject);

        // len = title bytes + 6 (offset "     0") + 2 (the two NULs).
        Assert.Equal((byte)(titleBytes.Length + 8), header[1]);
        Assert.Equal((byte)0x00, header[2 + titleBytes.Length]); // title NUL
        Assert.Equal(titleBytes, header[2..(2 + titleBytes.Length)]);
    }

    [Fact]
    public void B1Title_Latin1HighByteTitle_DecodesAsPoundAndUpgradesOnEgress()
    {
        // A raw Latin-1 0xA3 title (invalid UTF-8) decodes to '£'; egress
        // re-encodes it to the 2-byte UTF-8 form (the deliberate upgrade).
        byte[] latin1Title = Encoding.Latin1.GetBytes(Latin1PoundSubject); // contains 0xA3
        var offset = Encoding.ASCII.GetBytes("     0");
        var len = latin1Title.Length + offset.Length + 2;
        var raw = new List<byte> { BlockFraming.Soh, (byte)len };
        raw.AddRange(latin1Title);
        raw.Add(0x00);
        raw.AddRange(offset);
        raw.Add(0x00);
        raw.AddRange([BlockFraming.Stx, 1, (byte)'x']);
        raw.Add(BlockFraming.Eot);
        raw.Add(BlockFraming.ComputeTrailerChecksum([(byte)'x']));

        var reader = new FbbBlockReader();
        Assert.Equal(FbbBlockReaderStatus.Complete, reader.Feed([.. raw], out _));
        Assert.Equal(Latin1PoundSubject, reader.Title);

        var reEmitted = BlockFraming.EncodeHeader(reader.Title, 0);
        var expectedTitle = Encoding.UTF8.GetBytes(Latin1PoundSubject);
        Assert.Equal(expectedTitle, reEmitted[2..(2 + expectedTitle.Length)]);
    }

    // --- B2F Subject header ---------------------------------------------------

    private static B2Message MakeMessage(string subject) => new()
    {
        Mid = "1_GB7PDN",
        Type = B2MessageType.Private,
        To = ["G8BPQ"],
        Subject = subject,
        Body = Encoding.ASCII.GetBytes("Hello, world!\r\n"),
    };

    [Fact]
    public void B2Subject_AsciiSubject_IsByteIdenticalToAsciiEncode()
    {
        // Egress for an all-ASCII message must be byte-for-byte the old ASCII
        // output (no interop change).
        var encoded = MakeMessage("Hello there").Encode();

        const string expectedHeader =
            "MID: 1_GB7PDN\r\n" +
            "Type: Private\r\n" +
            "To: G8BPQ\r\n" +
            "Subject: Hello there\r\n" +
            "Content-Type: text/plain\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "Body: 15\r\n" +
            "\r\n";
        var expected = Encoding.ASCII.GetBytes(expectedHeader + "Hello, world!\r\n" + "\r\n");
        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void B2Subject_Utf8Subject_RoundTripsByteFaithfully()
    {
        var encoded = MakeMessage(Utf8Subject).Encode();
        var decoded = B2Message.Decode(encoded);

        Assert.Equal(Utf8Subject, decoded.Subject);
        Assert.Equal(encoded, decoded.Encode()); // re-encode reproduces same bytes
        Assert.True(ContainsSequence(encoded, Encoding.UTF8.GetBytes("Subject: " + Utf8Subject)));
    }

    [Fact]
    public void B2Subject_Latin1HighByteSubject_DecodesAsPoundAndUpgradesOnEgress()
    {
        // Hand-build a B2 object whose Subject carries a raw Latin-1 0xA3.
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("MID: 1_GB7PDN\r\nType: Private\r\nTo: G8BPQ\r\nSubject: "));
        bytes.AddRange(Encoding.Latin1.GetBytes(Latin1PoundSubject)); // 0xA3 high byte
        bytes.AddRange(Encoding.ASCII.GetBytes("\r\nBody: 15\r\n\r\nHello, world!\r\n\r\n"));

        var decoded = B2Message.Decode(bytes.ToArray());
        Assert.Equal(Latin1PoundSubject, decoded.Subject);

        // Egress upgrades the Subject to UTF-8 (2 bytes for '£').
        Assert.True(ContainsSequence(decoded.Encode(), Encoding.UTF8.GetBytes("Subject: " + Latin1PoundSubject)));
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
