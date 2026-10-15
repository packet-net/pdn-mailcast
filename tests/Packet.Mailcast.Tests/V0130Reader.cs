using System.Globalization;

namespace Packet.Mailcast.Tests;

/// <summary>
/// How receivers 0.8.0 to 0.13.0 read a rebuilt object and a directory, copied from v0.13.0's
/// ReceiverStore.Finish, TransferObject.Unpack and BroadcastDirectory.Parse (all three unchanged
/// from v0.8.0 to v0.13.0), so the tests can show those receivers read a signed directory exactly
/// as an unsigned one.
/// </summary>
internal static class V0130Reader
{
    /// <summary>What v0.13.0 made of a directory: its date, entries, timetable and mode.</summary>
    public sealed record Directory(DateOnly Date, List<DirectoryEntry> Entries, SlotTimetable? Schedule, string? Mode);

    /// <summary>
    /// v0.13.0's ReceiverStore.Finish up to the directory parse: the content type octet (and any
    /// metadata block, skipped), then only types 1 and 2 are unpacked; then BroadcastDirectory.Parse.
    /// </summary>
    public static Directory ReadDirectoryObject(ReadOnlySpan<byte> data, ushort dictionaryId, Compression compression)
    {
        if (!ContentType.TryRead(data, out byte type, out _, out var content))
        {
            throw new InvalidDataException("matches its ID but its content type or metadata block is cut short");
        }
        if (type != (byte)ObjectKind.Directory)
        {
            throw new InvalidDataException($"not a directory: content type {type}");
        }
        return ParseDirectory(compression.Decompress(content, dictionaryId));
    }

    /// <summary>v0.13.0's BroadcastDirectory.Parse, and the checks its constructor made.</summary>
    public static Directory ParseDirectory(ReadOnlySpan<byte> serialized)
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
        var entries = new List<DirectoryEntry>();
        SlotTimetable? schedule = null;
        string? mode = null;
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
            byte type = (byte)ObjectKind.Bulletin;
            foreach (var field in parts.AsSpan(5))
            {
                if (field.StartsWith("type=", StringComparison.Ordinal)
                    && byte.TryParse(field.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out byte t) && t is > 0 and < ContentType.MetadataFollows)
                {
                    type = t;
                    break;
                }
            }
            entries.Add(new DirectoryEntry(objectId, dictionaryId, size, parts[3], parts[4], type));
            if (entries.Count == 1 && parts.Length > 5)
            {
                schedule = SlotTimetable.FromFields(parts[5..]);
                mode = parts[5..].FirstOrDefault(f => f.StartsWith("mode=", StringComparison.Ordinal) && f.Length > 5)?[5..];
            }
        }
        // v0.13.0's constructor: a tab in a title became a space, and a BID or title that cannot
        // be serialised made the whole directory a FormatException.
        foreach (var e in entries)
        {
            if (e.Bid.Length == 0 || e.Bid.Any(c => c is ' ' or '\t' or '\n' or '\r') || e.Title.Contains('\n') || e.Title.Contains('\r'))
            {
                throw new FormatException($"Entry {e.Bid} cannot be serialised.");
            }
        }
        return new Directory(date, entries, schedule, mode);
    }
}
