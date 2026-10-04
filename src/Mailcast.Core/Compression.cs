using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Mailcast.Core;

/// <summary>A zstd dictionary and the 2-octet ID frames carry for it.</summary>
[SuppressMessage("Naming", "CA1711", Justification = "It is a zstd dictionary, not a collection.")]
public sealed class ZstdDictionary
{
    /// <summary>The dictionary trained on GB7RDG's bulletins of September and October 2026.</summary>
    public const ushort Gb7rdg1Id = 1;

    /// <summary>Creates a dictionary entry. ID 0 is reserved for no dictionary.</summary>
    public ZstdDictionary(ushort id, byte[] content)
    {
        ArgumentOutOfRangeException.ThrowIfZero(id);
        ArgumentNullException.ThrowIfNull(content);
        Id = id;
        Content = content;
    }

    /// <summary>The ID frames carry.</summary>
    public ushort Id { get; }

    /// <summary>The dictionary as zstd's trainer wrote it.</summary>
    public byte[] Content { get; }

    /// <summary>The dictionaries that ship with this library.</summary>
    public static IReadOnlyList<ZstdDictionary> BuiltIn { get; } = [Load(Gb7rdg1Id, "gb7rdg-1.zdict")];

    private static ZstdDictionary Load(ushort id, string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mailcast.Core.Dictionaries." + file)
            ?? throw new InvalidOperationException($"Dictionary {file} is not embedded.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new ZstdDictionary(id, buffer.ToArray());
    }
}

/// <summary>
/// zstd compression, with or without a dictionary. Each compressed object is one zstd frame
/// with its content size, a content checksum and zstd's own dictionary ID. Decompression
/// refuses a frame without the checksum.
/// </summary>
public sealed class Compression
{
    /// <summary>The dictionary ID meaning plain zstd, no dictionary.</summary>
    public const ushort NoDictionary = 0;

    /// <summary>The compression level: zstd's maximum short of its ultra levels.</summary>
    public const int Level = 19;

    /// <summary>The most a decompressed object may be, to bound memory on bad input.</summary>
    public const int MaxDecompressedSize = 1 << 20;

    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    private readonly Dictionary<ushort, ZstdDictionary> _dictionaries = [];

    /// <summary>Compression with the given dictionaries available, by ID.</summary>
    public Compression(IEnumerable<ZstdDictionary> dictionaries)
    {
        foreach (var d in dictionaries)
        {
            _dictionaries.Add(d.Id, d);
        }
    }

    /// <summary>Compression with the dictionaries that ship with this library.</summary>
    public static Compression Default { get; } = new(ZstdDictionary.BuiltIn);

    /// <summary>Whether a dictionary ID can be decompressed here.</summary>
    public bool Knows(ushort dictionaryId) => dictionaryId == NoDictionary || _dictionaries.ContainsKey(dictionaryId);

    /// <summary>Compresses with the given dictionary, or none for <see cref="NoDictionary"/>.</summary>
    public byte[] Compress(ReadOnlySpan<byte> data, ushort dictionaryId)
    {
        using var compressor = new Compressor(Level);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_contentSizeFlag, 1);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_dictIDFlag, 1);
        if (dictionaryId != NoDictionary)
        {
            compressor.LoadDictionary(Find(dictionaryId).Content);
        }
        return compressor.Wrap(data).ToArray();
    }

    /// <summary>
    /// Decompresses. Throws <see cref="InvalidDataException"/> if the data is not one zstd frame
    /// with a content checksum, made with this dictionary, that passes its checksum and is no
    /// larger than <see cref="MaxDecompressedSize"/>.
    /// </summary>
    public byte[] Decompress(ReadOnlySpan<byte> compressed, ushort dictionaryId)
    {
        // A zstd frame: magic 28 B5 2F FD, then the frame header descriptor, whose bit 2 is the
        // content checksum flag (RFC 8878 section 3.1.1.1.1).
        if (compressed.Length < 5 || !compressed[..4].SequenceEqual(ZstdMagic))
        {
            throw new InvalidDataException("Not a zstd frame.");
        }
        if ((compressed[4] & 0x04) == 0)
        {
            throw new InvalidDataException("The zstd frame has no content checksum.");
        }

        using var decompressor = new Decompressor();
        if (dictionaryId != NoDictionary)
        {
            decompressor.LoadDictionary(Find(dictionaryId).Content);
        }
        try
        {
            ulong size = Decompressor.GetDecompressedSize(compressed);
            if (size > MaxDecompressedSize)
            {
                throw new InvalidDataException($"The object says it is {size} octets, over the {MaxDecompressedSize} limit.");
            }
            return decompressor.Unwrap(compressed, MaxDecompressedSize).ToArray();
        }
        catch (ZstdException e)
        {
            throw new InvalidDataException("Not a valid zstd frame for this dictionary: " + e.Message, e);
        }
    }

    private ZstdDictionary Find(ushort id) =>
        _dictionaries.TryGetValue(id, out var d) ? d : throw new KeyNotFoundException($"No zstd dictionary with ID {id}.");
}
