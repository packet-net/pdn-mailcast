using Mailcast.RaptorQ;

namespace Mailcast.Core.Tests;

public class FrameTests
{
    [Fact]
    public void ToBytes_IsTheDocumentedLayout()
    {
        var oti = new ObjectTransmissionInformation(2000, 940, 1, 1, 4);
        var frame = new MailcastFrame(0x11223344, 0x0102, oti, 0x0A0B0C, new byte[] { 0xEE, 0xFF });
        var bytes = frame.ToBytes();
        Assert.Equal("01" + "11223344" + "0102" + "00000007d0" + "00" + "03ac" + "01" + "0001" + "04" + "0a0b0c" + "eeff", Convert.ToHexStringLower(bytes));
        Assert.Equal(MailcastFrame.HeaderLength + 2, bytes.Length);
    }

    [Fact]
    public void StandardSymbolSize_FitsUnderIl2pLimitWithMargin()
    {
        int whole = MailcastFrame.Ax25UiOverhead + MailcastFrame.HeaderLength + MailcastFrame.StandardSymbolSize;
        Assert.Equal(978, whole);
        Assert.True(whole <= 1023 - 40);
        Assert.Equal(0, MailcastFrame.StandardSymbolSize % MailcastFrame.StandardAlignment);
    }

    [Fact]
    public void TryParse_RoundTrips()
    {
        var oti = new ObjectTransmissionInformation(5000, 940, 1, 1, 4);
        var symbol = Enumerable.Range(0, 940).Select(i => (byte)i).ToArray();
        var frame = new MailcastFrame(0xDEADBEEF, 1, oti, PayloadId.MaxEncodingSymbolId, symbol);
        Assert.True(MailcastFrame.TryParse(frame.ToBytes(), out var back));
        Assert.Equal(0xDEADBEEF, back!.ObjectId);
        Assert.Equal(1, back.DictionaryId);
        Assert.Equal(oti, back.Oti);
        Assert.Equal(PayloadId.MaxEncodingSymbolId, back.EncodingSymbolId);
        Assert.Equal(symbol, back.Symbol.ToArray());
    }

    [Fact]
    public void TryParse_RejectsOtherTraffic()
    {
        var oti = new ObjectTransmissionInformation(5000, 100, 1, 1, 4);
        var good = new MailcastFrame(1, 1, oti, 7, new byte[100]).ToBytes();
        Assert.True(MailcastFrame.TryParse(good, out _));

        Assert.False(MailcastFrame.TryParse([], out _));
        Assert.False(MailcastFrame.TryParse(good.AsSpan(0, MailcastFrame.HeaderLength), out _)); // no symbol

        var version = good.ToArray();
        version[0] = 2;
        Assert.False(MailcastFrame.TryParse(version, out _));

        var twoBlocks = good.ToArray();
        twoBlocks[15] = 2; // Z
        Assert.False(MailcastFrame.TryParse(twoBlocks, out _));

        var zeroSymbolSize = good.ToArray();
        zeroSymbolSize[13] = 0;
        zeroSymbolSize[14] = 0;
        Assert.False(MailcastFrame.TryParse(zeroSymbolSize, out _));

        var tooLong = new byte[good.Length + 1];
        good.CopyTo(tooLong, 0);
        Assert.False(MailcastFrame.TryParse(tooLong, out _));

        Assert.False(MailcastFrame.TryParse("Hello, this is plain text from someone else"u8, out _));
    }

    [Fact]
    public void Constructor_RequiresOneSourceBlock()
    {
        var oti = new ObjectTransmissionInformation(5000, 100, 2, 1, 4);
        Assert.Throws<ArgumentException>(() => new MailcastFrame(1, 1, oti, 0, new byte[100]));
    }

    [Fact]
    public void ObjectId_IsAHashOfTheBidIgnoringCase()
    {
        Assert.Equal(ObjectId.ForBid("12345_GB7RDG"), ObjectId.ForBid("12345_gb7rdg"));
        Assert.NotEqual(ObjectId.ForBid("12345_GB7RDG"), ObjectId.ForBid("12346_GB7RDG"));
        // The first four octets of SHA-256 of the BID: pinned, since it goes on the air.
        var sha = System.Security.Cryptography.SHA256.HashData("12345_GB7RDG"u8);
        Assert.Equal(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(sha), ObjectId.ForBid("12345_GB7RDG"));
        Assert.NotEqual(ObjectId.ForDirectory(new DateOnly(2026, 10, 4)), ObjectId.ForDirectory(new DateOnly(2026, 10, 5)));
    }
}
