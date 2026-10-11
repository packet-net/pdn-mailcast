using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// pdn-soundmodem's channel audio stream, in-process: a WebSocket on loopback that says hello
/// and then sends whatever blocks a test gives it, in pdn-soundmodem's wire format
/// (docs/reference/ports-and-endpoints.md there, "The channel audio stream"). The upgrade is
/// done by hand over a <see cref="TcpListener"/>, which can bind port 0 and say which port it got.
/// </summary>
internal sealed class FakeSoundModem : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _accept;

    /// <summary>A stream on a port nothing else has.</summary>
    public FakeSoundModem(int rateHz = OnAir.SampleRate, double? dialHz = null, int port = 0)
    {
        RateHz = rateHz;
        DialHz = dialHz;
        Start(port);
    }

    /// <summary>The channel's rate the hello says.</summary>
    public int RateHz { get; }

    /// <summary>The dial the hello says, or null for "not known".</summary>
    public double? DialHz { get; }

    /// <summary>The first message instead of the hello, as a pdn-soundmodem from before the stream sends its page's config.</summary>
    public string? Hello { get; init; }

    /// <summary>The port it listens on.</summary>
    public int Port { get; private set; }

    /// <summary>Each connection, once its hello has gone.</summary>
    public System.Threading.Channels.Channel<Connection> Connections { get; } = System.Threading.Channels.Channel.CreateUnbounded<Connection>();

    /// <summary>A port free now, for a test that starts the receiver before pdn-soundmodem.</summary>
    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync(_listener);
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var stream = client.GetStream();
        // The request, a byte at a time, so nothing after it is read too.
        var request = new StringBuilder();
        var one = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0)
            {
                client.Dispose();
                return;
            }
            request.Append((char)one[0]);
        }
        string text = request.ToString();
        string key = text.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))["Sec-WebSocket-Key:".Length..].Trim();
        // RFC 6455's own handshake, which is SHA-1 by definition: nothing here is secured by it.
#pragma warning disable CA5350
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
        var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        string hello = Hello ?? System.Text.Json.JsonSerializer.Serialize(new { type = "hello", rateHz = RateHz, dialHz = DialHz });
        await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, _stop.Token);
        var connection = new Connection(socket, client, text);
        connection.Listen(_stop.Token);
        Connections.Writer.TryWrite(connection);
    }

    /// <summary>The next connection, failing the test if none comes within <paramref name="within"/> (10 s).</summary>
    public async Task<Connection> NextAsync(TimeSpan? within = null) =>
        await Connections.Reader.ReadAsync().AsTask().WaitAsync(within ?? TimeSpan.FromSeconds(10));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener?.Stop();
        if (_accept is not null)
        {
            await _accept;
        }
        _stop.Dispose();
    }

    /// <summary>One reader connected to the stream.</summary>
    internal sealed class Connection(WebSocket socket, TcpClient client, string request) : IDisposable
    {
        /// <summary>The HTTP upgrade request as it came, headers and all.</summary>
        public string Request { get; } = request;

        /// <summary>The text messages the reader sent: its identify.</summary>
        public System.Threading.Channels.Channel<string> Said { get; } = System.Threading.Channels.Channel.CreateUnbounded<string>();

        /// <summary>Completes when the reader has gone.</summary>
        public Task Gone => _gone.Task;

        private readonly TaskCompletionSource _gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _sending = new(1, 1);

        internal void Listen(CancellationToken cancellation) => _ = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer, cancellation);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    if (received.MessageType == WebSocketMessageType.Text)
                    {
                        Said.Writer.TryWrite(Encoding.UTF8.GetString(buffer, 0, received.Count));
                    }
                }
            }
            catch (Exception)
            {
            }
            _gone.TrySetResult();
        }, CancellationToken.None);

        /// <summary>One block as pdn-soundmodem sends it.</summary>
        public async Task SendAsync(ulong index, float[] samples, bool transmitted = false, bool gap = false)
        {
            var message = new byte[16 + (4 * samples.Length)];
            message[0] = 1;
            message[1] = (byte)((transmitted ? 1 : 0) | (gap ? 2 : 0));
            BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(4, 8), index);
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(12, 4), samples.Length);
            for (int i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(message.AsSpan(16 + (4 * i), 4), transmitted ? 0 : samples[i]);
            }
            await _sending.WaitAsync();
            try
            {
                await socket.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            finally
            {
                _sending.Release();
            }
        }

        public void Dispose()
        {
            client.Dispose();
            _sending.Dispose();
        }

        /// <summary>Ends the connection the way pdn-soundmodem stopping does: a close, then the socket gone.</summary>
        public async Task CloseAsync()
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "stopping", CancellationToken.None);
            }
            catch (Exception)
            {
            }
            client.Dispose();
        }
    }
}
