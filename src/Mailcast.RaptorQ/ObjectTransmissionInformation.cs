using System.Buffers.Binary;

namespace Mailcast.RaptorQ;

/// <summary>
/// The FEC Object Transmission Information of RFC 6330 section 3.3: everything a receiver needs,
/// besides the symbols, to rebuild an object. Encoded as 12 octets.
/// </summary>
public readonly record struct ObjectTransmissionInformation
{
    /// <summary>The FEC Encoding ID of RaptorQ, section 3.3.1.</summary>
    public const int FecEncodingId = 6;

    /// <summary>The encoded length in octets.</summary>
    public const int EncodedLength = 12;

    /// <summary>
    /// The largest transfer length. RFC 6330 says 946270874880, but erratum 5548 corrects it to
    /// 942574504275 (56403 symbols of 65535 octets in each of 255 source blocks).
    /// </summary>
    public const long MaxTransferLength = 942574504275;

    /// <summary>Creates and checks the parameters (section 4.4.1.2).</summary>
    /// <param name="transferLength">F, the object's length in octets, at least 1.</param>
    /// <param name="symbolSize">T, the symbol size in octets, a multiple of the alignment.</param>
    /// <param name="sourceBlocks">Z, the number of source blocks, 1 to 255.</param>
    /// <param name="subBlocks">N, the number of sub-blocks in each source block.</param>
    /// <param name="alignment">Al, the symbol alignment in octets, 1 to 255.</param>
    public ObjectTransmissionInformation(long transferLength, int symbolSize, int sourceBlocks = 1, int subBlocks = 1, int alignment = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(transferLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(transferLength, MaxTransferLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(symbolSize, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceBlocks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceBlocks, byte.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(subBlocks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subBlocks, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(alignment, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(alignment, byte.MaxValue);
        if (symbolSize % alignment != 0)
        {
            throw new ArgumentException("The symbol size must be a multiple of the alignment.", nameof(symbolSize));
        }
        if (subBlocks > symbolSize / alignment)
        {
            throw new ArgumentException("Each sub-symbol must be at least one alignment unit.", nameof(subBlocks));
        }
        long kt = (transferLength + symbolSize - 1) / symbolSize;
        if (sourceBlocks > kt)
        {
            throw new ArgumentException("More source blocks than symbols.", nameof(sourceBlocks));
        }
        if ((kt + sourceBlocks - 1) / sourceBlocks > BlockParameters.MaxSourceSymbols)
        {
            throw new ArgumentException($"A source block would hold more than {BlockParameters.MaxSourceSymbols} symbols.", nameof(sourceBlocks));
        }
        TransferLength = transferLength;
        SymbolSize = symbolSize;
        SourceBlocks = sourceBlocks;
        SubBlocks = subBlocks;
        Alignment = alignment;
    }

    /// <summary>F, the object's length in octets.</summary>
    public long TransferLength { get; }

    /// <summary>T, the symbol size in octets.</summary>
    public int SymbolSize { get; }

    /// <summary>Z, the number of source blocks.</summary>
    public int SourceBlocks { get; }

    /// <summary>N, the number of sub-blocks in each source block.</summary>
    public int SubBlocks { get; }

    /// <summary>Al, the symbol alignment in octets.</summary>
    public int Alignment { get; }

    /// <summary>Kt, the total number of source symbols in the object.</summary>
    public long TotalSourceSymbols => (TransferLength + SymbolSize - 1) / SymbolSize;

    /// <summary>
    /// Parameters for an object with one sub-block per source block, using as few source blocks
    /// as <paramref name="maxSourceSymbolsPerBlock"/> allows.
    /// </summary>
    public static ObjectTransmissionInformation ForObject(long transferLength, int symbolSize, int alignment = 1, int maxSourceSymbolsPerBlock = BlockParameters.MaxSourceSymbols)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(transferLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSourceSymbolsPerBlock, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSourceSymbolsPerBlock, BlockParameters.MaxSourceSymbols);
        long kt = (transferLength + symbolSize - 1) / symbolSize;
        long z = (kt + maxSourceSymbolsPerBlock - 1) / maxSourceSymbolsPerBlock;
        if (z > byte.MaxValue)
        {
            throw new ArgumentException("The object needs more than 255 source blocks.", nameof(transferLength));
        }
        return new ObjectTransmissionInformation(transferLength, symbolSize, (int)z, 1, alignment);
    }

    /// <summary>The source block sizes in symbols: Z values, from Partition[Kt, Z].</summary>
    public int SourceBlockSymbols(int sourceBlockNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceBlockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sourceBlockNumber, SourceBlocks);
        var (kl, ks, zl, _) = Partitioning.Partition(TotalSourceSymbols, SourceBlocks);
        return (int)(sourceBlockNumber < zl ? kl : ks);
    }

    /// <summary>The octet offset of a source block within the (padded) object.</summary>
    internal long SourceBlockOffset(int sourceBlockNumber)
    {
        var (kl, ks, zl, _) = Partitioning.Partition(TotalSourceSymbols, SourceBlocks);
        long blocksLarge = Math.Min(sourceBlockNumber, zl);
        long blocksSmall = sourceBlockNumber - blocksLarge;
        return ((blocksLarge * kl) + (blocksSmall * ks)) * SymbolSize;
    }

    /// <summary>Writes the 12-octet encoding of section 3.3: common then scheme-specific.</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException("Destination is shorter than 12 octets.", nameof(destination));
        }
        destination[0] = (byte)(TransferLength >> 32);
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)TransferLength);
        destination[5] = 0; // reserved
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], (ushort)SymbolSize);
        destination[8] = (byte)SourceBlocks;
        BinaryPrimitives.WriteUInt16BigEndian(destination[9..], (ushort)SubBlocks);
        destination[11] = (byte)Alignment;
    }

    /// <summary>Returns the 12-octet encoding.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[EncodedLength];
        Write(bytes);
        return bytes;
    }

    /// <summary>Reads the 12-octet encoding, checking the values as the constructor does.</summary>
    public static ObjectTransmissionInformation Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < EncodedLength)
        {
            throw new ArgumentException("An encoded OTI is 12 octets.", nameof(source));
        }
        long f = ((long)source[0] << 32) | BinaryPrimitives.ReadUInt32BigEndian(source[1..]);
        int t = BinaryPrimitives.ReadUInt16BigEndian(source[6..]);
        int z = source[8];
        int n = BinaryPrimitives.ReadUInt16BigEndian(source[9..]);
        int al = source[11];
        return new ObjectTransmissionInformation(f, t, z, n, al);
    }
}
