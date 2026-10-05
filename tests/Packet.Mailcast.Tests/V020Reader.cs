using System.Globalization;

namespace Packet.Mailcast.Tests;

/// <summary>
/// How a v0.2.0 receiver read objects and directories, copied from that release's
/// TransferObject.Unpack and BroadcastDirectory.Parse, so the tests can show that receivers
/// already in the field read what this version sends.
/// </summary>
internal static class V020Reader
{
    /// <summary>v0.2.0's TransferObject.Unpack: kind 1 or 2, then the zstd frame.</summary>
    public static (byte Kind, byte[] Content) Unpack(ReadOnlySpan<byte> data, ushort dictionaryId, Compression compression)
    {
        if (data.Length < 2 || data[0] is not (1 or 2))
        {
            throw new InvalidDataException("Not a mailcast object.");
        }
        return (data[0], compression.Decompress(data[1..], dictionaryId));
    }

    /// <summary>v0.2.0's BroadcastDirectory.Parse, returning the date and the five fields of each entry.</summary>
    public static (DateOnly Date, List<(ulong ObjectId, ushort DictionaryId, int Size, string Bid, string Title)> Entries) ParseDirectory(ReadOnlySpan<byte> serialized)
    {
        var lines = Bulletin.TextEncoding.GetString(serialized).Split('\n');
        if (lines.Length < 3 || lines[^1].Length != 0)
        {
            throw new FormatException("A directory is lines ended by LF.");
        }
        if (lines[0] != "MAILCAST DIRECTORY 1")
        {
            throw new FormatException($"Not a directory this code reads: {lines[0]}");
        }
        if (!DateOnly.TryParseExact(lines[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new FormatException("The directory's date is not yyyy-MM-dd.");
        }
        var entries = new List<(ulong, ushort, int, string, string)>();
        foreach (var line in lines[2..^1])
        {
            var parts = line.Split('\t');
            if (parts.Length < 5
                || !ObjectId.TryParse(parts[0], out ulong objectId)
                || !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort dictionaryId)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int size))
            {
                throw new FormatException($"Bad directory line: {line}");
            }
            if (parts[3].Length == 0 || parts[3].Any(c => c is ' ' or '\t' or '\n' or '\r'))
            {
                throw new FormatException($"Entry {parts[3]} cannot be serialised.");
            }
            entries.Add((objectId, dictionaryId, size, parts[3], parts[4]));
        }
        return (date, entries);
    }
}
