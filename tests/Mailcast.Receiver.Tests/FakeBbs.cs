using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Packet.Fbb;

namespace Mailcast.Receiver.Tests;

/// <summary>A message the fake BBS took.</summary>
internal sealed record TakenMessage(Proposal Proposal, string Title, string Body);

/// <summary>
/// A BBS on a loopback port that behaves like LinBPQ's FBBPORT: it reads the user name, the
/// password and the application command, then answers as a forwarding partner with pdn-bbs's
/// FBB session in the answering role.
/// </summary>
internal sealed class FakeBbs : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;

    public FakeBbs()
    {
        _listener.Start();
        _accept = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>BIDs the BBS already has: proposals of these are answered FS -.</summary>
    public ConcurrentDictionary<string, bool> Known { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>BIDs the BBS wants later: proposals of these are answered FS =.</summary>
    public ConcurrentDictionary<string, bool> Later { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Messages taken, in order.</summary>
    public ConcurrentQueue<TakenMessage> Taken { get; } = new();

    /// <summary>The login lines each connection sent.</summary>
    public ConcurrentQueue<string[]> Logins { get; } = new();

    /// <summary>Messages the BBS offers back when the turn passes to it.</summary>
    public List<FbbOutboundMessage> Queued { get; } = [];

    /// <summary>The answers the receiver gave to <see cref="Queued"/>.</summary>
    public ConcurrentQueue<string> ReverseAnswers { get; } = new();

    /// <summary>The password the BBS accepts.</summary>
    public string Password { get; init; } = "secret";

    /// <summary>Drop the connection straight after the first transfer, before answering it.</summary>
    public bool HangUpAfterTransfer { get; set; }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var pending = new List<byte>();
            var lines = new List<string>();
            try
            {
                while (lines.Count < 3)
                {
                    int read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }
                    pending.AddRange(buffer.AsSpan(0, read).ToArray());
                    int cr;
                    while (lines.Count < 3 && (cr = pending.IndexOf((byte)'\r')) >= 0)
                    {
                        lines.Add(Encoding.Latin1.GetString([.. pending.Take(cr)]));
                        pending.RemoveRange(0, cr + 1);
                    }
                }
                Logins.Enqueue([.. lines]);
                if (lines[1] != Password)
                {
                    await stream.WriteAsync(Encoding.Latin1.GetBytes("password:"), _stop.Token);
                    return;
                }

                await stream.WriteAsync(Encoding.Latin1.GetBytes("TST:GB7TST} Connected to BBS\r"), _stop.Token);
                var session = new FbbSession(new FbbSessionConfig { Role = FbbRole.Answerer, OwnCallsign = "GB7TST" }, Queued);
                await ApplyAsync(stream, session, session.Advance(new FbbStart()));
                if (pending.Count > 0)
                {
                    await ApplyAsync(stream, session, session.Advance(new FbbPeerData(pending.ToArray())));
                }
                while (session.Phase is not (FbbSessionPhase.Finished or FbbSessionPhase.Failed))
                {
                    int read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }
                    if (!await ApplyAsync(stream, session, session.Advance(new FbbPeerData(buffer.AsMemory(0, read).ToArray()))))
                    {
                        return;
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    /// <summary>Carries out the session's actions; false to hang up.</summary>
    private async Task<bool> ApplyAsync(NetworkStream stream, FbbSession session, IReadOnlyList<FbbAction> actions)
    {
        var queue = new Queue<FbbAction>(actions);
        while (queue.Count > 0)
        {
            switch (queue.Dequeue())
            {
                case FbbSendLine line:
                    await stream.WriteAsync(Encoding.Latin1.GetBytes(line.Line + "\r"), _stop.Token);
                    break;
                case FbbSendBytes bytes:
                    await stream.WriteAsync(bytes.Data, _stop.Token);
                    break;
                case FbbProposalsReceived proposals:
                    var answers = proposals.Proposals
                        .Select(p => p is FaProposal fa && Known.ContainsKey(fa.Bid) ? FsAnswer.AlreadyHave
                            : p is FaProposal later && Later.ContainsKey(later.Bid) ? FsAnswer.Defer
                            : FsAnswer.Accept)
                        .ToList();
                    foreach (var next in session.Advance(new FbbProposalDecisions(answers)))
                    {
                        queue.Enqueue(next);
                    }
                    break;
                case FbbMessageDelivered delivered:
                    Taken.Enqueue(new TakenMessage(delivered.Proposal, delivered.Title, Encoding.Latin1.GetString(delivered.Body.Span)));
                    if (delivered.Proposal is FaProposal fa)
                    {
                        Known[fa.Bid] = true;
                    }
                    if (HangUpAfterTransfer)
                    {
                        HangUpAfterTransfer = false;
                        return false;
                    }
                    break;
                case FbbOutboundResult result:
                    ReverseAnswers.Enqueue(result.Answer.Kind.ToString());
                    break;
                default:
                    break;
            }
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        _stop.Dispose();
    }
}
