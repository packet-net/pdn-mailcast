using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Mailcast.RaptorQ;

namespace Packet.Mailcast;

/// <summary>
/// A station's own order for a bulletin's pieces (issue #143): a keyed permutation of the 24-bit
/// ESI space. A head end counts the pieces it has sent of an object (0, 1, 2 ...) and sends the
/// piece numbered <see cref="Map"/> of that count, so it never repeats one (a permutation has no
/// repeats), while two stations sending the same object almost never send the same piece.
/// </summary>
/// <remarks>
/// <para>The format is in docs/design.md and is fixed by the test vectors. The key is
/// SHA-256 of the upper-case callsign in ASCII, one zero octet, the object ID as 8 big-endian
/// octets and, when the station gives its frequency, that frequency in Hz as 8 more big-endian
/// octets (so one callsign on two frequencies sends two different orders); its 32 octets are eight big-endian 32-bit round keys. The permutation is a balanced
/// Feistel network on 12-bit halves with 8 rounds, round function F(r, x) = low 12 bits of the
/// murmur3 finalizer of (roundKey[r] xor x). Receivers need none of this: any piece number is a
/// piece.</para>
/// </remarks>
public sealed class EsiShuffle
{
    private const int Rounds = 8;
    private const uint HalfMask = 0xFFF;

    private readonly uint[] _keys = new uint[Rounds];

    private EsiShuffle(string station, long? frequencyHz, uint[] keys)
    {
        Station = station;
        FrequencyHz = frequencyHz;
        _keys = keys;
    }

    /// <summary>The callsign the order is keyed by.</summary>
    public string Station { get; }

    /// <summary>The frequency the order is keyed by, or null for a station that gives none.</summary>
    public long? FrequencyHz { get; }

    /// <summary>
    /// The order for a station and an object. The callsign is upper-cased. A station that gives its
    /// frequency (<paramref name="frequencyHz"/>) has an order of its own on each frequency.
    /// </summary>
    public static EsiShuffle For(string station, ulong objectId, long? frequencyHz = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(station);
        if (frequencyHz is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "a frequency is at least 1 Hz");
        }
        string upper = station.ToUpperInvariant();
        var input = new byte[Encoding.ASCII.GetByteCount(upper) + 1 + 8 + (frequencyHz is null ? 0 : 8)];
        int n = Encoding.ASCII.GetBytes(upper, input);
        input[n] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(input.AsSpan(n + 1), objectId);
        if (frequencyHz is { } hz)
        {
            BinaryPrimitives.WriteUInt64BigEndian(input.AsSpan(n + 9), (ulong)hz);
        }
        var hash = SHA256.HashData(input);
        var keys = new uint[Rounds];
        for (int i = 0; i < Rounds; i++)
        {
            keys[i] = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(i * 4));
        }
        return new EsiShuffle(upper, frequencyHz, keys);
    }

    /// <summary>The ESI to send as this station's count-th piece (0 for its first).</summary>
    public uint Map(uint count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, PayloadId.MaxEncodingSymbolId);
        uint left = count >> 12;
        uint right = count & HalfMask;
        for (int r = 0; r < Rounds; r++)
        {
            (left, right) = (right, left ^ Round(_keys[r], right));
        }
        return (left << 12) | right;
    }

    /// <summary>The count a piece number stands for: <see cref="Map"/> backwards.</summary>
    public uint Unmap(uint esi)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(esi, PayloadId.MaxEncodingSymbolId);
        uint left = esi >> 12;
        uint right = esi & HalfMask;
        for (int r = Rounds - 1; r >= 0; r--)
        {
            (left, right) = (right ^ Round(_keys[r], left), left);
        }
        return (left << 12) | right;
    }

    private static uint Round(uint key, uint x)
    {
        uint h = key ^ x;
        h ^= h >> 16;
        h *= 0x85EBCA6B;
        h ^= h >> 13;
        h *= 0xC2B2AE35;
        h ^= h >> 16;
        return h & HalfMask;
    }
}
