using System.Buffers.Binary;
using Mailcast.RaptorQ;

namespace Mailcast.Core;

/// <summary>
/// The payload of one broadcast AX.25 UI frame: a header saying which object a symbol belongs to
/// and how the object was coded, one RaptorQ encoding symbol, and a CRC.
/// </summary>
/// <remarks>
/// <code>
/// offset  size  field
///      0     1  version, 2
///      1     1  flags: senders write 0, receivers ignore bits they do not know
///      2     8  object ID, big-endian (see <see cref="Core.ObjectId"/>)
///     10     2  dictionary ID, big-endian: which zstd dictionary the object was compressed with, 0 for none
///     12    12  RaptorQ OTI, RFC 6330 section 3.3: transfer length, symbol size, Z, N, Al
///     24     3  ESI, big-endian, RFC 6330 section 3.2
///     27     T  the encoding symbol (the last source symbol may be shorter, without its padding)
///   27+T     4  CRC-32 (see <see cref="Crc32"/>) of everything before it, big-endian
/// </code>
/// <para>
/// There is no source block number, so an object is always one source block (Z = 1). With
/// <see cref="StandardSymbolSize"/> that allows objects up to 53 MB, far beyond any bulletin.
/// </para>
/// </remarks>
public sealed class MailcastFrame
{
    /// <summary>The format version this code writes and reads.</summary>
    public const byte CurrentVersion = 2;

    /// <summary>The header length in octets, before the symbol.</summary>
    public const int HeaderLength = 27;

    /// <summary>The CRC's length in octets, after the symbol.</summary>
    public const int CrcLength = 4;

    /// <summary>Octets of a frame payload besides the symbol.</summary>
    public const int Overhead = HeaderLength + CrcLength;

    /// <summary>
    /// The symbol size the head end uses: 940 octets. With the 31 octets of header and CRC and
    /// the 16 octets of an AX.25 UI frame's addresses, control and PID, a whole frame is 987
    /// octets, 36 under IL2P's 1023-octet limit, so it fits even when IL2P carries the AX.25
    /// header untranslated. 940 is a multiple of the alignment, 4.
    /// </summary>
    public const int StandardSymbolSize = 940;

    /// <summary>The symbol alignment the head end uses (RFC 6330 recommends 4).</summary>
    public const int StandardAlignment = 4;

    /// <summary>Octets of an AX.25 UI frame without digipeaters, before its information field.</summary>
    public const int Ax25UiOverhead = 16;

    /// <summary>Creates a frame.</summary>
    public MailcastFrame(ulong objectId, ushort dictionaryId, ObjectTransmissionInformation oti, uint encodingSymbolId, ReadOnlyMemory<byte> symbol, byte flags = 0)
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
        Flags = flags;
    }

    /// <summary>Which object the symbol belongs to.</summary>
    public ulong ObjectId { get; }

    /// <summary>Which zstd dictionary the object was compressed with; 0 for none.</summary>
    public ushort DictionaryId { get; }

    /// <summary>How the object was RaptorQ coded.</summary>
    public ObjectTransmissionInformation Oti { get; }

    /// <summary>The symbol's ESI.</summary>
    public uint EncodingSymbolId { get; }

    /// <summary>The encoding symbol.</summary>
    public ReadOnlyMemory<byte> Symbol { get; }

    /// <summary>The flags octet. None are defined yet.</summary>
    public byte Flags { get; }

    /// <summary>The payload length in octets.</summary>
    public int Length => Overhead + Symbol.Length;

    /// <summary>Writes the payload.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[Length];
        bytes[0] = CurrentVersion;
        bytes[1] = Flags;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(2), ObjectId);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), DictionaryId);
        Oti.Write(bytes.AsSpan(12));
        bytes[24] = (byte)(EncodingSymbolId >> 16);
        bytes[25] = (byte)(EncodingSymbolId >> 8);
        bytes[26] = (byte)EncodingSymbolId;
        Symbol.Span.CopyTo(bytes.AsSpan(HeaderLength));
        int crcAt = bytes.Length - CrcLength;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(crcAt), Crc32.Compute(bytes.AsSpan(0, crcAt)));
        return bytes;
    }

    /// <summary>
    /// Reads a payload, checking its CRC. Returns false, rather than throwing, for anything that
    /// is not a valid frame of this version: other traffic on the channel is expected.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out MailcastFrame? frame)
    {
        frame = null;
        if (payload.Length <= Overhead || payload[0] != CurrentVersion)
        {
            return false;
        }
        int crcAt = payload.Length - CrcLength;
        if (BinaryPrimitives.ReadUInt32BigEndian(payload[crcAt..]) != Crc32.Compute(payload[..crcAt]))
        {
            return false;
        }
        ObjectTransmissionInformation oti;
        try
        {
            oti = ObjectTransmissionInformation.Read(payload.Slice(12, ObjectTransmissionInformation.EncodedLength));
        }
        catch (ArgumentException)
        {
            return false;
        }
        int symbolLength = crcAt - HeaderLength;
        if (oti.SourceBlocks != 1 || symbolLength > oti.SymbolSize)
        {
            return false;
        }
        uint esi = ((uint)payload[24] << 16) | ((uint)payload[25] << 8) | payload[26];
        frame = new MailcastFrame(
            BinaryPrimitives.ReadUInt64BigEndian(payload[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[10..]),
            oti,
            esi,
            payload[HeaderLength..crcAt].ToArray(),
            payload[1]);
        return true;
    }
}
