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

    /// <summary>The BBS said the password was wrong.</summary>
    WrongPassword,

    /// <summary>The login worked, but it is not set up as a BBS forwarding partner.</summary>
    NotForwardingPartner,

    /// <summary>The BBS could not be reached: it refused the connection, or never answered.</summary>
    Unreachable,

    /// <summary>Something else happened; <see cref="BbsLoginTestResult.Detail"/> has the BBS's first line.</summary>
    UnexpectedReply,
}

/// <summary>
/// What testing a BBS login found. <see cref="Said"/> is a short, friendly sentence for the
/// settings page; <see cref="Detail"/> is the BBS's own first line of reply, given only for
/// <see cref="BbsLoginTestOutcome.UnexpectedReply"/>.
/// </summary>
public sealed record BbsLoginTestResult(BbsLoginTestOutcome Outcome, string Said, string? Detail = null);

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
        var (text, closed) = await ReadTestReplyAsync(stream, Settled, cancellation).ConfigureAwait(false);
        return Classify(text, closed);
    }

    /// <summary>FBB prompts for both; a leading dot asks for a binary session.</summary>
    private async Task<BbsLoginTestResult> TestFbbAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var (first, closed1) = await ReadTestReplyAsync(stream,
            t => t.Contains("allsign", StringComparison.OrdinalIgnoreCase) || Settled(t), cancellation).ConfigureAwait(false);
        if (closed1 || !first.Contains("allsign", StringComparison.OrdinalIgnoreCase))
        {
            return Classify(first, closed1);
        }
        await stream.WriteAsync(Encoding.Latin1.GetBytes("." + _settings.Login + "\r"), cancellation).ConfigureAwait(false);
        var (second, closed2) = await ReadTestReplyAsync(stream,
            t => t.Contains("assword", StringComparison.OrdinalIgnoreCase) || Settled(t), cancellation).ConfigureAwait(false);
        if (closed2 || !second.Contains("assword", StringComparison.OrdinalIgnoreCase))
        {
            return Classify(second, closed2);
        }
        await stream.WriteAsync(Encoding.Latin1.GetBytes(_settings.Password + "\r"), cancellation).ConfigureAwait(false);
        var (text, closed) = await ReadTestReplyAsync(stream, Settled, cancellation).ConfigureAwait(false);
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

    /// <summary>The phrases a BBS refuses a login with: LinBPQ's FBBPORT re-sends its prompts; FBB says one of these.</summary>
    private static readonly string[] FbbLoginRefusals = ["Invalid callsign", "Unregistered callsign", "Callsign error", "Password error", "Bad password"];

    private static bool RefusalFound(string text) =>
        text.Contains("user:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("password:", StringComparison.OrdinalIgnoreCase)
        || FbbLoginRefusals.Any(r => text.Contains(r, StringComparison.OrdinalIgnoreCase));

    /// <summary>The reply has said enough to classify: a refusal, an unknown command, or a prompt at the end.</summary>
    private static bool Settled(string text) =>
        RefusalFound(text) || text.Contains("Invalid command", StringComparison.OrdinalIgnoreCase) || text.TrimEnd().EndsWith('>');

    /// <summary>
    /// What a login test's reply means. <see cref="PromptCall"/> matching means the BBS sent its
    /// own forwarding prompt ("de CALL>"), exactly as it does to a real forwarding partner; a bare
    /// prompt with no "de CALL" means the login worked but is not a BBS user set up to forward,
    /// such as LinBPQ asking a brand new login to register a name.
    /// </summary>
    private BbsLoginTestResult Classify(string text, bool closed)
    {
        if (RefusalFound(text))
        {
            return new BbsLoginTestResult(BbsLoginTestOutcome.WrongPassword, "That password is not right.");
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
        if (text.TrimEnd().EndsWith('>'))
        {
            return new BbsLoginTestResult(BbsLoginTestOutcome.NotForwardingPartner,
                $"\"{_settings.Login}\" logged in, but it is not set up as a BBS forwarding partner.");
        }
        string? line = FirstLine(text);
        return new BbsLoginTestResult(BbsLoginTestOutcome.UnexpectedReply,
            line is null
                ? (closed ? "The BBS closed the connection without answering." : "The BBS did not answer in time.")
                : "The BBS said something unexpected.",
            line);
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
        private FbbSession? _session;
        private Dictionary<FbbOutboundMessage, string> _bids = new(ReferenceEqualityComparer.Instance);
        private NetworkStream? _stream;
        private int _reverseRounds;

        public Dictionary<string, FsAnswer> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Graceful { get; private set; }

        public string? Failure { get; private set; }

        public int ReverseOffered { get; private set; }

        private bool Over { get; set; }

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
                if (read == 0)
                {
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
                foreach (string refusal in new[] { "Invalid callsign", "Unregistered callsign", "Callsign error", "Password error", "Bad password" })
                {
                    if (text.Contains(refusal, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SessionFailedException($"FBB refused the login ({refusal}); " + LoginHint());
                    }
                }

                int read;
                using (var idle = Linked(owner.IdleTimeout, cancellation))
                {
                    read = await _stream!.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                if (read == 0)
                {
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
                int sid = text.IndexOf('[', StringComparison.Ordinal);
                if (sid < 0 || !text.AsSpan(sid).TrimEnd().EndsWith(">", StringComparison.Ordinal))
                {
                    if (owner._settings.Type == BbsKind.LinBpq
                        && (text.Contains("user:", StringComparison.OrdinalIgnoreCase) || text.Contains("password:", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new SessionFailedException("LinBPQ refused the login; " + LoginHint());
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
