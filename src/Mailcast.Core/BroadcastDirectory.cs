using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mailcast.Core;

/// <summary>One object in rotation, as the directory lists it.</summary>
/// <param name="ObjectId">The object ID frames carry.</param>
/// <param name="DictionaryId">The zstd dictionary the object was compressed with.</param>
/// <param name="Size">The serialised bulletin's length in octets.</param>
/// <param name="ContentHash">The first 8 octets of the serialised bulletin's SHA-256, as 16 hex digits.</param>
/// <param name="Bid">The bulletin ID.</param>
/// <param name="Title">The subject line.</param>
public sealed record DirectoryEntry(uint ObjectId, ushort DictionaryId, int Size, string ContentHash, string Bid, string Title)
{
    /// <summary>The hash a directory entry carries for a serialised bulletin.</summary>
    public static string HashOf(ReadOnlySpan<byte> serializedBulletin) =>
        Convert.ToHexStringLower(SHA256.HashData(serializedBulletin)[..8]);
}

/// <summary>
/// The list of objects in rotation on one day's broadcast, so that a receiver can tell how many
/// bulletins it has heard of, how many it has rebuilt, and check each one it rebuilds.
/// </summary>
/// <remarks>
/// <para>Serialised as Latin-1 text, lines ended by LF: the date, then one line per entry.</para>
/// <code>
/// 2026-10-04
/// 1a2b3c4d 1 2345 0123456789abcdef 12345_GB7RDG Title text to the end of the line
/// </code>
/// <para>The entry fields are object ID (8 hex digits), dictionary ID, size, content hash, BID and title.</para>
/// </remarks>
public sealed class BroadcastDirectory
{
    /// <summary>Creates a directory.</summary>
    public BroadcastDirectory(DateOnly date, IEnumerable<DirectoryEntry> entries)
    {
        Date = date;
        Entries = entries.ToArray();
        foreach (var e in Entries)
        {
            if (e.ContentHash.Length != 16 || !e.ContentHash.All(char.IsAsciiHexDigitLower))
            {
                throw new ArgumentException("A content hash is 16 lower-case hex digits.", nameof(entries));
            }
            if (e.Bid.Length == 0 || e.Bid.Contains(' ') || e.Bid.Contains('\n') || e.Title.Contains('\n') || e.Title.Contains('\r'))
            {
                throw new ArgumentException($"Entry {e.Bid} cannot be serialised.", nameof(entries));
            }
        }
    }

    /// <summary>The broadcast day.</summary>
    public DateOnly Date { get; }

    /// <summary>The objects in rotation.</summary>
    public IReadOnlyList<DirectoryEntry> Entries { get; }

    /// <summary>The directory's own object ID, which changes every day.</summary>
    public uint ObjectId => Core.ObjectId.ForDirectory(Date);

    /// <summary>The entry for an object, if listed.</summary>
    public DirectoryEntry? Find(uint objectId) => Entries.FirstOrDefault(e => e.ObjectId == objectId);

    /// <summary>The serialised form described in the class remarks.</summary>
    public byte[] Serialize()
    {
        var text = new StringBuilder();
        text.Append(Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var e in Entries)
        {
            text.Append(CultureInfo.InvariantCulture, $"{Core.ObjectId.Format(e.ObjectId)} {e.DictionaryId} {e.Size} {e.ContentHash} {e.Bid} {e.Title}\n");
        }
        return Bulletin.TextEncoding.GetBytes(text.ToString());
    }

    /// <summary>Reads the serialised form. Throws <see cref="FormatException"/> if it is not one.</summary>
    public static BroadcastDirectory Parse(ReadOnlySpan<byte> serialized)
    {
        var lines = Bulletin.TextEncoding.GetString(serialized).Split('\n');
        if (lines.Length < 2 || lines[^1].Length != 0)
        {
            throw new FormatException("A directory is lines ended by LF.");
        }
        if (!DateOnly.TryParseExact(lines[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new FormatException("The directory does not start with its date.");
        }
        var entries = new List<DirectoryEntry>();
        foreach (var line in lines[1..^1])
        {
            var parts = line.Split(' ', 6);
            if (parts.Length != 6
                || parts[0].Length != 8
                || !uint.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint objectId)
                || !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort dictionaryId)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int size))
            {
                throw new FormatException($"Bad directory line: {line}");
            }
            entries.Add(new DirectoryEntry(objectId, dictionaryId, size, parts[3], parts[4], parts[5]));
        }
        try
        {
            return new BroadcastDirectory(date, entries);
        }
        catch (ArgumentException e)
        {
            throw new FormatException(e.Message, e);
        }
    }
}
