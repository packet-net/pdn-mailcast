namespace Mailcast.RaptorQ;

/// <summary>
/// Encodes a whole object (RFC 6330 section 4.4): splits it into source blocks and sub-blocks as
/// the OTI says, and produces any encoding symbol of any block.
/// </summary>
public sealed class ObjectEncoder
{
    private readonly SourceBlockEncoder[] _blocks;

    /// <summary>Prepares to encode <paramref name="data"/>, whose length must be the OTI's transfer length.</summary>
    public ObjectEncoder(ReadOnlySpan<byte> data, ObjectTransmissionInformation oti)
    {
        if (oti.TransferLength == 0)
        {
            throw new ArgumentException("The OTI is not initialised.", nameof(oti));
        }
        if (data.Length != oti.TransferLength)
        {
            throw new ArgumentException($"The object is {data.Length} octets but the OTI says {oti.TransferLength}.", nameof(data));
        }
        Oti = oti;
        int t = oti.SymbolSize;
        _blocks = new SourceBlockEncoder[oti.SourceBlocks];
        for (int sbn = 0; sbn < _blocks.Length; sbn++)
        {
            int k = oti.SourceBlockSymbols(sbn);
            long offset = oti.SourceBlockOffset(sbn);
            var block = new byte[k * t]; // zero padding past the end of the object
            int available = (int)Math.Max(0, Math.Min(block.Length, data.Length - offset));
            data.Slice((int)offset, available).CopyTo(block);
            _blocks[sbn] = new SourceBlockEncoder(k, t, Partitioning.BlockToSymbols(oti, block, k));
        }
    }

    /// <summary>The object's transmission information.</summary>
    public ObjectTransmissionInformation Oti { get; }

    /// <summary>The encoder for one source block.</summary>
    public SourceBlockEncoder Block(int sourceBlockNumber) => _blocks[sourceBlockNumber];

    /// <summary>Returns the encoding symbol with the given payload ID.</summary>
    public byte[] Encode(PayloadId id) => _blocks[id.SourceBlockNumber].Encode(id.EncodingSymbolId);

    /// <summary>Writes the encoding symbol with the given payload ID.</summary>
    public void Encode(PayloadId id, Span<byte> destination) =>
        _blocks[id.SourceBlockNumber].Encode(id.EncodingSymbolId, destination);
}
