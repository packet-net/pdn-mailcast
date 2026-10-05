namespace Mailcast.RaptorQ;

/// <summary>
/// Rebuilds one source block of K symbols from any sufficient set of its encoding symbols
/// (RFC 6330 section 5.4), source or repair, in any order.
/// </summary>
public sealed class SourceBlockDecoder
{
    private readonly BlockParameters _p;
    private readonly Dictionary<uint, byte[]> _received = [];
    private int _sourceReceived;
    private int _failedAtCount = -1;
    private byte[]? _result;

    /// <summary>Prepares to decode a source block.</summary>
    /// <param name="sourceSymbolCount">K, from 1 to 56403.</param>
    /// <param name="symbolSize">T, the size of each symbol in octets.</param>
    public SourceBlockDecoder(int sourceSymbolCount, int symbolSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolSize, 1);
        _p = BlockParameters.ForSourceSymbols(sourceSymbolCount);
        SymbolSize = symbolSize;
    }

    /// <summary>K, the number of source symbols.</summary>
    public int SourceSymbolCount => _p.K;

    /// <summary>T, the symbol size in octets.</summary>
    public int SymbolSize { get; }

    /// <summary>How many distinct encoding symbols have been added.</summary>
    public int ReceivedCount => _received.Count;

    /// <summary>Whether the block has been rebuilt.</summary>
    public bool IsDecoded => _result is not null;

    /// <summary>
    /// Adds an encoding symbol. Returns false if a symbol with this ESI was already added, in
    /// which case the new copy is ignored.
    /// </summary>
    public bool Add(uint encodingSymbolId, ReadOnlySpan<byte> symbol)
    {
        if (symbol.Length != SymbolSize)
        {
            throw new ArgumentException($"A symbol is {SymbolSize} octets.", nameof(symbol));
        }
        if (encodingSymbolId > PayloadId.MaxEncodingSymbolId)
        {
            throw new ArgumentOutOfRangeException(nameof(encodingSymbolId), encodingSymbolId, "An ESI is 24 bits.");
        }
        if (_received.ContainsKey(encodingSymbolId))
        {
            return false;
        }
        _received[encodingSymbolId] = symbol.ToArray();
        if (encodingSymbolId < (uint)_p.K)
        {
            _sourceReceived++;
        }
        return true;
    }

    /// <summary>
    /// Tries to rebuild the block from the symbols added so far. Returns the K source symbols,
    /// concatenated, or null if they are not yet determined. Fewer than K symbols never suffice;
    /// K usually do, and each symbol beyond K makes failure about a hundred times less likely.
    /// </summary>
    public byte[]? TryDecode()
    {
        if (_result is not null)
        {
            return _result;
        }
        int t = SymbolSize;
        if (_sourceReceived == _p.K)
        {
            _result = new byte[_p.K * t];
            for (uint esi = 0; esi < (uint)_p.K; esi++)
            {
                _received[esi].CopyTo(_result, (int)esi * t);
            }
            return _result;
        }
        if (_received.Count < _p.K || _received.Count == _failedAtCount)
        {
            return null;
        }

        int padding = _p.KPrime - _p.K;
        int count = _received.Count + padding;
        var isis = new uint[count];
        var symbols = new byte[count * t];
        int n = 0;
        foreach (var (esi, data) in _received)
        {
            isis[n] = (uint)_p.IsiOf(esi);
            data.CopyTo(symbols, n * t);
            n++;
        }
        for (int i = 0; i < padding; i++)
        {
            isis[n++] = (uint)(_p.K + i); // padding symbols are all zero
        }

        var intermediate = IntermediateSymbolSolver.Solve(_p, isis, symbols, t);
        if (intermediate is null)
        {
            _failedAtCount = _received.Count;
            return null;
        }

        var result = new byte[_p.K * t];
        for (uint esi = 0; esi < (uint)_p.K; esi++)
        {
            var dst = result.AsSpan((int)esi * t, t);
            if (_received.TryGetValue(esi, out var data))
            {
                data.CopyTo(dst);
            }
            else
            {
                SourceBlockEncoder.EncodeIsi(_p, intermediate, t, esi, dst);
            }
        }
        _result = result;
        return _result;
    }
}
