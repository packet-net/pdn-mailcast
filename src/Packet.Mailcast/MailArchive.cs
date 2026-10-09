using System.Globalization;
using System.Text;

namespace Packet.Mailcast;

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
/// <param name="ObjectId">The object it was rebuilt from, which names its files.</param>
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
/// <param name="Completed">
/// When the bulletin was rebuilt (went into the outbox), kept alongside <paramref name="Time"/> so
/// that an archived entry still shows it, not only the BBS's answer time. Null for an archive file
/// written before this was recorded; it means unknown, not zero.
/// </param>
public sealed record MailEntry(
    ulong ObjectId, bool Waiting, char Type, string Bid, string From, string To, string At, string Title,
    DateTimeOffset Date, int Size, DateTimeOffset Time, BbsVerdict? Verdict, string? Detail, DateTimeOffset? Completed)
{
    internal static MailEntry Of(ulong objectId, Bulletin bulletin, int size, DateTimeOffset time, BbsVerdict? verdict, string? detail, DateTimeOffset? completed) =>
        new(objectId, verdict is null, bulletin.Type, bulletin.Bid, bulletin.From, bulletin.To, bulletin.At, bulletin.Title,
            bulletin.Date, size, time, verdict, detail, completed);
}

/// <summary>
/// The bulletins held, newest first, as they were at one moment. It never changes, so it can be
/// read from any thread while the store goes on.
/// </summary>
public sealed class MailSnapshot
{
    private readonly MailEntry[] _newestFirst;
    private readonly Dictionary<ulong, byte[]> _waiting;
    private readonly Dictionary<ulong, MailEntry> _byId;

    /// <summary>Nothing held.</summary>
    public static readonly MailSnapshot Empty = new([], 0, []);

    internal MailSnapshot(MailEntry[] newestFirst, int waiting, Dictionary<ulong, byte[]> waitingBytes)
    {
        _newestFirst = newestFirst;
        Waiting = waiting;
        _waiting = waitingBytes;
        _byId = newestFirst.ToDictionary(e => e.ObjectId);
    }

    /// <summary>How many bulletins are held in all.</summary>
    public int Count => _newestFirst.Length;

    /// <summary>How many are waiting in the outbox.</summary>
    public int Waiting { get; }

    /// <summary>How many are archived and not waiting.</summary>
    public int Archived => _newestFirst.Length - Waiting;

    /// <summary>Every bulletin held, newest first.</summary>
    public IReadOnlyList<MailEntry> NewestFirst => _newestFirst;

    /// <summary>Up to <paramref name="limit"/> entries from <paramref name="offset"/>, newest first, without copying the rest.</summary>
    public IReadOnlyList<MailEntry> Page(int offset, int limit)
    {
        offset = Math.Clamp(offset, 0, _newestFirst.Length);
        return new ArraySegment<MailEntry>(_newestFirst, offset, Math.Clamp(limit, 0, _newestFirst.Length - offset));
    }

    /// <summary>Whether a bulletin is waiting in the outbox.</summary>
    public bool IsWaiting(ulong objectId) => _waiting.ContainsKey(objectId);

    /// <summary>A waiting bulletin as the outbox holds it, if it is waiting.</summary>
    internal byte[]? WaitingBytes(ulong objectId) => _waiting.GetValueOrDefault(objectId);

    /// <summary>The entry for an object, whether waiting or archived, if it is held at all.</summary>
    public MailEntry? ById(ulong objectId) => _byId.GetValueOrDefault(objectId);
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
/// Completed: 2026-10-05T11:58:00Z  (when it was rebuilt; missing in a file from before this was kept)
/// Verdict: accepted          (accepted, already-had or refused)
/// Detail: ...                (only if there is more to say)
///
/// Type: B ...                the bulletin's own header block and message text
/// </code>
/// <para>An index of what is held is kept in memory in answer order, so listing and pruning read
/// no files; at start-up only each file's header lines are read. Entries go oldest answer first
/// once older than the retention or once the archive is over its size cap, checked when one is
/// added and when the store expires old things. A file that cannot be read is moved to the
/// quarantine folder and logged.</para>
/// <para>Not thread-safe, except <see cref="ReadFile"/>; the <see cref="ReceiverStore"/> that
/// owns it is used from one thread.</para>
/// </remarks>
internal sealed class MailArchive
{
    private const string Extension = ".mail";
    private const string Magic = "Mailcast-Archive";
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>The most of a file read at start-up to find its two header blocks.</summary>
    internal const int MaxHeaderBytes = 16 * 1024;

    private static readonly string[] BulletinKeys = ["Type", "From", "To", "At", "Bid", "Date", "Title"];

    private readonly string _folder;
    private readonly string _quarantine;
    private readonly TimeSpan _retention;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;
    private readonly bool _flush;
    private readonly Action<string> _log;
    private readonly Action _countRead;
    private readonly Dictionary<ulong, (MailEntry Entry, long FileBytes)> _index = [];
    private static readonly IComparer<MailEntry> ByAnswer = Comparer<MailEntry>.Create((a, b) =>
        a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.ObjectId.CompareTo(b.ObjectId));

    private System.Collections.Immutable.ImmutableSortedSet<MailEntry> _order = System.Collections.Immutable.ImmutableSortedSet.Create(ByAnswer);
    private long _totalBytes;

    /// <summary>
    /// Opens the archive in <paramref name="folder"/>, reading the header lines of what is there.
    /// A missing folder is an empty archive. Nothing here throws: unreadable files are quarantined
    /// or skipped, and logged. <paramref name="countRead"/> is called for each read of the disk.
    /// </summary>
    public MailArchive(string folder, string quarantine, TimeSpan retention, long maxBytes, TimeProvider time, bool flush, Action<string> log, Action countRead)
    {
        _folder = folder;
        _quarantine = quarantine;
        _retention = retention;
        _maxBytes = maxBytes;
        _time = time;
        _flush = flush;
        _log = log;
        _countRead = countRead;
        if (!System.IO.Directory.Exists(folder))
        {
            return; // an install from before the archive, or nothing answered yet
        }
        List<string> files;
        try
        {
            _countRead();
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
                _countRead();
                var (entry, fileBytes) = ReadHeader(id, file);
                Index(entry, fileBytes);
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

    /// <summary>What is held, newest answer first.</summary>
    public IEnumerable<MailEntry> NewestFirst() => _order.Reverse();

    /// <summary>What is held, oldest answer first, as an immutable set any thread may read.</summary>
    public System.Collections.Immutable.ImmutableSortedSet<MailEntry> Order => _order;

    /// <summary>How many bulletins are held.</summary>
    public int Count => _index.Count;

    /// <summary>The archive's files' total size in bytes.</summary>
    public long TotalBytes => _totalBytes;

    /// <summary>Whether a bulletin is held.</summary>
    public bool Contains(ulong objectId) => _index.ContainsKey(objectId);

    /// <summary>Where a bulletin's file is.</summary>
    public string PathOf(ulong objectId) => Path.Combine(_folder, ObjectId.Format(objectId) + Extension);

    /// <summary>
    /// Keeps <paramref name="serialized"/> (the bulletin as the outbox held it) with the BBS's
    /// answer, replacing any copy already held, then prunes. An answer of already had to a
    /// bulletin the archive has as accepted (sent again, or offered twice after a crash) keeps
    /// accepted: the BBS has it either way, and accepted says more. Throws on a write that fails.
    /// </summary>
    public void Add(ulong objectId, ReadOnlySpan<byte> serialized, Bulletin bulletin, BbsVerdict verdict, string? detail, DateTimeOffset completed)
    {
        if (!Enabled)
        {
            return;
        }
        if (verdict == BbsVerdict.AlreadyHad && _index.TryGetValue(objectId, out var held) && held.Entry.Verdict == BbsVerdict.Accepted)
        {
            verdict = BbsVerdict.Accepted;
            detail = "offered again; the BBS already had it";
        }
        var now = _time.GetUtcNow();
        now = Rounded(now);
        completed = Rounded(completed);
        detail = OneLine(detail);
        var header = new StringBuilder()
            .Append(Magic).Append(": 1\n")
            .Append("Answered: ").Append(now.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)).Append('\n')
            .Append("Completed: ").Append(completed.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)).Append('\n')
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
        Index(MailEntry.Of(objectId, bulletin, serialized.Length, now, verdict, detail, completed), content.Length);
        Prune();
    }

    /// <summary>A time with no finer than one-second precision, which is all the header format carries.</summary>
    private static DateTimeOffset Rounded(DateTimeOffset time) =>
        new(time.Ticks - (time.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    /// <summary>
    /// Reads a held bulletin's file whole: its entry and the bulletin as stored. Safe from any
    /// thread, since it only reads. Throws <see cref="FormatException"/> for a file that does not
    /// read, and <see cref="IOException"/> (including <see cref="FileNotFoundException"/>) or
    /// <see cref="UnauthorizedAccessException"/> for one that cannot be read.
    /// </summary>
    public static (MailEntry Entry, byte[] Serialized) ReadFile(ulong objectId, string file) => Parse(objectId, File.ReadAllBytes(file));

    /// <summary>Forgets a bulletin whose file turned out to be unreadable, and quarantines the file.</summary>
    public void Unreadable(ulong objectId, string why)
    {
        Forget(objectId);
        Quarantine(PathOf(objectId), why);
    }

    /// <summary>Drops entries past the retention, then the oldest until the archive is within its size cap; only the oldest is looked at.</summary>
    public void Prune()
    {
        var now = _time.GetUtcNow();
        int dropped = 0, stuck = 0;
        string? why = null;
        while (_order.Count > 0)
        {
            var oldest = _order.Min!;
            if (now - oldest.Time <= _retention && _totalBytes <= _maxBytes)
            {
                break;
            }
            try
            {
                File.Delete(PathOf(oldest.ObjectId));
                dropped++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Forgotten all the same, so one file that will not go cannot stop the rest; it
                // is found again, and tried again, at the next start.
                stuck++;
                why ??= $"{ObjectId.Format(oldest.ObjectId)}{Extension}: {e.Message}";
            }
            Forget(oldest.ObjectId);
        }
        if (stuck > 0)
        {
            _log($"archive: {stuck} old file{(stuck == 1 ? "" : "s")} could not be removed and {(stuck == 1 ? "is" : "are")} no longer listed (first: {why})");
        }
        if (dropped > 0)
        {
            _log($"archive: removed the {dropped} oldest bulletin{(dropped == 1 ? "" : "s")}; {_index.Count} kept, {_totalBytes / 1024} KB");
        }
    }

    private void Index(MailEntry entry, long fileBytes)
    {
        _index[entry.ObjectId] = (entry, fileBytes);
        _order = _order.Add(entry);
        _totalBytes += fileBytes;
    }

    private void Forget(ulong objectId)
    {
        if (_index.Remove(objectId, out var held))
        {
            _order = _order.Remove(held.Entry);
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

    /// <summary>
    /// Reads only a file's header lines, the archive's and the bulletin's, for the index: the
    /// text is not read until the bulletin is. Throws <see cref="FormatException"/>.
    /// </summary>
    internal static (MailEntry Entry, long FileBytes) ReadHeader(ulong objectId, string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
        long length = stream.Length;
        var buffer = new byte[(int)Math.Min(length, MaxHeaderBytes)];
        int read = 0, archiveEnd = -1, bulletinStart = 0, bulletinEnd = -1;
        // A little at a time: the headers are normally well inside the first 1 KB.
        while (bulletinEnd < 0 && read < buffer.Length)
        {
            int n = stream.Read(buffer, read, Math.Min(1024, buffer.Length - read));
            if (n <= 0)
            {
                break;
            }
            read += n;
            var seen = buffer.AsSpan(0, read);
            if (archiveEnd < 0 && (archiveEnd = seen.IndexOf("\n\n"u8)) >= 0)
            {
                bulletinStart = archiveEnd + 2;
            }
            if (archiveEnd >= 0 && read > bulletinStart)
            {
                bulletinEnd = seen[bulletinStart..].IndexOf("\n\n"u8);
            }
        }
        var span = buffer.AsSpan(0, read);
        if (archiveEnd < 0)
        {
            throw new FormatException("no end to the archive header");
        }
        if (bulletinEnd < 0)
        {
            throw new FormatException("no end to the bulletin header in the first " + MaxHeaderBytes / 1024 + " KB");
        }
        var (time, completed, verdict, detail) = ArchiveFields(Bulletin.TextEncoding.GetString(span[..archiveEnd]));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in Bulletin.TextEncoding.GetString(span.Slice(bulletinStart, bulletinEnd)).Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new FormatException("a bulletin header line without a key");
            }
            int valueStart = colon + 1 < line.Length && line[colon + 1] == ' ' ? colon + 2 : colon + 1;
            if (!fields.TryAdd(line[..colon], line[valueStart..]))
            {
                throw new FormatException($"the bulletin header has {line[..colon]} twice");
            }
        }
        foreach (var key in BulletinKeys)
        {
            if (!fields.ContainsKey(key))
            {
                throw new FormatException($"the bulletin header has no {key}");
            }
        }
        if (fields["Type"] is not { Length: 1 } type || type[0] is < 'A' or > 'Z')
        {
            throw new FormatException("the bulletin type is not one letter");
        }
        if (!DateTimeOffset.TryParseExact(fields["Date"], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            throw new FormatException("the bulletin date is not yyyy-MM-ddTHH:mm:ssZ");
        }
        long size = length - bulletinStart;
        var entry = new MailEntry(objectId, false, type[0], fields["Bid"], fields["From"], fields["To"], fields["At"], fields["Title"],
            date, (int)Math.Min(size, int.MaxValue), time, verdict, detail, completed);
        return (entry, length);
    }

    /// <summary>Reads an archive file whole: its entry, and the bulletin as stored. Throws <see cref="FormatException"/>.</summary>
    internal static (MailEntry Entry, byte[] Serialized) Parse(ulong objectId, byte[] bytes)
    {
        int split = bytes.AsSpan().IndexOf("\n\n"u8);
        if (split < 0)
        {
            throw new FormatException("no end to the archive header");
        }
        var (time, completed, verdict, detail) = ArchiveFields(Bulletin.TextEncoding.GetString(bytes, 0, split));
        byte[] serialized = bytes[(split + 2)..];
        var bulletin = Bulletin.Parse(serialized);
        return (MailEntry.Of(objectId, bulletin, serialized.Length, time, verdict, detail, completed), serialized);
    }

    /// <summary>
    /// Reads the archive header's own fields. <c>Completed</c> is optional: a file written before
    /// it was recorded, or one with an unparseable value, gives null rather than failing to read.
    /// </summary>
    private static (DateTimeOffset Time, DateTimeOffset? Completed, BbsVerdict Verdict, string? Detail) ArchiveFields(string header)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in header.Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new FormatException("an archive header line without a key");
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
        DateTimeOffset? completed = fields.TryGetValue("Completed", out var completedText)
            && DateTimeOffset.TryParseExact(completedText, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var c)
                ? c : null;
        return (time, completed, verdict, fields.GetValueOrDefault("Detail"));
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
