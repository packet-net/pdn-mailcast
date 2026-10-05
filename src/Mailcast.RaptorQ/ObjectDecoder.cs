namespace Mailcast.RaptorQ;

/// <summary>
/// Rebuilds a whole object from encoding symbols of its source blocks (RFC 6330 section 4.4.3).
/// </summary>
public sealed class ObjectDecoder
{
    private readonly SourceBlockDecoder[] _blocks;
    private byte[]? _result;

    /// <summary>Prepares to decode the object the OTI describes.</summary>
    public ObjectDecoder(ObjectTransmissionInformation oti)
    {
        if (oti.TransferLength == 0)
        {
            throw new ArgumentException("The OTI is not initialised.", nameof(oti));
        }
        Oti = oti;
        _blocks = new SourceBlockDecoder[oti.SourceBlocks];
        for (int sbn = 0; sbn < _blocks.Length; sbn++)
        {
            _blocks[sbn] = new SourceBlockDecoder(oti.SourceBlockSymbols(sbn), oti.SymbolSize);
        }
    }

    /// <summary>The object's transmission information.</summary>
    public ObjectTransmissionInformation Oti { get; }

    /// <summary>The decoder for one source block.</summary>
    public SourceBlockDecoder Block(int sourceBlockNumber) => _blocks[sourceBlockNumber];

    /// <summary>Whether the whole object has been rebuilt.</summary>
    public bool IsDecoded => _result is not null;

    /// <summary>
    /// Adds an encoding symbol. The last source symbol of the object may arrive without its
    /// padding (section 4.4.2); it is padded with zeros here. Returns false for a duplicate.
    /// </summary>
    public bool Add(PayloadId id, ReadOnlySpan<byte> symbol)
    {
        if (id.SourceBlockNumber >= _blocks.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id.SourceBlockNumber, "No such source block.");
        }
        var block = _blocks[id.SourceBlockNumber];
        int t = Oti.SymbolSize;
        if (symbol.Length < t
            && id.SourceBlockNumber == _blocks.Length - 1
            && id.EncodingSymbolId == (uint)(block.SourceSymbolCount - 1)
            && Oti.SubBlocks == 1
            && symbol.Length >= t - PaddingInLastSymbol())
        {
            var padded = new byte[t];
            symbol.CopyTo(padded);
            return block.Add(id.EncodingSymbolId, padded);
        }
        return block.Add(id.EncodingSymbolId, symbol);
    }

    private int PaddingInLastSymbol() => (int)((Oti.TotalSourceSymbols * Oti.SymbolSize) - Oti.TransferLength);

    /// <summary>Tries to rebuild the object. Returns it, F octets, or null if some block is not yet determined.</summary>
    public byte[]? TryDecode()
    {
        if (_result is not null)
        {
            return _result;
        }
        var blocks = new byte[_blocks.Length][];
        for (int sbn = 0; sbn < _blocks.Length; sbn++)
        {
            var symbols = _blocks[sbn].TryDecode();
            if (symbols is null)
            {
                return null;
            }
            blocks[sbn] = symbols;
        }
        var result = new byte[Oti.TransferLength];
        for (int sbn = 0; sbn < _blocks.Length; sbn++)
        {
            int k = _blocks[sbn].SourceSymbolCount;
            var block = Partitioning.SymbolsToBlock(Oti, blocks[sbn], k);
            long offset = Oti.SourceBlockOffset(sbn);
            int length = (int)Math.Min(block.Length, result.Length - offset);
            block.AsSpan(0, length).CopyTo(result.AsSpan((int)offset));
        }
        _result = result;
        return _result;
    }
}
