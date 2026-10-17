using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Mailcast.Receiver.Retune;

/// <summary>
/// LinBPQ said no, or could not be asked. <see cref="MaybeApplied"/> says whether LinBPQ may
/// have acted on a command that changes the port anyway: it was sent and no answer that says
/// otherwise came back.
/// </summary>
public sealed class BpqNodeException(string message, bool maybeApplied = false) : Exception(message)
{
    /// <summary>LinBPQ may have done it: only a known refusal, or not sending it at all, says it did not.</summary>
    public bool MaybeApplied { get; } = maybeApplied;
}

/// <summary>
/// A sysop session on LinBPQ's node telnet port, for its <c>XMITOFF</c> command: turning a
/// port's transmit off and on, reading it back, and reading the port's name from <c>PORTS</c>.
/// </summary>
/// <remarks>
/// <para>The exchange, as LinBPQ 6.0.25 has it (TelnetV6.c and Cmd.c):</para>
/// <code>
/// &lt;- user:
/// -&gt; USER
/// &lt;- password:
/// -&gt; PASSWORD-OF-USER
/// &lt;- (a new line, then the Telnet port's CTEXT, ending in a bare CR, or "Connected to
///     GB7TST's Telnet Server" when it has none; input that came with the password is lost)
/// -&gt; PASSWORD                          (once the welcome has gone quiet; once more if no answer)
/// &lt;- TST:GB7TST} Ok                     (a USER= line with SYSOP gets sysop status this way)
/// -&gt; XMITOFF 2
/// &lt;- TST:GB7TST} XMITOFF 0
/// -&gt; XMITOFF 2 1
/// &lt;- TST:GB7TST} XMITOFF was 0 now 1
/// -&gt; PORTS
/// &lt;- TST:GB7TST} Ports
/// &lt;-   1 Telnet
/// &lt;-   2 HF through QtSoundModem
/// </code>
/// <para>A user without SYSOP gets five numbers back for PASSWORD (a challenge this does not
/// answer); a wrong password gets the password prompt again, an unknown user the user prompt
/// again; a port that is not there, <c>Invalid Port Number</c>.</para>
/// <para>A read is always pending on the session. When LinBPQ ends the node session (its
/// IDLETIME, 900 s unless set, says "Disconnected from Node - Telnet Session kept" and keeps the
/// socket, but sysop status is gone) or closes the socket (LinBPQ stopping or restarting),
/// <see cref="SessionLost"/> is raised at once and the next command logs in afresh. A command
/// refused for want of sysop status on a session that had been used before is tried once more
/// on a fresh login.</para>
/// <para>LinBPQ can hand a new connection the end of an earlier one ("*** Disconnected from
/// Stream 1"), so until the login is done those lines are passed over.</para>
/// <para>Real I/O on a real socket, so real timeouts: a node that accepts and never answers is
/// given up on after <see cref="ReplyTimeout"/>.</para>
/// <para>Never puts the password in a message, nor anything about it: text LinBPQ sends back
/// that holds it is not quoted at all, nor is any while the password is under 8 characters.</para>
/// </remarks>
public sealed partial class BpqNode : IAsyncDisposable
{
    /// <summary>How long one answer from LinBPQ may take.</summary>
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the welcome after the password may take to start.</summary>
    private static readonly TimeSpan WelcomeWait = TimeSpan.FromSeconds(2);

    /// <summary>How long LinBPQ must have said nothing for its welcome to count as finished.</summary>
    private static readonly TimeSpan SettleQuiet = TimeSpan.FromMilliseconds(300);

    /// <summary>After PASSWORD was sent twice, how long to wait for a second answer to it.</summary>
    private static readonly TimeSpan ResentQuiet = TimeSpan.FromSeconds(1);

    private readonly BpqNodeSettings _settings;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly object _gate = new();
    private Session? _session;

    /// <summary>A node at <paramref name="settings"/>; nothing connects until a command is sent.</summary>
    public BpqNode(BpqNodeSettings settings) => _settings = settings;

    /// <summary>
    /// Raised, on the session's reader, with the reason, when LinBPQ ends a logged-in session
    /// that this did not end itself. The next command logs in again.
    /// </summary>
    public event Action<string>? SessionLost;

    /// <summary>Whether a logged-in session is open now.</summary>
    public bool Connected
    {
        get
        {
            lock (_gate)
            {
                return _session is { LoggedIn: true, LostWhy: null };
            }
        }
    }

    /// <summary>
    /// Turns transmit off (<paramref name="off"/>) or back on for <paramref name="port"/>, and
    /// returns what LinBPQ said it was before.
    /// </summary>
    /// <exception cref="BpqNodeException">
    /// LinBPQ refused, or could not be reached or understood. Only a known refusal (no such
    /// port, no sysop status) or a command never sent leaves <see cref="BpqNodeException.MaybeApplied"/> false.
    /// </exception>
    public async Task<bool> SetTransmitOffAsync(int port, bool off, CancellationToken cancellation)
    {
        string command = string.Create(CultureInfo.InvariantCulture, $"XMITOFF {port} {(off ? 1 : 0)}");
        var (reply, lostAfterSending) = await CommandAsync(command, null, cancellation).ConfigureAwait(false);
        var match = WasNow().Match(reply);
        if (!match.Success)
        {
            throw KnownRefusal(reply) is { } refused
                ? new BpqNodeException(refused, lostAfterSending)
                : new BpqNodeException($"LinBPQ answered \"{command}\" with {Quoted(reply)}, which says neither yes nor no", maybeApplied: true);
        }
        if ((match.Groups[2].Value != "0") != off)
        {
            throw new BpqNodeException($"LinBPQ answered \"{command}\" with {Quoted(reply)}, which leaves it the wrong way round", maybeApplied: true);
        }
        return match.Groups[1].Value != "0";
    }

    /// <summary>Whether transmit is off for <paramref name="port"/>, as LinBPQ says.</summary>
    /// <exception cref="BpqNodeException">LinBPQ refused, or could not be reached or understood.</exception>
    public async Task<bool> TransmitOffAsync(int port, CancellationToken cancellation)
    {
        string command = string.Create(CultureInfo.InvariantCulture, $"XMITOFF {port}");
        var (reply, _) = await CommandAsync(command, null, cancellation).ConfigureAwait(false);
        var match = Value().Match(reply);
        return match.Success
            ? match.Groups[1].Value != "0"
            : throw new BpqNodeException(KnownRefusal(reply) ?? $"LinBPQ answered \"{command}\" with {Quoted(reply)}");
    }

    /// <summary>The ID LinBPQ's <c>PORTS</c> gives <paramref name="port"/>, or null when it lists no such port.</summary>
    /// <exception cref="BpqNodeException">LinBPQ could not be reached or understood.</exception>
    public async Task<string?> PortIdAsync(int port, CancellationToken cancellation)
    {
        // PORTS has no end marker, so a second command follows it: its reply ends the list.
        string sentinel = string.Create(CultureInfo.InvariantCulture, $"XMITOFF {port}");
        var (reply, _) = await CommandAsync("PORTS", sentinel, cancellation).ConfigureAwait(false);
        foreach (string line in reply.Split(['\r', '\n']))
        {
            var match = PortLine().Match(line);
            if (match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == port)
            {
                // As LinBPQ has it, for comparing; the log shows it through Quoted.
                return OneLine(match.Groups[2].Value);
            }
        }
        return null;
    }

    private string? KnownRefusal(string reply) =>
        reply.Contains("Invalid Port", StringComparison.OrdinalIgnoreCase)
            ? $"LinBPQ has no port {_settings.HfPort}: set \"bpq\".\"hfPort\" to the number of the port on the shared radio, as LinBPQ's PORTS command lists it"
        : reply.Contains("SYSOP", StringComparison.OrdinalIgnoreCase)
            ? $"LinBPQ did not give {_settings.User} sysop status: its USER= line needs SYSOP as the fifth field"
        : null;

    /// <summary>
    /// Sends one node command (and <paramref name="sentinel"/> after it, whose reply ends the
    /// first's) and returns the text of LinBPQ's reply after its <c>NODE}</c>, with whether an
    /// earlier try had been sent on a session that was then lost.
    /// </summary>
    private async Task<(string Reply, bool LostAfterSending)> CommandAsync(string command, string? sentinel, CancellationToken cancellation)
    {
        await _one.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            bool lostAfterSending = false;
            for (int attempt = 0; ; attempt++)
            {
                Session? reuse;
                lock (_gate)
                {
                    reuse = _session is { LostWhy: null } open ? open : null;
                }
                bool fresh = reuse is null;
                Session session;
                if (reuse is null)
                {
                    Drop();
                    try
                    {
                        session = await LogInAsync(cancellation).ConfigureAwait(false);
                    }
                    catch (BpqNodeException e)
                    {
                        throw new BpqNodeException(e.Message, lostAfterSending);
                    }
                }
                else
                {
                    session = reuse;
                }
                bool sent = false;
                try
                {
                    session.Clear();
                    await session.SendAsync(command, cancellation).ConfigureAwait(false);
                    if (sentinel is not null)
                    {
                        await session.SendAsync(sentinel, cancellation).ConfigureAwait(false);
                    }
                    sent = true;
                    int wanted = sentinel is null ? 1 : 2;
                    var replies = await session.ReadUntilAsync(() => session.Replies() is { Count: var n } r && n >= wanted ? r : null, $"its answer to \"{command}\"", cancellation).ConfigureAwait(false);
                    string reply = sentinel is null ? replies[0].Text : session.Between(replies[0], replies[1]);
                    if (!fresh && attempt == 0 && KnownRefusal(reply) is not null && reply.Contains("SYSOP", StringComparison.OrdinalIgnoreCase))
                    {
                        // A session LinBPQ has idled out keeps its socket and loses sysop status.
                        Drop();
                        continue;
                    }
                    return (reply, lostAfterSending);
                }
                catch (Exception e) when (e is BpqNodeException or IOException or SocketException or ObjectDisposedException)
                {
                    Drop();
                    lostAfterSending |= sent;
                    if (!fresh && attempt == 0)
                    {
                        // A session that had died while it sat idle: once more on a fresh login.
                        continue;
                    }
                    string why = e is BpqNodeException ? e.Message : $"lost LinBPQ's node at {_settings.Host}:{_settings.Port} ({Clean(e.Message)})";
                    throw new BpqNodeException(why, lostAfterSending);
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<Session> LogInAsync(CancellationToken cancellation)
    {
        var client = new TcpClient { NoDelay = true };
        Session? session = null;
        try
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                limit.CancelAfter(ReplyTimeout);
                try
                {
                    await client.ConnectAsync(_settings.Host, _settings.Port, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new BpqNodeException($"cannot reach LinBPQ's node at {_settings.Host}:{_settings.Port} (no answer within {ReplyTimeout.TotalSeconds:F0} s)");
                }
            }
            session = new Session(client, this);
            lock (_gate)
            {
                _session = session;
            }
            session.Start();

            string userPrompt = await session.ReadUntilAsync(session.Prompt, "its user prompt", cancellation).ConfigureAwait(false);
            session.Clear();
            await session.SendAsync(_settings.User, cancellation).ConfigureAwait(false);
            string passwordPrompt = await session.ReadUntilAsync(session.Prompt, "its password prompt", cancellation).ConfigureAwait(false);
            if (passwordPrompt.Equals(userPrompt, StringComparison.OrdinalIgnoreCase))
            {
                throw new BpqNodeException($"LinBPQ does not know the user {_settings.User} on its Telnet port (user names are case-sensitive)");
            }

            // LinBPQ answers the password line with a new line straight away, right or wrong, then
            // either the prompt again or the login's welcome: the Telnet port's CTEXT, or its own
            // "Connected to ..." when none is set. It throws away whatever came in with the password
            // line, so nothing is sent until the welcome has come and gone quiet.
            session.Clear();
            await session.SendAsync(_settings.Password, cancellation).ConfigureAwait(false);
            await session.ReadUntilAsync(() => session.DropThroughNewLine() ? "" : null, "an answer to the password", cancellation).ConfigureAwait(false);
            await session.SettleAsync(WelcomeWait, SettleQuiet, cancellation).ConfigureAwait(false);
            if (session.Prompt() is { } again && again.Equals(passwordPrompt, StringComparison.OrdinalIgnoreCase))
            {
                throw new BpqNodeException($"LinBPQ refused the password for {_settings.User}");
            }

            // A configured CTEXT ends in a bare CR, so the answer can share its line; and a
            // PASSWORD LinBPQ was not ready for is lost without a word, so it is sent once more.
            string welcome = session.Text();
            session.Clear();
            Box? box = null;
            bool resent = false;
            for (int sent = 1; box is null; sent++)
            {
                await session.SendAsync("PASSWORD", cancellation).ConfigureAwait(false);
                try
                {
                    box = await session.ReadUntilAsync(
                        () => session.Replies() is [var first, ..] ? new Box(first.Text)
                            : session.Prompt() is { } prompt && prompt.Equals(passwordPrompt, StringComparison.OrdinalIgnoreCase) ? new Box(null)
                            : null,
                        "an answer to PASSWORD", cancellation, ReplyTimeout / 2).ConfigureAwait(false);
                }
                catch (BpqNodeException) when (session.LostWhy is null)
                {
                    if (sent == 2)
                    {
                        string since = welcome + session.Text();
                        throw new BpqNodeException($"LinBPQ did not answer PASSWORD, sent twice {ReplyTimeout.TotalSeconds / 2:F0} s apart; after the login it sent {(OneLine(since).Length == 0 ? "nothing" : Quoted(since))}");
                    }
                    resent = true;
                }
            }
            if (resent)
            {
                // Both may have been taken: let a second answer come before the first command.
                await session.SettleAsync(TimeSpan.Zero, ResentQuiet, cancellation).ConfigureAwait(false);
            }
            string? answer = box.Text;
            if (answer is null)
            {
                throw new BpqNodeException($"LinBPQ refused the password for {_settings.User}");
            }
            if (!OkReply().IsMatch(answer))
            {
                throw new BpqNodeException(Digits().IsMatch(answer)
                    ? $"LinBPQ did not give {_settings.User} sysop status: its USER= line in the Telnet port needs SYSOP as the fifth field, as in USER={_settings.User},password,CALL,,SYSOP"
                    : $"LinBPQ answered PASSWORD with {Quoted(answer)} rather than Ok");
            }
            session.LogInDone();
            return session;
        }
        catch (Exception e)
        {
            Drop(session);
            client.Dispose();
            throw e switch
            {
                BpqNodeException refused => new BpqNodeException(refused.Message),
                IOException or SocketException or ObjectDisposedException => new BpqNodeException($"cannot log in to LinBPQ's node at {_settings.Host}:{_settings.Port} ({Clean(e.Message)})"),
                _ => e,
            };
        }
    }

    private sealed record Box(string? Text);

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "...";

    /// <summary>What stands for LinBPQ's text when it is not shown.</summary>
    public const string ReplyNotShown = "(reply not shown)";

    /// <summary>
    /// The shortest password with which LinBPQ's text is quoted at all, as for the BBS: with a
    /// shorter one, too much of what LinBPQ sends could happen to match it.
    /// </summary>
    internal const int ShortestQuotingPassword = 8;

    /// <summary>
    /// Text from LinBPQ for the log, in quotes, one line of ASCII; or <see cref="ReplyNotShown"/>
    /// when it holds the password, or the password is shorter than <see cref="ShortestQuotingPassword"/>.
    /// Nothing about the password is ever given away, not even where in the text it was.
    /// </summary>
    public string Quoted(string text)
    {
        string line = OneLine(text);
        return _settings.Password.Length is > 0 and < ShortestQuotingPassword || Holds(text) || Holds(line)
            ? ReplyNotShown
            : $"\"{Shorten(line)}\"";
    }

    /// <summary>Other text for the log (a socket's error, say): one line of ASCII, or not shown at all if it holds the password.</summary>
    private string Clean(string text)
    {
        string line = OneLine(text);
        return Holds(text) || Holds(line) ? "(not shown)" : line;
    }

    private bool Holds(string text) => _settings.Password.Length > 0 && text.Contains(_settings.Password, StringComparison.Ordinal);

    private static string OneLine(string text) => Ascii.Clean(text.Replace('\r', ' ').Replace('\n', ' ').Trim());

    /// <summary>Closes the session, quietly; the next command logs in again.</summary>
    public void Drop()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        session?.Close();
    }

    private void Drop(Session? session)
    {
        if (session is null)
        {
            return;
        }
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }
        }
        session.Close();
    }

    private void Lost(Session session, string why)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
            {
                return;
            }
        }
        SessionLost?.Invoke(Clean(why));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null)
        {
            await session.ByeAsync().ConfigureAwait(false);
        }
        _one.Dispose();
    }

    /// <summary>One TCP connection to the node, with a read always pending on it.</summary>
    private sealed class Session(TcpClient client, BpqNode node)
    {
        private readonly NetworkStream _stream = client.GetStream();
        private readonly object _gate = new();
        private string _text = "";
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _closed;

        public bool LoggedIn { get; private set; }

        public string? LostWhy { get; private set; }

        public void Start() => _ = ReadAsync();

        public void LogInDone()
        {
            lock (_gate)
            {
                LoggedIn = true;
            }
        }

        private async Task ReadAsync()
        {
            var buffer = new byte[2048];
            try
            {
                while (true)
                {
                    int read = await _stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read == 0)
                    {
                        Lose("LinBPQ closed the connection");
                        return;
                    }
                    string text = Printable(buffer.AsSpan(0, read));
                    bool ended;
                    lock (_gate)
                    {
                        // The end of an earlier session that LinBPQ hands the next connection is
                        // passed over until the login is done; after it, it is this session's end.
                        _text = LoggedIn ? _text + text : Ended().Replace(_text + text, "");
                        ended = LoggedIn && Ended().IsMatch(_text);
                        if (_text.Length > 16 * 1024)
                        {
                            _text = _text[^4096..];
                        }
                        _changed.TrySetResult();
                    }
                    if (ended)
                    {
                        Lose("LinBPQ ended the node session (its IDLETIME, or a sysop disconnect), which takes sysop status with it");
                        return;
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                Lose($"the connection to LinBPQ failed ({e.Message})");
            }
        }

        private void Lose(string why)
        {
            bool tell;
            lock (_gate)
            {
                tell = LostWhy is null && !_closed && LoggedIn;
                LostWhy ??= why;
                _changed.TrySetResult();
            }
            if (tell)
            {
                node.Lost(this, why);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _text = "";
            }
        }

        /// <summary>The text received since it was last cleared.</summary>
        public string Text()
        {
            lock (_gate)
            {
                return _text;
            }
        }

        /// <summary>
        /// Waits, never longer than <see cref="ReplyTimeout"/>, until something has come (giving up
        /// on that after <paramref name="start"/>) and then nothing more for <paramref name="quiet"/>.
        /// </summary>
        public async Task SettleAsync(TimeSpan start, TimeSpan quiet, CancellationToken cancellation)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(ReplyTimeout);
            var startBy = DateTime.UtcNow + start;
            try
            {
                while (true)
                {
                    Task changed;
                    bool any;
                    lock (_gate)
                    {
                        if (_changed.Task.IsCompleted)
                        {
                            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                        changed = _changed.Task;
                        any = _text.Length > 0;
                    }
                    if (LostWhy is not null)
                    {
                        return;
                    }
                    var wait = any ? quiet : startBy - DateTime.UtcNow;
                    if (wait <= TimeSpan.Zero)
                    {
                        return;
                    }
                    if (await Task.WhenAny(changed, Task.Delay(wait, limit.Token)).ConfigureAwait(false) != changed)
                    {
                        if (any)
                        {
                            return;
                        }
                        startBy = DateTime.MinValue;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
            }
        }

        /// <summary>Drops the text up to and including its first new line; false while there is none.</summary>
        public bool DropThroughNewLine()
        {
            lock (_gate)
            {
                int at = _text.IndexOf('\n', StringComparison.Ordinal);
                if (at < 0)
                {
                    return false;
                }
                _text = _text[(at + 1)..];
                return true;
            }
        }

        /// <summary>The prompt the text ends with (no new line after it, ending in a colon), or null.</summary>
        public string? Prompt()
        {
            lock (_gate)
            {
                if (_text.EndsWith('\n') || _text.EndsWith('\r'))
                {
                    return null;
                }
                string tail = _text.TrimEnd();
                string last = tail[(tail.LastIndexOfAny(['\r', '\n']) + 1)..].Trim();
                return last.EndsWith(':') ? last : null;
            }
        }

        /// <summary>A node reply: its line number in the text and the text after <c>NODE}</c>.</summary>
        public sealed record Reply(int Line, string Text);

        /// <summary>The complete node reply lines in the text, in order.</summary>
        public List<Reply> Replies()
        {
            lock (_gate)
            {
                string[] lines = Lines();
                var replies = new List<Reply>();
                for (int i = 0; i < lines.Length - 1; i++)
                {
                    var match = ReplyLine().Match(lines[i]);
                    if (match.Success)
                    {
                        replies.Add(new Reply(i, match.Groups[1].Value.Trim()));
                    }
                }
                return replies;
            }
        }

        /// <summary>A reply's text with the lines after it, up to the next reply.</summary>
        public string Between(Reply first, Reply next)
        {
            lock (_gate)
            {
                string[] lines = Lines();
                return string.Join('\n', [first.Text, .. lines[(first.Line + 1)..next.Line]]);
            }
        }

        /// <summary>
        /// The text's lines, the last one unfinished. CR LF, a bare LF and a bare CR each end a
        /// line: LinBPQ ends a configured CTEXT with a bare CR, and the next line follows it.
        /// </summary>
        private string[] Lines() => LineEnd().Split(_text);

        public async Task<T> ReadUntilAsync<T>(Func<T?> found, string what, CancellationToken cancellation, TimeSpan? timeout = null)
            where T : class
        {
            TimeSpan within = timeout ?? ReplyTimeout;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(within);
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_changed.Task.IsCompleted)
                    {
                        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    changed = _changed.Task;
                }
                if (found() is { } result)
                {
                    return result;
                }
                if (LostWhy is { } why)
                {
                    throw new BpqNodeException($"{why} before sending {what}");
                }
                try
                {
                    await changed.WaitAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new BpqNodeException($"LinBPQ did not send {what} within {within.TotalSeconds:F0} s");
                }
            }
        }

        public async Task SendAsync(string line, CancellationToken cancellation)
        {
            if (LostWhy is { } why)
            {
                throw new BpqNodeException(why);
            }
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r"), cancellation).ConfigureAwait(false);
        }

        public void Close()
        {
            _closed = true;
            client.Dispose();
        }

        /// <summary>Leaves the node politely, so LinBPQ's goodbye is not handed to the next connection, then closes.</summary>
        public async Task ByeAsync()
        {
            _closed = true;
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _stream.WriteAsync("BYE\r"u8.ToArray(), limit.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
            client.Dispose();
        }

        /// <summary>Text as ASCII, with telnet negotiation and other control bytes left out but for CR and LF.</summary>
        private static string Printable(ReadOnlySpan<byte> bytes)
        {
            var text = new StringBuilder(bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (b == 0xFF && i + 1 < bytes.Length)
                {
                    // IAC: two bytes, or three for WILL, WONT, DO and DONT.
                    i += bytes[i + 1] is >= 0xFB and <= 0xFE ? 2 : 1;
                    continue;
                }
                if (b is (byte)'\r' or (byte)'\n' or (>= 0x20 and < 0x7F))
                {
                    text.Append((char)b);
                }
            }
            return text.ToString();
        }
    }

    [GeneratedRegex("\r\n|\r|\n")]
    private static partial Regex LineEnd();

    /// <summary>A node reply line: <c>TST:GB7TST} text</c>.</summary>
    [GeneratedRegex(@"^\S*\}\s(.*)$")]
    private static partial Regex ReplyLine();

    [GeneratedRegex(@"^Ok\b", RegexOptions.IgnoreCase)]
    private static partial Regex OkReply();

    [GeneratedRegex(@"^\d+(\s+\d+){4}\s*$")]
    private static partial Regex Digits();

    [GeneratedRegex(@"XMITOFF\s+was\s+(\d+)\s+now\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WasNow();

    [GeneratedRegex(@"XMITOFF\s+(\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Value();

    /// <summary>A line of PORTS: the number, then the port's ID.</summary>
    [GeneratedRegex(@"^\s*(\d+)\s+(\S.*?)\s*$")]
    private static partial Regex PortLine();

    /// <summary>The lines LinBPQ sends when a node session ends.</summary>
    [GeneratedRegex(@"(\*\*\* Disconnected from Stream \d+|Disconnected from Node - Telnet Session kept)\r?\n?")]
    private static partial Regex Ended();
}
