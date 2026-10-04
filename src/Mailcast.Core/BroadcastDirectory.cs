using System.Globalization;
using System.Text;

namespace Mailcast.Core;

/// <summary>One object in rotation, as the directory lists it.</summary>
/// <param name="ObjectId">The object ID frames carry, which is also the object's own hash.</param>
/// <param name="DictionaryId">The zstd dictionary the object was compressed with.</param>
/// <param name="Size">The serialised bulletin's length in octets, before compression.</param>
/// <param name="Bid">The bulletin ID.</param>
/// <param name="Title">The subject line.</param>
public sealed record DirectoryEntry(ulong ObjectId, ushort DictionaryId, int Size, string Bid, string Title);

/// <summary>
/// The list of objects in rotation on one day's broadcast, so that a receiver can tell how many
/// bulletins it has heard of and how many it has rebuilt.
/// </summary>
/// <remarks>
/// <para>Serialised as Latin-1 text, lines ended by LF: a version line, the date, then one line per entry.</para>
/// <code>
/// MAILCAST DIRECTORY 1
/// 2026-10-04
/// 0123456789abcdef TAB 1 TAB 2345 TAB 12345_GB7RDG TAB Title text
/// </code>
/// <para>
/// The entry fields, separated by tabs, are object ID (16 hex digits), dictionary ID, size, BID
/// and title. A reader ignores any fields after the title, so later versions can add some. A tab
/// in a title becomes a space.
/// </para>
/// <para>
/// Like every object, the directory's ID is the hash of its own octets, so two directories with
/// different contents never share an ID, even on the same day.
/// </para>
/// </remarks>
public sealed class BroadcastDirectory
{
    /// <summary>The first line of a serialised directory.</summary>
    public const string VersionLine = "MAILCAST DIRECTORY 1";

    /// <summary>Creates a directory.</summary>
    public BroadcastDirectory(DateOnly date, IEnumerable<DirectoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Date = date;
        Entries = entries.Select(e => e with { Title = e.Title.Replace('\t', ' ') }).ToArray();
        foreach (var e in Entries)
        {
            if (e.Bid.Length == 0 || e.Bid.Any(c => c is ' ' or '\t' or '\n' or '\r') || e.Title.Contains('\n') || e.Title.Contains('\r'))
            {
                throw new ArgumentException($"Entry {e.Bid} cannot be serialised.", nameof(entries));
            }
        }
    }

    /// <summary>The broadcast day.</summary>
    public DateOnly Date { get; }

    /// <summary>The objects in rotation.</summary>
    public IReadOnlyList<DirectoryEntry> Entries { get; }

    /// <summary>The entry for an object, if listed.</summary>
    public DirectoryEntry? Find(ulong objectId) => Entries.FirstOrDefault(e => e.ObjectId == objectId);

    /// <summary>The serialised form described in the class remarks.</summary>
    public byte[] Serialize()
    {
        var text = new StringBuilder();
        text.Append(VersionLine).Append('\n');
        text.Append(Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var e in Entries)
        {
            text.Append(CultureInfo.InvariantCulture, $"{ObjectId.Format(e.ObjectId)}\t{e.DictionaryId}\t{e.Size}\t{e.Bid}\t{e.Title}\n");
        }
        return Bulletin.TextEncoding.GetBytes(text.ToString());
    }

    /// <summary>Reads the serialised form. Throws <see cref="FormatException"/> if it is not one.</summary>
    public static BroadcastDirectory Parse(ReadOnlySpan<byte> serialized)
    {
        var lines = Bulletin.TextEncoding.GetString(serialized).Split('\n');
        if (lines.Length < 3 || lines[^1].Length != 0)
        {
            throw new FormatException("A directory is lines ended by LF.");
        }
        if (lines[0] != VersionLine)
        {
            throw new FormatException($"Not a directory this code reads: {lines[0]}");
        }
        if (!DateOnly.TryParseExact(lines[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new FormatException("The directory's date is not yyyy-MM-dd.");
        }
        var entries = new List<DirectoryEntry>();
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
            entries.Add(new DirectoryEntry(objectId, dictionaryId, size, parts[3], parts[4]));
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
