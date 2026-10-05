namespace Mailcast.RaptorQ;

/// <summary>
/// The parameters RFC 6330 derives from the number of source symbols in a source block
/// (sections 5.3.1 and 5.3.3.3), and the generators of section 5.3.5 that depend on them.
/// </summary>
internal sealed class BlockParameters
{
    /// <summary>The largest number of source symbols in one source block, K'_max.</summary>
    public const int MaxSourceSymbols = 56403;

    private static readonly int[] DegreeTable =
    [
        0, 5243, 529531, 704294, 791675, 844104, 879057, 904023, 922747, 937311, 948962,
        958494, 966438, 973160, 978921, 983914, 988283, 992138, 995565, 998631, 1001391,
        1003887, 1006157, 1008229, 1010129, 1011876, 1013490, 1014983, 1016370, 1017662,
        1048576,
    ];

    private BlockParameters(int k, int row)
    {
        var table = RfcTables.SystematicIndices;
        K = k;
        KPrime = table[row * 5];
        J = table[(row * 5) + 1];
        S = table[(row * 5) + 2];
        H = table[(row * 5) + 3];
        W = table[(row * 5) + 4];
        L = KPrime + S + H;
        P = L - W;
        P1 = NextPrime(P);
        U = P - H;
        B = W - S;
    }

    /// <summary>Source symbols in the block, K.</summary>
    public int K { get; }

    /// <summary>Source plus padding symbols in the extended block, K'.</summary>
    public int KPrime { get; }

    /// <summary>Systematic index J(K').</summary>
    public int J { get; }

    /// <summary>LDPC symbols, S(K').</summary>
    public int S { get; }

    /// <summary>HDPC symbols, H(K').</summary>
    public int H { get; }

    /// <summary>LT symbols, W(K').</summary>
    public int W { get; }

    /// <summary>Intermediate symbols, L = K' + S + H.</summary>
    public int L { get; }

    /// <summary>PI symbols, P = L - W.</summary>
    public int P { get; }

    /// <summary>The smallest prime at least P.</summary>
    public int P1 { get; }

    /// <summary>PI symbols that are not HDPC symbols, U = P - H.</summary>
    public int U { get; }

    /// <summary>LT symbols that are not LDPC symbols, B = W - S.</summary>
    public int B { get; }

    /// <summary>Parameters for a source block of K source symbols, 1 &lt;= K &lt;= K'_max.</summary>
    public static BlockParameters ForSourceSymbols(int k)
    {
        if (k < 1 || k > MaxSourceSymbols)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, $"A source block holds 1 to {MaxSourceSymbols} symbols.");
        }
        return new BlockParameters(k, RowFor(k));
    }

    /// <summary>The smallest K' in Table 2 that is at least K.</summary>
    public static int ExtendedSize(int k) => RfcTables.SystematicIndices[RowFor(k) * 5];

    private static int RowFor(int k)
    {
        var table = RfcTables.SystematicIndices;
        int lo = 0;
        int hi = (table.Length / 5) - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (table[mid * 5] >= k)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return lo;
    }

    private static int NextPrime(int n)
    {
        for (int candidate = Math.Max(n, 2); ; candidate++)
        {
            bool prime = true;
            for (int d = 2; d * d <= candidate; d++)
            {
                if (candidate % d == 0)
                {
                    prime = false;
                    break;
                }
            }
            if (prime)
            {
                return candidate;
            }
        }
    }

    /// <summary>The internal symbol ID of an encoding symbol ID (section 5.3.1).</summary>
    public long IsiOf(long esi) => esi < K ? esi : esi + (KPrime - K);

    /// <summary>Rand[y, i, m], section 5.3.5.1.</summary>
    public static uint Rand(uint y, int i, uint m)
    {
        uint x0 = (y + (uint)i) & 0xFF;
        uint x1 = ((y >> 8) + (uint)i) & 0xFF;
        uint x2 = ((y >> 16) + (uint)i) & 0xFF;
        uint x3 = ((y >> 24) + (uint)i) & 0xFF;
        return (RfcTables.V0[(int)x0] ^ RfcTables.V1[(int)x1] ^ RfcTables.V2[(int)x2] ^ RfcTables.V3[(int)x3]) % m;
    }

    /// <summary>Deg[v], section 5.3.5.2.</summary>
    public int Degree(uint v)
    {
        int d = 1;
        while (v >= DegreeTable[d])
        {
            d++;
        }
        return Math.Min(d, W - 2);
    }

    /// <summary>Tuple[K', X], section 5.3.5.4.</summary>
    public SymbolTuple Tuple(uint x)
    {
        uint a = 53591u + ((uint)J * 997u);
        if (a % 2 == 0)
        {
            a++;
        }
        uint b = 10267u * ((uint)J + 1u);
        uint y = unchecked(b + (x * a));
        uint v = Rand(y, 0, 1u << 20);
        int d = Degree(v);
        int ta = 1 + (int)Rand(y, 1, (uint)(W - 1));
        int tb = (int)Rand(y, 2, (uint)W);
        int d1 = d < 4 ? 2 + (int)Rand(x, 3, 2) : 2;
        int a1 = 1 + (int)Rand(x, 4, (uint)(P1 - 1));
        int b1 = (int)Rand(x, 5, (uint)P1);
        return new SymbolTuple(d, ta, tb, d1, a1, b1);
    }

    /// <summary>
    /// The intermediate symbol indices whose sum is the encoding symbol with ISI x, as Enc[]
    /// in section 5.3.5.3 visits them. An index can appear more than once only if the sum
    /// visits it more than once, in which case the repeats cancel.
    /// </summary>
    public int EncodingIndices(uint x, Span<int> indices)
    {
        var t = Tuple(x);
        int n = 0;
        int b = t.B;
        indices[n++] = b;
        for (int j = 1; j < t.D; j++)
        {
            b = (b + t.A) % W;
            indices[n++] = b;
        }
        int b1 = t.B1;
        while (b1 >= P)
        {
            b1 = (b1 + t.A1) % P1;
        }
        indices[n++] = W + b1;
        for (int j = 1; j < t.D1; j++)
        {
            b1 = (b1 + t.A1) % P1;
            while (b1 >= P)
            {
                b1 = (b1 + t.A1) % P1;
            }
            indices[n++] = W + b1;
        }
        return n;
    }

    /// <summary>The most indices <see cref="EncodingIndices"/> can return: degree at most 30, plus 3.</summary>
    public const int MaxEncodingIndices = 33;
}

/// <summary>The tuple (d, a, b, d1, a1, b1) of section 5.3.5.4.</summary>
internal readonly record struct SymbolTuple(int D, int A, int B, int D1, int A1, int B1);
