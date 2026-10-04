using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Mailcast.Core;

/// <summary>
/// The 8-octet object field of a frame. Objects are content-addressed: the ID is the first eight
/// octets, big-endian, of the SHA-256 of the object as RaptorQ encodes it (the kind octet and the
/// zstd frame). A receiver checks every rebuilt object against its own ID, so an object checks
/// itself with or without the directory, and two different objects never share an ID.
/// </summary>
public static class ObjectId
{
    /// <summary>The ID of an object's octets.</summary>
    public static ulong Of(ReadOnlySpan<byte> objectBytes) =>
        BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(objectBytes));

    /// <summary>Sixteen hex digits, as used in the directory, file names and logs.</summary>
    public static string Format(ulong objectId) => objectId.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Reads sixteen hex digits.</summary>
    public static bool TryParse(string text, out ulong objectId) =>
        ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out objectId) && text.Length == 16;
}
