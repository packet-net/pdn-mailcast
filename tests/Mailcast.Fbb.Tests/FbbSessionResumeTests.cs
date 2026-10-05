// Copied from packet-net/pdn-bbs (AGPL-3.0) at 92a54cafc1eb7f05779c0d740c67a088eacc3e1a, tests/Bbs.Fbb.Tests/FbbSessionResumeTests.cs.
// Changed only to replace dashes in comments with hyphens; see this project's csproj.
using System.Text;

namespace Bbs.Fbb.Tests;

/// <summary>
/// Receiver-side restart granting (issue #38 / spec §3.8): when a partial inbound transfer is held
/// for a re-offered message, the answerer grants <c>FS !offset</c> at the right offset, the peer
/// resends only the tail, and the reassembled body is byte-identical to a from-zero receive. These
/// drive the sans-IO FSM directly with an in-memory <see cref="IInboundResumeStore"/>; the host's
/// crash-safe on-disk store is exercised separately.
/// </summary>
public class FbbSessionResumeTests
{
    private const string PeerSidLine = "[BPQ-6.0.25.30-B12FWIHJM$]";
    private const string Mid = "555_GB7BPQ";

    /// <summary>An in-memory partial store with the same per-peer-instance contract as the real one.</summary>
    private sealed class MemoryResumeStore : IInboundResumeStore
    {
        private readonly Dictionary<string, InboundPartial> _partials = new(StringComparer.OrdinalIgnoreCase);

        public int SaveCount { get; private set; }

        public InboundPartial? TryLoad(string messageId) =>
            _partials.TryGetValue(messageId, out var p) ? p : null;

        public void Save(string messageId, ReadOnlySpan<byte> compressedSoFar, int expectedCompressedSize)
        {
            SaveCount++;
            _partials[messageId] = new InboundPartial(compressedSoFar.ToArray(), expectedCompressedSize);
        }

        public void Discard(string messageId) => _partials.Remove(messageId);

        public void Seed(string messageId, byte[] bytes) =>
            _partials[messageId] = new InboundPartial(bytes, 0);

        public bool Has(string messageId) => _partials.ContainsKey(messageId);
    }

    private static FbbSessionConfig AnswererConfig(IInboundResumeStore? resume) => new()
    {
        Role = FbbRole.Answerer,
        OwnCallsign = "GB7PDN",
        SidVersion = "0.1.0",
        InboundResume = resume,
    };

    private static IReadOnlyList<FbbAction> FeedLine(FbbSession session, string line) =>
        session.Advance(new FbbPeerData(Encoding.ASCII.GetBytes(line + "\r")));

    private static IReadOnlyList<FbbAction> FeedBytes(FbbSession session, byte[] data) =>
        session.Advance(new FbbPeerData(data));

    private static List<string> Lines(IEnumerable<FbbAction> actions) =>
        [.. actions.OfType<FbbSendLine>().Select(a => a.Line)];

    /// <summary>Drives an answerer to the point where it has accepted/declined a single FA proposal for the MID.</summary>
    private static FbbSession StartedToProposal(IInboundResumeStore? resume, string body, out byte[] compressed)
    {
        compressed = LzhufContainer.Encode(LzhufContainerKind.B1, Encoding.ASCII.GetBytes(body));
        var session = new FbbSession(AnswererConfig(resume));
        session.Advance(new FbbStart());
        Assert.Empty(FeedLine(session, PeerSidLine));
        string proposal = $"FA P M0LTE GB7BPQ.#23.GBR.EURO G8BPQ {Mid} {body.Length}";
        Assert.Empty(FeedLine(session, proposal));
        var terminator = ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([proposal]));
        Assert.Single(FeedLine(session, terminator).OfType<FbbProposalsReceived>());
        return session;
    }

    /// <summary>The sender's framing for a resume at <paramref name="offset"/> - mirrors FbbSession.SendMessage.</summary>
    private static byte[] FrameResumed(string title, byte[] compressed, int offset)
    {
        var payload = new byte[compressed.Length - offset];
        compressed.AsSpan(0, 6).CopyTo(payload);
        compressed.AsSpan(offset + 6).CopyTo(payload.AsSpan(6));
        return BlockFraming.EncodeMessage(title, offset, payload);
    }

    private static byte[] FrameWhole(string title, byte[] compressed) =>
        BlockFraming.EncodeMessage(title, 0, compressed);

    [Fact]
    public void NoPartialHeld_GrantsPlainAccept()
    {
        var store = new MemoryResumeStore();
        var session = StartedToProposal(store, "Hello restart world body", out byte[] compressed);

        var fs = Lines(session.Advance(new FbbProposalDecisions([FsAnswer.Accept])));

        Assert.Equal(["FS +"], fs); // nothing held → ordinary accept
        Assert.Single(FeedBytes(session, FrameWhole("Body", compressed)).OfType<FbbMessageDelivered>());
    }

    [Fact]
    public void PartialHeld_GrantsOffset_AndReconstructsFullBody()
    {
        const string body = "The quick brown fox jumps over the lazy dog, repeatedly, to make the LZHUF stream long enough to span a restart.";
        var store = new MemoryResumeStore();

        // First (interrupted) attempt: receive a PREFIX of the whole transfer, leaving a held partial.
        var first = StartedToProposal(store, body, out byte[] compressed);
        Assert.Equal(["FS +"], Lines(first.Advance(new FbbProposalDecisions([FsAnswer.Accept]))));
        byte[] whole = FrameWhole("Body", compressed);
        // Feed only enough to land mid-payload (past the SOH header + into the first STX block).
        int cut = whole.Length - 12;
        Assert.Empty(Lines(FeedBytes(first, whole[..cut])));
        Assert.True(store.Has(Mid), "an interrupted transfer must leave a persisted partial");
        int held = store.TryLoad(Mid)!.Value.Compressed.Length;
        Assert.True(held > 6 && held < compressed.Length, "partial must be a strict, resumable prefix");

        // Second attempt (fresh session, same store): the SAME MID is re-offered.
        var second = StartedToProposal(store, body, out byte[] compressed2);
        Assert.Equal(compressed, compressed2); // deterministic encode
        var fs = Lines(second.Advance(new FbbProposalDecisions([FsAnswer.Accept])));

        // We grant a restart at (held - 6) into the compressed image (spec §3.8).
        Assert.Equal([$"FS !{held - 6}"], fs);

        // The peer resends header + tail from that offset; the body must reconstruct exactly.
        var deliveries = FeedBytes(second, FrameResumed("Body", compressed, held - 6));
        var delivered = Assert.Single(deliveries.OfType<FbbMessageDelivered>());
        Assert.Equal(body, Encoding.ASCII.GetString(delivered.Body.Span));
        Assert.False(store.Has(Mid), "a committed message discards its partial");
    }

    [Fact]
    public void ResumedReceive_IsByteIdenticalToFromZeroReceive()
    {
        const string body = "Payload that we will receive once whole and once via a granted restart; both must yield the same bytes.";

        // Baseline: a clean from-zero receive.
        var clean = StartedToProposal(null, body, out byte[] compressed);
        clean.Advance(new FbbProposalDecisions([FsAnswer.Accept]));
        var cleanBody = Assert.Single(FeedBytes(clean, FrameWhole("Body", compressed)).OfType<FbbMessageDelivered>()).Body.ToArray();

        // Resumed: hold a prefix, grant the offset, receive the tail.
        var store = new MemoryResumeStore();
        int held = 6 + (compressed.Length / 3);
        store.Seed(Mid, compressed[..held]);
        var session = StartedToProposal(store, body, out _);
        Assert.Equal([$"FS !{held - 6}"], Lines(session.Advance(new FbbProposalDecisions([FsAnswer.Accept]))));
        var resumedBody = Assert.Single(
            FeedBytes(session, FrameResumed("Body", compressed, held - 6)).OfType<FbbMessageDelivered>()).Body.ToArray();

        Assert.Equal(cleanBody, resumedBody);
        Assert.Equal(Encoding.ASCII.GetBytes(body), resumedBody);
    }

    [Fact]
    public void TinyPartial_NotResumable_GrantsPlainAccept()
    {
        const string body = "Body for the too-small-to-resume case.";
        var store = new MemoryResumeStore();
        store.Seed(Mid, [1, 2, 3]); // < 7 bytes: not a useful restart point
        var session = StartedToProposal(store, body, out byte[] compressed);

        Assert.Equal(["FS +"], Lines(session.Advance(new FbbProposalDecisions([FsAnswer.Accept]))));
        // Falls back to a full receive cleanly.
        var delivered = Assert.Single(FeedBytes(session, FrameWhole("Body", compressed)).OfType<FbbMessageDelivered>());
        Assert.Equal(body, Encoding.ASCII.GetString(delivered.Body.Span));
    }

    [Fact]
    public void RejectedProposal_NeverGrantsOffset_EvenWithPartialHeld()
    {
        const string body = "Body the host declines this time.";
        var store = new MemoryResumeStore();
        var session = StartedToProposal(store, body, out byte[] compressed);
        store.Seed(Mid, compressed[..(6 + compressed.Length / 2)]);

        // Host says '-' (already have / oversize): the FS is a plain reject and NO offset is granted
        // even though a partial is held. With nothing accepted the turn reverses (empty queue → FF).
        var fs = Lines(session.Advance(new FbbProposalDecisions([FsAnswer.AlreadyHave])));
        Assert.Equal(["FS -", "FF"], fs);
        Assert.DoesNotContain(fs, l => l.Contains('!'));
    }

    [Fact]
    public void TwoSession_InterruptThenResume_SenderAndReceiverAgreeOnTheWire()
    {
        // End-to-end across the SAME FSM both roles (the two-session harness): a caller proposes a
        // message; the receiver is interrupted mid-body on the first attempt, leaving a persisted
        // partial; on a second connection the receiver GRANTS the restart and a fresh caller - whose
        // own SendMessage honours !offset - resends only the tail. The receiver reconstructs the full
        // body. Every byte is produced by the real FSM on both ends, not a hand-rolled frame.
        const string body =
            "Resume me end to end. Padding to make the compressed stream span multiple bytes so a " +
            "restart at a non-trivial offset is meaningful and exercises the header-repeat skip path.";
        var store = new MemoryResumeStore();
        var outbound = new FbbOutboundMessage
        {
            MessageType = 'P', From = "M0LTE", AtBbs = "GB7PDN", To = "G8BPQ",
            Bid = Mid, Title = "Resume", Body = Encoding.ASCII.GetBytes(body),
        };

        // --- Attempt 1: caller proposes, receiver accepts, transfer is INTERRUPTED mid-body. ---
        var send1 = new FbbSession(new FbbSessionConfig { Role = FbbRole.Caller, OwnCallsign = "GB7BPQ" }, [outbound]);
        var recv1 = new FbbSession(AnswererConfig(store));
        PumpUntilBody(send1, recv1);
        Assert.True(store.Has(Mid), "the interrupted transfer must have staged a partial");
        int held = store.TryLoad(Mid)!.Value.Compressed.Length;
        Assert.True(held > 6, "need a resumable prefix");

        // --- Attempt 2: a fresh caller + receiver, same partial store. The receiver grants !offset
        // and the caller's own SendMessage honours it; the body must reconstruct exactly. ---
        var send2 = new FbbSession(new FbbSessionConfig { Role = FbbRole.Caller, OwnCallsign = "GB7BPQ" }, [outbound]);
        var recv2 = new FbbSession(AnswererConfig(store));
        var delivered = PumpToCompletion(send2, recv2);

        var msg = Assert.Single(delivered);
        Assert.Equal(body, Encoding.ASCII.GetString(msg.Body.Span));
        Assert.False(store.Has(Mid), "a committed resume discards the partial");
    }

    /// <summary>Pumps a caller→answerer pair until the answerer is mid-body, then stops (interrupt).</summary>
    private static void PumpUntilBody(FbbSession caller, FbbSession answerer)
    {
        var pending = new Queue<(FbbSession Target, byte[] Data)>();
        void Dispatch(FbbSession src, FbbSession peer, IReadOnlyList<FbbAction> actions, bool stopAtBytes)
        {
            foreach (var a in actions)
            {
                switch (a)
                {
                    case FbbSendLine line:
                        pending.Enqueue((peer, Encoding.ASCII.GetBytes(line.Line + "\r\n")));
                        break;
                    case FbbSendBytes bytes when stopAtBytes && peer == answerer:
                        // INTERRUPT: deliver only a prefix of the body bytes to the answerer, then stop.
                        int cut = Math.Max(1, bytes.Data.Length - 8);
                        answerer.Advance(new FbbPeerData(bytes.Data[..cut]));
                        pending.Clear();
                        return;
                    case FbbSendBytes bytes:
                        pending.Enqueue((peer, bytes.Data.ToArray()));
                        break;
                    case FbbProposalsReceived proposals:
                        // The answerer accepts; its own resume logic decides + / !offset.
                        foreach (var n in src.Advance(new FbbProposalDecisions(
                            [.. proposals.Proposals.Select(_ => FsAnswer.Accept)])))
                        {
                            Dispatch(src, src == caller ? answerer : caller, [n], stopAtBytes);
                        }

                        break;
                    default:
                        break;
                }
            }
        }

        Dispatch(answerer, caller, answerer.Advance(new FbbStart()), stopAtBytes: true);
        Dispatch(caller, answerer, caller.Advance(new FbbStart()), stopAtBytes: true);
        int guard = 0;
        while (pending.Count > 0 && guard++ < 500)
        {
            var (target, data) = pending.Dequeue();
            Dispatch(target, target == caller ? answerer : caller, target.Advance(new FbbPeerData(data)), stopAtBytes: true);
        }
    }

    /// <summary>Pumps a caller→answerer pair to completion, collecting deliveries.</summary>
    private static List<FbbMessageDelivered> PumpToCompletion(FbbSession caller, FbbSession answerer)
    {
        var delivered = new List<FbbMessageDelivered>();
        var pending = new Queue<(FbbSession Target, byte[] Data)>();
        void Dispatch(FbbSession src, FbbSession peer, IReadOnlyList<FbbAction> actions)
        {
            foreach (var a in actions)
            {
                switch (a)
                {
                    case FbbSendLine line:
                        pending.Enqueue((peer, Encoding.ASCII.GetBytes(line.Line + "\r\n")));
                        break;
                    case FbbSendBytes bytes:
                        pending.Enqueue((peer, bytes.Data.ToArray()));
                        break;
                    case FbbProposalsReceived proposals:
                        foreach (var n in src.Advance(new FbbProposalDecisions(
                            [.. proposals.Proposals.Select(_ => FsAnswer.Accept)])))
                        {
                            Dispatch(src, src == caller ? answerer : caller, [n]);
                        }

                        break;
                    case FbbMessageDelivered d:
                        delivered.Add(d);
                        break;
                    default:
                        break;
                }
            }
        }

        Dispatch(answerer, caller, answerer.Advance(new FbbStart()));
        Dispatch(caller, answerer, caller.Advance(new FbbStart()));
        int guard = 0;
        while (pending.Count > 0 && guard++ < 500)
        {
            var (target, data) = pending.Dequeue();
            Dispatch(target, target == caller ? answerer : caller, target.Advance(new FbbPeerData(data)));
        }

        return delivered;
    }

    [Fact]
    public void NoResumeStore_BehavesExactlyAsBefore()
    {
        const string body = "No store configured at all.";
        var session = StartedToProposal(resume: null, body, out byte[] compressed);
        Assert.Equal(["FS +"], Lines(session.Advance(new FbbProposalDecisions([FsAnswer.Accept]))));
        var delivered = Assert.Single(FeedBytes(session, FrameWhole("Body", compressed)).OfType<FbbMessageDelivered>());
        Assert.Equal(body, Encoding.ASCII.GetString(delivered.Body.Span));
    }
}
