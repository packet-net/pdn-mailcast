using System.Net.Sockets;
using System.Text;
using Bbs.Fbb;
using Mailcast.Core;

namespace Mailcast.HeadEnd.Intake;

/// <summary>
/// Collects bulletins from the BBS as a forwarding partner, over FBB B1F with Mailcast.Fbb's
/// session state machine: the head end calls the BBS, logs in, says it has nothing to send, and
/// takes what the BBS proposes. Bulletins up to the size cap are accepted; bulletins over the cap
/// or already held are answered <c>-</c>; personal mail and NTS traffic, which should never be
/// routed here, are answered <c>=</c> (later) so the BBS keeps them, with a warning.
/// </summary>
/// <remarks>
/// <para>The head end always calls, on its own timer and just before each slot, so the BBS needs
/// a partner record but no connect script, and nothing listens for the BBS on this side. A BPQ
/// partner queues mail for its partners whether or not it ever dials them, and hands it over when
/// the partner connects in; pdn-bbs does the same for a partner that dials its FBB TCP port.</para>
/// <para>One session at a time; a pass that overlaps a running one waits for it.</para>
/// </remarks>
public sealed class FbbIntake : IBulletinIntake, IDisposable
{
    private readonly FbbIntakeConfig _config;
    private readonly RotationStore _store;
    private readonly IntakePolicy _policy;
    private readonly IJournal _journal;
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly SemaphoreSlim _one = new(1, 1);

    public FbbIntake(FbbIntakeConfig config, RotationStore store, IntakePolicy policy, IJournal journal, TimeProvider time, Func<CancellationToken, Task<Stream>>? connect = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _connect = connect ?? ConnectTcpAsync;
    }

    /// <inheritdoc />
    public string Name => "FBB";

    /// <inheritdoc />
    public void Dispose() => _one.Dispose();

    /// <inheritdoc />
    public async Task<IntakeResult> CollectAsync(DateOnly today, CancellationToken cancellation)
    {
        await _one.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_config.SessionTimeoutSeconds), _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
            try
            {
                return await SessionAsync(today, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                return new IntakeResult(0, 0, $"the session with {_config.Host}:{_config.Port} took over {_config.SessionTimeoutSeconds:0} s and was dropped");
            }
            catch (Exception e) when (e is IOException or SocketException or FbbProtocolException or InvalidOperationException)
            {
                return new IntakeResult(0, 0, $"{_config.Host}:{_config.Port}: {Ascii.Plain(e.Message)}");
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

    private async Task<IntakeResult> SessionAsync(DateOnly today, CancellationToken cancellation)
    {
        await using Stream stream = await _connect(cancellation).ConfigureAwait(false);
        byte[] leftover = await LoginAsync(stream, cancellation).ConfigureAwait(false);

        var session = new FbbSession(new FbbSessionConfig
        {
            Role = FbbRole.Caller,
            OwnCallsign = _config.PartnerCallsign,
            SidVersion = SidVersion(),
        });
        var counts = new Counts();
        bool over = await ApplyAsync(stream, session, session.Advance(new FbbStart()), today, counts, cancellation).ConfigureAwait(false);
        if (!over && leftover.Length > 0)
        {
            over = await ApplyAsync(stream, session, session.Advance(new FbbPeerData(leftover)), today, counts, cancellation).ConfigureAwait(false);
        }
        var buffer = new byte[4096];
        while (!over)
        {
            int n = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (n == 0)
            {
                counts.Problem ??= session.Phase == FbbSessionPhase.Finished ? null : $"the BBS closed the connection ({session.Phase})";
                break;
            }
            over = await ApplyAsync(stream, session, session.Advance(new FbbPeerData(buffer.AsMemory(0, n))), today, counts, cancellation).ConfigureAwait(false);
        }
        return new IntakeResult(counts.Accepted, counts.Refused, counts.Problem);
    }

    /// <summary>Carries out the session's actions. True once the session is over.</summary>
    private async Task<bool> ApplyAsync(Stream stream, FbbSession session, IReadOnlyList<FbbAction> actions, DateOnly today, Counts counts, CancellationToken cancellation)
    {
        bool over = false;
        foreach (FbbAction action in actions)
        {
            switch (action)
            {
                case FbbSendLine line:
                    await stream.WriteAsync(Encoding.Latin1.GetBytes(line.Line + "\r\n"), cancellation).ConfigureAwait(false);
                    break;
                case FbbSendBytes bytes:
                    await stream.WriteAsync(bytes.Data, cancellation).ConfigureAwait(false);
                    break;
                case FbbProposalsReceived proposals:
                    var answers = proposals.Proposals.Select(p => Decide(p, counts)).ToList();
                    over |= await ApplyAsync(stream, session, session.Advance(new FbbProposalDecisions(answers)), today, counts, cancellation).ConfigureAwait(false);
                    break;
                case FbbMessageDelivered delivered:
                    Take(delivered, today, counts);
                    break;
                case FbbProtocolError error:
                    counts.Problem = $"FBB protocol error: {Ascii.Plain(error.ErrorLine)}";
                    break;
                case FbbSessionOver:
                    over = true;
                    break;
            }
        }
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
        return over;
    }

    private FsAnswer Decide(Proposal proposal, Counts counts)
    {
        if (proposal is not FaProposal fa)
        {
            counts.Refused++;
            _journal.Write("intake: refused a B2F (FC) proposal; this partner speaks B1F only");
            return FsAnswer.AlreadyHave;
        }
        if (char.ToUpperInvariant(fa.MessageType) != 'B')
        {
            // A personal or NTS message routed here is a routing mistake on the BBS. "=" (later)
            // leaves it queued there rather than letting the BBS count it delivered, as "-" would.
            counts.Refused++;
            _journal.Write($"intake: WARNING - the BBS offered {fa.Bid} (type {fa.MessageType}, to {fa.To}@{fa.AtBbs}) to the mailcast partner; answered \"later\" so the BBS keeps it. Only bulletins should be routed here: check the partner's TO, AT and personal HR routes, then send the message on by hand");
            return FsAnswer.Defer;
        }
        string? why = _policy.Refusal(fa.MessageType, fa.Bid, fa.Size)
            ?? (_store.Holds(fa.Bid) ? "its BID is already held" : null);
        if (why is null)
        {
            return FsAnswer.Accept;
        }
        counts.Refused++;
        _journal.Write($"intake: refused {fa.Bid} to {fa.To}@{fa.AtBbs}: {why}");
        return FsAnswer.AlreadyHave;
    }

    private void Take(FbbMessageDelivered delivered, DateOnly today, Counts counts)
    {
        if (delivered.Proposal is not FaProposal fa)
        {
            return;
        }
        string text = Encoding.Latin1.GetString(delivered.Body.Span);
        long seconds = _time.GetUtcNow().ToUnixTimeSeconds();
        Bulletin bulletin;
        try
        {
            bulletin = Bulletin.FromMessageText(fa.MessageType, fa.From, fa.To, fa.AtBbs, fa.Bid, delivered.Title, DateTimeOffset.FromUnixTimeSeconds(seconds), text);
        }
        catch (ArgumentException e)
        {
            counts.Refused++;
            _journal.Write($"intake: {fa.Bid} could not be kept: {Ascii.Plain(e.Message)}");
            return;
        }
        switch (_store.Offer(bulletin, today))
        {
            case OfferOutcome.Added:
                counts.Accepted++;
                _journal.Write($"intake: {bulletin.Bid} from {bulletin.From} to {bulletin.To}@{bulletin.At}, {text.Length} octets: {bulletin.Title}");
                break;
            case OfferOutcome.AlreadyHeld:
                _journal.Write($"intake: {bulletin.Bid} is already held");
                break;
            default:
                counts.Refused++;
                _journal.Write($"intake: {bulletin.Bid} is over the size cap once serialised; not kept");
                break;
        }
    }

    /// <summary>
    /// Works through the login steps: waits for each step's text (any case, ignoring telnet
    /// negotiation), then sends its line. Returns whatever arrived after the last expected text,
    /// which belongs to the forwarding session.
    /// </summary>
    private async Task<byte[]> LoginAsync(Stream stream, CancellationToken cancellation)
    {
        var received = new List<byte>();
        var buffer = new byte[1024];
        foreach (LoginStep step in _config.Login)
        {
            if (!string.IsNullOrEmpty(step.Expect))
            {
                int at;
                while ((at = Find(received, step.Expect)) < 0)
                {
                    int n = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
                    if (n == 0)
                    {
                        throw new IOException($"the BBS closed the connection while the head end waited for \"{step.Expect}\"");
                    }
                    received.AddRange(StripTelnet(buffer.AsSpan(0, n)));
                }
                received.RemoveRange(0, at + step.Expect.Length);
            }
            if (step.Send is not null)
            {
                string line = step.Send
                    .Replace("{user}", _config.User, StringComparison.Ordinal)
                    .Replace("{password}", _config.Password, StringComparison.Ordinal)
                    .Replace("{call}", _config.PartnerCallsign, StringComparison.Ordinal);
                await stream.WriteAsync(Encoding.Latin1.GetBytes(line + "\r"), cancellation).ConfigureAwait(false);
                await stream.FlushAsync(cancellation).ConfigureAwait(false);
            }
        }
        return [.. received];
    }

    private static int Find(List<byte> haystack, string needle)
    {
        string text = Encoding.Latin1.GetString([.. haystack]);
        return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drops telnet IAC sequences, which a telnet port may send before its prompt.</summary>
    private static List<byte> StripTelnet(ReadOnlySpan<byte> data)
    {
        var plain = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == 0xFF && i + 1 < data.Length)
            {
                byte command = data[i + 1];
                i += command is >= 0xFB and <= 0xFE ? 2 : 1;
                continue;
            }
            plain.Add(data[i]);
        }
        return plain;
    }

    private static string SidVersion()
    {
        string version = Program.Version;
        int end = version.IndexOfAny(['+', '-']);
        version = end >= 0 ? version[..end] : version;
        return new string([.. version.Where(c => char.IsAsciiLetterOrDigit(c) || c == '.')]) is { Length: > 0 } v ? v : "0";
    }

    private sealed class Counts
    {
        public int Accepted { get; set; }

        public int Refused { get; set; }

        public string? Problem { get; set; }
    }

    /// <summary>A network stream that closes its client.</summary>
    private sealed class OwningStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
