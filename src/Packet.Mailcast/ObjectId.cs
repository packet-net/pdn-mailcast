using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Packet.Mailcast;

/// <summary>
/// The 8-octet object field of a frame. Objects are content-addressed: the ID is the first eight
/// octets, big-endian, of the SHA-256 of the dictionary ID (two octets, big-endian) followed by
/// the object as RaptorQ encodes it (the kind octet and the zstd frame). A receiver checks every
/// rebuilt object against its own ID, so an object checks itself with or without the directory,
/// two different objects never share an ID, and a frame claiming the wrong dictionary cannot
/// pass for the right one.
/// </summary>
public static class ObjectId
{
    /// <summary>The ID of an object's octets, compressed with the given dictionary.</summary>
    public static ulong Of(ushort dictionaryId, ReadOnlySpan<byte> objectBytes) =>
        BinaryPrimitives.ReadUInt64BigEndian(FullHash(dictionaryId, objectBytes));

    /// <summary>
    /// How many octets of the SHA-256 a signed directory lists for each object
    /// (<see cref="DirectoryEntry.Hash"/>): the eight of the ID and eight more. 128 bits put
    /// making a second object to match a listed one far out of reach. A collision, two objects
    /// made together to share a hash, would take about 2^64 work, but would gain nothing: to get
    /// one of the pair into a signed rotation an attacker must post it as a bulletin, and could as
    /// well post what they wanted delivered. The whole 32 octets would cost about 16 more octets
    /// an entry on the air, every time the directory is sent.
    /// </summary>
    public const int LongHashOctets = 16;

    /// <summary>
    /// The first <see cref="LongHashOctets"/> octets of the SHA-256 that <see cref="Of"/> takes
    /// the first eight of, as 32 lower-case hex digits: what a signed directory lists for each
    /// entry, so that an object made to match the short ID still cannot match a signed listing.
    /// </summary>
    public static string LongHash(ushort dictionaryId, ReadOnlySpan<byte> objectBytes) =>
        Convert.ToHexStringLower(FullHash(dictionaryId, objectBytes).AsSpan(0, LongHashOctets));

    private static byte[] FullHash(ushort dictionaryId, ReadOnlySpan<byte> objectBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> id = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(id, dictionaryId);
        hash.AppendData(id);
        hash.AppendData(objectBytes);
        return hash.GetHashAndReset();
    }

    /// <summary>Sixteen hex digits, as used in the directory, file names and logs.</summary>
    public static string Format(ulong objectId) => objectId.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Reads sixteen hex digits.</summary>
    public static bool TryParse(string text, out ulong objectId) =>
        ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out objectId) && text.Length == 16;
}
