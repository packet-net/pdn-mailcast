using System.Buffers.Binary;
using Mailcast.RaptorQ;

namespace Mailcast.Core.Tests;

public class FrameTests
{
    [Fact]
    public void ToBytes_IsTheDocumentedLayout()
    {
        var oti = new ObjectTransmissionInformation(2000, 940, 1, 1, 4);
        var frame = new MailcastFrame(0x1122334455667788, 0x0102, oti, 0x0A0B0C, new byte[] { 0xEE, 0xFF });
        var bytes = frame.ToBytes();
        var withoutCrc = "02" + "00" + "1122334455667788" + "0102" + "00000007d0" + "00" + "03ac" + "01" + "0001" + "04" + "0a0b0c" + "eeff";
        Assert.Equal(withoutCrc, Convert.ToHexStringLower(bytes.AsSpan(0, bytes.Length - 4)));
        Assert.Equal(Crc32.Compute(bytes.AsSpan(0, bytes.Length - 4)), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(bytes.Length - 4)));
        Assert.Equal(MailcastFrame.Overhead + 2, bytes.Length);
    }

    [Fact]
    public void Crc32_IsTheStandardOne()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        Assert.Equal(0u, Crc32.Compute([]));
    }

    [Fact]
    public void StandardSymbolSize_FitsUnderIl2pLimitWithMargin()
    {
        int whole = MailcastFrame.Ax25UiOverhead + MailcastFrame.Overhead + MailcastFrame.StandardSymbolSize;
        Assert.Equal(987, whole);
        Assert.True(whole <= 1023 - 32);
        Assert.Equal(0, MailcastFrame.StandardSymbolSize % MailcastFrame.StandardAlignment);
    }

    [Fact]
    public void TryParse_RoundTrips()
    {
        var oti = new ObjectTransmissionInformation(5000, 940, 1, 1, 4);
        var symbol = Enumerable.Range(0, 940).Select(i => (byte)i).ToArray();
        var frame = new MailcastFrame(0xDEADBEEFCAFEF00D, 1, oti, PayloadId.MaxEncodingSymbolId, symbol, flags: 0x80);
        Assert.True(MailcastFrame.TryParse(frame.ToBytes(), out var back));
        Assert.Equal(0xDEADBEEFCAFEF00D, back!.ObjectId);
        Assert.Equal(1, back.DictionaryId);
        Assert.Equal(oti, back.Oti);
        Assert.Equal(PayloadId.MaxEncodingSymbolId, back.EncodingSymbolId);
        Assert.Equal(symbol, back.Symbol.ToArray());
        Assert.Equal(0x80, back.Flags); // unknown flags are carried, not refused
    }

    [Fact]
    public void TryParse_RejectsAnyDamage()
    {
        var oti = new ObjectTransmissionInformation(5000, 100, 1, 1, 4);
        var good = new MailcastFrame(1, 1, oti, 7, new byte[100]).ToBytes();
        Assert.True(MailcastFrame.TryParse(good, out _));
        for (int i = 0; i < good.Length; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                var damaged = good.ToArray();
                damaged[i] ^= (byte)(1 << bit);
                Assert.False(MailcastFrame.TryParse(damaged, out _), $"octet {i} bit {bit}");
            }
        }
    }

    [Fact]
    public void TryParse_RejectsOtherTraffic()
    {
        var oti = new ObjectTransmissionInformation(5000, 100, 1, 1, 4);
        var good = new MailcastFrame(1, 1, oti, 7, new byte[100]).ToBytes();

        Assert.False(MailcastFrame.TryParse([], out _));
        Assert.False(MailcastFrame.TryParse(good.AsSpan(0, MailcastFrame.Overhead), out _)); // no symbol
        Assert.False(MailcastFrame.TryParse(WithCrc(good, b => b[0] = 1), out _)); // version 1
        Assert.False(MailcastFrame.TryParse(WithCrc(good, b => b[20] = 2), out _)); // Z = 2
        Assert.False(MailcastFrame.TryParse(WithCrc(good, b => { b[18] = 0; b[19] = 0; }), out _)); // T = 0
        Assert.True(MailcastFrame.TryParse(WithCrc(good, b => b[1] = 0xFF), out _)); // flags are ignored

        var tooLong = new byte[good.Length + 1];
        good.AsSpan(0, good.Length - 4).CopyTo(tooLong);
        Assert.False(MailcastFrame.TryParse(WithCrc(tooLong, _ => { }), out _)); // symbol longer than T

        Assert.False(MailcastFrame.TryParse("Hello, this is plain text from someone else"u8, out _));
    }

    /// <summary>Changes a frame and puts a correct CRC back.</summary>
    private static byte[] WithCrc(byte[] frame, Action<byte[]> change)
    {
        var copy = frame.ToArray();
        change(copy);
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(copy.Length - 4), Crc32.Compute(copy.AsSpan(0, copy.Length - 4)));
        return copy;
    }

    [Fact]
    public void Constructor_RequiresOneSourceBlock()
    {
        var oti = new ObjectTransmissionInformation(5000, 100, 2, 1, 4);
        Assert.Throws<ArgumentException>(() => new MailcastFrame(1, 1, oti, 0, new byte[100]));
    }

    [Fact]
    public void ObjectId_IsTheFirstEightOctetsOfTheObjectsSha256()
    {
        var bulletin = TestBulletins.Make(1, 2000);
        var obj = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var sha = System.Security.Cryptography.SHA256.HashData(obj.Bytes);
        Assert.Equal(BinaryPrimitives.ReadUInt64BigEndian(sha), obj.ObjectId);
        Assert.Equal(obj.ObjectId, ObjectId.Of(obj.Bytes));
        Assert.Equal((byte)ObjectKind.Bulletin, obj.Bytes[0]);

        // Same bulletin, same settings: same object. Any change: another object.
        Assert.Equal(obj.ObjectId, Ids.Of(bulletin));
        Assert.NotEqual(obj.ObjectId, Ids.Of(TestBulletins.Make(2, 2000)));
        Assert.NotEqual(obj.ObjectId, TransferObject.ForBulletin(bulletin, Compression.NoDictionary, Compression.Default).ObjectId);
    }

    [Fact]
    public void FromStored_GivesTheSameObject()
    {
        var obj = TransferObject.ForBulletin(TestBulletins.Make(3, 5000), ZstdDictionary.Gb7rdg1Id, Compression.Default);
        var again = TransferObject.FromStored(obj.Bytes, obj.DictionaryId, obj.Oti);
        Assert.Equal(obj.ObjectId, again.ObjectId);
        Assert.Equal(obj.Frame(17).ToBytes(), again.Frame(17).ToBytes());
        Assert.Throws<ArgumentException>(() => TransferObject.FromStored(obj.Bytes[1..], obj.DictionaryId, obj.Oti));
    }
}
