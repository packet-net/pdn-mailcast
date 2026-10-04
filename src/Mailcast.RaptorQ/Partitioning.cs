namespace Mailcast.RaptorQ;

/// <summary>
/// Source block and sub-block partitioning, RFC 6330 section 4.4.1.2. A source block of K
/// symbols is split into N sub-blocks; symbol m of the block is the m-th sub-symbol of each
/// sub-block in turn. With one sub-block, symbols are simply consecutive runs of T octets.
/// </summary>
internal static class Partitioning
{
    /// <summary>Partition[I, J] = (IL, IS, JL, JS).</summary>
    public static (long Il, long Is, long Jl, long Js) Partition(long i, long j)
    {
        long il = (i + j - 1) / j;
        long iS = i / j;
        long jl = i - (iS * j);
        long js = j - jl;
        return (il, iS, jl, js);
    }

    /// <summary>The sub-symbol size in octets of each of the N sub-blocks.</summary>
    private static int[] SubSymbolSizes(ObjectTransmissionInformation oti)
    {
        var (tl, ts, nl, ns) = Partition(oti.SymbolSize / oti.Alignment, oti.SubBlocks);
        var sizes = new int[nl + ns];
        for (int n = 0; n < sizes.Length; n++)
        {
            sizes[n] = (int)(n < nl ? tl : ts) * oti.Alignment;
        }
        return sizes;
    }

    /// <summary>Rearranges a source block's octets (K * T, padded) into its K symbols.</summary>
    public static byte[] BlockToSymbols(ObjectTransmissionInformation oti, ReadOnlySpan<byte> block, int k)
    {
        if (oti.SubBlocks == 1)
        {
            return block.ToArray();
        }
        int t = oti.SymbolSize;
        var symbols = new byte[k * t];
        int blockOffset = 0;
        int symbolOffset = 0;
        foreach (int size in SubSymbolSizes(oti))
        {
            for (int m = 0; m < k; m++)
            {
                block.Slice(blockOffset + (m * size), size).CopyTo(symbols.AsSpan((m * t) + symbolOffset));
            }
            blockOffset += k * size;
            symbolOffset += size;
        }
        return symbols;
    }

    /// <summary>The inverse of <see cref="BlockToSymbols"/>.</summary>
    public static byte[] SymbolsToBlock(ObjectTransmissionInformation oti, ReadOnlySpan<byte> symbols, int k)
    {
        if (oti.SubBlocks == 1)
        {
            return symbols.ToArray();
        }
        int t = oti.SymbolSize;
        var block = new byte[k * t];
        int blockOffset = 0;
        int symbolOffset = 0;
        foreach (int size in SubSymbolSizes(oti))
        {
            for (int m = 0; m < k; m++)
            {
                symbols.Slice((m * t) + symbolOffset, size).CopyTo(block.AsSpan(blockOffset + (m * size)));
            }
            blockOffset += k * size;
            symbolOffset += size;
        }
        return block;
    }
}
