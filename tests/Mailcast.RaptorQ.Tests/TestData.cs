namespace Mailcast.RaptorQ.Tests;

internal static class TestData
{
    /// <summary>The object contents the interop vectors use: xorshift32 from the seed, one step per octet.</summary>
    public static byte[] Xorshift(uint seed, long length)
    {
        var data = new byte[length];
        uint x = seed;
        for (long i = 0; i < length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            data[i] = (byte)x;
        }
        return data;
    }

    /// <summary>Random octets from a seeded generator.</summary>
    public static byte[] Random(int seed, int length)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
