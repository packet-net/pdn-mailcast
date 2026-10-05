using System.Net.Sockets;
using System.Text;
using Packet.SoundModem.Rig;

namespace Mailcast.Receiver.Retune;

/// <summary>
/// Reads whether the radio is transmitting, with rigctld's <c>t</c>, on a short connection of its
/// own: pdn-soundmodem's <see cref="RigControl"/> keys and unkeys but does not read the rig's PTT,
/// and rigctld serves more than one client.
/// </summary>
public static class RigPtt
{
    /// <summary>How long the connection and the answer may take, each.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>True while the rig says it is transmitting.</summary>
    /// <exception cref="IOException">rigctld could not be reached, or did not say.</exception>
    public static async Task<bool> TransmittingAsync(RigctldEndpoint endpoint, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var client = new TcpClient { NoDelay = true };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(Timeout);
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, limit.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync("t\n"u8.ToArray(), limit.Token).ConfigureAwait(false);
            var line = new StringBuilder();
            var buffer = new byte[64];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("rigctld closed the connection before answering \"t\"");
                }
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        string answer = line.ToString().Trim();
                        return answer.StartsWith("RPRT", StringComparison.Ordinal)
                            ? throw new IOException($"rigctld answered \"t\" with {Ascii.Clean(answer)}, so this rig's PTT cannot be read")
                            : answer != "0";
                    }
                    if (line.Length < 64 && buffer[i] is >= 0x20 and < 0x7F)
                    {
                        line.Append((char)buffer[i]);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new IOException($"rigctld at {endpoint} did not answer \"t\" within {Timeout.TotalSeconds:F0} s");
        }
        catch (SocketException e)
        {
            throw new IOException($"cannot reach rigctld at {endpoint} ({e.Message})", e);
        }
    }
}
