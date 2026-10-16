using System.Net.Sockets;
using System.Text;

namespace Mailcast.HeadEnd.Intake;

/// <summary>
/// Logging in to the BBS by a list of <see cref="LoginStep"/>s, shared by the forwarding intake
/// and the report reader. Nothing it sends is ever logged, so the password never reaches the
/// journal.
/// </summary>
public static class BbsLogin
{
    /// <summary>
    /// Works through the login steps: waits for each step's text (any case, ignoring telnet
    /// negotiation), then sends its line, ended by CR. <c>{user}</c>, <c>{password}</c> and
    /// <c>{call}</c> are filled in. Returns whatever arrived after the last expected text, which
    /// belongs to what follows.
    /// </summary>
    public static async Task<byte[]> LogInAsync(Stream stream, IReadOnlyList<LoginStep> steps, string user, string password, string call, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(steps);
        var received = new List<byte>();
        var buffer = new byte[1024];
        foreach (LoginStep step in steps)
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
                    .Replace("{user}", user, StringComparison.Ordinal)
                    .Replace("{password}", password, StringComparison.Ordinal)
                    .Replace("{call}", call, StringComparison.Ordinal);
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
    public static List<byte> StripTelnet(ReadOnlySpan<byte> data)
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
}

/// <summary>A network stream that closes its client.</summary>
internal sealed class OwningStream(TcpClient client) : Stream
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
