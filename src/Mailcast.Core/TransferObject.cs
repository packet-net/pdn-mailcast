using Mailcast.RaptorQ;

namespace Mailcast.Core;

/// <summary>What a broadcast object holds.</summary>
public enum ObjectKind : byte
{
    /// <summary>A serialised <see cref="Bulletin"/>.</summary>
    Bulletin = 1,

    /// <summary>A serialised <see cref="BroadcastDirectory"/>.</summary>
    Directory = 2,
}

/// <summary>
/// An object ready to broadcast: one octet of <see cref="ObjectKind"/> then the zstd-compressed
/// content, RaptorQ coded as one source block. These octets are what the OTI's transfer length
/// counts, what the object ID hashes (after the dictionary ID), and what a receiver rebuilds.
/// </summary>
public sealed class TransferObject
{
    private readonly byte[] _bytes;
    private readonly ObjectEncoder _encoder;

    private TransferObject(byte[] bytes, ushort dictionaryId, ObjectTransmissionInformation oti)
    {
        if (bytes.Length < 2 || !Enum.IsDefined((ObjectKind)bytes[0]))
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
        ObjectId = Core.ObjectId.Of(dictionaryId, bytes);
        _encoder = new ObjectEncoder(bytes, oti);
    }

    /// <summary>The object ID frames carry: the hash of the dictionary ID and <see cref="Bytes"/>.</summary>
    public ulong ObjectId { get; }

    /// <summary>The zstd dictionary the content was compressed with.</summary>
    public ushort DictionaryId { get; }

    /// <summary>What the object holds.</summary>
    public ObjectKind Kind => (ObjectKind)_bytes[0];

    /// <summary>How the object is RaptorQ coded.</summary>
    public ObjectTransmissionInformation Oti { get; }

    /// <summary>The object's octets: the kind octet and the zstd frame. Keep these to send the object again unchanged.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>K, the number of source symbols: the fewest frames that can rebuild the object.</summary>
    public int SourceSymbols => Oti.SourceBlockSymbols(0);

    /// <summary>The object's length on the air, kind octet and compressed content.</summary>
    public long Length => Oti.TransferLength;

    /// <summary>Compresses and prepares a bulletin.</summary>
    public static TransferObject ForBulletin(Bulletin bulletin, ushort dictionaryId, Compression compression, int symbolSize = MailcastFrame.StandardSymbolSize, int alignment = MailcastFrame.StandardAlignment)
    {
        ArgumentNullException.ThrowIfNull(bulletin);
        return Pack(ObjectKind.Bulletin, bulletin.Serialize(), dictionaryId, compression, symbolSize, alignment);
    }

    /// <summary>Compresses and prepares a directory.</summary>
    public static TransferObject ForDirectory(BroadcastDirectory directory, ushort dictionaryId, Compression compression, int symbolSize = MailcastFrame.StandardSymbolSize, int alignment = MailcastFrame.StandardAlignment)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return Pack(ObjectKind.Directory, directory.Serialize(), dictionaryId, compression, symbolSize, alignment);
    }

    /// <summary>An object kept from an earlier day, exactly as it was first prepared.</summary>
    public static TransferObject FromStored(ReadOnlySpan<byte> bytes, ushort dictionaryId, ObjectTransmissionInformation oti) =>
        new(bytes.ToArray(), dictionaryId, oti);

    private static TransferObject Pack(ObjectKind kind, byte[] content, ushort dictionaryId, Compression compression, int symbolSize, int alignment)
    {
        ArgumentNullException.ThrowIfNull(compression);
        var compressed = compression.Compress(content, dictionaryId);
        var bytes = new byte[1 + compressed.Length];
        bytes[0] = (byte)kind;
        compressed.CopyTo(bytes, 1);
        return new TransferObject(bytes, dictionaryId, new ObjectTransmissionInformation(bytes.Length, symbolSize, 1, 1, alignment));
    }

    /// <summary>The frame carrying the encoding symbol with this ESI.</summary>
    public MailcastFrame Frame(uint encodingSymbolId) =>
        new(ObjectId, DictionaryId, Oti, encodingSymbolId, _encoder.Encode(new PayloadId(0, encodingSymbolId)));

    /// <summary>
    /// Reads a rebuilt object back into its kind and decompressed content. Throws
    /// <see cref="InvalidDataException"/> if it is not one, and <see cref="KeyNotFoundException"/>
    /// if the dictionary is not available.
    /// </summary>
    public static (ObjectKind Kind, byte[] Content) Unpack(ReadOnlySpan<byte> data, ushort dictionaryId, Compression compression)
    {
        ArgumentNullException.ThrowIfNull(compression);
        if (data.Length < 2 || !Enum.IsDefined((ObjectKind)data[0]))
        {
            throw new InvalidDataException("Not a mailcast object.");
        }
        return ((ObjectKind)data[0], compression.Decompress(data[1..], dictionaryId));
    }
}
