using System.Globalization;
using System.Text;

namespace Mailcast.Core;

/// <summary>A final answer from the BBS about a bulletin, as the archive records it.</summary>
public enum BbsVerdict
{
    /// <summary>The BBS took it.</summary>
    Accepted,

    /// <summary>The BBS already had its BID.</summary>
    AlreadyHad,

    /// <summary>The BBS refused it, or it could not be offered at all.</summary>
    Refused,
}

/// <summary>One bulletin the receiver holds, waiting in the outbox or kept in the archive.</summary>
/// <param name="ObjectId">The broadcast object it was rebuilt from, which names its files.</param>
/// <param name="Waiting">True if it is in the outbox, waiting for the BBS; false if archived.</param>
/// <param name="Type">The message type, B for a bulletin.</param>
/// <param name="Bid">Its BID.</param>
/// <param name="From">The sender.</param>
/// <param name="To">The addressee or category.</param>
/// <param name="At">The distribution, possibly empty.</param>
/// <param name="Title">The subject line.</param>
/// <param name="Date">When the bulletin was written.</param>
/// <param name="Size">The stored bulletin's size in bytes: its header block and message text.</param>
/// <param name="Time">When it went into the outbox, or, archived, when the BBS answered for it.</param>
/// <param name="Verdict">What the BBS said, for an archived bulletin.</param>
/// <param name="Detail">More about the answer, if the BBS gave any.</param>
public sealed record MailEntry(
    ulong ObjectId, bool Waiting, char Type, string Bid, string From, string To, string At, string Title,
    DateTimeOffset Date, int Size, DateTimeOffset Time, BbsVerdict? Verdict, string? Detail)
{
    internal static MailEntry Of(ulong objectId, Bulletin bulletin, int size, DateTimeOffset time, BbsVerdict? verdict, string? detail) =>
        new(objectId, verdict is null, bulletin.Type, bulletin.Bid, bulletin.From, bulletin.To, bulletin.At, bulletin.Title,
            bulletin.Date, size, time, verdict, detail);
}

/// <summary>
/// The receiver's own copies of bulletins the BBS has answered for, with the answer, kept for a
/// while so that nothing is lost if the BBS later loses or refuses mail.
/// </summary>
/// <remarks>
/// <para>One file per bulletin, <c>OOOOOOOOOOOOOOOO.mail</c>, written to a temporary name,
/// flushed, renamed and the folder flushed. The file is a short header block, an empty line,
/// then the bulletin exactly as the outbox held it:</para>
/// <code>
/// Mailcast-Archive: 1
/// Answered: 2026-10-05T12:00:00Z
/// Verdict: accepted          (accepted, already-had or refused)
/// Detail: ...                (only if there is more to say)
///
/// Type: B ...                the bulletin's own header block and message text
/// </code>
/// <para>An index of what is held is kept in memory, so listing and pruning read no files.
/// Entries go oldest answer first once older than the retention or once the archive is over its
/// size cap. A file that cannot be read is moved to the quarantine folder and logged.</para>
/// <para>Not thread-safe; the <see cref="ReceiverStore"/> that owns it is used from one thread.</para>
/// </remarks>
internal sealed class MailArchive
{
    private const string Extension = ".mail";
    private const string Magic = "Mailcast-Archive";
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private readonly string _folder;
    private readonly string _quarantine;
    private readonly TimeSpan _retention;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;
    private readonly bool _flush;
    private readonly Action<string> _log;
    private readonly Dictionary<ulong, (MailEntry Entry, long FileBytes)> _index = [];
    private long _totalBytes;

    /// <summary>
    /// Opens the archive in <paramref name="folder"/>, reading what is there. A missing folder is
    /// an empty archive. Nothing here throws: unreadable files are quarantined or skipped, and logged.
    /// </summary>
    public MailArchive(string folder, string quarantine, TimeSpan retention, long maxBytes, TimeProvider time, bool flush, Action<string> log)
    {
        _folder = folder;
        _quarantine = quarantine;
        _retention = retention;
        _maxBytes = maxBytes;
        _time = time;
        _flush = flush;
        _log = log;
        if (!System.IO.Directory.Exists(folder))
        {
            return; // an install from before the archive, or nothing answered yet
        }
        List<string> files;
        try
        {
            files = [.. System.IO.Directory.EnumerateFiles(folder, "*" + Extension)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing in the archive is worth failing start-up for.
            _log($"archive folder cannot be read, starting with an empty archive: {e.Message}");
            return;
        }
        foreach (var file in files)
        {
            if (!ObjectId.TryParse(Path.GetFileNameWithoutExtension(file), out ulong id))
            {
                continue; // not ours; leave it alone
            }
            try
            {
                var bytes = File.ReadAllBytes(file);
                var (entry, _) = Parse(id, bytes);
                Index(entry, bytes.Length);
            }
            catch (FormatException e)
            {
                Quarantine(file, e.Message);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log($"archive file {Path.GetFileName(file)} cannot be read, skipped: {e.Message}");
            }
        }
        Prune();
    }

    /// <summary>Whether anything is kept at all: a retention or size cap of zero keeps nothing.</summary>
    public bool Enabled => _retention > TimeSpan.Zero && _maxBytes > 0;

    /// <summary>What is held, in no particular order.</summary>
    public IEnumerable<MailEntry> Entries => _index.Values.Select(v => v.Entry);

    /// <summary>How many bulletins are held.</summary>
    public int Count => _index.Count;

    /// <summary>The archive's files' total size in bytes.</summary>
    public long TotalBytes => _totalBytes;

    /// <summary>Whether a bulletin is held.</summary>
    public bool Contains(ulong objectId) => _index.ContainsKey(objectId);

    /// <summary>
    /// Keeps <paramref name="serialized"/> (the bulletin as the outbox held it) with the BBS's
    /// answer, replacing any copy already held, then prunes. Throws on a write that fails.
    /// </summary>
    public void Add(ulong objectId, ReadOnlySpan<byte> serialized, Bulletin bulletin, BbsVerdict verdict, string? detail)
    {
        if (!Enabled)
        {
            return;
        }
        var now = _time.GetUtcNow();
        now = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        detail = OneLine(detail);
        var header = new StringBuilder()
            .Append(Magic).Append(": 1\n")
            .Append("Answered: ").Append(now.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)).Append('\n')
            .Append("Verdict: ").Append(Token(verdict)).Append('\n');
        if (detail is not null)
        {
            header.Append("Detail: ").Append(detail).Append('\n');
        }
        header.Append('\n');
        byte[] head = Bulletin.TextEncoding.GetBytes(header.ToString());
        byte[] content = new byte[head.Length + serialized.Length];
        head.CopyTo(content, 0);
        serialized.CopyTo(content.AsSpan(head.Length));

        System.IO.Directory.CreateDirectory(_folder);
        DurableFile.WriteAtomically(PathOf(objectId), content, flush: _flush); // flushes the folder too
        Forget(objectId);
        Index(MailEntry.Of(objectId, bulletin, serialized.Length, now, verdict, detail), content.Length);
        Prune();
    }

    /// <summary>
    /// The bulletin as it was stored, and its entry, or null if it is not held. A file that turns
    /// out to be unreadable is quarantined and forgotten.
    /// </summary>
    public (MailEntry Entry, byte[] Serialized)? Read(ulong objectId)
    {
        if (!_index.ContainsKey(objectId))
        {
            return null;
        }
        string file = PathOf(objectId);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (FileNotFoundException)
        {
            Forget(objectId);
            return null;
        }
        try
        {
            var (entry, serialized) = Parse(objectId, bytes);
            return (entry, serialized);
        }
        catch (FormatException e)
        {
            Forget(objectId);
            Quarantine(file, e.Message);
            return null;
        }
    }

    /// <summary>Removes a bulletin from the archive (it has gone back to the outbox).</summary>
    public void Remove(ulong objectId)
    {
        if (!_index.ContainsKey(objectId))
        {
            return;
        }
        File.Delete(PathOf(objectId));
        Forget(objectId);
        if (_flush)
        {
            DurableFile.FlushDirectory(_folder);
        }
    }

    /// <summary>Drops entries past the retention, then the oldest until the archive is within its size cap.</summary>
    public void Prune()
    {
        if (_index.Count == 0)
        {
            return;
        }
        var now = _time.GetUtcNow();
        int dropped = 0;
        foreach (var (id, (entry, _)) in _index.OrderBy(e => e.Value.Entry.Time).ThenBy(e => e.Key).ToList())
        {
            if (now - entry.Time <= _retention && _totalBytes <= _maxBytes)
            {
                break;
            }
            try
            {
                File.Delete(PathOf(id));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log($"archive file {ObjectId.Format(id)}{Extension} cannot be removed: {e.Message}");
                break;
            }
            Forget(id);
            dropped++;
        }
        if (dropped > 0)
        {
            _log($"archive: removed the {dropped} oldest bulletin{(dropped == 1 ? "" : "s")}; {_index.Count} kept, {_totalBytes / 1024} KB");
        }
    }

    private void Index(MailEntry entry, long fileBytes)
    {
        _index[entry.ObjectId] = (entry, fileBytes);
        _totalBytes += fileBytes;
    }

    private void Forget(ulong objectId)
    {
        if (_index.Remove(objectId, out var held))
        {
            _totalBytes -= held.FileBytes;
        }
    }

    private void Quarantine(string file, string why)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_quarantine);
            File.Move(file, Path.Combine(_quarantine, Path.GetFileName(file)), overwrite: true);
            _log($"archive file {Path.GetFileName(file)} unreadable, quarantined: {why}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"archive file {Path.GetFileName(file)} unreadable ({why}) and cannot be quarantined: {e.Message}");
        }
    }

    private string PathOf(ulong objectId) => Path.Combine(_folder, ObjectId.Format(objectId) + Extension);

    /// <summary>Reads an archive file: its entry, and the bulletin as stored. Throws <see cref="FormatException"/>.</summary>
    internal static (MailEntry Entry, byte[] Serialized) Parse(ulong objectId, byte[] bytes)
    {
        int split = bytes.AsSpan().IndexOf("\n\n"u8);
        if (split < 0)
        {
            throw new FormatException("no end to the archive header");
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in Bulletin.TextEncoding.GetString(bytes, 0, split).Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new FormatException("a header line without a key");
            }
            fields[line[..colon]] = line[(colon + 1)..].TrimStart(' ');
        }
        if (!fields.TryGetValue(Magic, out var version) || version != "1")
        {
            throw new FormatException("not an archive file of a version this receiver reads");
        }
        if (!fields.TryGetValue("Answered", out var answered)
            || !DateTimeOffset.TryParseExact(answered, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
        {
            throw new FormatException("no Answered time");
        }
        if (!fields.TryGetValue("Verdict", out var token) || Verdict(token) is not { } verdict)
        {
            throw new FormatException("no Verdict");
        }
        byte[] serialized = bytes[(split + 2)..];
        var bulletin = Bulletin.Parse(serialized);
        return (MailEntry.Of(objectId, bulletin, serialized.Length, time, verdict, fields.GetValueOrDefault("Detail")), serialized);
    }

    private static string Token(BbsVerdict verdict) => verdict switch
    {
        BbsVerdict.Accepted => "accepted",
        BbsVerdict.AlreadyHad => "already-had",
        _ => "refused",
    };

    private static BbsVerdict? Verdict(string token) => token switch
    {
        "accepted" => BbsVerdict.Accepted,
        "already-had" => BbsVerdict.AlreadyHad,
        "refused" => BbsVerdict.Refused,
        _ => null,
    };

    /// <summary>A detail fit for one header line: control characters become spaces, and anything past Latin-1 a question mark.</summary>
    private static string? OneLine(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }
        var clean = new StringBuilder(detail.Length);
        foreach (char c in detail)
        {
            clean.Append(c < ' ' || c == '\x7f' ? ' ' : c > '\xff' ? '?' : c);
        }
        string text = clean.ToString().Trim();
        return text.Length > 500 ? text[..500] : text;
    }
}
