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
/// content, RaptorQ coded as one source block. This octet string is what the OTI's transfer
/// length counts and what a receiver rebuilds.
/// </summary>
public sealed class TransferObject
{
    private readonly ObjectEncoder _encoder;

    private TransferObject(uint objectId, ushort dictionaryId, ObjectKind kind, byte[] content, Compression compression, int symbolSize, int alignment)
    {
        var compressed = compression.Compress(content, dictionaryId);
        var data = new byte[1 + compressed.Length];
        data[0] = (byte)kind;
        compressed.CopyTo(data, 1);

        ObjectId = objectId;
        DictionaryId = dictionaryId;
        Kind = kind;
        Content = content;
        Oti = new ObjectTransmissionInformation(data.Length, symbolSize, 1, 1, alignment);
        _encoder = new ObjectEncoder(data, Oti);
    }

    /// <summary>The object ID frames carry.</summary>
    public uint ObjectId { get; }

    /// <summary>The zstd dictionary the content was compressed with.</summary>
    public ushort DictionaryId { get; }

    /// <summary>What the object holds.</summary>
    public ObjectKind Kind { get; }

    /// <summary>The content before compression.</summary>
    public byte[] Content { get; }

    /// <summary>How the object is RaptorQ coded.</summary>
    public ObjectTransmissionInformation Oti { get; }

    /// <summary>K, the number of source symbols: the fewest frames that can rebuild the object.</summary>
    public int SourceSymbols => Oti.SourceBlockSymbols(0);

    /// <summary>The object's length on the air, kind octet and compressed content.</summary>
    public long Length => Oti.TransferLength;

    /// <summary>Prepares a bulletin.</summary>
    public static TransferObject ForBulletin(Bulletin bulletin, ushort dictionaryId, Compression compression, int symbolSize = MailcastFrame.StandardSymbolSize, int alignment = MailcastFrame.StandardAlignment)
    {
        ArgumentNullException.ThrowIfNull(bulletin);
        return new TransferObject(Core.ObjectId.ForBid(bulletin.Bid), dictionaryId, ObjectKind.Bulletin, bulletin.Serialize(), compression, symbolSize, alignment);
    }

    /// <summary>Prepares a directory.</summary>
    public static TransferObject ForDirectory(BroadcastDirectory directory, ushort dictionaryId, Compression compression, int symbolSize = MailcastFrame.StandardSymbolSize, int alignment = MailcastFrame.StandardAlignment)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return new TransferObject(directory.ObjectId, dictionaryId, ObjectKind.Directory, directory.Serialize(), compression, symbolSize, alignment);
    }

    /// <summary>The frame carrying the encoding symbol with this ESI.</summary>
    public MailcastFrame Frame(uint encodingSymbolId) =>
        new(ObjectId, DictionaryId, Oti, encodingSymbolId, _encoder.Encode(new PayloadId(0, encodingSymbolId)));

    /// <summary>
    /// Reads a rebuilt object back into its kind and decompressed content. Throws
    /// <see cref="InvalidDataException"/> if it is not one.
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
