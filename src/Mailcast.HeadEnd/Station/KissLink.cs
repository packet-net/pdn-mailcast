using System.Net.Sockets;
using System.Threading.Channels;

namespace Mailcast.HeadEnd.Station;

/// <summary>The head end's KISS connection to the broadcast modem.</summary>
public interface IKissLink : IAsyncDisposable
{
    /// <summary>
    /// The id of each ACKMODE frame the modem has finished with, in the order the modem says so.
    /// pdn-soundmodem acknowledges a frame once its audio has been handed to the sound card, and
    /// every frame of a packed burst at once when the burst has been. A frame the station refuses
    /// is never acknowledged.
    /// </summary>
    ChannelReader<ushort> Acks { get; }

    /// <summary>Queues one AX.25 frame for transmission as ACKMODE data with this id.</summary>
    Task SendAsync(ushort id, ReadOnlyMemory<byte> ax25, CancellationToken cancellation);

    /// <summary>
    /// Sends a KISS SETHW frame (command 6) and waits up to <paramref name="wait"/> for the modem to
    /// echo it back, which pdn-soundmodem does once it has applied it. False when no echo came: the
    /// modem refused it (pdn-soundmodem journals why) or does not know it.
    /// </summary>
    Task<bool> SetHardwareAsync(ReadOnlyMemory<byte> payload, TimeSpan wait, TimeProvider time, CancellationToken cancellation);
}

/// <summary>Opens a <see cref="IKissLink"/>.</summary>
public interface IKissConnector
{
    /// <summary>Connects, or throws.</summary>
    Task<IKissLink> ConnectAsync(CancellationToken cancellation);
}

/// <summary>KISS over TCP to pdn-soundmodem, on the broadcast modem's own port or the shared one.</summary>
public sealed class KissTcpConnector(string host, int port, int portNibble) : IKissConnector
{
    /// <inheritdoc />
    public async Task<IKissLink> ConnectAsync(CancellationToken cancellation)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellation).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new KissTcpLink(client, portNibble);
    }
}

/// <summary>One KISS TCP connection.</summary>
public sealed class KissTcpLink : IKissLink
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly int _portNibble;
    private readonly Channel<ushort> _acks = Channel.CreateUnbounded<ushort>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Channel<byte[]> _setHardware = Channel.CreateUnbounded<byte[]>();

    internal KissTcpLink(TcpClient client, int portNibble)
    {
        _client = client;
        _stream = client.GetStream();
        _portNibble = portNibble;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <inheritdoc />
    public ChannelReader<ushort> Acks => _acks.Reader;

    /// <inheritdoc />
    public async Task SendAsync(ushort id, ReadOnlyMemory<byte> ax25, CancellationToken cancellation)
    {
        byte[] frame = Kiss.EncodeAckMode(_portNibble, id, ax25.Span);
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellation).ConfigureAwait(false);
            await _stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetHardwareAsync(ReadOnlyMemory<byte> payload, TimeSpan wait, TimeProvider time, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(time);
        while (_setHardware.Reader.TryRead(out _))
        {
            // An echo left from before is not the answer to this one.
        }
        byte[] frame = Kiss.Encode(_portNibble, Kiss.SetHardwareCommand, payload.Span);
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellation).ConfigureAwait(false);
            await _stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
        using var timeout = new CancellationTokenSource(wait, time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        try
        {
            while (true)
            {
                byte[] echo = await _setHardware.Reader.ReadAsync(either.Token).ConfigureAwait(false);
                if (echo.AsSpan().SequenceEqual(payload.Span))
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    private async Task ReadLoopAsync()
    {
        var decoder = new KissDecoder();
        var buffer = new byte[4096];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                foreach (var frame in decoder.Feed(buffer.AsSpan(0, n)))
                {
                    // An acknowledgement is the id alone, and a SETHW echo confirms a waveform
                    // change; anything else on the port (received frames) is not ours to read.
                    if (frame.Command == Kiss.AckModeCommand && frame.Payload.Length == 2)
                    {
                        _acks.Writer.TryWrite((ushort)(frame.Payload[0] | (frame.Payload[1] << 8)));
                    }
                    else if (frame.Command == Kiss.SetHardwareCommand)
                    {
                        _setHardware.Writer.TryWrite(frame.Payload);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The connection ended; the slot notices when its acknowledgements stop.
        }
        finally
        {
            _acks.Writer.TryComplete();
            _setHardware.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _client.Dispose();
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
        _writeGate.Dispose();
    }
}
