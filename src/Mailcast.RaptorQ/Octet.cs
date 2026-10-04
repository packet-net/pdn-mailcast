using System.Numerics;
using System.Runtime.InteropServices;

namespace Mailcast.RaptorQ;

/// <summary>
/// Arithmetic on octets as elements of GF(256), RFC 6330 section 5.7, and the symbol operations
/// built on it (section 5.7.5). Addition is exclusive-or.
/// </summary>
internal static class Octet
{
    /// <summary>OCT_EXP, 510 entries, so the sum of two logarithms indexes it directly.</summary>
    private static readonly byte[] Exp = RfcTables.OctExp.ToArray();

    /// <summary>OCT_LOG indexed by octet; entry 0 is unused.</summary>
    private static readonly byte[] Log = BuildLog();

    /// <summary>Full product table, MulTable[a * 256 + b] = a * b.</summary>
    private static readonly byte[] MulTable = BuildMulTable();

    private static byte[] BuildLog()
    {
        var log = new byte[256];
        RfcTables.OctLogFrom1.CopyTo(log.AsSpan(1));
        return log;
    }

    private static byte[] BuildMulTable()
    {
        var table = new byte[256 * 256];
        for (int a = 1; a < 256; a++)
        {
            for (int b = 1; b < 256; b++)
            {
                table[(a << 8) | b] = Exp[Log[a] + Log[b]];
            }
        }
        return table;
    }

    /// <summary>The octet alpha^^i, for 0 &lt;= i &lt; 510.</summary>
    public static byte AlphaPower(int i) => Exp[i];

    public static byte Mul(byte u, byte v) => MulTable[(u << 8) | v];

    public static byte Div(byte u, byte v)
    {
        if (v == 0)
        {
            throw new DivideByZeroException();
        }
        return u == 0 ? (byte)0 : Exp[Log[u] - Log[v] + 255];
    }

    public static byte Inverse(byte u)
    {
        if (u == 0)
        {
            throw new DivideByZeroException();
        }
        return Exp[255 - Log[u]];
    }

    /// <summary>dst = dst + src (exclusive-or).</summary>
    public static void AddAssign(Span<byte> dst, ReadOnlySpan<byte> src)
    {
        if (dst.Length != src.Length)
        {
            throw new ArgumentException("Symbol lengths differ.", nameof(src));
        }
        int i = 0;
        if (Vector.IsHardwareAccelerated && dst.Length >= Vector<byte>.Count)
        {
            var dv = MemoryMarshal.Cast<byte, Vector<byte>>(dst);
            var sv = MemoryMarshal.Cast<byte, Vector<byte>>(src);
            for (int j = 0; j < dv.Length; j++)
            {
                dv[j] ^= sv[j];
            }
            i = dv.Length * Vector<byte>.Count;
        }
        for (; i < dst.Length; i++)
        {
            dst[i] ^= src[i];
        }
    }

    /// <summary>dst = dst + beta * src.</summary>
    public static void MulAddAssign(Span<byte> dst, ReadOnlySpan<byte> src, byte beta)
    {
        if (beta == 0)
        {
            return;
        }
        if (beta == 1)
        {
            AddAssign(dst, src);
            return;
        }
        if (dst.Length != src.Length)
        {
            throw new ArgumentException("Symbol lengths differ.", nameof(src));
        }
        var row = MulTable.AsSpan(beta << 8, 256);
        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] ^= row[src[i]];
        }
    }

    /// <summary>dst = beta * dst.</summary>
    public static void MulAssign(Span<byte> dst, byte beta)
    {
        if (beta == 1)
        {
            return;
        }
        var row = MulTable.AsSpan(beta << 8, 256);
        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] = row[dst[i]];
        }
    }
}
