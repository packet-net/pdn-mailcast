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
    public void Decompress_WithTheWrongDictionaryFails()
    {
        var data = TestBulletins.Make(3, 3000).Serialize();
        var compressed = Compression.Default.Compress(data, ZstdDictionary.Gb7rdg1Id);
        Assert.Throws<InvalidDataException>(() => Compression.Default.Decompress(compressed, Compression.NoDictionary));
        Assert.Throws<KeyNotFoundException>(() => Compression.Default.Decompress(compressed, 999));
    }
}
