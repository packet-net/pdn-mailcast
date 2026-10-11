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
/// The same port serves a fake receive window, <c>/rig-window</c> (issue #78): a rig on
/// <see cref="RigDialHz"/>, one window at a time, refusals on demand, every request kept.
/// </summary>
internal sealed class FakeSoundModem : IAsyncDisposable
{
    // Every test that opens the source talks to one of these. On a loaded CI runner (5 minutes for
    // a suite that usually takes 1.5) the fake's hello can be later than the receiver's 10 s, which
    // is then a refusal the test never meant to test; nothing here waits for it to run out.
    static FakeSoundModem() => SoundModemAudioInput.OpenTimeout = TimeSpan.FromSeconds(30);

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

    /// <summary>The dial the hello says, or null for "not known"; a change applies to the next connection.</summary>
    public double? DialHz { get; set; }

    /// <summary>The first message instead of the hello, as a pdn-soundmodem from before the stream sends its page's config; a change applies to the next connection.</summary>
    public string? Hello { get; set; }

    /// <summary>The hello waits for this, as a pdn-soundmodem slow to answer does; a change applies to the next connection.</summary>
    public Task HelloAfter { get; set; } = Task.CompletedTask;

    /// <summary>Drops the connection straight after the upgrade, before any hello, as a pdn-soundmodem stopping just then does.</summary>
    public bool DropBeforeHello { get; init; }

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
        if (!text.Contains("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
        {
            await ServeWindowAsync(client, stream, text);
            return;
        }
        string key = text.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))["Sec-WebSocket-Key:".Length..].Trim();
        // RFC 6455's own handshake, which is SHA-1 by definition: nothing here is secured by it.
#pragma warning disable CA5350
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
        if (DropBeforeHello)
        {
            client.Dispose();
            return;
        }
        var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        await HelloAfter.WaitAsync(_stop.Token);
        string hello = Hello ?? System.Text.Json.JsonSerializer.Serialize(new { type = "hello", rateHz = RateHz, dialHz = DialHz });
        await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, _stop.Token);
        var connection = new Connection(socket, client, text);
        connection.Listen(_stop.Token);
        Connections.Writer.TryWrite(connection);
    }

    // ---------- the receive window ----------

    private readonly object _window = new();
    private long _restoreTo = 7_048_000;

    /// <summary>The rig's dial, as rig control last read it: where the packet station keeps it, until a window moves it.</summary>
    public long RigDialHz { get; set; } = 7_048_000;

    /// <summary>The open window's dial, or null.</summary>
    public long? WindowDialHz { get; private set; }

    /// <summary>How long the open window was last asked for, in seconds.</summary>
    public int WindowSeconds { get; private set; }

    /// <summary>False for a station with no <c>rig</c> section: every window request is a 404.</summary>
    public bool HasRig { get; set; } = true;

    /// <summary>When set, a request is held without an answer until this is cleared, as a stuck pdn-soundmodem would.</summary>
    public bool Hang { get; set; }

    /// <summary>When set, a request for a window is refused with a 409 and this reason, as pdn-soundmodem does while a lease is held.</summary>
    public string? RefuseWith { get; set; }

    /// <summary>One request to the receive window, as it came.</summary>
    internal sealed record WindowRequest(string Method, string Path, string Headers, string Body);

    /// <summary>Every request to the receive window, in order.</summary>
    public System.Threading.Channels.Channel<WindowRequest> WindowRequests { get; } = System.Threading.Channels.Channel.CreateUnbounded<WindowRequest>();

    /// <summary>Every request to the receive window so far, in order.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<WindowRequest> WindowLog { get; } = new();

    /// <summary>The window ends by itself (it ran out, or something released it) and the rig goes back to <paramref name="backTo"/>.</summary>
    public void DropWindow(long backTo = 7_048_000)
    {
        lock (_window)
        {
            WindowDialHz = null;
            RigDialHz = backTo;
        }
    }

    private async Task ServeWindowAsync(TcpClient client, NetworkStream stream, string head)
    {
        using var _ = client;
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        string method = first[0], path = first.Length > 1 ? first[1] : "/";
        int length = lines.Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
        var body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(read), _stop.Token);
            if (n == 0)
            {
                return;
            }
            read += n;
        }
        string bodyText = Encoding.UTF8.GetString(body);
        var request = new WindowRequest(method, path, head, bodyText);
        WindowLog.Enqueue(request);
        WindowRequests.Writer.TryWrite(request);
        if (Hang)
        {
            // Answered never: the client gives up first.
            while (Hang && !_stop.IsCancellationRequested)
            {
                await Task.Delay(50);
            }
            return;
        }

        int status;
        System.Text.Json.Nodes.JsonObject answer;
        lock (_window)
        {
            (status, answer) = path != "/rig-window" || !HasRig ? (404, new System.Text.Json.Nodes.JsonObject { ["refused"] = "no rig" }) : Answer(method, bodyText);
        }
        byte[] payload = Encoding.UTF8.GetBytes(answer.ToJsonString());
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "No")}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
        await stream.WriteAsync(payload, _stop.Token);
    }

    private (int, System.Text.Json.Nodes.JsonObject) Answer(string method, string body)
    {
        if (method == "GET")
        {
            return (200, State());
        }
        var asked = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        if (asked["release"]?.GetValue<bool>() == true)
        {
            bool released = WindowDialHz is not null;
            WindowDialHz = null;
            if (released)
            {
                RigDialHz = _restoreTo;
            }
            var done = State();
            done["released"] = released;
            return (200, done);
        }
        if (RefuseWith is { } why)
        {
            var refused = State();
            refused["refused"] = why;
            return (409, refused);
        }
        bool renewed = WindowDialHz is not null;
        if (!renewed)
        {
            _restoreTo = RigDialHz;
        }
        WindowDialHz = asked["dialHz"]!.GetValue<long>();
        WindowSeconds = Math.Min(300, asked["seconds"]!.GetValue<int>());
        RigDialHz = WindowDialHz.Value;
        var tuned = State();
        tuned["renewed"] = renewed;
        tuned["seconds"] = WindowSeconds;
        tuned["capped"] = asked["seconds"]!.GetValue<int>() > 300;
        return (200, tuned);
    }

    private System.Text.Json.Nodes.JsonObject State() => new()
    {
        ["connected"] = true,
        ["dialHz"] = RigDialHz,
        ["mode"] = "PKTUSB",
        ["passbandHz"] = 3000,
        ["transmitHeld"] = WindowDialHz is not null,
        ["window"] = WindowDialHz is { } dial
            ? new System.Text.Json.Nodes.JsonObject { ["owner"] = "a receive window", ["dialHz"] = dial, ["mode"] = "PKTUSB", ["passbandHz"] = 3250, ["expires"] = "2026-10-05T12:05:00Z" }
            : null,
        ["problem"] = null,
    };

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
