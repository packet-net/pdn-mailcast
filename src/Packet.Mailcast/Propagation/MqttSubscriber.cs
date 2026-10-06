using System.Buffers.Binary;
using System.Text;

namespace Packet.Mailcast.Propagation;

/// <summary>
/// Just enough MQTT 3.1.1 to subscribe at QoS 0 and read what arrives: CONNECT with a clean
/// session, one SUBSCRIBE, PUBLISH in, PINGREQ to keep the connection alive, DISCONNECT. Nothing
/// is ever acknowledged or stored by the broker for us, so a slow or absent reader costs the
/// broker nothing. One instance is one connection; it is not safe to share between threads,
/// except that <see cref="SendPingAsync"/> may be called while <see cref="ReadAsync"/> waits.
/// </summary>
internal sealed class MqttSubscriber(Stream stream) : IAsyncDisposable
{
    /// <summary>The longest packet taken: a spot is about 250 octets with its topic, so anything this size is not one.</summary>
    public const int MostPacket = 4 * 1024;

    private readonly SemaphoreSlim _writing = new(1, 1);

    /// <summary>A packet that arrived: a PUBLISH's topic and payload, or another type with neither.</summary>
    public readonly record struct Packet(byte Type, string? Topic, ReadOnlyMemory<byte> Payload);

    /// <summary>Packet types used here.</summary>
    public const byte Connack = 2, Publish = 3, Suback = 9, Pingresp = 13;

    /// <summary>Sends CONNECT (clean session, no will, no login) and waits for CONNACK. Throws <see cref="IOException"/> if the broker refuses.</summary>
    public async Task ConnectAsync(string clientId, TimeSpan keepAlive, CancellationToken cancellation)
    {
        var body = new List<byte>();
        AddString(body, "MQTT");
        body.Add(4); // protocol level: 3.1.1
        body.Add(0x02); // clean session
        ushort seconds = (ushort)Math.Clamp(keepAlive.TotalSeconds, 0, ushort.MaxValue);
        body.Add((byte)(seconds >> 8));
        body.Add((byte)seconds);
        AddString(body, clientId);
        await WriteAsync(0x10, body, cancellation).ConfigureAwait(false);
        var answer = await ReadAsync(cancellation).ConfigureAwait(false);
        if (answer.Type != Connack || answer.Payload.Length < 2)
        {
            throw new IOException($"expected CONNACK, got packet type {answer.Type}");
        }
        byte code = answer.Payload.Span[1];
        if (code != 0)
        {
            throw new IOException(code switch
            {
                1 => "broker refused: protocol version",
                2 => "broker refused: client id",
                3 => "broker refused: server unavailable",
                4 or 5 => "broker refused: not authorised",
                _ => $"broker refused: code {code}",
            });
        }
    }

    /// <summary>Sends one SUBSCRIBE for all of <paramref name="filters"/> at QoS 0 and waits for its SUBACK, skipping anything that comes first. Throws <see cref="IOException"/> if any filter is refused.</summary>
    public async Task SubscribeAsync(IReadOnlyList<string> filters, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var body = new List<byte> { 0, 1 }; // packet identifier 1
        foreach (string filter in filters)
        {
            AddString(body, filter);
            body.Add(0); // QoS 0
        }
        await WriteAsync(0x82, body, cancellation).ConfigureAwait(false);
        while (true)
        {
            var answer = await ReadAsync(cancellation).ConfigureAwait(false);
            if (answer.Type != Suback)
            {
                continue;
            }
            var codes = answer.Payload.Span[Math.Min(2, answer.Payload.Length)..];
            int refused = 0;
            foreach (byte c in codes)
            {
                refused += c == 0x80 ? 1 : 0;
            }
            if (refused > 0 || codes.Length != filters.Count)
            {
                throw new IOException($"broker refused {refused} of {filters.Count} subscriptions");
            }
            return;
        }
    }

    /// <summary>Sends PINGREQ.</summary>
    public Task SendPingAsync(CancellationToken cancellation) => WriteAsync(0xC0, [], cancellation);

    /// <summary>Sends DISCONNECT, quietly.</summary>
    public async Task DisconnectAsync(CancellationToken cancellation)
    {
        try
        {
            await WriteAsync(0xE0, [], cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // going anyway
        }
    }

    /// <summary>The next packet. Throws <see cref="EndOfStreamException"/> when the broker closes, <see cref="IOException"/> for a packet that is not MQTT or too long.</summary>
    public async Task<Packet> ReadAsync(CancellationToken cancellation)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, cancellation).ConfigureAwait(false);
        byte header = one[0];
        int length = 0;
        for (int i = 0, shift = 0; ; i++, shift += 7)
        {
            if (i == 4)
            {
                throw new IOException("bad remaining length");
            }
            await stream.ReadExactlyAsync(one, cancellation).ConfigureAwait(false);
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0)
            {
                break;
            }
        }
        if (length > MostPacket)
        {
            throw new IOException($"a {length}-octet packet, more than {MostPacket}");
        }
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellation).ConfigureAwait(false);
        byte type = (byte)(header >> 4);
        if (type != Publish)
        {
            return new Packet(type, null, body);
        }
        if (body.Length < 2)
        {
            throw new IOException("PUBLISH cut short");
        }
        int topicLength = BinaryPrimitives.ReadUInt16BigEndian(body);
        int qos = (header >> 1) & 0x03;
        int payloadAt = 2 + topicLength + (qos > 0 ? 2 : 0);
        if (payloadAt > body.Length)
        {
            throw new IOException("PUBLISH cut short");
        }
        string topic = Encoding.UTF8.GetString(body, 2, topicLength);
        return new Packet(type, topic, body.AsMemory(payloadAt));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        _writing.Dispose();
    }

    private async Task WriteAsync(byte header, List<byte> body, CancellationToken cancellation)
    {
        var packet = new List<byte>(body.Count + 5) { header };
        int length = body.Count;
        do
        {
            byte digit = (byte)(length % 128);
            length /= 128;
            packet.Add(length > 0 ? (byte)(digit | 0x80) : digit);
        }
        while (length > 0);
        packet.AddRange(body);
        await _writing.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(packet.ToArray(), cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    private static void AddString(List<byte> body, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        body.Add((byte)(bytes.Length >> 8));
        body.Add((byte)bytes.Length);
        body.AddRange(bytes);
    }
}
