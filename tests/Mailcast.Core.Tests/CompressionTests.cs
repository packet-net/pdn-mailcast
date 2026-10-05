namespace Mailcast.Core.Tests;

public class CompressionTests
{
    [Fact]
    public void BuiltInDictionary_IsLoaded()
    {
        var d = Assert.Single(ZstdDictionary.BuiltIn);
        Assert.Equal(ZstdDictionary.Gb7rdg1Id, d.Id);
        Assert.Equal(65536, d.Content.Length);
        Assert.True(Compression.Default.Knows(ZstdDictionary.Gb7rdg1Id));
        Assert.True(Compression.Default.Knows(Compression.NoDictionary));
        Assert.False(Compression.Default.Knows(999));
    }

    [Theory]
    [InlineData(Compression.NoDictionary)]
    [InlineData(ZstdDictionary.Gb7rdg1Id)]
    public void RoundTrip(ushort dictionaryId)
    {
        foreach (int size in new[] { 0, 100, 5000, 30000 })
        {
            var data = TestBulletins.Make(size, size).Serialize();
            var compressed = Compression.Default.Compress(data, dictionaryId);
            Assert.Equal(data, Compression.Default.Decompress(compressed, dictionaryId));
        }
    }

    [Fact]
    public void Dictionary_HelpsOnBulletinText()
    {
        var data = TestBulletins.Make(1, 1500).Serialize();
        int plain = Compression.Default.Compress(data, Compression.NoDictionary).Length;
        int withDictionary = Compression.Default.Compress(data, ZstdDictionary.Gb7rdg1Id).Length;
        Assert.True(withDictionary < plain, $"{withDictionary} vs {plain}");
    }

    [Fact]
    public void Decompress_NeverReturnsDamagedContent()
    {
        // Damage anywhere either fails, mostly by the content checksum, or (in a few octets
        // zstd does not need) changes nothing. It never yields different content.
        var data = TestBulletins.Make(2, 3000).Serialize();
        var compressed = Compression.Default.Compress(data, ZstdDictionary.Gb7rdg1Id);
        int detected = 0;
        for (int i = 0; i < compressed.Length; i++)
        {
            var damaged = compressed.ToArray();
            damaged[i] ^= 0x40;
            try
            {
                Assert.Equal(data, Compression.Default.Decompress(damaged, ZstdDictionary.Gb7rdg1Id));
            }
            catch (InvalidDataException)
            {
                detected++;
            }
        }
        Assert.True(detected > compressed.Length * 9 / 10, $"{detected} of {compressed.Length}");
    }

    [Fact]
    public void Decompress_ChecksTheContentChecksum()
    {
        var data = TestBulletins.Make(4, 3000).Serialize();
        var compressed = Compression.Default.Compress(data, ZstdDictionary.Gb7rdg1Id);
        compressed[^1] ^= 1; // the last four octets are the checksum
        Assert.Throws<InvalidDataException>(() => Compression.Default.Decompress(compressed, ZstdDictionary.Gb7rdg1Id));
    }

    [Fact]
    public void Decompress_RefusesAFrameWithoutAChecksum()
    {
        var data = TestBulletins.Make(5, 3000).Serialize();
        using var compressor = new ZstdSharp.Compressor(3);
        compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 0);
        var noChecksum = compressor.Wrap(data).ToArray();
        Assert.Equal(data, new ZstdSharp.Decompressor().Unwrap(noChecksum).ToArray());
        Assert.Throws<InvalidDataException>(() => Compression.Default.Decompress(noChecksum, Compression.NoDictionary));
    }

    [Fact]
    public void Compress_KeepsZstdsDictionaryId()
    {
        // The frame header descriptor's low two bits give the size of the dictionary ID field.
        var compressed = Compression.Default.Compress(TestBulletins.Make(6, 3000).Serialize(), ZstdDictionary.Gb7rdg1Id);
        Assert.NotEqual(0, compressed[4] & 0x03);
        var dictionary = ZstdDictionary.BuiltIn[0].Content;
        uint dictId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(dictionary.AsSpan(4));
        int fieldSize = (compressed[4] & 0x03) switch { 1 => 1, 2 => 2, _ => 4 };
        int at = 5 + ((compressed[4] & 0x20) == 0 ? 1 : 0); // a window descriptor unless single segment
        uint inFrame = 0;
        for (int i = 0; i < fieldSize; i++)
        {
            inFrame |= (uint)compressed[at + i] << (8 * i);
        }
        Assert.Equal(dictId, inFrame);
    }

    [Fact]
    public void Decompress_WithTheWrongDictionaryFails()
    {
        var data = TestBulletins.Make(3, 3000).Serialize();
        var compressed = Compression.Default.Compress(data, ZstdDictionary.Gb7rdg1Id);
        Assert.Throws<InvalidDataException>(() => Compression.Default.Decompress(compressed, Compression.NoDictionary));
        Assert.Throws<KeyNotFoundException>(() => Compression.Default.Decompress(compressed, 999));
    }
}
