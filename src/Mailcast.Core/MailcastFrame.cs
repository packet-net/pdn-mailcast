using System.Buffers.Binary;
using Mailcast.RaptorQ;

namespace Mailcast.Core;

/// <summary>
/// The payload of one broadcast AX.25 UI frame: a header saying which object a symbol belongs to
/// and how the object was coded, then one RaptorQ encoding symbol.
/// </summary>
/// <remarks>
/// <code>
/// offset  size  field
///      0     1  version, 1
///      1     4  object ID, big-endian (see <see cref="ObjectId"/>)
///      5     2  dictionary ID, big-endian: which zstd dictionary the object was compressed with, 0 for none
///      7    12  RaptorQ OTI, RFC 6330 section 3.3: transfer length, symbol size, Z, N, Al
///     19     3  ESI, big-endian, RFC 6330 section 3.2
///     22     T  the encoding symbol (the last source symbol may be shorter, without its padding)
/// </code>
/// <para>
/// There is no source block number, so an object is always one source block (Z = 1). With
/// <see cref="StandardSymbolSize"/> that allows objects up to 53 MB, far beyond any bulletin.
/// </para>
/// </remarks>
public sealed class MailcastFrame
{
    /// <summary>The format version this code writes and reads.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>The header length in octets.</summary>
    public const int HeaderLength = 22;

    /// <summary>
    /// The symbol size the head end uses: 940 octets. With the 22-octet header and the 16 octets
    /// of an AX.25 UI frame's addresses, control and PID, a whole frame is 978 octets, 45 under
    /// IL2P's 1023-octet limit, so it fits even when IL2P carries the AX.25 header untranslated.
    /// 940 is a multiple of the alignment, 4.
    /// </summary>
    public const int StandardSymbolSize = 940;

    /// <summary>The symbol alignment the head end uses (RFC 6330 recommends 4).</summary>
    public const int StandardAlignment = 4;

    /// <summary>Octets of an AX.25 UI frame without digipeaters, before its information field.</summary>
    public const int Ax25UiOverhead = 16;

    /// <summary>Creates a frame.</summary>
    public MailcastFrame(uint objectId, ushort dictionaryId, ObjectTransmissionInformation oti, uint encodingSymbolId, ReadOnlyMemory<byte> symbol)
    {
        if (oti.TransferLength == 0)
        {
            throw new ArgumentException("The OTI is not initialised.", nameof(oti));
        }
        if (oti.SourceBlocks != 1)
        {
            throw new ArgumentException("A frame has no source block number, so the object must be one source block.", nameof(oti));
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(encodingSymbolId, PayloadId.MaxEncodingSymbolId);
        if (symbol.Length == 0 || symbol.Length > oti.SymbolSize)
        {
            throw new ArgumentException($"A symbol is 1 to {oti.SymbolSize} octets.", nameof(symbol));
        }
        ObjectId = objectId;
        DictionaryId = dictionaryId;
        Oti = oti;
        EncodingSymbolId = encodingSymbolId;
        Symbol = symbol;
    }

    /// <summary>Which object the symbol belongs to.</summary>
    public uint ObjectId { get; }

    /// <summary>Which zstd dictionary the object was compressed with; 0 for none.</summary>
    public ushort DictionaryId { get; }

    /// <summary>How the object was RaptorQ coded.</summary>
    public ObjectTransmissionInformation Oti { get; }

    /// <summary>The symbol's ESI.</summary>
    public uint EncodingSymbolId { get; }

    /// <summary>The encoding symbol.</summary>
    public ReadOnlyMemory<byte> Symbol { get; }

    /// <summary>The payload length in octets.</summary>
    public int Length => HeaderLength + Symbol.Length;

    /// <summary>Writes the payload.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[Length];
        bytes[0] = CurrentVersion;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), ObjectId);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(5), DictionaryId);
        Oti.Write(bytes.AsSpan(7));
        bytes[19] = (byte)(EncodingSymbolId >> 16);
        bytes[20] = (byte)(EncodingSymbolId >> 8);
        bytes[21] = (byte)EncodingSymbolId;
        Symbol.Span.CopyTo(bytes.AsSpan(HeaderLength));
        return bytes;
    }

    /// <summary>
    /// Reads a payload. Returns false, rather than throwing, for anything that is not a valid
    /// frame of this version: other traffic on the channel is expected.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out MailcastFrame? frame)
    {
        frame = null;
        if (payload.Length <= HeaderLength || payload[0] != CurrentVersion)
        {
            return false;
        }
        ObjectTransmissionInformation oti;
        try
        {
            oti = ObjectTransmissionInformation.Read(payload.Slice(7, ObjectTransmissionInformation.EncodedLength));
        }
        catch (ArgumentException)
        {
            return false;
        }
        int symbolLength = payload.Length - HeaderLength;
        if (oti.SourceBlocks != 1 || symbolLength > oti.SymbolSize)
        {
            return false;
        }
        uint esi = ((uint)payload[19] << 16) | ((uint)payload[20] << 8) | payload[21];
        frame = new MailcastFrame(
            BinaryPrimitives.ReadUInt32BigEndian(payload[1..]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[5..]),
            oti,
            esi,
            payload[HeaderLength..].ToArray());
        return true;
    }
}
