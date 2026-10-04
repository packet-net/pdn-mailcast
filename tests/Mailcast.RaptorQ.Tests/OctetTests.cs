namespace Mailcast.RaptorQ.Tests;

public class OctetTests
{
    [Fact]
    public void OctExp_IsPowersOfAlphaUnderTheRfcPolynomial()
    {
        // RFC 6330 section 5.7.2: GF(256) with the irreducible polynomial x^8 + x^4 + x^3 + x^2 + 1.
        int x = 1;
        for (int i = 0; i < 510; i++)
        {
            Assert.Equal((byte)x, Octet.AlphaPower(i));
            x <<= 1;
            if (x >= 256)
            {
                x ^= 0x11D;
            }
        }
    }

    [Fact]
    public void MulAndDiv_AreInverse()
    {
        for (int u = 0; u < 256; u++)
        {
            for (int v = 1; v < 256; v++)
            {
                byte product = Octet.Mul((byte)u, (byte)v);
                Assert.Equal(product, Octet.Mul((byte)v, (byte)u));
                Assert.Equal((byte)u, Octet.Div(product, (byte)v));
            }
        }
    }

    [Fact]
    public void Inverse_GivesOne()
    {
        for (int u = 1; u < 256; u++)
        {
            Assert.Equal(1, Octet.Mul((byte)u, Octet.Inverse((byte)u)));
        }
    }

    [Fact]
    public void MulAddAssign_MatchesOctetByOctet()
    {
        var src = TestData.Random(1, 100);
        var dst = TestData.Random(2, 100);
        var expected = dst.ToArray();
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] ^= Octet.Mul(0x53, src[i]);
        }
        Octet.MulAddAssign(dst, src, 0x53);
        Assert.Equal(expected, dst);
    }

    [Fact]
    public void AddAssign_HandlesLengthsAroundTheVectorWidth()
    {
        for (int length = 0; length < 100; length++)
        {
            var src = TestData.Random(length, length);
            var dst = TestData.Random(length + 1000, length);
            var expected = dst.Select((b, i) => (byte)(b ^ src[i])).ToArray();
            Octet.AddAssign(dst, src);
            Assert.Equal(expected, dst);
        }
    }
}
