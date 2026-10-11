using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using M0LTE.Dsp;
using M0LTE.Radio.Audio;

namespace Mailcast.Receiver;

/// <summary>
/// The channel audio stream of a pdn-soundmodem on this machine (pdn-soundmodem 0.88.0 and
/// later, its <c>/channel-audio</c> WebSocket on the station page port), as an audio input at the
/// pipeline's 48 kHz.
/// </summary>
/// <remarks>
/// <para><b>The stream.</b> pdn-soundmodem sends one text message first,
/// <c>{"type":"hello","rateHz":N,"dialHz":N or null}</c>: its channel's own rate and, when it
/// knows it, its radio's dial. Then binary blocks of about 100 ms: <c>[kind 1][flags 1][reserved
/// 2][sample index u64 LE][count i32 LE][count float32 LE]</c>, flag bit 0 "transmitted" (silence
/// standing in for audio the station did not hear because it was keyed) and bit 1 "gap" (audio
/// was lost before this block). It is refused from anything but loopback and from anything that
/// sends an <c>Origin</c> header, so this connects to 127.0.0.1 and sends none; it needs no key.</para>
/// <para><b>Rate.</b> A channel slower than 48 kHz (12 kHz on a station with only 12 kHz
/// modems, 24 kHz, and so on) is upsampled by the whole factor, as a recording is. A rate that
/// is not a whole fraction of 48 kHz is refused when the stream is opened.</para>
/// <para><b>Gaps.</b> A block is marked as following a gap when pdn-soundmodem says so, when its
/// sample index does not follow on from the block before (belt and braces: pdn-soundmodem moves
/// the index past what a slow reader's queue dropped), and when this input's own queue had to
/// drop blocks because the audio thread fell behind. The pipeline resets the modem on each.</para>
/// <para><b>Reconnecting</b> is the supervisor's: a stream that ends raises <see cref="Lost"/>,
/// the pipeline ends, and the supervisor opens a new one, so a new connection always comes with
/// a new hello, a new dial check and a new modem.</para>
/// </remarks>
public sealed class SoundModemAudioInput : IAudioInput, IDisposable
{
    /// <summary>The stream's path on pdn-soundmodem's page port.</summary>
    public const string Path = "/channel-audio";

    /// <summary>The name this receiver gives pdn-soundmodem, which shows it on its own page.</summary>
    public const string ClientName = "pdn-mailcast-receiver";

    /// <summary>The longest connecting and waiting for the hello may take.</summary>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Blocks held for the audio thread before the oldest is dropped: 6.4 s at 100 ms a block,
    /// the same as pdn-soundmodem holds for each reader.
    /// </summary>
    internal const int QueueBlocks = 64;

    /// <summary>How long a read waits for a block before returning nothing, so the audio thread can look at its stop.</summary>
    private static readonly TimeSpan ReadWait = TimeSpan.FromMilliseconds(500);

    private const byte AudioKind = 0x01;
    private const byte FlagTransmitted = 0x01;
    private const byte FlagGap = 0x02;
    private const int HeaderBytes = 16;

    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Upsampler? _upsampler;
    private readonly int _factor;
    private readonly object _gate = new();
    private readonly Queue<Block> _blocks = new();
    private readonly SemaphoreSlim _available = new(0);
    private bool _dropped;
    private long _received;
    private Task? _receive;
    private int _lost;

    private SoundModemAudioInput(ClientWebSocket socket, int port, int channelRate, double? dialHz)
    {
        _socket = socket;
        Port = port;
        ChannelRate = channelRate;
        DialHz = dialHz;
        _factor = OnAir.SampleRate / channelRate;
        _upsampler = _factor > 1 ? new Upsampler(OnAir.SampleRate, _factor) : null;
    }

    /// <summary>One block, at 48 kHz, with what pdn-soundmodem (or this input) said about it.</summary>
    private sealed record Block(float[] Samples, bool Transmitted, bool Gap);

    /// <inheritdoc />
    public int SampleRate => OnAir.SampleRate;

    /// <summary>pdn-soundmodem's page port this reads from.</summary>
    public int Port { get; }

    /// <summary>The channel's own rate, as the hello gave it: 48000, or a whole fraction of it.</summary>
    public int ChannelRate { get; }

    /// <summary>The dial pdn-soundmodem says its radio is on, in Hz, or null when it does not know.</summary>
    public double? DialHz { get; }

    /// <summary>For tests: how many audio blocks have come in, whether or not the queue kept them.</summary>
    internal long BlocksReceived => Interlocked.Read(ref _received);

    /// <summary>Why the stream ended, once it has; null while it is open.</summary>
    public string? LostReason { get; private set; }

    /// <summary>Raised once, on the receiving task, when the stream ends by itself, with why.</summary>
    public event Action<string>? Lost;

    /// <summary>Where the stream is, for the log: 127.0.0.1:PORT.</summary>
    public string Where => Describe(Port);

    /// <summary>Where pdn-soundmodem on <paramref name="port"/> is, for the log.</summary>
    internal static string Describe(int port) => $"127.0.0.1:{port}";

    /// <summary>
    /// Connects to pdn-soundmodem on <paramref name="port"/> and reads its hello. Audio starts
    /// queueing at once; say what band is needed with <see cref="IdentifyAsync"/>.
    /// </summary>
    /// <exception cref="AudioSourceException">It could not be opened, or cannot be used; the message says why.</exception>
    public static async Task<SoundModemAudioInput> OpenAsync(int port, CancellationToken cancellation)
    {
        string where = Describe(port);
        var socket = new ClientWebSocket();
        // pdn-soundmodem refuses the stream through a proxy, and a proxy from the environment
        // would never be what is wanted for 127.0.0.1 anyway.
        socket.Options.Proxy = null;
        socket.Options.CollectHttpResponseDetails = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(OpenTimeout);
        try
        {
            try
            {
                await socket.ConnectAsync(new Uri($"ws://{where}{Path}"), timeout.Token).ConfigureAwait(false);
            }
            catch (WebSocketException e) when (socket.HttpStatusCode is HttpStatusCode.Forbidden)
            {
                throw new AudioSourceException($"pdn-soundmodem on {where} refused the channel audio stream (HTTP 403): it serves it only to a program on the same machine, connecting directly. {Ascii.Clean(e.Message)}");
            }
            catch (WebSocketException e) when (Refused(e))
            {
                throw new AudioSourceException($"pdn-soundmodem is not answering on {where} (connection refused): check it is running on this machine, with its page on port {port}");
            }
            catch (WebSocketException e)
            {
                throw new AudioSourceException($"cannot open pdn-soundmodem's channel audio stream on {where}: {Ascii.Clean(e.Message)}");
            }

            var (rate, dial) = await ReadHelloAsync(socket, where, timeout.Token).ConfigureAwait(false);
            if (rate > OnAir.SampleRate || OnAir.SampleRate % rate != 0)
            {
                throw new AudioSourceException(
                    $"pdn-soundmodem's channel on {where} runs at {rate} Hz, which is not a whole fraction of {OnAir.SampleRate} Hz (48000, 24000, 16000, 12000 or 8000), so this receiver cannot use its audio");
            }
            var input = new SoundModemAudioInput(socket, port, rate, dial);
            input._receive = input.ReceiveAsync();
            return input;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            socket.Dispose();
            throw new AudioSourceException($"pdn-soundmodem on {where} did not answer within {OpenTimeout.TotalSeconds:F0} s");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool Refused(Exception e)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The first message, which must be the hello. A pdn-soundmodem from before the stream
    /// serves its page's own WebSocket on any path, which starts with its config instead.
    /// </summary>
    private static async Task<(int Rate, double? Dial)> ReadHelloAsync(ClientWebSocket socket, string where, CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024];
        int filled = 0;
        while (true)
        {
            if (filled == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            var received = await socket.ReceiveAsync(buffer.AsMemory(filled), cancellation).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new AudioSourceException($"pdn-soundmodem on {where} closed the channel audio stream before saying hello");
            }
            filled += received.Count;
            if (received.EndOfMessage)
            {
                break;
            }
        }
        const string tooOld = "has no channel audio stream: this receiver needs pdn-soundmodem 0.88.0 or later";
        try
        {
            using var hello = JsonDocument.Parse(buffer.AsMemory(0, filled));
            var root = hello.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "hello")
            {
                throw new AudioSourceException($"pdn-soundmodem on {where} {tooOld}");
            }
            if (!root.TryGetProperty("rateHz", out var rate) || rate.ValueKind != JsonValueKind.Number || !rate.TryGetInt32(out int rateHz) || rateHz <= 0)
            {
                throw new AudioSourceException($"pdn-soundmodem on {where} said hello without its channel's rate");
            }
            double? dialHz = root.TryGetProperty("dialHz", out var dial) && dial.ValueKind == JsonValueKind.Number && dial.GetDouble() > 0 ? dial.GetDouble() : null;
            return (rateHz, dialHz);
        }
        catch (JsonException)
        {
            throw new AudioSourceException($"pdn-soundmodem on {where} {tooOld}");
        }
    }

    /// <summary>
    /// Tells pdn-soundmodem who this is: <see cref="ClientName"/>, this receiver's own page port
    /// (its page links to it), and the band of its channel's audio the signal needs, in Hz, which
    /// a headless FlexRadio opens its slice filter to cover.
    /// </summary>
    public async Task IdentifyAsync(int pagePort, double lowHz, double highHz, CancellationToken cancellation)
    {
        byte[] message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = ClientName,
            pagePort,
            band = new { lowHz = (int)Math.Floor(lowHz), highHz = (int)Math.Ceiling(highHz) },
        });
        try
        {
            await _socket.SendAsync(message, WebSocketMessageType.Text, true, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
            // The stream has gone: the receive loop says so.
        }
    }

    /// <summary>
    /// Reads the stream until it ends, a block at a time into the queue for the audio thread.
    /// </summary>
    private async Task ReceiveAsync()
    {
        var buffer = new byte[HeaderBytes + (4 * OnAir.SampleRate / 10)];
        ulong? expected = null;
        string why;
        try
        {
            while (true)
            {
                int filled = 0;
                ValueWebSocketReceiveResult received;
                do
                {
                    if (filled == buffer.Length)
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }
                    received = await _socket.ReceiveAsync(buffer.AsMemory(filled), _stop.Token).ConfigureAwait(false);
                    filled += received.Count;
                }
                while (!received.EndOfMessage && received.MessageType != WebSocketMessageType.Close);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    why = $"pdn-soundmodem on {Where} closed the channel audio stream (it may be restarting)";
                    break;
                }
                if (received.MessageType != WebSocketMessageType.Binary || filled < HeaderBytes || buffer[0] != AudioKind)
                {
                    // Anything else pdn-soundmodem may one day send is not audio, and not ours.
                    continue;
                }
                byte flags = buffer[1];
                ulong index = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(4, 8));
                int count = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(12, 4));
                if (count < 0 || HeaderBytes + (4L * count) != filled)
                {
                    why = $"pdn-soundmodem on {Where} sent an audio block this receiver does not understand";
                    break;
                }
                bool gap = (flags & FlagGap) != 0 || (expected is { } next && next != index);
                expected = index + (ulong)count;
                Enqueue(Convert(buffer.AsSpan(HeaderBytes, 4 * count), count), (flags & FlagTransmitted) != 0, gap);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException)
        {
            why = $"the channel audio stream from pdn-soundmodem on {Where} broke (it may be restarting): {Ascii.Clean(e.Message)}";
        }
        End(why);
    }

    /// <summary>A block's samples at the channel's rate, as 48 kHz.</summary>
    private float[] Convert(ReadOnlySpan<byte> bytes, int count)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4, 4));
        }
        if (_upsampler is null)
        {
            return samples;
        }
        var output = new float[_upsampler.OutputLength(count)];
        _upsampler.Process(samples, output);
        return output;
    }

    private void Enqueue(float[] samples, bool transmitted, bool gap)
    {
        lock (_gate)
        {
            if (_blocks.Count == QueueBlocks)
            {
                _blocks.Dequeue();
                _dropped = true;
            }
            else
            {
                _available.Release();
            }
            _blocks.Enqueue(new Block(samples, transmitted, gap));
        }
        Interlocked.Increment(ref _received);
    }

    private void End(string why)
    {
        if (Interlocked.Exchange(ref _lost, 1) != 0)
        {
            return;
        }
        LostReason = why;
        // Wakes a read waiting for a block that is not coming.
        _available.Release();
        Lost?.Invoke(why);
    }

    /// <inheritdoc />
    public int Read(Span<float> destination) => ReadBlock(destination, out _, out _);

    /// <summary>
    /// The next block, waiting up to half a second for one: its sample count (0 if none came, or
    /// the stream has ended), whether it is silence standing in for a keyup, and whether audio was
    /// lost before it. <paramref name="destination"/> must hold 100 ms at 48 kHz.
    /// </summary>
    public int ReadBlock(Span<float> destination, out bool transmitted, out bool gap)
    {
        transmitted = false;
        gap = false;
        if (!_available.Wait(ReadWait))
        {
            return 0;
        }
        Block block;
        lock (_gate)
        {
            if (!_blocks.TryDequeue(out block!))
            {
                return 0;
            }
            gap = block.Gap || _dropped;
            _dropped = false;
        }
        if (block.Samples.Length > destination.Length)
        {
            throw new InvalidOperationException($"a {block.Samples.Length}-sample block does not fit a {destination.Length}-sample read");
        }
        block.Samples.CopyTo(destination);
        transmitted = block.Transmitted;
        return block.Samples.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _lost, 1) == 0)
        {
            LostReason = "closed";
        }
        _stop.Cancel();
        try
        {
            _receive?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _socket.Abort();
        _socket.Dispose();
        _stop.Dispose();
        _available.Dispose();
    }
}
