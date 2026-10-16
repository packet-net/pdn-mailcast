using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// SDRconnect's WebSocket API, in-process: a WebSocket on loopback, as SDRplay's "SDRconnect
/// WebSocket API" document (1.0.3) describes it. It answers <c>get_property</c> from one table
/// shared by every connection (as one SDRconnect is), takes <c>set_property</c> and reports each
/// change back, and once <c>iq_stream_enable</c> is on streams IQ (binary type 2, signed 16-bit
/// I, Q pairs) of one tone at <see cref="ToneHz"/>, in real time, against whatever LO it has then.
/// It keeps its own sample rate, as a real one may: the client takes the rate it reports. The
/// upgrade is done by hand over a <see cref="TcpListener"/>, as <see cref="FakeSoundModem"/> does.
/// </summary>
internal sealed class FakeSdrConnect : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _listener;
    private readonly Task _accept;
    private readonly object _gate = new();
    private readonly List<TcpClient> _clients = [];
    private int _connections;

    /// <summary>An SDRconnect on a port nothing else has, running at <paramref name="rate"/> S/s.</summary>
    public FakeSdrConnect(int rate = 250_000, bool canControl = true)
    {
        Rate = rate;
        Properties = new(StringComparer.Ordinal)
        {
            ["api_version"] = "1.0.3",
            ["active_device"] = "RSP1A",
            ["valid_devices"] = "RSP1A",
            ["can_control"] = canControl ? "true" : "false",
            ["started"] = "true",
            ["device_sample_rate"] = rate.ToString(CultureInfo.InvariantCulture),
            ["device_center_frequency"] = "14000000",
            ["valid_antennas"] = "Antenna A",
            ["active_antenna"] = "Antenna A",
            ["lna_state"] = "3",
            ["lna_state_min"] = "0",
            ["lna_state_max"] = "9",
        };
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync();
    }

    /// <summary>The port it listens on.</summary>
    public int Port { get; }

    /// <summary>The receiver's <c>audio</c> setting for it.</summary>
    public string Audio => $"sdrconnect:127.0.0.1:{Port}";

    /// <summary>The rate it streams at, whatever it is asked for.</summary>
    public int Rate { get; }

    /// <summary>Where the tone is on the air, Hz; 0 for no tone (silence).</summary>
    public double ToneHz { get; set; }

    /// <summary>The tone's amplitude, as a fraction of full scale.</summary>
    public double ToneLevel { get; set; } = 0.1;

    /// <summary>The property table, every connection's.</summary>
    public Dictionary<string, string> Properties { get; }

    /// <summary>Every <c>set_property</c> any client sent, in order.</summary>
    public ConcurrentQueue<(string Property, string Value)> Sets { get; } = new();

    /// <summary>Connections taken.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>A property's value now.</summary>
    public string? Value(string property)
    {
        lock (_gate)
        {
            return Properties.GetValueOrDefault(property);
        }
    }

    /// <summary>
    /// While set, a new connection is ended as soon as it is taken, before the WebSocket upgrade,
    /// as SDRconnect closed (or its WebSocket Server off) would leave it.
    /// </summary>
    public bool Closed
    {
        get => Volatile.Read(ref _closed);
        set => Volatile.Write(ref _closed, value);
    }

    private bool _closed;

    /// <summary>Ends every connection abruptly, as SDRconnect closing or a pulled cable would.</summary>
    public void DropAll()
    {
        lock (_gate)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }
            _clients.Clear();
        }
    }

    /// <summary>Waits for <paramref name="condition"/>, up to 30 s.</summary>
    public static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException($"never saw {what}");
            }
            await Task.Delay(20);
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            if (Closed)
            {
                client.Dispose();
                continue;
            }
            lock (_gate)
            {
                _clients.Add(client);
            }
            Interlocked.Increment(ref _connections);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var request = new StringBuilder();
            var one = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0)
                {
                    return;
                }
                request.Append((char)one[0]);
            }
            string key = request.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))["Sec-WebSocket-Key:".Length..].Trim();
            // RFC 6455's own handshake, which is SHA-1 by definition: nothing here is secured by it.
#pragma warning disable CA5350
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
            using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
            var send = new SemaphoreSlim(1, 1);
            using var streaming = new CancellationTokenSource();
            Task? iq = null;
            var buffer = new byte[65536];
            while (!_stop.IsCancellationRequested)
            {
                int length = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), _stop.Token);
                    length += result.Count;
                }
                while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
                string type = json.RootElement.GetProperty("event_type").GetString() ?? "";
                string property = json.RootElement.TryGetProperty("property", out var p) ? p.GetString() ?? "" : "";
                string value = json.RootElement.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                switch (type)
                {
                    case "get_property" when Value(property) is { } answer:
                        await SendTextAsync(socket, send, "get_property_response", property, answer);
                        break;
                    case "set_property":
                        Sets.Enqueue((property, value));
                        if (property == "device_sample_rate")
                        {
                            break; // it keeps its own rate, and says nothing about a set that changed nothing
                        }
                        bool changed;
                        lock (_gate)
                        {
                            changed = Properties.GetValueOrDefault(property) != value;
                            Properties[property] = value;
                        }
                        if (changed)
                        {
                            await SendTextAsync(socket, send, "property_changed", property, value);
                        }
                        break;
                    case "iq_stream_enable" when value == "true" && iq is null:
                        iq = StreamIqAsync(socket, send, streaming.Token);
                        break;
                }
            }
            await streaming.CancelAsync();
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException or JsonException or InvalidOperationException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>The tone as IQ, 20 ms at a time against the clock, as the radio delivers it.</summary>
    private async Task StreamIqAsync(WebSocket socket, SemaphoreSlim send, CancellationToken cancellation)
    {
        const int chunkMs = 20;
        int frames = Rate * chunkMs / 1000;
        var message = new byte[2 + (frames * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(message, 2);
        long n = 0;
        double phase = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!cancellation.IsCancellationRequested && !_stop.IsCancellationRequested)
            {
                double centre = double.Parse(Value("device_center_frequency") ?? "0", CultureInfo.InvariantCulture);
                double step = ToneHz == 0 ? 0 : 2 * Math.PI * (ToneHz - centre) / Rate;
                double level = ToneHz == 0 ? 0 : ToneLevel * 32767;
                for (int k = 0; k < frames; k++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(message.AsSpan(2 + (4 * k)), (short)Math.Round(level * Math.Cos(phase)));
                    BinaryPrimitives.WriteInt16LittleEndian(message.AsSpan(4 + (4 * k)), (short)Math.Round(level * Math.Sin(phase)));
                    phase = (phase + step) % (2 * Math.PI);
                }
                n += frames;
                await send.WaitAsync(cancellation);
                try
                {
                    await socket.SendAsync(message, WebSocketMessageType.Binary, true, cancellation);
                }
                finally
                {
                    send.Release();
                }
                long due = n * 1000 / Rate;
                long ahead = due - clock.ElapsedMilliseconds;
                if (ahead > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(ahead), cancellation);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task SendTextAsync(WebSocket socket, SemaphoreSlim send, string eventType, string property, string value)
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["event_type"] = eventType,
            ["property"] = property,
            ["device"] = "primary",
            ["value"] = value,
        });
        await send.WaitAsync(_stop.Token);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _stop.Token);
        }
        finally
        {
            send.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropAll();
        try
        {
            await _accept;
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}
