// Copied from packet-net/pdn-bbs (AGPL-3.0) at 92a54cafc1eb7f05779c0d740c67a088eacc3e1a, src/Bbs.Fbb/IInboundResumeStore.cs.
// Changed only to replace dashes in comments with hyphens; see this project's csproj.
namespace Bbs.Fbb;

/// <summary>
/// A persisted partial INBOUND transfer (issue #38 / compat spec §3.8 receiver-side restart
/// granting). The receiver stages the compressed bytes it has received so far for an in-flight
/// message; if the link drops mid-transfer the partial survives, and when the peer re-offers the
/// same message the receiver can grant <c>FS !offset</c> and resume appending rather than
/// re-receiving from byte zero.
/// </summary>
/// <param name="Compressed">
/// The compressed image bytes received so far - the FULL compressed object prefix
/// (<c>compressed[0 .. Compressed.Length)</c>), i.e. it INCLUDES the leading 6-byte container
/// header. The resume offset granted to the peer is <c>Compressed.Length - 6</c> (the sender
/// re-sends the 6-byte header preamble then the tail from that offset - spec §3.8).
/// </param>
/// <param name="ExpectedCompressedSize">
/// The compressed object size the original proposal advertised, when known (FC carries it; FA
/// does not, so 0). Used as a divergence guard - a re-offer whose advertised compressed size no
/// longer matches what we hold is not trusted for resume.
/// </param>
public readonly record struct InboundPartial(byte[] Compressed, int ExpectedCompressedSize);

/// <summary>
/// A peer-scoped store of partial inbound transfers, keyed by the message's network id (the FA
/// <c>BID</c> or FC <c>MID</c>). Implementations persist durably (issue #38: a partial must
/// survive a daemon restart to be useful) with a defined commit boundary, so a crash leaves a
/// complete prior partial rather than a torn one. The instance handed to a session is already
/// scoped to the peer (the host keys it by partner), so callers pass only the message id.
///
/// <para>Sans-IO: <see cref="FbbSession"/> consults this through the interface only; the on-disk
/// format and fsync/rename commit boundary live in the host's concrete implementation. A null
/// store (the default) disables resume entirely - every accept is a from-zero receive, exactly the
/// pre-#38 behaviour.</para>
/// </summary>
public interface IInboundResumeStore
{
    /// <summary>
    /// Returns the partial held for <paramref name="messageId"/>, or <see langword="null"/> when
    /// none exists. A returned partial of fewer than 7 bytes is not resumable (the 6-byte header
    /// plus at least one tail byte is the minimum a restart can save) and callers should treat it
    /// as absent.
    /// </summary>
    InboundPartial? TryLoad(string messageId);

    /// <summary>
    /// Persists the compressed bytes received so far for <paramref name="messageId"/>, durably and
    /// atomically (the call returns once the bytes are committed, so a crash after it cannot lose
    /// them; a crash during it leaves the previous committed partial intact). Called as each block
    /// is appended during receive.
    /// </summary>
    void Save(string messageId, ReadOnlySpan<byte> compressedSoFar, int expectedCompressedSize);

    /// <summary>
    /// Discards the partial for <paramref name="messageId"/> - on a clean commit of the message, on
    /// a divergence that makes the partial untrustworthy, or on staleness GC. Idempotent (a no-op
    /// when none is held).
    /// </summary>
    void Discard(string messageId);
}
