namespace Mailcast.RaptorQ;

/// <summary>
/// Encodes one source block of K symbols (RFC 6330 section 5.3). Any encoding symbol can be
/// produced, by its encoding symbol ID: 0 to K - 1 are the source symbols themselves, K and up
/// are repair symbols.
/// </summary>
public sealed class SourceBlockEncoder
{
    private readonly BlockParameters _p;
    private readonly byte[] _source;
    private readonly byte[] _intermediate;

    /// <summary>Prepares to encode a source block.</summary>
    /// <param name="sourceSymbolCount">K, from 1 to 56403.</param>
    /// <param name="symbolSize">T, the size of each symbol in octets.</param>
    /// <param name="sourceSymbols">The K source symbols, concatenated: K * T octets.</param>
    public SourceBlockEncoder(int sourceSymbolCount, int symbolSize, ReadOnlySpan<byte> sourceSymbols)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolSize, 1);
        _p = BlockParameters.ForSourceSymbols(sourceSymbolCount);
        if (sourceSymbols.Length != sourceSymbolCount * symbolSize)
        {
            throw new ArgumentException($"Expected {sourceSymbolCount} symbols of {symbolSize} octets.", nameof(sourceSymbols));
        }
        SymbolSize = symbolSize;
        _source = sourceSymbols.ToArray();

        // Solve for the intermediate symbols from the K' symbols of the extended source block.
        var isis = new uint[_p.KPrime];
        for (int i = 0; i < isis.Length; i++)
        {
            isis[i] = (uint)i;
        }
        var extended = new byte[_p.KPrime * symbolSize];
        _source.CopyTo(extended, 0);
        _intermediate = IntermediateSymbolSolver.Solve(_p, isis, extended, symbolSize)
            ?? throw new InvalidOperationException($"RFC 6330 guarantees A is invertible for K' = {_p.KPrime}; the solver failed.");
    }

    /// <summary>K, the number of source symbols.</summary>
    public int SourceSymbolCount => _p.K;

    /// <summary>K', the number of symbols in the extended source block.</summary>
    public int ExtendedSourceSymbolCount => _p.KPrime;

    /// <summary>T, the symbol size in octets.</summary>
    public int SymbolSize { get; }

    /// <summary>Writes the encoding symbol with the given ESI into <paramref name="destination"/>.</summary>
    public void Encode(uint encodingSymbolId, Span<byte> destination)
    {
        if (destination.Length < SymbolSize)
        {
            throw new ArgumentException("Destination is shorter than a symbol.", nameof(destination));
        }
        if (encodingSymbolId > PayloadId.MaxEncodingSymbolId)
        {
            throw new ArgumentOutOfRangeException(nameof(encodingSymbolId), encodingSymbolId, "An ESI is 24 bits.");
        }
        var dst = destination[..SymbolSize];
        if (encodingSymbolId < (uint)_p.K)
        {
            _source.AsSpan((int)encodingSymbolId * SymbolSize, SymbolSize).CopyTo(dst);
            return;
        }
        uint isi = (uint)_p.IsiOf(encodingSymbolId);
        EncodeIsi(_p, _intermediate, SymbolSize, isi, dst);
    }

    /// <summary>Returns the encoding symbol with the given ESI.</summary>
    public byte[] Encode(uint encodingSymbolId)
    {
        var symbol = new byte[SymbolSize];
        Encode(encodingSymbolId, symbol);
        return symbol;
    }

    /// <summary>Enc[] of section 5.3.5.3 for the symbol with internal symbol ID isi.</summary>
    internal static void EncodeIsi(BlockParameters p, byte[] intermediate, int symbolSize, uint isi, Span<byte> destination)
    {
        Span<int> indices = stackalloc int[BlockParameters.MaxEncodingIndices];
        int n = p.EncodingIndices(isi, indices);
        destination.Clear();
        for (int i = 0; i < n; i++)
        {
            Octet.AddAssign(destination, intermediate.AsSpan(indices[i] * symbolSize, symbolSize));
        }
    }
}
