using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Packet.Fbb;
using Packet.Mailcast;

namespace Mailcast.Receiver.Delivery;

/// <summary>What the BBS said about one bulletin.</summary>
public enum DeliveryVerdict
{
    /// <summary>The BBS asked for it (FS +), took the transfer, and the session closed cleanly.</summary>
    Accepted,

    /// <summary>The BBS already had the BID (FS -). Final: it is not offered again.</summary>
    AlreadyHad,

    /// <summary>The BBS asked for it later (FS =). It stays in the outbox.</summary>
    Deferred,

    /// <summary>
    /// The BBS refused it (FS R or E), or it cannot be offered at all (a BID longer than FBB's
    /// 12 characters). Final: it is not offered again.
    /// </summary>
    Refused,

    /// <summary>
    /// The BBS asked for it and the transfer went, but the session did not close cleanly, so it
    /// is not certain the BBS kept it. It stays in the outbox; offered again, the BBS answers
    /// FS - if it did keep it.
    /// </summary>
    Unconfirmed,

    /// <summary>The session ended before the BBS answered for it.</summary>
    NotOffered,
}

/// <summary>The BBS's answer for one bulletin.</summary>
/// <param name="Bid">The bulletin's BID.</param>
/// <param name="Verdict">What it said.</param>
/// <param name="Detail">Why, where there is more to say.</param>
public sealed record DeliveryOutcome(string Bid, DeliveryVerdict Verdict, string? Detail = null);

/// <summary>How one forwarding session went.</summary>
/// <param name="Graceful">The session reached FBB's FF/FQ close.</param>
/// <param name="Failure">Why it did not, for the log; null when it did.</param>
/// <param name="Outcomes">One entry per bulletin given to the session, in the order given.</param>
/// <param name="ReverseOffered">Messages the BBS tried to send the receiver, all answered FS =.</param>
public sealed record SessionReport(bool Graceful, string? Failure, IReadOnlyList<DeliveryOutcome> Outcomes, int ReverseOffered);

/// <summary>What testing a BBS login found.</summary>
public enum BbsLoginTestOutcome
{
    /// <summary>Connected, logged in, and reached the forwarding prompt, as a partner would.</summary>
    Ok,

    /// <summary>
    /// The BBS asked for the password again after it was sent (LinBPQ's "password:"), or said in
    /// so many words that it was wrong (FBB's "Password error").
    /// </summary>
    WrongPassword,

    /// <summary>
    /// The BBS asked for the login again (LinBPQ's "user:"), or said it does not know the
    /// callsign (FBB). LinBPQ matches a login against its USER= lines case-sensitively.
    /// </summary>
    LoginNotRecognised,

    /// <summary>
    /// The port is LinBPQ's ordinary telnet port (TCPPORT), which negotiates telnet options and
    /// prompts for the login and password one at a time, not its FBBPORT.
    /// </summary>
    NotFbbPort,

    /// <summary>The login worked, but it is not set up as a BBS forwarding partner.</summary>
    NotForwardingPartner,

    /// <summary>
    /// The login worked, but LinBPQ is asking the login to register a Name, because its BBS user
    /// has none. LinBPQ's own new-user prompt ("Please enter your Name"), hardcoded and sent in
    /// place of the forwarding prompt; see <see cref="BbsClient.Classify"/>.
    /// </summary>
    NeedsName,

    /// <summary>The BBS could not be reached: it refused the connection, or never answered.</summary>
    Unreachable,

    /// <summary>Something else happened; <see cref="BbsLoginTestResult.Detail"/> has the BBS's first line.</summary>
    UnexpectedReply,
}

/// <summary>
/// What testing a BBS login found. <see cref="Said"/> is a short, friendly sentence for the
/// settings page; <see cref="Detail"/> is the BBS's own words: its first line of reply for
/// <see cref="BbsLoginTestOutcome.UnexpectedReply"/>, and its whole reply (see
/// <see cref="BbsClient.Shown"/>) for a refused login. <see cref="Sent"/> says what the receiver
/// sent: the login, the command, and that a password went, never anything about the password.
/// </summary>
public sealed record BbsLoginTestResult(BbsLoginTestOutcome Outcome, string Said, string? Detail = null, string? Sent = null);

/// <summary>
/// One FBB B1F forwarding session into the local BBS, as its calling partner: connect, log in,
/// propose every bulletin given, transfer those the BBS asks for, and close.
/// </summary>
/// <remarks>
/// <para>The protocol is <see cref="FbbSession"/>, the state machine from the Packet.Fbb package (packet-net/pdn-fbb), shared with pdn-bbs.
/// This class is the transport around it, as pdn-bbs's FbbSessionRunner is: lines go out with
/// CR LF, transfers go out raw, and everything received is fed back in.</para>
/// <para>The receiver only ever sends. If the BBS has messages queued for the receiver's login
/// and offers them when the turn passes to it, each is answered FS = (later), never FS - (already
/// have), so a BBS that has been set up to route mail to this login does not mark that mail as
/// delivered. The login should have no forwarding routes, and the receiver logs a warning when
/// this happens.</para>
/// </remarks>
public sealed partial class BbsClient : IBbsSession
{
    /// <summary>The longest a BID can be in an FBB proposal.</summary>
    public const int MaxBidLength = 12;

    private readonly BbsSettings _settings;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;

    /// <summary>Creates a client for the BBS in <paramref name="settings"/>.</summary>
    public BbsClient(BbsSettings settings, TimeProvider time, Action<string> log)
    {
        _settings = settings;
        _time = time;
        _log = log;
    }

    /// <summary>How long to wait for the connection to open.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the BBS may say nothing before the session is given up.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The longest a whole session may take, however busy the BBS keeps it.</summary>
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The software version in the receiver's SID.</summary>
    public string Version { get; init; } = "0.1.0";

    /// <summary>How long a login test may take in all: connecting, logging in and reading the reply.</summary>
    public TimeSpan TestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Tests the configured login: connects, logs in and enters the BBS command (for LinBPQ) or
    /// answers FBB's prompts, far enough to see whether it reaches the forwarding prompt as a
    /// partner would. Exchanges no mail, proposes nothing, and never starts the FBB session
    /// proper; it just closes the connection once it knows, which is as polite as a connection
    /// that commits to nothing needs to be.
    /// </summary>
    public async Task<BbsLoginTestResult> TestLoginAsync(CancellationToken cancellation)
    {
        var result = await TestLoginOnceAsync(cancellation).ConfigureAwait(false);
        _log($"bbs: login test: {Ascii.Clean(result.Said)}");
        return result;
    }

    private async Task<BbsLoginTestResult> TestLoginOnceAsync(CancellationToken cancellation)
    {
        using var whole = new Deadline(TestTimeout, _time, cancellation);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_settings.Host, _settings.Port, whole.Token).ConfigureAwait(false);
        }
        catch (SocketException e)
        {
            return new BbsLoginTestResult(BbsLoginTestOutcome.Unreachable,
                $"Can't reach {_settings.Host}:{_settings.Port}: {Ascii.Clean(e.Message)}.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new BbsLoginTestResult(BbsLoginTestOutcome.Unreachable,
                $"{_settings.Host}:{_settings.Port} did not answer within {TestTimeout.TotalSeconds:F0} s.");
        }
        client.NoDelay = true;
        using var stream = client.GetStream();
        try
        {
            return _settings.Type == BbsKind.LinBpq
                ? await TestLinBpqAsync(stream, whole.Token).ConfigureAwait(false)
                : await TestFbbAsync(stream, whole.Token).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            return new BbsLoginTestResult(BbsLoginTestOutcome.Unreachable, $"The connection broke: {Ascii.Clean(e.Message)}.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new BbsLoginTestResult(BbsLoginTestOutcome.UnexpectedReply, "The BBS did not answer in time.");
        }
    }

    /// <summary>LinBPQ's FBBPORT sends no prompts: send all three lines, then read the reply.</summary>
    private async Task<BbsLoginTestResult> TestLinBpqAsync(NetworkStream stream, CancellationToken cancellation)
    {
        await stream.WriteAsync(Encoding.Latin1.GetBytes(_settings.Login + "\r" + _settings.Password + "\r"
            + (_settings.Command.Length > 0 ? _settings.Command + "\r" : "")), cancellation).ConfigureAwait(false);
        LogSent("login test");
        var (text, closed) = await ReadTestReplyAsync(stream, Settled, cancellation).ConfigureAwait(false);
        LogHeard("login test", text, closed);
        return Classify(text, closed);
    }

    /// <summary>FBB prompts for both; a leading dot asks for a binary session.</summary>
    private async Task<BbsLoginTestResult> TestFbbAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var (first, closed1) = await ReadTestReplyAsync(stream,
            t => t.Contains("allsign", StringComparison.OrdinalIgnoreCase) || Settled(t), cancellation).ConfigureAwait(false);
        if (closed1 || !first.Contains("allsign", StringComparison.OrdinalIgnoreCase))
        {
            LogHeard("login test", first, closed1);
            return Classify(first, closed1);
        }
        await stream.WriteAsync(Encoding.Latin1.GetBytes("." + _settings.Login + "\r"), cancellation).ConfigureAwait(false);
        var (second, closed2) = await ReadTestReplyAsync(stream,
            t => t.Contains("assword", StringComparison.OrdinalIgnoreCase) || Settled(t), cancellation).ConfigureAwait(false);
        if (closed2 || !second.Contains("assword", StringComparison.OrdinalIgnoreCase))
        {
            LogHeard("login test", first + second, closed2);
            return Classify(second, closed2);
        }
        await stream.WriteAsync(Encoding.Latin1.GetBytes(_settings.Password + "\r"), cancellation).ConfigureAwait(false);
        LogSent("login test");
        var (text, closed) = await ReadTestReplyAsync(stream, Settled, cancellation).ConfigureAwait(false);
        LogHeard("login test", first + second + text, closed);
        return Classify(text, closed);
    }

    /// <summary>Reads until <paramref name="stop"/> says the reply is settled, or the connection closes or the deadline passes.</summary>
    private static async Task<(string Text, bool Closed)> ReadTestReplyAsync(NetworkStream stream, Func<string, bool> stop, CancellationToken cancellation)
    {
        var seen = new List<byte>();
        var buffer = new byte[1024];
        while (true)
        {
            string text = Encoding.Latin1.GetString([.. seen]);
            if (stop(text))
            {
                return (text, false);
            }
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (text, false);
            }
            if (read == 0)
            {
                return (text, true);
            }
            seen.AddRange(buffer.AsSpan(0, read).ToArray());
        }
    }

    /// <summary>The phrases FBB refuses a callsign with.</summary>
    private static readonly string[] FbbCallsignRefusals = ["Invalid callsign", "Unregistered callsign", "Callsign error"];

    /// <summary>The phrases FBB refuses a password with.</summary>
    private static readonly string[] FbbPasswordRefusals = ["Password error", "Bad password"];

    /// <summary>
    /// Why the BBS refused the login, from its reply, as a clause for the settings page and the
    /// log, or null if it did not. LinBPQ's FBBPORT sends nothing until it has the login, then
    /// re-sends its prompt for whichever it did not take (TelnetV6.c): "user:" for a login no
    /// USER= line on the port has (compared with strcmp, so case matters: a USER=m7taw line does
    /// not take M7TAW), "password:" for a known login with the wrong password. Checked against a
    /// real LinBPQ 6.0.25.41 in docker (issue #134). Its ordinary telnet port (TCPPORT) instead
    /// opens with telnet option negotiation (IAC, 0xFF) and "user:" before anything is sent, and
    /// takes one line at a time, so sending all three at once there always reads as "user:" then
    /// "password:", whether the password is right or not: that is the wrong port, not a refusal.
    /// FBB says one of <see cref="FbbCallsignRefusals"/> or <see cref="FbbPasswordRefusals"/>.
    /// </summary>
    /// <remarks>
    /// LinBPQ's prompts can be changed (LOGINPROMPT and PASSWORDPROMPT in the Telnet port's
    /// CONFIG); with others, a refusal reads as an unexpected reply, with the BBS's own words.
    /// </remarks>
    private (BbsLoginTestOutcome Outcome, string Clause)? Refusal(string text)
    {
        string login = Ascii.Clean(_settings.Login);
        if (_settings.Type == BbsKind.LinBpq)
        {
            if (text.Contains('\xFF', StringComparison.Ordinal))
            {
                return (BbsLoginTestOutcome.NotFbbPort,
                    $"{_settings.Host}:{_settings.Port} is LinBPQ's ordinary telnet port (TCPPORT), which asks for the login and the password one at a time; "
                    + "the receiver needs the port set as FBBPORT in the same Telnet port's CONFIG");
            }
            if (text.Contains("user:", StringComparison.OrdinalIgnoreCase))
            {
                return (BbsLoginTestOutcome.LoginNotRecognised,
                    $"LinBPQ asked for the login again, so no USER= line on this port has the login \"{login}\"; "
                    + "LinBPQ logins are case-sensitive, so it must match the USER= line exactly");
            }
            if (text.Contains("password:", StringComparison.OrdinalIgnoreCase))
            {
                return (BbsLoginTestOutcome.WrongPassword,
                    $"LinBPQ asked for the password again, so it did not take the password for \"{login}\"; check it against the USER= line for \"{login}\"");
            }
            return null;
        }
        if (FbbPasswordRefusals.Any(r => text.Contains(r, StringComparison.OrdinalIgnoreCase)))
        {
            return (BbsLoginTestOutcome.WrongPassword,
                $"FBB did not take the password for \"{login}\"; check it is {login}'s password on FBB, which keeps only its first 12 characters");
        }
        if (FbbCallsignRefusals.Any(r => text.Contains(r, StringComparison.OrdinalIgnoreCase)))
        {
            return (BbsLoginTestOutcome.LoginNotRecognised,
                $"FBB does not know the login \"{login}\"; add it as a user with the BBS flag on FBB's console");
        }
        return null;
    }

    /// <summary>The reply has said enough to classify: a refusal, an unknown command, or a prompt at the end.</summary>
    private bool Settled(string text) =>
        Refusal(text) is not null || text.Contains("Invalid command", StringComparison.OrdinalIgnoreCase)
        || text.TrimEnd().EndsWith('>') || AsksForFirstName(text);

    /// <summary>
    /// What the receiver sends to log in, in words, for the log and the settings page: the login
    /// and the command, and only that a password was sent. Nothing about the password itself, not
    /// even its length, ever goes into a log, the page or the API. Every line goes with a bare CR,
    /// as Latin-1, on every platform, Windows included.
    /// </summary>
    internal string SentDescription() => _settings.Type == BbsKind.LinBpq
        ? $"the login \"{Ascii.Clean(_settings.Login)}\", a password"
            + (_settings.Command.Length > 0 ? $" and the command \"{Ascii.Clean(_settings.Command)}\"" : " and no command")
            + ", each line ending in CR, all at once"
        : $"the login \".{Ascii.Clean(_settings.Login)}\" at FBB's callsign prompt and a password at its password prompt, each line ending in CR";

    /// <summary>Logs what was sent to log in, as <see cref="SentDescription"/> gives it.</summary>
    private void LogSent(string what) =>
        _log($"bbs: {what}: sent {(_settings.Type == BbsKind.LinBpq ? "LinBPQ" : "FBB")} at {Ascii.Clean(_settings.Host)}:{_settings.Port} {SentDescription()}");

    /// <summary>Logs what the BBS answered a login with, as <see cref="Shown"/> gives it.</summary>
    private void LogHeard(string what, string text, bool closed) =>
        _log($"bbs: {what}: the BBS answered {(text.Length == 0 ? "nothing" : $"{text.Length} bytes, \"{Shown(text)}\"")}{(closed ? ", then closed the connection" : "")}");

    /// <summary>
    /// The BBS's whole reply, as one plain ASCII line for the log and the settings page: the
    /// password tested redacted, CR and LF shown as &lt;CR&gt; and &lt;LF&gt;, and any other octet
    /// outside printable ASCII in hex, such as &lt;FF&gt; for telnet's IAC; cut short past 300
    /// characters.
    /// </summary>
    internal string Shown(string text)
    {
        const int Longest = 300;
        var shown = new StringBuilder();
        foreach (char c in Redact(text))
        {
            if (shown.Length >= Longest)
            {
                shown.Append("...");
                break;
            }
            shown.Append(c switch
            {
                '\r' => "<CR>",
                '\n' => "<LF>",
                '"' => "'",
                >= ' ' and <= '~' => c.ToString(),
                _ => $"<{(int)c:X2}>",
            });
        }
        return shown.ToString();
    }

    /// <summary>
    /// What a login test's reply means. <see cref="PromptCall"/> matching means the BBS sent its
    /// own forwarding prompt ("de CALL>"), exactly as LinBPQ does to a real forwarding partner.
    /// FBB never sends "de CALL": a real Linux FBB 7.0.11 (issue #108, captured in docker) answers
    /// a correctly set-up partner with its SID and a bare "(n) CALLSIGN BBS>" prompt, nothing
    /// else. A login FBB has never seen connect before goes through its own new-user registration
    /// ("Please enter your first name :") unless it has the BBS flag, which skips straight to the
    /// bare prompt above, so that prompt reliably means "not a forwarding partner".
    /// </summary>
    /// <remarks>
    /// A bare prompt from FBB is not reliable the other way: once a login has registered once
    /// (first name, city, home BBS, zip code) and reconnects, it reaches a bare "(n) CALLSIGN BBS
    /// (H for help) >" too, BBS flag or not. Tried telling the two apart with the same harmless
    /// SID-and-FF handshake a real delivery session opens with when it has nothing to send
    /// (<see cref="Run.FeedAsync"/>): against a real FBB 7.0.11 in docker, both a correctly set-up
    /// partner and an already-registered non-partner answered FQ and closed cleanly, identically,
    /// so the handshake does not tell them apart either (see BbsLoginTestFbbTests.cs and the PR
    /// that added this remark). So for FBB a bare prompt is only ever Ok, never
    /// NotForwardingPartner, and its wording says plainly that this is as far as the login test
    /// can tell.
    /// </remarks>
    private BbsLoginTestResult Classify(string text, bool closed)
    {
        if (Refusal(text) is { } refusal)
        {
            return new BbsLoginTestResult(refusal.Outcome, refusal.Clause + ".", Shown(text), SentDescription());
        }
        if (text.Contains("Invalid command", StringComparison.OrdinalIgnoreCase))
        {
            return new BbsLoginTestResult(BbsLoginTestOutcome.UnexpectedReply,
                $"The BBS did not know the command \"{_settings.Command}\".", FirstLine(text));
        }
        if (PromptCall().IsMatch(text))
        {
            return new BbsLoginTestResult(BbsLoginTestOutcome.Ok, "OK, logged in as a forwarding partner.");
        }
        if (AsksForName(text))
        {
            return new BbsLoginTestResult(BbsLoginTestOutcome.NeedsName, NeedsNameExplanation(_settings.Login));
        }
        if (_settings.Type == BbsKind.Fbb)
        {
            if (AsksForFirstName(text))
            {
                return new BbsLoginTestResult(BbsLoginTestOutcome.NotForwardingPartner,
                    FbbNotSetUpExplanation(_settings.Login));
            }
            if (text.TrimEnd().EndsWith('>'))
            {
                return new BbsLoginTestResult(BbsLoginTestOutcome.Ok,
                    "Logged in; FBB can't confirm the BBS flag at login, so check it's set.");
            }
        }
        else if (text.TrimEnd().EndsWith('>'))
        {
            return new BbsLoginTestResult(BbsLoginTestOutcome.NotForwardingPartner,
                $"\"{_settings.Login}\" logged in, but it is not set up as a BBS forwarding partner.");
        }
        string? line = FirstLine(text);
        return new BbsLoginTestResult(BbsLoginTestOutcome.UnexpectedReply,
            line is null
                ? (closed ? "The BBS closed the connection without answering." : "The BBS did not answer in time.")
                : "The BBS said something unexpected.",
            line, SentDescription());
    }

    /// <summary>
    /// The first line of <paramref name="text"/>, cleaned of anything not printable and with the
    /// password tested redacted, or null for nothing. The password is taken out before this ever
    /// reaches a log or a reply: whether a BBS ever echoes back what it was sent is not something
    /// to rely on, FBB's included.
    /// </summary>
    private string? FirstLine(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        int end = trimmed.IndexOfAny(['\r', '\n']);
        string line = Ascii.Clean(Redact(end < 0 ? trimmed : trimmed[..end])).Trim();
        return line.Length == 0 ? null : line;
    }

    /// <summary>Replaces every occurrence of the password tested with asterisks.</summary>
    private string Redact(string text) =>
        _settings.Password.Length == 0 ? text : text.Replace(_settings.Password, "***", StringComparison.Ordinal);

    /// <summary>
    /// True if <paramref name="text"/> is LinBPQ asking a login with no Name to register one,
    /// rather than reaching the forwarding prompt. LinBPQ's new-user prompt is "Please enter
    /// your Name" (MailDataDefs.c's NewUserPrompt), a fixed C string never read from its config,
    /// unlike the regular and new-user BBS prompts (also confusingly called "NewUserPrompt" in
    /// LinBPQ's config, but a different, configurable string sent only after a Name is set), so
    /// matching the wording, case-insensitively, is as robust as this gets.
    /// </summary>
    private static bool AsksForName(string text) =>
        text.Contains("enter your name", StringComparison.OrdinalIgnoreCase);

    /// <summary>The plain-English explanation for <see cref="AsksForName"/>, used both for the login test and a real delivery session.</summary>
    private static string NeedsNameExplanation(string login) =>
        $"\"{login}\" logged in, but LinBPQ is asking for a name: give this user a Name in LinBPQ's BBS user settings (Web Mgmt, BBS Users), then test again.";

    /// <summary>
    /// True if <paramref name="text"/> is FBB's own new-user prompt, "Please enter your first
    /// name :" (issue #108, captured from a real FBB 7.0.11), asking a login FBB has never seen
    /// connect before to register. A login with the BBS flag skips this and reaches the bare
    /// forwarding prompt directly, so seeing this reliably means the login is not set up as a BBS
    /// forwarding partner (see <see cref="Classify"/>'s remarks on why a bare prompt cannot be
    /// read the other way around). Giving the login a name on its own would not change that,
    /// unlike LinBPQ's name prompt (#84): only the BBS flag does.
    /// </summary>
    private static bool AsksForFirstName(string text) =>
        text.Contains("enter your first name", StringComparison.OrdinalIgnoreCase);

    /// <summary>The plain-English explanation for <see cref="AsksForFirstName"/>, used both for the login test and a real delivery session.</summary>
    private static string FbbNotSetUpExplanation(string login) =>
        $"\"{login}\" logged in, but FBB is treating it as a new user: give it the BBS flag (\"EU {login}\", then \"B\", on FBB's console), then test again.";

    /// <summary>Offers <paramref name="bulletins"/> to the BBS in one session.</summary>
    public async Task<SessionReport> DeliverAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation)
    {
        var outcomes = new Dictionary<string, DeliveryOutcome>(StringComparer.OrdinalIgnoreCase);
        var offerable = new List<Bulletin>();
        foreach (var bulletin in bulletins)
        {
            if (bulletin.Bid.Length > MaxBidLength)
            {
                outcomes[bulletin.Bid] = new DeliveryOutcome(bulletin.Bid, DeliveryVerdict.Refused,
                    $"its BID is longer than FBB's {MaxBidLength} characters");
            }
            else
            {
                offerable.Add(bulletin);
            }
        }

        var run = new Run(this, offerable);
        string? failure = null;
        if (offerable.Count > 0)
        {
            try
            {
                using var whole = new Deadline(SessionTimeout, _time, cancellation);
                try
                {
                    await run.ExecuteAsync(whole.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (whole.Token.IsCancellationRequested && !cancellation.IsCancellationRequested)
                {
                    throw new SessionFailedException($"the session was still going after {SessionTimeout.TotalMinutes:F0} min");
                }
            }
            catch (Exception e) when (e is SocketException or IOException or TimeoutException or SessionFailedException)
            {
                failure = e is SessionFailedException ? e.Message : $"{Ascii.Clean(e.Message)}";
            }
            catch (FbbProtocolException e)
            {
                failure = "protocol error: " + Ascii.Clean(e.Message);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                failure = "the BBS stopped answering";
            }
        }

        bool graceful = run.Graceful && failure is null;
        foreach (var bulletin in offerable)
        {
            outcomes[bulletin.Bid] = run.Answers.TryGetValue(bulletin.Bid, out var answer)
                ? answer.Kind switch
                {
                    FsAnswerKind.Accept => graceful
                        ? new DeliveryOutcome(bulletin.Bid, DeliveryVerdict.Accepted)
                        : new DeliveryOutcome(bulletin.Bid, DeliveryVerdict.Unconfirmed, "the session did not close cleanly after the transfer"),
                    FsAnswerKind.AlreadyHave => new DeliveryOutcome(bulletin.Bid, DeliveryVerdict.AlreadyHad),
                    FsAnswerKind.Defer => new DeliveryOutcome(bulletin.Bid, DeliveryVerdict.Deferred),
                    _ => new DeliveryOutcome(bulletin.Bid, DeliveryVerdict.Refused, $"the BBS answered {answer.Kind}"),
                }
                : new DeliveryOutcome(bulletin.Bid, DeliveryVerdict.NotOffered);
        }

        if (failure is null && !graceful && offerable.Count > 0)
        {
            failure = run.Failure ?? "the session ended early";
        }
        if (failure is not null && run.FailedAtLogin)
        {
            // Issue #134: what was sent and what came back, so a refused login can be told
            // apart from a wrong port or a login in the wrong case from the log alone.
            LogSent("delivery login");
            LogHeard("delivery login", Encoding.Latin1.GetString([.. run.HeardAtLogin]), run.ClosedAtLogin);
        }

        return new SessionReport(graceful, failure, [.. bulletins.Select(b => outcomes[b.Bid])], run.ReverseOffered);
    }

    /// <summary>
    /// Builds the message as a partner sends it: FA B from @at to BID, the routing lines and body
    /// as text. The daily report (see <see cref="Feedback.FeedbackService"/>) goes the same way, a
    /// bulletin to MCAST at GB7RDG's full address. A message of type P would go as a personal
    /// one, FA P from @at to MID.
    /// </summary>
    internal static FbbOutboundMessage ToOutbound(Bulletin bulletin, string fallbackAt) => new()
    {
        MessageType = bulletin.Type is 'P' or 'B' or 'T' ? bulletin.Type : 'B',
        From = FaProposal.NormalizeCallsign(bulletin.From),
        AtBbs = bulletin.At.Length > 0 ? (bulletin.At.Length > 40 ? bulletin.At[..40] : bulletin.At) : fallbackAt,
        To = FaProposal.NormalizeCallsign(bulletin.To),
        Bid = bulletin.Bid,
        Title = bulletin.Title.Replace('\0', ' '),
        Body = Bulletin.TextEncoding.GetBytes(bulletin.MessageText),
    };

    [GeneratedRegex(@"de ([A-Za-z0-9]+(?:-[0-9]+)?)\s*>")]
    private static partial Regex PromptCall();

    /// <summary>A cancellation token for "the caller cancelled, or this long passed on the receiver's clock".</summary>
    private sealed class Deadline : IDisposable
    {
        private readonly CancellationTokenSource _timer;
        private readonly CancellationTokenSource _linked;

        public Deadline(TimeSpan timeout, TimeProvider time, CancellationToken cancellation)
        {
            _timer = new CancellationTokenSource(timeout, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _timer.Token);
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            _linked.Dispose();
            _timer.Dispose();
        }
    }

    /// <summary>A session that went wrong in a way worth a sentence of its own.</summary>
    private sealed class SessionFailedException(string message) : Exception(message);

    /// <summary>One session's state.</summary>
    private sealed class Run(BbsClient owner, IReadOnlyList<Bulletin> bulletins)
    {
        private readonly List<byte> _beforeSession = [];
        private bool _connected;
        private FbbSession? _session;
        private Dictionary<FbbOutboundMessage, string> _bids = new(ReferenceEqualityComparer.Instance);
        private NetworkStream? _stream;
        private int _reverseRounds;

        public Dictionary<string, FsAnswer> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Graceful { get; private set; }

        public string? Failure { get; private set; }

        public int ReverseOffered { get; private set; }

        private bool Over { get; set; }

        /// <summary>Everything the BBS sent before the FBB session began, as received, for the log.</summary>
        public List<byte> HeardAtLogin { get; } = [];

        /// <summary>The BBS closed the connection before the FBB session began.</summary>
        public bool ClosedAtLogin { get; private set; }

        /// <summary>The connection opened, but the session ended before the FBB session began.</summary>
        public bool FailedAtLogin => _connected && _session is null;

        private void Heard(ReadOnlySpan<byte> data)
        {
            if (_session is null && HeardAtLogin.Count < 4096)
            {
                HeardAtLogin.AddRange(data[..Math.Min(data.Length, 4096 - HeardAtLogin.Count)].ToArray());
            }
        }

        public async Task ExecuteAsync(CancellationToken cancellation)
        {
            var settings = owner._settings;
            using var client = new TcpClient();
            using (var connect = Linked(owner.ConnectTimeout, cancellation))
            {
                try
                {
                    await client.ConnectAsync(settings.Host, settings.Port, connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new SessionFailedException($"no answer from {settings.Host}:{settings.Port} within {owner.ConnectTimeout.TotalSeconds:F0} s");
                }
            }
            client.NoDelay = true;
            _stream = client.GetStream();
            _connected = true;

            byte[] early = await LogInAsync(cancellation).ConfigureAwait(false);
            await FeedAsync(early, cancellation).ConfigureAwait(false);

            var buffer = new byte[4096];
            while (!Over)
            {
                int read;
                using (var idle = Linked(owner.IdleTimeout, cancellation))
                {
                    read = await _stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                Heard(buffer.AsSpan(0, read));
                if (read == 0)
                {
                    ClosedAtLogin = _session is null;
                    Failure ??= _session is null
                        ? "the BBS closed the connection before it offered a forwarding session; " + LoginHint()
                        : "the BBS closed the connection";
                    return;
                }
                await FeedAsync(buffer.AsMemory(0, read).ToArray(), cancellation).ConfigureAwait(false);
            }
        }

        private Deadline Linked(TimeSpan timeout, CancellationToken cancellation) => new(timeout, owner._time, cancellation);

        /// <summary>
        /// Logs in, and returns whatever arrived after the last prompt, which belongs to the session.
        /// </summary>
        private async Task<byte[]> LogInAsync(CancellationToken cancellation)
        {
            var settings = owner._settings;
            if (settings.Type == BbsKind.LinBpq)
            {
                // LinBPQ's FBBPORT sends no prompts: user, password, then the node command for the
                // mail application. Its answer is "Connected to BBS", the SID, the welcome and the
                // prompt, all of which the session reads.
                await SendAsync(settings.Login + "\r" + settings.Password + "\r"
                    + (settings.Command.Length > 0 ? settings.Command + "\r" : ""), cancellation).ConfigureAwait(false);
                return [];
            }

            // FBB prompts for both. A leading dot asks for a binary session (drv_tcp.c,
            // tcp_check_call), without which FBB would read 0xFF octets in a transfer as telnet.
            byte[] rest = await ExpectAsync("allsign", cancellation).ConfigureAwait(false);
            await SendAsync("." + settings.Login + "\r", cancellation).ConfigureAwait(false);
            rest = await ExpectAsync("assword", cancellation, rest).ConfigureAwait(false);
            await SendAsync(settings.Password + "\r", cancellation).ConfigureAwait(false);
            return rest;
        }

        private async Task<byte[]> ExpectAsync(string prompt, CancellationToken cancellation, byte[]? already = null)
        {
            var seen = new List<byte>(already ?? []);
            var buffer = new byte[1024];
            while (true)
            {
                string text = Encoding.Latin1.GetString([.. seen]);
                int at = text.IndexOf(prompt, StringComparison.OrdinalIgnoreCase);
                int colon = at < 0 ? -1 : text.IndexOf(':', at);
                if (colon >= 0)
                {
                    return [.. seen.Skip(colon + 1)];
                }
                if (owner.Refusal(text) is { } refusal)
                {
                    throw new SessionFailedException($"{refusal.Clause}; it said \"{owner.Shown(text)}\"");
                }

                int read;
                using (var idle = Linked(owner.IdleTimeout, cancellation))
                {
                    read = await _stream!.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                Heard(buffer.AsSpan(0, read));
                if (read == 0)
                {
                    ClosedAtLogin = true;
                    throw new SessionFailedException($"FBB closed the connection while the receiver waited for \"{prompt}\"; " + LoginHint());
                }
                seen.AddRange(buffer.AsSpan(0, read).ToArray());
            }
        }

        private string LoginHint() => owner._settings.Type == BbsKind.LinBpq
            ? $"check that \"{owner._settings.Login}\" and its password match a USER= line in LinBPQ's Telnet port, that the port is its FBBPORT, and that \"{owner._settings.Command}\" reaches the mail application"
            : $"check that {owner._settings.Login} is a BBS user on FBB with this password";

        /// <summary>
        /// Feeds received bytes to the session. The session is made once the BBS's prompt has
        /// arrived, so that the prompt's callsign is known: it stands in for an empty @ field.
        /// </summary>
        private async Task FeedAsync(byte[] data, CancellationToken cancellation)
        {
            if (_session is null)
            {
                _beforeSession.AddRange(data);
                string text = Encoding.Latin1.GetString([.. _beforeSession]);
                if (AsksForName(text))
                {
                    // LinBPQ's new-user prompt also ends in a bare '>', which would otherwise
                    // read as the forwarding prompt and start an FBB session over a connection
                    // that is actually waiting for a Name; fail plainly instead.
                    throw new SessionFailedException(NeedsNameExplanation(owner._settings.Login));
                }
                if (AsksForFirstName(text))
                {
                    // FBB's own new-user prompt (issue #108): it ends in ':', not '>', so this
                    // would otherwise just sit here until the idle timeout, waiting for an answer
                    // that will never come; fail plainly instead, as the LinBPQ case above does.
                    throw new SessionFailedException(FbbNotSetUpExplanation(owner._settings.Login));
                }
                int sid = text.IndexOf('[', StringComparison.Ordinal);
                if (sid < 0 || !text.AsSpan(sid).TrimEnd().EndsWith(">", StringComparison.Ordinal))
                {
                    if (owner._settings.Type == BbsKind.LinBpq && owner.Refusal(text) is { } refusal)
                    {
                        throw new SessionFailedException($"{refusal.Clause}; it said \"{owner.Shown(text)}\"");
                    }
                    if (text.Contains("Invalid command", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SessionFailedException($"LinBPQ did not know the command \"{owner._settings.Command}\"; " + LoginHint());
                    }
                    return;
                }

                var call = PromptCall().Match(text, sid);
                string fallbackAt = call.Success ? call.Groups[1].Value.ToUpperInvariant() : "WW";
                var messages = bulletins.Select(b => (Bulletin: b, Message: ToOutbound(b, fallbackAt))).ToList();
                _bids = new Dictionary<FbbOutboundMessage, string>(ReferenceEqualityComparer.Instance);
                foreach (var (bulletin, message) in messages)
                {
                    _bids[message] = bulletin.Bid;
                }
                _session = new FbbSession(
                    new FbbSessionConfig { Role = FbbRole.Caller, OwnCallsign = owner._settings.Login.ToUpperInvariant(), SidVersion = "MAILCAST" + owner.Version },
                    messages.Select(m => m.Message));
                await ApplyAsync(_session.Advance(new FbbStart()), cancellation).ConfigureAwait(false);
                data = [.. _beforeSession];
                _beforeSession.Clear();
            }

            await ApplyAsync(_session.Advance(new FbbPeerData(data)), cancellation).ConfigureAwait(false);
        }

        private async Task ApplyAsync(IReadOnlyList<FbbAction> actions, CancellationToken cancellation)
        {
            var queue = new Queue<FbbAction>(actions);
            while (queue.Count > 0)
            {
                switch (queue.Dequeue())
                {
                    case FbbSendLine line:
                        await SendAsync(line.Line + "\r\n", cancellation).ConfigureAwait(false);
                        break;
                    case FbbSendBytes bytes:
                        await WriteAsync(bytes.Data, cancellation).ConfigureAwait(false);
                        break;
                    case FbbOutboundResult result:
                        Answers[_bids[result.Message]] = result.Answer;
                        break;
                    case FbbProposalsReceived proposals:
                        // Never FS -: that would tell the BBS the receiver has these, and it would
                        // mark them delivered. See the class remarks.
                        ReverseOffered += proposals.Proposals.Count;
                        // The FBB session answers FS - itself to a proposal whose TO is over six
                        // characters, whatever it is told. Rather than let it, hang up unanswered:
                        // the BBS keeps the message. And one round of the BBS's mail is plenty
                        // for a receiver that takes none of it.
                        if (proposals.Proposals.Any(p => p is FaProposal { RequiresPoliteReject: true }) || ++_reverseRounds > 1)
                        {
                            Failure = _reverseRounds > 1
                                ? "the BBS kept offering mail to the receiver; hung up"
                                : "the BBS offered the receiver a message it cannot decline without saying it has it; hung up";
                            Over = true;
                            return;
                        }
                        foreach (var next in _session!.Advance(new FbbProposalDecisions([.. proposals.Proposals.Select(_ => FsAnswer.Defer)])))
                        {
                            queue.Enqueue(next);
                        }
                        break;
                    case FbbProtocolError error:
                        Failure = Explain(error.ErrorLine);
                        break;
                    case FbbSessionOver over:
                        Over = true;
                        Graceful = over.Graceful;
                        break;
                    default:
                        break;
                }
            }
        }

        private string Explain(string errorLine)
        {
            var sid = _session?.PeerSid;
            if (sid is not null && (!sid.SupportsBlockedFbb || !sid.SupportsCompression))
            {
                return $"the BBS offered {Ascii.Clean(sid.Raw)}, which is not compressed FBB forwarding. "
                    + (owner._settings.Type == BbsKind.LinBpq
                        ? $"In LinBPQ's mail configuration, make {owner._settings.Login} a BBS user and tick FBB Blocked, Allow Binary and Use B1 Protocol on its forwarding page"
                        : $"In FBB, give {owner._settings.Login} the BBS flag");
            }
            return "protocol error: " + Ascii.Clean(errorLine);
        }

        private Task SendAsync(string text, CancellationToken cancellation) =>
            WriteAsync(Encoding.Latin1.GetBytes(text), cancellation);

        /// <summary>A write the BBS has <see cref="IdleTimeout"/> to take, as a read has to answer.</summary>
        private async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation)
        {
            using var idle = Linked(owner.IdleTimeout, cancellation);
            await _stream!.WriteAsync(data, idle.Token).ConfigureAwait(false);
        }
    }
}
