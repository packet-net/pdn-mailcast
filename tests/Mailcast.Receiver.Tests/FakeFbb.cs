using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// A BBS on a loopback port that behaves like FBB's own TCP port for a login test: it prompts
/// "Callsign :" then "Password :", and once a line has been sent for each, answers with one
/// fixed reply. Used to check <see cref="Mailcast.Receiver.Delivery.BbsClient.Classify"/> against
/// bytes captured from a real Linux FBB 7.0.11 (issue #108; see docs/receiver.md and
/// tests/Mailcast.Receiver.Tests/Fbb for how it was set up), without needing docker for every run.
/// </summary>
internal sealed class FakeFbb : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;

    /// <summary>The fixed reply to send once the login line and the password line have both arrived.</summary>
    public FakeFbb(string reply)
    {
        Reply = reply;
        _listener.Start();
        _accept = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Reply { get; }

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
            try
            {
                // The real FBB's telnet negotiation byte and banner, as captured; BbsClient only
                // looks for "allsign" in here, so the exact banner text does not matter to it.
                await stream.WriteAsync(Encoding.Latin1.GetBytes("ÿü\u0001\r\nGB7FBB BBS. TELNET Access\r\n\r\nCallsign : "), _stop.Token).ConfigureAwait(false);
                await ReadLineAsync(stream).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.Latin1.GetBytes("Password : "), _stop.Token).ConfigureAwait(false);
                await ReadLineAsync(stream).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.Latin1.GetBytes(Reply), _stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    private async Task ReadLineAsync(NetworkStream stream)
    {
        var buffer = new byte[1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
            if (read == 0 || Array.IndexOf(buffer, (byte)'\r', 0, read) >= 0)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        _stop.Dispose();
    }
}
