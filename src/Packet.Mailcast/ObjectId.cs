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
    /// How many octets of the SHA-256 a signed directory checks each object against: the eight
    /// of the ID, which the entry already gives, and the next eight, which it lists as
    /// <see cref="DirectoryEntry.Hash"/>. With the 8-octet ID alone, two bulletins made together
    /// to share an ID take about 2^32 work: post one through a BBS that feeds the head end, send
    /// the other, and the signed listing would vouch for it. At 16 octets that is about 2^64, and
    /// making an object to match a listed one about 2^128. The whole 32 octets would cost 16 more
    /// octets an entry on the air, every time the directory is sent.
    /// </summary>
    public const int LongHashOctets = 16;

    /// <summary>How many octets of the hash a signed directory lists, beyond the ID's eight.</summary>
    public const int ListedHashOctets = LongHashOctets - 8;

    /// <summary>
    /// Octets 8 to 15 of the SHA-256 that <see cref="Of"/> takes the first eight of, as 16
    /// lower-case hex digits: what a signed directory lists for each entry, so that with the ID
    /// an object is checked against 16 octets, and one made to match the short ID still cannot
    /// match a signed listing.
    /// </summary>
    public static string LongHash(ushort dictionaryId, ReadOnlySpan<byte> objectBytes) =>
        Convert.ToHexStringLower(FullHash(dictionaryId, objectBytes).AsSpan(8, ListedHashOctets));

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
