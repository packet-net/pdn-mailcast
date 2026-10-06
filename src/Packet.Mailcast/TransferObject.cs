using Mailcast.RaptorQ;

namespace Packet.Mailcast;

/// <summary>
/// What an object holds: its content type, the low 7 bits of the object's first octet. The
/// registry is in docs/design.md; values not named here are either unassigned or experiments
/// (<see cref="ContentType.FirstExperimental"/> to <see cref="ContentType.LastExperimental"/>).
/// </summary>
public enum ObjectKind : byte
{
    /// <summary>A packet mail bulletin (FBB/BPQ message format): a serialised <see cref="Bulletin"/>.</summary>
    Bulletin = 1,

    /// <summary>A serialised <see cref="BroadcastDirectory"/>.</summary>
    Directory = 2,

    /// <summary>A DAPPS message. Reserved: receivers know it, keep it out of the BBS and log it once.</summary>
    DappsMessage = 3,

    /// <summary>
    /// A propagation reading from the head end, one object per source (the ionosonde now): a
    /// short binary record, not compressed and sent with dictionary 0 (see
    /// <see cref="Propagation.IonoRecord"/>). Observe only, never for the BBS.
    /// </summary>
    Propagation = 4,
}

/// <summary>
/// The content type octet that starts every object, and the optional metadata block after it.
/// </summary>
/// <remarks>
/// <para>The octet's low 7 bits are the type (<see cref="ObjectKind"/>). Its top bit, when set,
/// says a metadata block follows: a 2-octet big-endian length, then that many octets of the
/// wrapper's own header, which a reader that does not know the wrapper skips. The zstd frame
/// comes after. Types 1 and 2 are sent without metadata, so they are exactly what receivers
/// before v0.3.0 read; those receivers drop any other first octet as an object they cannot use.</para>
/// </remarks>
public static class ContentType
{
    /// <summary>The top bit of the type octet: a metadata block follows it.</summary>
    public const byte MetadataFollows = 0x80;

    /// <summary>The first type set aside for experiments, never assigned.</summary>
    public const byte FirstExperimental = 0x70;

    /// <summary>The last type set aside for experiments.</summary>
    public const byte LastExperimental = 0x7F;

    /// <summary>The longest metadata block a 2-octet length can say.</summary>
    public const int MostMetadata = ushort.MaxValue;

    /// <summary>Whether <paramref name="type"/> (the low 7 bits) is one this code knows the meaning of.</summary>
    public static bool IsKnown(byte type) => Enum.IsDefined((ObjectKind)type);

    /// <summary>A content type in words, for logs and the page.</summary>
    public static string Describe(byte type) => type switch
    {
        (byte)ObjectKind.Bulletin => "packet mail bulletin (FBB/BPQ message format)",
        (byte)ObjectKind.Directory => "directory",
        (byte)ObjectKind.DappsMessage => "DAPPS message",
        (byte)ObjectKind.Propagation => "propagation reading",
        >= FirstExperimental and <= LastExperimental => $"experimental content type {type}",
        _ => $"unassigned content type {type}",
    };

    /// <summary>
    /// Splits an object into its type, metadata and compressed content. False when it is too short
    /// for its own header, or the type is 0.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out byte type, out ReadOnlySpan<byte> metadata, out ReadOnlySpan<byte> content)
    {
        type = 0;
        metadata = default;
        content = default;
        if (data.Length < 2)
        {
            return false;
        }
        type = (byte)(data[0] & ~MetadataFollows);
        int offset = 1;
        if ((data[0] & MetadataFollows) != 0)
        {
            if (data.Length < 3)
            {
                return false;
            }
            int length = (data[1] << 8) | data[2];
            if (data.Length < 3 + length + 1)
            {
                return false;
            }
            metadata = data.Slice(3, length);
            offset = 3 + length;
        }
        content = data[offset..];
        return type != 0;
    }
}

/// <summary>
/// An object ready to send: one octet of content type (<see cref="ContentType"/>), an optional
/// metadata block, then the zstd-compressed content, RaptorQ coded as one source block. The one
/// exception is <see cref="ObjectKind.Propagation"/>, a short record sent as it is. These
/// octets are what the OTI's transfer length counts, what the object ID hashes (after the
/// dictionary ID), and what a receiver rebuilds.
/// </summary>
public sealed class TransferObject
{
    private readonly byte[] _bytes;
    private readonly ObjectEncoder _encoder;

    private TransferObject(byte[] bytes, ushort dictionaryId, ObjectTransmissionInformation oti)
    {
        if (!ContentType.TryRead(bytes, out _, out _, out _))
        {
            throw new ArgumentException("Not a mailcast object.", nameof(bytes));
        }
        if (oti.TransferLength != bytes.Length || oti.SourceBlocks != 1)
        {
            throw new ArgumentException("The OTI does not describe these octets as one source block.", nameof(oti));
        }
        _bytes = bytes;
        DictionaryId = dictionaryId;
        Oti = oti;
        ObjectId = Packet.Mailcast.ObjectId.Of(dictionaryId, bytes);
        _encoder = new ObjectEncoder(bytes, oti);
    }

    /// <summary>The object ID frames carry: the hash of the dictionary ID and <see cref="Bytes"/>.</summary>
    public ulong ObjectId { get; }

    /// <summary>The zstd dictionary the content was compressed with.</summary>
    public ushort DictionaryId { get; }

    /// <summary>What the object holds: its content type, without the metadata bit.</summary>
    public ObjectKind Kind => (ObjectKind)(_bytes[0] & ~ContentType.MetadataFollows);

    /// <summary>How the object is RaptorQ coded.</summary>
    public ObjectTransmissionInformation Oti { get; }

    /// <summary>The object's octets: the type octet, any metadata, and the zstd frame. Keep these to send the object again unchanged.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>K, the number of source symbols: the fewest frames that can rebuild the object.</summary>
    public int SourceSymbols => Oti.SourceBlockSymbols(0);

    /// <summary>The object's length on the air, kind octet and compressed content.</summary>
    public long Length => Oti.TransferLength;

    /// <summary>Compresses and prepares a bulletin.</summary>
    public static TransferObject ForBulletin(Bulletin bulletin, ushort dictionaryId, Compression compression, int symbolSize = MailcastFrame.StandardSymbolSize, int alignment = MailcastFrame.StandardAlignment)
    {
        ArgumentNullException.ThrowIfNull(bulletin);
        return Pack((byte)ObjectKind.Bulletin, bulletin.Serialize(), null, dictionaryId, compression, symbolSize, alignment);
    }

    /// <summary>Compresses and prepares a directory.</summary>
    public static TransferObject ForDirectory(BroadcastDirectory directory, ushort dictionaryId, Compression compression, int symbolSize = MailcastFrame.StandardSymbolSize, int alignment = MailcastFrame.StandardAlignment)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return Pack((byte)ObjectKind.Directory, directory.Serialize(), null, dictionaryId, compression, symbolSize, alignment);
    }

    /// <summary>
    /// Compresses and prepares content of any type, with a metadata block when
    /// <paramref name="withMetadata"/> is set: how a later wrapper (a DAPPS message, say) is sent.
    /// </summary>
    public static TransferObject ForContent(byte type, byte[] content, ReadOnlySpan<byte> metadata, bool withMetadata, ushort dictionaryId, Compression compression, int symbolSize = MailcastFrame.StandardSymbolSize, int alignment = MailcastFrame.StandardAlignment)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (type is 0 or >= ContentType.MetadataFollows)
        {
            throw new ArgumentOutOfRangeException(nameof(type), "A content type is 1 to 127.");
        }
        if (metadata.Length > ContentType.MostMetadata)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata), "A metadata block is at most 65535 octets.");
        }
        return Pack(type, content, withMetadata ? metadata.ToArray() : null, dictionaryId, compression, symbolSize, alignment);
    }

    /// <summary>
    /// A short record of <paramref name="type"/>, not compressed and with dictionary 0, as one
    /// symbol of exactly its own length rounded up to <paramref name="alignment"/>, so that every
    /// frame of it is small and any one rebuilds it: how <see cref="ObjectKind.Propagation"/> is sent.
    /// </summary>
    public static TransferObject ForRecord(byte type, ReadOnlySpan<byte> record, int alignment = MailcastFrame.StandardAlignment)
    {
        if (type is 0 or >= ContentType.MetadataFollows)
        {
            throw new ArgumentOutOfRangeException(nameof(type), "A content type is 1 to 127.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(record.Length, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(alignment, 1);
        var bytes = new byte[1 + record.Length];
        bytes[0] = type;
        record.CopyTo(bytes.AsSpan(1));
        int symbolSize = (bytes.Length + alignment - 1) / alignment * alignment;
        return new TransferObject(bytes, Compression.NoDictionary, new ObjectTransmissionInformation(bytes.Length, symbolSize, 1, 1, alignment));
    }

    /// <summary>An object kept from an earlier day, exactly as it was first prepared.</summary>
    public static TransferObject FromStored(ReadOnlySpan<byte> bytes, ushort dictionaryId, ObjectTransmissionInformation oti) =>
        new(bytes.ToArray(), dictionaryId, oti);

    private static TransferObject Pack(byte type, byte[] content, byte[]? metadata, ushort dictionaryId, Compression compression, int symbolSize, int alignment)
    {
        ArgumentNullException.ThrowIfNull(compression);
        var compressed = compression.Compress(content, dictionaryId);
        int header = metadata is null ? 1 : 3 + metadata.Length;
        var bytes = new byte[header + compressed.Length];
        bytes[0] = metadata is null ? type : (byte)(type | ContentType.MetadataFollows);
        if (metadata is not null)
        {
            bytes[1] = (byte)(metadata.Length >> 8);
            bytes[2] = (byte)metadata.Length;
            metadata.CopyTo(bytes, 3);
        }
        compressed.CopyTo(bytes, header);
        return new TransferObject(bytes, dictionaryId, new ObjectTransmissionInformation(bytes.Length, symbolSize, 1, 1, alignment));
    }

    /// <summary>The frame carrying the encoding symbol with this ESI.</summary>
    public MailcastFrame Frame(uint encodingSymbolId) =>
        new(ObjectId, DictionaryId, Oti, encodingSymbolId, _encoder.Encode(new PayloadId(0, encodingSymbolId)));

    /// <summary>
    /// Reads a rebuilt object back into its type and decompressed content, skipping any metadata.
    /// Throws <see cref="InvalidDataException"/> if it is not an object or not of a type this code
    /// knows (check <see cref="ContentType.TryRead"/> first to tell those apart), and
    /// <see cref="KeyNotFoundException"/> if the dictionary is not available.
    /// </summary>
    public static (ObjectKind Kind, byte[] Content) Unpack(ReadOnlySpan<byte> data, ushort dictionaryId, Compression compression)
    {
        ArgumentNullException.ThrowIfNull(compression);
        if (!ContentType.TryRead(data, out byte type, out _, out var content) || !ContentType.IsKnown(type))
        {
            throw new InvalidDataException("Not a mailcast object.");
        }
        if (type == (byte)ObjectKind.Propagation)
        {
            return (ObjectKind.Propagation, content.ToArray()); // a record, not compressed
        }
        return ((ObjectKind)type, compression.Decompress(content, dictionaryId));
    }
}
