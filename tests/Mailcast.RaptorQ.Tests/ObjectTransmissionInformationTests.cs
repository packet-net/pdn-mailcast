namespace Mailcast.RaptorQ.Tests;

public class ObjectTransmissionInformationTests
{
    [Fact]
    public void Encoding_IsTheLayoutOfSection3_3()
    {
        var oti = new ObjectTransmissionInformation(0x01_2345_6789, 0x0ABC, 0xFF, 0x0102, 4);
        Assert.Equal("0123456789000abcff010204", Convert.ToHexStringLower(oti.ToBytes()));
        Assert.Equal(oti, ObjectTransmissionInformation.Read(oti.ToBytes()));
    }

    [Fact]
    public void MaxTransferLength_IsAccepted()
    {
        var oti = new ObjectTransmissionInformation(ObjectTransmissionInformation.MaxTransferLength, 65535, 255, 1, 1);
        Assert.Equal(oti, ObjectTransmissionInformation.Read(oti.ToBytes()));
    }

    [Theory]
    [InlineData(0, 16, 1, 1, 1)] // empty object
    [InlineData(100, 0, 1, 1, 1)] // no symbol size
    [InlineData(100, 10, 1, 1, 4)] // T not a multiple of Al
    [InlineData(100, 8, 1, 3, 4)] // more sub-blocks than alignment units
    [InlineData(100, 10, 11, 1, 1)] // more source blocks than symbols
    [InlineData(56404, 1, 1, 1, 1)] // a block bigger than K'_max
    [InlineData(942574504276, 65535, 255, 1, 1)] // past erratum 5548's limit
    public void Constructor_RejectsInvalidParameters(long f, int t, int z, int n, int al)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ObjectTransmissionInformation(f, t, z, n, al));
    }

    [Fact]
    public void Read_RejectsAZeroSymbolSize()
    {
        var bytes = new ObjectTransmissionInformation(100, 10).ToBytes();
        bytes[6] = 0;
        bytes[7] = 0;
        Assert.ThrowsAny<ArgumentException>(() => ObjectTransmissionInformation.Read(bytes));
    }

    [Fact]
    public void ForObject_UsesAsFewBlocksAsAllowed()
    {
        var single = ObjectTransmissionInformation.ForObject(30_000, 940);
        Assert.Equal(1, single.SourceBlocks);
        Assert.Equal(32, single.SourceBlockSymbols(0));

        var split = ObjectTransmissionInformation.ForObject(100_000, 100, maxSourceSymbolsPerBlock: 300);
        Assert.Equal(4, split.SourceBlocks);
        Assert.Equal(1000, Enumerable.Range(0, 4).Sum(split.SourceBlockSymbols));
        Assert.Equal(250, split.SourceBlockSymbols(3));
    }

    [Fact]
    public void SourceBlockSymbols_FollowsPartition()
    {
        // Kt = 10, Z = 3: Partition[10, 3] = (4, 3, 1, 2).
        var oti = new ObjectTransmissionInformation(95, 10, 3);
        Assert.Equal([4, 3, 3], Enumerable.Range(0, 3).Select(oti.SourceBlockSymbols));
    }

    [Fact]
    public void PayloadId_EncodesSbnAndEsi()
    {
        var id = new PayloadId(0x12, 0x345678);
        var bytes = new byte[4];
        id.Write(bytes);
        Assert.Equal("12345678", Convert.ToHexStringLower(bytes));
        Assert.Equal(id, PayloadId.Read(bytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PayloadId(0, 1u << 24));
    }
}
