using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Packet.Mailcast;
using Packet.Mailcast.Feedback;

namespace Mailcast.HeadEnd.Intake;

/// <summary>
/// Learns whose daily reports have reached the BBS (issue #136) by reading its list of them, as
/// any user can: it logs in as an ordinary reader, lists the bulletins to MCAST
/// (<see cref="ListCommand"/>), takes the callsign and day from each <c>MCR G4ABC 2026-10-15</c>
/// title, notes them in the <see cref="ReportLog"/>, and logs off. It never reads a report, takes
/// nothing for the broadcast, and changes nothing on the BBS.
/// </summary>
/// <remarks>
/// <para>It runs with the other intakes, just before each slot plans its directory and on their
/// timer, but reads at most every <see cref="Every"/>: in between, a pass does nothing.</para>
/// <para>A line counts only when the callsign in its title is also the message's sender, the
/// column just before the title, as it is for every report a receiver sends. Nothing it sends is
/// logged, and a reply quoted in the journal has the password taken out.</para>
/// </remarks>
public sealed partial class ReportReader : IBulletinIntake, IDisposable
{
    /// <summary>What lists the reports: LinBPQ's list of messages to MCAST.</summary>
    public const string ListCommand = "L> " + DailyReport.BulletinTo;

    /// <summary>The least time between two readings.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    /// <summary>The longest one session may take.</summary>
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(60);

    private readonly ReportsReaderConfig _config;
    private readonly ReportLog _log;
    private readonly IJournal _journal;
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly SemaphoreSlim _one = new(1, 1);
    private DateTimeOffset? _lastTry;

    public ReportReader(ReportsReaderConfig config, ReportLog log, IJournal journal, TimeProvider time, Func<CancellationToken, Task<Stream>>? connect = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _connect = connect ?? ConnectTcpAsync;
    }

    /// <inheritdoc />
    public string Name => "reports";

    /// <inheritdoc />
    public void Dispose() => _one.Dispose();

    /// <inheritdoc />
    public async Task<IntakeResult> CollectAsync(DateOnly today, CancellationToken cancellation)
    {
        await _one.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_lastTry is { } last && now - last < Every)
            {
                return IntakeResult.Nothing;
            }
            _lastTry = now;
            using var timeout = new CancellationTokenSource(SessionTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
            try
            {
                string listing = await ListAsync(linked.Token).ConfigureAwait(false);
                Take(listing);
                return IntakeResult.Nothing;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                return new IntakeResult(0, 0, $"the session with {_config.Host}:{_config.Port} took over {SessionTimeout.TotalSeconds:0} s and was dropped");
            }
            catch (Exception e) when (e is IOException or SocketException or InvalidOperationException)
            {
                return new IntakeResult(0, 0, $"{_config.Host}:{_config.Port}: {Ascii.Plain(Redact(e.Message))}");
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<Stream> ConnectTcpAsync(CancellationToken cancellation)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(_config.Host, _config.Port, cancellation).ConfigureAwait(false);
            return new OwningStream(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Logs in, lists, logs off; returns what the list command answered.</summary>
    private async Task<string> ListAsync(CancellationToken cancellation)
    {
        await using Stream stream = await _connect(cancellation).ConfigureAwait(false);
        byte[] leftover = await BbsLogin.LogInAsync(stream, _config.Login, _config.User, _config.Password, _config.User, cancellation).ConfigureAwait(false);
        var reader = new PromptReader(stream, leftover);

        for (int pages = 0; ; pages++)
        {
            string welcome = await reader.UntilAsync(text => Prompt(text) is not null || Refusal(text) is not null, cancellation).ConfigureAwait(false);
            if (Refusal(welcome) is { } refusal)
            {
                throw new InvalidOperationException($"{refusal}; it said {Quoted(welcome)}");
            }
            if (!IsPagePrompt(Prompt(welcome)!) || pages >= MostPages)
            {
                break;
            }
            // A long welcome, paged.
            await stream.WriteAsync("\r"u8.ToArray(), cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }

        await stream.WriteAsync(Encoding.Latin1.GetBytes(ListCommand + "\r"), cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
        // The listing ends at the next prompt, whatever it says (it may carry a time or a count).
        // A user with paging on gets LinBPQ's page prompt every so many lines, answered with CR.
        var listing = new StringBuilder();
        for (int pages = 0; ; pages++)
        {
            string chunk = await reader.UntilAsync(text => Prompt(text) is not null, cancellation).ConfigureAwait(false);
            if (!IsPagePrompt(Prompt(chunk)!))
            {
                listing.Append(chunk);
                break;
            }
            if (pages >= MostPages)
            {
                throw new InvalidOperationException($"the list ran to more than {MostPages} pages");
            }
            listing.Append(chunk.AsSpan(0, chunk.TrimEnd().LastIndexOfAny(['\r', '\n']) + 1));
            await stream.WriteAsync("\r"u8.ToArray(), cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }

        try
        {
            await stream.WriteAsync("B\r"u8.ToArray(), cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Gone already: the list is what matters.
        }
        return listing.ToString();
    }

    /// <summary>Notes each report the listing shows.</summary>
    private void Take(string listing)
    {
        var now = _time.GetUtcNow();
        int listed = 0;
        var fresh = new List<string>();
        // Each callsign's newest only, so one listener with many reports counts once.
        var newest = new Dictionary<string, (string Message, DateOnly Day)>(StringComparer.Ordinal);
        foreach (var (message, callsign, day) in Reports(listing))
        {
            listed++;
            if (!newest.TryGetValue(callsign, out var had) || day > had.Day)
            {
                newest[callsign] = (message, day);
            }
        }
        foreach (var (callsign, (message, day)) in newest.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (_log.Note(callsign, day, now, message))
            {
                fresh.Add(string.Create(CultureInfo.InvariantCulture, $"{callsign} for {day:yyyy-MM-dd}"));
            }
        }
        _log.MarkRead();
        if (fresh.Count > 0)
        {
            _journal.Write($"reports: {fresh.Count} new of {listed} listed on the BBS, which the directory now lists: {string.Join(", ", fresh)}");
        }
    }

    /// <summary>
    /// The reports in a listing: each line starting with a message number and ending in a report
    /// title whose callsign is also the column before it, the sender.
    /// </summary>
    public static IEnumerable<(string Message, string Callsign, DateOnly Day)> Reports(string listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        foreach (string raw in listing.Split('\r', '\n'))
        {
            var m = ListingLine().Match(raw.Trim());
            if (m.Success
                && m.Groups["from"].Value.Equals(m.Groups["call"].Value, StringComparison.OrdinalIgnoreCase)
                && DailyReport.TryParseTitle($"{DailyReport.Tag} {m.Groups["call"].Value} {m.Groups["day"].Value}", out string callsign, out DateOnly day)
                && ReportReceipts.IsListable(callsign))
            {
                yield return (m.Groups["number"].Value, callsign, day);
            }
        }
    }

    /// <summary>The most pages of a paged listing the reader goes through.</summary>
    private const int MostPages = 1000;

    /// <summary>
    /// The prompt at the end of <paramref name="text"/>, or null if none has come: a last line
    /// ending in <c>&gt;</c> that is not a listing line (those start with a message number and a
    /// date), such as <c>de GB7RDG&gt;</c>, one with a time or a count in it, or LinBPQ's page
    /// prompt (<see cref="IsPagePrompt"/>).
    /// </summary>
    internal static string? Prompt(string text)
    {
        string trimmed = text.TrimEnd();
        if (!trimmed.EndsWith('>'))
        {
            return null;
        }
        int start = trimmed.LastIndexOfAny(['\r', '\n']);
        string line = trimmed[(start + 1)..].Trim();
        return line.Length > 0 && !ListingStart().IsMatch(line) ? line : null;
    }

    /// <summary>LinBPQ's prompt between pages for a user with paging on: <c>&lt;A&gt;bort, &lt;CR&gt; Continue..&gt;</c>.</summary>
    internal static bool IsPagePrompt(string prompt) =>
        prompt.Contains("Continue", StringComparison.OrdinalIgnoreCase) && prompt.Contains("<A>bort", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why the BBS did not let the reader in, from what it said, or null if it did.</summary>
    private string? Refusal(string text)
    {
        string login = Ascii.Plain(_config.User);
        if (text.Contains("user:", StringComparison.OrdinalIgnoreCase))
        {
            return $"the BBS asked for the login again, so it does not know \"{login}\" (LinBPQ logins are case-sensitive: it must match the USER= line exactly)";
        }
        if (text.Contains("password:", StringComparison.OrdinalIgnoreCase))
        {
            return $"the BBS asked for the password again, so it did not take the password for \"{login}\"";
        }
        if (text.Contains("Name", StringComparison.Ordinal) && text.Contains("enter", StringComparison.OrdinalIgnoreCase))
        {
            return $"the BBS asked for a name, so \"{login}\" is a new user there; log in once by hand and give one";
        }
        return null;
    }

    /// <summary>
    /// The shortest password whose occurrences can be taken out of a quoted reply; with a shorter
    /// one where the marks fell would say too much about it, so the reply is not quoted at all.
    /// </summary>
    internal const int ShortestRedactedPassword = 8;

    /// <summary>The BBS's reply in quotes for the journal, as <see cref="Shown"/> gives it, or <c>(reply not shown)</c> with a short password.</summary>
    private string Quoted(string text) =>
        _config.Password.Length is > 0 and < ShortestRedactedPassword ? "(reply not shown)" : $"\"{Shown(text)}\"";

    /// <summary>The BBS's reply as one plain ASCII line for the journal, the password taken out, cut short past 200 characters.</summary>
    private string Shown(string text)
    {
        string plain = Ascii.Plain(Redact(text).Replace("\r", "<CR>", StringComparison.Ordinal).Replace("\n", "<LF>", StringComparison.Ordinal));
        return plain.Length > 200 ? plain[..200] + "..." : plain;
    }

    private string Redact(string text) =>
        _config.Password.Length == 0 ? text : text.Replace(_config.Password, "<password>", StringComparison.Ordinal);

    // LinBPQ: "13414  16-Oct B$     500 MCAST  @GB7RDG G4ABC  MCR G4ABC 2026-10-15".
    [GeneratedRegex(@"^(?<number>\d+)\s.*\s(?<from>[A-Za-z0-9]+)\s+MCR (?<call>[A-Z0-9]{1,12}) (?<day>\d{4}-\d{2}-\d{2})$")]
    private static partial Regex ListingLine();

    // The start of any LinBPQ listing line: the message number, then its date ("13414  16-Oct").
    [GeneratedRegex(@"^\d+\s+\d{1,2}-[A-Za-z]{3}\s")]
    private static partial Regex ListingStart();

    /// <summary>Reads what the BBS sends, telnet negotiation dropped, until a test says enough has come.</summary>
    private sealed class PromptReader(Stream stream, byte[] already)
    {
        private readonly List<byte> _pending = [.. already];

        /// <summary>Reads until <paramref name="enough"/> holds of what has come since the last call; returns that and starts afresh.</summary>
        public async Task<string> UntilAsync(Func<string, bool> enough, CancellationToken cancellation)
        {
            var buffer = new byte[4096];
            while (true)
            {
                string text = Encoding.Latin1.GetString([.. _pending]);
                if (enough(text))
                {
                    _pending.Clear();
                    return text;
                }
                int n = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new IOException("the BBS closed the connection before its prompt");
                }
                _pending.AddRange(BbsLogin.StripTelnet(buffer.AsSpan(0, n)));
            }
        }
    }
}
