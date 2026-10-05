namespace Mailcast.RaptorQ.Tests;

public class DecodeTests
{
    private static ObjectEncoder Encoder(int seed, int length, ObjectTransmissionInformation oti) =>
        new(TestData.Random(seed, length), oti);

    [Fact]
    public void AllSourceSymbols_DecodeWithoutRepair()
    {
        var oti = new ObjectTransmissionInformation(1000, 64);
        var data = TestData.Random(1, 1000);
        var encoder = new ObjectEncoder(data, oti);
        var decoder = new ObjectDecoder(oti);
        for (uint esi = 0; esi < 16; esi++)
        {
            decoder.Add(new PayloadId(0, esi), encoder.Encode(new PayloadId(0, esi)));
        }
        Assert.Equal(data, decoder.TryDecode());
    }

    [Fact]
    public void RepairOnly_Decodes()
    {
        var oti = new ObjectTransmissionInformation(5000, 50);
        var data = TestData.Random(2, 5000);
        var encoder = new ObjectEncoder(data, oti);
        var decoder = new ObjectDecoder(oti);
        uint esi = 100;
        while (decoder.TryDecode() is null)
        {
            decoder.Add(new PayloadId(0, esi), encoder.Encode(new PayloadId(0, esi)));
            esi++;
        }
        Assert.Equal(data, decoder.TryDecode());
        Assert.InRange(decoder.Block(0).ReceivedCount, 100, 103);
    }

    [Fact]
    public void FewerThanK_NeverDecodes()
    {
        var oti = new ObjectTransmissionInformation(1000, 10);
        var encoder = Encoder(3, 1000, oti);
        var decoder = new ObjectDecoder(oti);
        for (uint esi = 1; esi < 100; esi++)
        {
            decoder.Add(new PayloadId(0, esi), encoder.Encode(new PayloadId(0, esi)));
            Assert.Null(decoder.TryDecode());
        }
    }

    [Theory]
    [InlineData(0.1, 11)]
    [InlineData(0.3, 12)]
    [InlineData(0.5, 13)]
    [InlineData(0.8, 14)]
    public void RandomLoss_DecodesFromWhateverArrives(double loss, int seed)
    {
        // A sender sends ESIs in order, source first, and each is lost independently.
        const int t = 940;
        var data = TestData.Random(seed, 60_000);
        var oti = ObjectTransmissionInformation.ForObject(data.Length, t);
        var encoder = new ObjectEncoder(data, oti);
        var decoder = new ObjectDecoder(oti);
        var rng = new Random(seed);
        int k = oti.SourceBlockSymbols(0);
        uint esi = 0;
        byte[]? result = null;
        while (result is null)
        {
            Assert.True(esi < 10 * k, "never decoded");
            if (rng.NextDouble() >= loss)
            {
                decoder.Add(new PayloadId(0, esi), encoder.Encode(new PayloadId(0, esi)));
                result = decoder.TryDecode();
            }
            esi++;
        }
        Assert.Equal(data, result);
        Assert.InRange(decoder.Block(0).ReceivedCount, k, k + 5);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public void BurstLoss_Decodes(int seed)
    {
        // Two-state fades: in a fade nearly everything is lost, out of one nearly nothing.
        const int t = 200;
        var data = TestData.Random(seed, 40_000);
        var oti = new ObjectTransmissionInformation(data.Length, t);
        var encoder = new ObjectEncoder(data, oti);
        var decoder = new ObjectDecoder(oti);
        var rng = new Random(seed);
        bool fade = false;
        uint esi = 0;
        byte[]? result = null;
        while (result is null)
        {
            Assert.True(esi < 2000, "never decoded");
            fade = fade ? rng.NextDouble() > 0.1 : rng.NextDouble() < 0.05;
            bool lost = fade ? rng.NextDouble() < 0.95 : rng.NextDouble() < 0.02;
            if (!lost)
            {
                decoder.Add(new PayloadId(0, esi), encoder.Encode(new PayloadId(0, esi)));
                result = decoder.TryDecode();
            }
            esi++;
        }
        Assert.Equal(data, result);
    }

    [Fact]
    public void SingleSymbolObject_K1()
    {
        var data = TestData.Random(30, 7);
        var oti = new ObjectTransmissionInformation(7, 8);
        var encoder = new ObjectEncoder(data, oti);

        var fromSource = new ObjectDecoder(oti);
        fromSource.Add(new PayloadId(0, 0), encoder.Encode(new PayloadId(0, 0)));
        Assert.Equal(data, fromSource.TryDecode());

        // With K = 1 the block is extended to K' = 10, so some lone repair symbols say nothing
        // about the one real symbol. Taking repair symbols in order always gets there.
        var fromRepair = new ObjectDecoder(oti);
        uint esi = 1;
        while (fromRepair.TryDecode() is null)
        {
            fromRepair.Add(new PayloadId(0, esi), encoder.Encode(new PayloadId(0, esi)));
            esi++;
            Assert.True(esi < 50);
        }
        Assert.Equal(data, fromRepair.TryDecode());
    }

    [Fact]
    public void LargestBlock_KMax()
    {
        // K = K'_max = 56403, the largest block RFC 6330 allows, with one-octet symbols.
        const int k = BlockParameters.MaxSourceSymbols;
        var data = TestData.Random(40, k);
        var oti = new ObjectTransmissionInformation(k, 1);
        var encoder = new ObjectEncoder(data, oti);
        var decoder = new ObjectDecoder(oti);
        var rng = new Random(40);
        int added = 0;
        for (uint esi = 0; added < k + 2; esi++)
        {
            if (rng.NextDouble() >= 0.2)
            {
                decoder.Add(new PayloadId(0, esi), encoder.Encode(new PayloadId(0, esi)));
                added++;
            }
        }
        Assert.Equal(data, decoder.TryDecode());
    }

    [Theory]
    [InlineData(1000, 100)] // exact multiple, no padding
    [InlineData(1001, 100)] // one octet in the last symbol
    [InlineData(1099, 100)] // last symbol one octet short
    public void LastSymbolPadding_IsInvisibleAndMayBeOmitted(int f, int t)
    {
        var data = TestData.Random(f, f);
        var oti = new ObjectTransmissionInformation(f, t);
        var encoder = new ObjectEncoder(data, oti);
        int k = oti.SourceBlockSymbols(0);

        var padded = encoder.Encode(new PayloadId(0, (uint)(k - 1)));
        int tail = f - ((k - 1) * t);
        Assert.All(padded.AsSpan(tail).ToArray(), b => Assert.Equal(0, b));

        // The last source symbol sent without its padding, the rest lost and replaced by repair.
        var decoder = new ObjectDecoder(oti);
        decoder.Add(new PayloadId(0, (uint)(k - 1)), padded.AsSpan(0, tail));
        for (uint esi = (uint)k; decoder.TryDecode() is null; esi++)
        {
            decoder.Add(new PayloadId(0, esi), encoder.Encode(new PayloadId(0, esi)));
        }
        var result = decoder.TryDecode();
        Assert.Equal(f, result!.Length);
        Assert.Equal(data, result);
    }

    [Fact]
    public void ShortSymbol_OtherThanTheLast_IsRejected()
    {
        var oti = new ObjectTransmissionInformation(1000, 100);
        var decoder = new ObjectDecoder(oti);
        Assert.Throws<ArgumentException>(() => decoder.Add(new PayloadId(0, 3), new byte[99]));
        Assert.Throws<ArgumentException>(() => decoder.Add(new PayloadId(0, 20), new byte[99]));
    }

    [Fact]
    public void Duplicates_AreIgnored()
    {
        var oti = new ObjectTransmissionInformation(1000, 100);
        var encoder = Encoder(50, 1000, oti);
        var decoder = new ObjectDecoder(oti);
        var id = new PayloadId(0, 12);
        Assert.True(decoder.Add(id, encoder.Encode(id)));
        Assert.False(decoder.Add(id, encoder.Encode(id)));
        Assert.Equal(1, decoder.Block(0).ReceivedCount);
    }

    [Theory]
    [InlineData(10_000, 64, 2, 2, 8)]
    [InlineData(5_003, 48, 3, 5, 4)]
    [InlineData(20_000, 100, 7, 3, 1)]
    public void SourceAndSubBlocks_RoundTripUnderLoss(int f, int t, int z, int n, int al)
    {
        var data = TestData.Random(f + z, f);
        var oti = new ObjectTransmissionInformation(f, t, z, n, al);
        var encoder = new ObjectEncoder(data, oti);
        var decoder = new ObjectDecoder(oti);
        var rng = new Random(f);
        for (uint esi = 0; decoder.TryDecode() is null; esi++)
        {
            Assert.True(esi < 1000);
            for (int sbn = 0; sbn < z; sbn++)
            {
                if (rng.NextDouble() >= 0.4)
                {
                    var id = new PayloadId(sbn, esi);
                    decoder.Add(id, encoder.Encode(id));
                }
            }
        }
        Assert.Equal(data, decoder.TryDecode());
    }

    [Fact]
    public void LargestEsi_Encodes()
    {
        var oti = new ObjectTransmissionInformation(1000, 100);
        var encoder = Encoder(60, 1000, oti);
        var decoder = new ObjectDecoder(oti);
        for (uint esi = PayloadId.MaxEncodingSymbolId; decoder.TryDecode() is null; esi--)
        {
            decoder.Add(new PayloadId(0, esi), encoder.Encode(new PayloadId(0, esi)));
        }
        Assert.Equal(TestData.Random(60, 1000), decoder.TryDecode());
    }
}
