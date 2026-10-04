using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Mailcast.Core;

/// <summary>
/// The 4-octet object field of a frame: the first four octets, big-endian, of a SHA-256 hash.
/// </summary>
public static class ObjectId
{
    /// <summary>
    /// The object ID of a bulletin: SHA-256 of its BID in capitals, as Latin-1 octets. BIDs
    /// are compared without regard to case by BBSs, so the hash is too.
    /// </summary>
    public static uint ForBid(string bid)
    {
        ArgumentException.ThrowIfNullOrEmpty(bid);
        return FirstFour(Bulletin.TextEncoding.GetBytes(bid.ToUpperInvariant()));
    }

    /// <summary>The object ID of the directory for a day: SHA-256 of "MAILCAST DIRECTORY yyyy-MM-dd".</summary>
    public static uint ForDirectory(DateOnly date) =>
        FirstFour(Bulletin.TextEncoding.GetBytes("MAILCAST DIRECTORY " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

    /// <summary>Eight hex digits, as used in file names and logs.</summary>
    public static string Format(uint objectId) => objectId.ToString("x8", CultureInfo.InvariantCulture);

    private static uint FirstFour(byte[] data) => BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(data));
}
