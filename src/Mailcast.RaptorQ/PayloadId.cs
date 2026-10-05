using System.Buffers.Binary;

namespace Mailcast.RaptorQ;

/// <summary>
/// The FEC Payload ID of RFC 6330 section 3.2: which source block an encoding symbol belongs to,
/// and its encoding symbol ID within that block. Encoded as 4 octets.
/// </summary>
public readonly record struct PayloadId
{
    /// <summary>The largest ESI, as it is a 24-bit field.</summary>
    public const uint MaxEncodingSymbolId = (1u << 24) - 1;

    /// <summary>The encoded length in octets.</summary>
    public const int EncodedLength = 4;

    /// <summary>Creates a payload ID.</summary>
    public PayloadId(int sourceBlockNumber, uint encodingSymbolId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceBlockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceBlockNumber, byte.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(encodingSymbolId, MaxEncodingSymbolId);
        SourceBlockNumber = sourceBlockNumber;
        EncodingSymbolId = encodingSymbolId;
    }

    /// <summary>SBN, 8 bits.</summary>
    public int SourceBlockNumber { get; }

    /// <summary>ESI, 24 bits.</summary>
    public uint EncodingSymbolId { get; }

    /// <summary>Writes the 4-octet encoding.</summary>
    public void Write(Span<byte> destination) =>
        BinaryPrimitives.WriteUInt32BigEndian(destination, ((uint)SourceBlockNumber << 24) | EncodingSymbolId);

    /// <summary>Reads the 4-octet encoding.</summary>
    public static PayloadId Read(ReadOnlySpan<byte> source)
    {
        uint v = BinaryPrimitives.ReadUInt32BigEndian(source);
        return new PayloadId((int)(v >> 24), v & MaxEncodingSymbolId);
    }
}
