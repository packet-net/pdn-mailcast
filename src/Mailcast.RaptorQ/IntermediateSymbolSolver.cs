using System.Numerics;

namespace Mailcast.RaptorQ;

/// <summary>
/// Solves A * C = D for the L intermediate symbols C of one extended source block
/// (RFC 6330 sections 5.3.3.4 and 5.4).
/// </summary>
/// <remarks>
/// <para>
/// This follows the shape of the RFC's example decoder rather than its exact steps. The binary
/// rows (the S LDPC rows and one row per received symbol) are worked through first, as in the
/// first phase of section 5.4.2.2: the row with the fewest nonzeros among the still active
/// columns is chosen, one of those columns becomes its pivot, and its other active columns are
/// inactivated. The PI columns start inactive. Because a chosen row has no other active column
/// left, eliminating its pivot from the other rows never adds a nonzero to an active column, so
/// rows stay sparse and only the inactive part, held as a bitset per row, fills in.
/// </para>
/// <para>
/// What is left is a small dense system over the inactive columns: the binary rows that were
/// never chosen, plus the H HDPC rows with the pivots substituted out. It is solved by Gaussian
/// elimination over GF(256). Each pivot then follows from its row and the inactive symbols.
/// </para>
/// <para>
/// The result is exact: it succeeds whenever A has rank L, whichever symbols were received.
/// </para>
/// </remarks>
internal static class IntermediateSymbolSolver
{
    private const byte Active = 0;
    private const byte Inactive = 1;
    private const byte Pivoted = 2;

    /// <summary>
    /// Returns the L intermediate symbols, each <paramref name="symbolSize"/> octets, as one
    /// array, or null if the received symbols do not determine them.
    /// </summary>
    /// <param name="p">The block's parameters.</param>
    /// <param name="isis">The ISI of each received symbol, padding symbols included.</param>
    /// <param name="symbols">The received symbols in the same order, concatenated.</param>
    /// <param name="symbolSize">The symbol size T.</param>
    public static byte[]? Solve(BlockParameters p, ReadOnlySpan<uint> isis, ReadOnlySpan<byte> symbols, int symbolSize)
    {
        int t = symbolSize;
        int l = p.L;
        int rowCount = p.S + isis.Length;
        if (isis.Length < p.KPrime)
        {
            return null;
        }

        // Binary rows as sorted lists of their nonzero columns.
        int[][] rowCols = BuildBinaryRows(p, isis);

        // Which rows have a nonzero in each column, in compressed form.
        var colStart = new int[l + 1];
        foreach (var cols in rowCols)
        {
            foreach (int c in cols)
            {
                colStart[c + 1]++;
            }
        }
        for (int c = 0; c < l; c++)
        {
            colStart[c + 1] += colStart[c];
        }
        var colRows = new int[colStart[l]];
        var fill = colStart.AsSpan(0, l).ToArray();
        for (int r = 0; r < rowCount; r++)
        {
            foreach (int c in rowCols[r])
            {
                colRows[fill[c]++] = r;
            }
        }

        var data = new byte[rowCount * t];
        symbols[..(isis.Length * t)].CopyTo(data.AsSpan(p.S * t));

        var state = new State(rowCount, p.P);
        var colState = new byte[l];
        var qIndexOfCol = new int[l];
        Array.Fill(qIndexOfCol, -1);
        var pivotRowOfCol = new int[l];
        Array.Fill(pivotRowOfCol, -1);
        var isPivotRow = new bool[rowCount];
        var activeWeight = new int[rowCount];
        var queue = new PriorityQueue<int, long>();

        for (int r = 0; r < rowCount; r++)
        {
            foreach (int c in rowCols[r])
            {
                if (c < p.W)
                {
                    activeWeight[r]++;
                }
            }
        }

        // The PI columns start inactive.
        for (int c = p.W; c < l; c++)
        {
            Inactivate(c);
        }

        for (int r = 0; r < rowCount; r++)
        {
            if (activeWeight[r] > 0)
            {
                queue.Enqueue(r, Priority(r));
            }
        }

        while (queue.TryDequeue(out int chosen, out long priority))
        {
            if (isPivotRow[chosen] || activeWeight[chosen] == 0 || priority != Priority(chosen))
            {
                continue; // stale entry
            }

            int pivotCol = -1;
            foreach (int c in rowCols[chosen])
            {
                if (colState[c] != Active)
                {
                    continue;
                }
                if (pivotCol < 0)
                {
                    pivotCol = c;
                }
                else
                {
                    Inactivate(c);
                }
            }

            colState[pivotCol] = Pivoted;
            pivotRowOfCol[pivotCol] = chosen;
            isPivotRow[chosen] = true;
            var chosenData = data.AsSpan(chosen * t, t);
            for (int i = colStart[pivotCol]; i < colStart[pivotCol + 1]; i++)
            {
                int r = colRows[i];
                if (r == chosen || isPivotRow[r])
                {
                    continue;
                }
                state.XorRow(r, chosen);
                Octet.AddAssign(data.AsSpan(r * t, t), chosenData);
                activeWeight[r]--;
                if (activeWeight[r] > 0)
                {
                    queue.Enqueue(r, Priority(r));
                }
            }
        }

        // Any column still active is in no unchosen row; only the HDPC rows can settle it.
        for (int c = 0; c < l; c++)
        {
            if (colState[c] == Active)
            {
                Inactivate(c);
            }
        }

        int q = state.Count;
        var qCols = new int[q];
        for (int c = 0; c < l; c++)
        {
            if (qIndexOfCol[c] >= 0)
            {
                qCols[qIndexOfCol[c]] = c;
            }
        }

        // The dense system over the inactive columns.
        var denseRows = new List<byte[]>();
        var denseData = new List<byte[]>();
        for (int r = 0; r < rowCount; r++)
        {
            if (isPivotRow[r] || state.IsZero(r))
            {
                continue;
            }
            var coeffs = new byte[q];
            state.Expand(r, coeffs);
            denseRows.Add(coeffs);
            denseData.Add(data.AsSpan(r * t, t).ToArray());
        }

        byte[] hdpc = BuildHdpcRows(p);
        for (int h = 0; h < p.H; h++)
        {
            var coeffs = new byte[q];
            var hData = new byte[t];
            var row = hdpc.AsSpan(h * l, l);
            for (int c = 0; c < l; c++)
            {
                byte beta = row[c];
                if (beta == 0)
                {
                    continue;
                }
                if (colState[c] == Pivoted)
                {
                    int pr = pivotRowOfCol[c];
                    state.MulAddInto(pr, coeffs, beta);
                    Octet.MulAddAssign(hData, data.AsSpan(pr * t, t), beta);
                }
                else
                {
                    coeffs[qIndexOfCol[c]] ^= beta;
                }
            }
            denseRows.Add(coeffs);
            denseData.Add(hData);
        }

        if (!SolveDense(denseRows, denseData, q))
        {
            return null;
        }

        var result = new byte[l * t];
        for (int i = 0; i < q; i++)
        {
            denseData[i].CopyTo(result.AsSpan(qCols[i] * t, t));
        }
        for (int c = 0; c < l; c++)
        {
            int pr = pivotRowOfCol[c];
            if (pr < 0)
            {
                continue;
            }
            var dst = result.AsSpan(c * t, t);
            data.AsSpan(pr * t, t).CopyTo(dst);
            foreach (int i in state.SetBits(pr))
            {
                Octet.AddAssign(dst, result.AsSpan(qCols[i] * t, t));
            }
        }
        return result;

        long Priority(int r) => ((long)activeWeight[r] << 32) | (uint)rowCols[r].Length;

        void Inactivate(int c)
        {
            colState[c] = Inactive;
            int qi = state.AddColumn();
            qIndexOfCol[c] = qi;
            for (int i = colStart[c]; i < colStart[c + 1]; i++)
            {
                int r = colRows[i];
                if (isPivotRow[r])
                {
                    continue;
                }
                state.Toggle(r, qi);
                if (c < p.W)
                {
                    activeWeight[r]--;
                    if (activeWeight[r] > 0)
                    {
                        queue.Enqueue(r, Priority(r));
                    }
                }
            }
        }
    }

    /// <summary>
    /// The S LDPC rows (section 5.3.3.3) then one row per received symbol (section 5.3.5.3), as
    /// sorted column lists. A column summed an even number of times cancels out.
    /// </summary>
    private static int[][] BuildBinaryRows(BlockParameters p, ReadOnlySpan<uint> isis)
    {
        var rows = new int[p.S + isis.Length][];
        var ldpc = new List<int>[p.S];
        for (int i = 0; i < p.S; i++)
        {
            ldpc[i] = [];
        }
        for (int i = 0; i < p.B; i++)
        {
            int a = 1 + (i / p.S);
            int b = i % p.S;
            ldpc[b].Add(i);
            b = (b + a) % p.S;
            ldpc[b].Add(i);
            b = (b + a) % p.S;
            ldpc[b].Add(i);
        }
        for (int i = 0; i < p.S; i++)
        {
            ldpc[i].Add(p.B + i);
            ldpc[i].Add(p.W + (i % p.P));
            ldpc[i].Add(p.W + ((i + 1) % p.P));
            rows[i] = CancelPairs(ldpc[i]);
        }

        Span<int> indices = stackalloc int[BlockParameters.MaxEncodingIndices];
        var scratch = new List<int>(BlockParameters.MaxEncodingIndices);
        for (int j = 0; j < isis.Length; j++)
        {
            int n = p.EncodingIndices(isis[j], indices);
            scratch.Clear();
            for (int k = 0; k < n; k++)
            {
                scratch.Add(indices[k]);
            }
            rows[p.S + j] = CancelPairs(scratch);
        }
        return rows;
    }

    private static int[] CancelPairs(List<int> cols)
    {
        cols.Sort();
        var result = new List<int>(cols.Count);
        int i = 0;
        while (i < cols.Count)
        {
            int j = i;
            while (j < cols.Count && cols[j] == cols[i])
            {
                j++;
            }
            if ((j - i) % 2 == 1)
            {
                result.Add(cols[i]);
            }
            i = j;
        }
        return [.. result];
    }

    /// <summary>
    /// The H HDPC rows of A, G_HDPC | I_H (section 5.3.3.3), as H rows of L octets.
    /// G_HDPC = MT * GAMMA is formed column by column from the right, since column j of it is
    /// column j of MT plus alpha times column j + 1 of it.
    /// </summary>
    internal static byte[] BuildHdpcRows(BlockParameters p)
    {
        int l = p.L;
        int width = p.KPrime + p.S;
        var rows = new byte[p.H * l];
        for (int h = 0; h < p.H; h++)
        {
            rows[(h * l) + width - 1] = Octet.AlphaPower(h);
        }
        for (int j = width - 2; j >= 0; j--)
        {
            for (int h = 0; h < p.H; h++)
            {
                rows[(h * l) + j] = Octet.Mul(Octet.AlphaPower(1), rows[(h * l) + j + 1]);
            }
            uint r6 = BlockParameters.Rand((uint)(j + 1), 6, (uint)p.H);
            uint r7 = BlockParameters.Rand((uint)(j + 1), 7, (uint)(p.H - 1));
            int h1 = (int)r6;
            int h2 = (int)((r6 + r7 + 1) % (uint)p.H);
            rows[(h1 * l) + j] ^= 1;
            rows[(h2 * l) + j] ^= 1;
        }
        for (int h = 0; h < p.H; h++)
        {
            rows[(h * l) + width + h] = 1;
        }
        return rows;
    }

    /// <summary>
    /// Gauss-Jordan elimination over GF(256). On success the first q rows hold the identity and
    /// their data holds the solution, in column order.
    /// </summary>
    private static bool SolveDense(List<byte[]> rows, List<byte[]> data, int q)
    {
        int n = rows.Count;
        for (int col = 0; col < q; col++)
        {
            int pivot = -1;
            for (int r = col; r < n; r++)
            {
                if (rows[r][col] != 0)
                {
                    pivot = r;
                    break;
                }
            }
            if (pivot < 0)
            {
                return false;
            }
            (rows[col], rows[pivot]) = (rows[pivot], rows[col]);
            (data[col], data[pivot]) = (data[pivot], data[col]);
            var pivotRow = rows[col];
            var pivotData = data[col];
            byte inv = Octet.Inverse(pivotRow[col]);
            if (inv != 1)
            {
                Octet.MulAssign(pivotRow.AsSpan(col), inv);
                Octet.MulAssign(pivotData, inv);
            }
            for (int r = 0; r < n; r++)
            {
                if (r == col)
                {
                    continue;
                }
                byte beta = rows[r][col];
                if (beta == 0)
                {
                    continue;
                }
                Octet.MulAddAssign(rows[r].AsSpan(col), pivotRow.AsSpan(col), beta);
                Octet.MulAddAssign(data[r], pivotData, beta);
            }
        }
        return true;
    }

    /// <summary>
    /// The inactive part of every binary row, as a bitset over the inactive columns in the order
    /// they were inactivated. Grows as columns are inactivated.
    /// </summary>
    private sealed class State
    {
        private readonly int _rows;
        private ulong[] _bits;
        private int _words;

        public State(int rows, int initialColumns)
        {
            _rows = rows;
            _words = Math.Max(1, (initialColumns + 64) / 64);
            _bits = new ulong[rows * _words];
        }

        public int Count { get; private set; }

        public int AddColumn()
        {
            if (Count == _words * 64)
            {
                int words = _words * 2;
                var bits = new ulong[_rows * words];
                for (int r = 0; r < _rows; r++)
                {
                    _bits.AsSpan(r * _words, _words).CopyTo(bits.AsSpan(r * words));
                }
                _bits = bits;
                _words = words;
            }
            return Count++;
        }

        public void Toggle(int row, int qi) => _bits[(row * _words) + (qi >> 6)] ^= 1UL << (qi & 63);

        public void XorRow(int dst, int src)
        {
            int used = (Count + 63) >> 6;
            var d = _bits.AsSpan(dst * _words, used);
            var s = _bits.AsSpan(src * _words, used);
            for (int i = 0; i < used; i++)
            {
                d[i] ^= s[i];
            }
        }

        public bool IsZero(int row)
        {
            foreach (ulong w in _bits.AsSpan(row * _words, _words))
            {
                if (w != 0)
                {
                    return false;
                }
            }
            return true;
        }

        public void Expand(int row, byte[] coeffs)
        {
            foreach (int i in SetBits(row))
            {
                coeffs[i] = 1;
            }
        }

        public void MulAddInto(int row, byte[] coeffs, byte beta)
        {
            foreach (int i in SetBits(row))
            {
                coeffs[i] ^= beta;
            }
        }

        public List<int> SetBits(int row)
        {
            var result = new List<int>();
            var span = _bits.AsSpan(row * _words, _words);
            for (int w = 0; w < span.Length; w++)
            {
                ulong v = span[w];
                while (v != 0)
                {
                    int b = BitOperations.TrailingZeroCount(v);
                    result.Add((w << 6) + b);
                    v &= v - 1;
                }
            }
            return result;
        }
    }
}
