using System.Globalization;
using M0LTE.Flex;

namespace Mailcast.HeadEnd.Flex;

/// <summary>The Flex's frequency reference, as the head end reports it.</summary>
/// <param name="Description">The radio's state in a few words, such as "GPSDO locked".</param>
/// <param name="GpsLocked">True when the radio runs on its GPS-disciplined oscillator and it is locked; null when unknown.</param>
public sealed record ReferenceReading(string Description, bool? GpsLocked)
{
    /// <summary>Nothing known.</summary>
    public static ReferenceReading Unknown { get; } = new("unknown", null);

    /// <summary>The line the journal carries.</summary>
    public string Summary => GpsLocked switch
    {
        true => $"GPS locked ({Description})",
        false => $"NOT GPS locked ({Description})",
        null => $"unknown ({Description})",
    };
}

/// <summary>
/// Reads the Flex's PA temperature and frequency reference. Read-only by construction: it never
/// asks for a slice, a DAX stream, the GUI role or the transmitter, so it cannot disturb the
/// pdn-soundmodem that owns the radio.
/// </summary>
public interface IFlexMonitor : IAsyncDisposable
{
    /// <summary>Connects. False, with <see cref="Problem"/> set, when the radio cannot be reached.</summary>
    Task<bool> ConnectAsync(CancellationToken cancellation);

    /// <summary>Why the last connect failed, or why the session ended.</summary>
    string? Problem { get; }

    /// <summary>Whether the session is up.</summary>
    bool Connected { get; }

    /// <summary>The reference state now.</summary>
    ReferenceReading Reference { get; }

    /// <summary>The latest PA temperature in degrees C, or null if the radio has not sent one.</summary>
    double? PaTemperatureC { get; }
}

/// <summary>
/// <see cref="IFlexMonitor"/> on the M0LTE.Flex package, as a second, non-GUI API client.
/// </summary>
/// <remarks>
/// <para>The commands it sends are all reads or subscriptions that belong to this client alone:
/// <c>client udpport</c> (where this client's own meter packets go; a local ephemeral port, so
/// nothing clashes with pdn-soundmodem's sockets on the same machine), <c>sub radio all</c> (for the
/// <c>radio oscillator</c> status that carries the reference state), <c>meter list</c>,
/// <c>sub meter all</c> and <c>sub meter N</c>, and <c>unsub meter all</c> on the way out. It never
/// sends <c>client gui</c>, <c>client bind</c>, any <c>slice</c>, <c>stream</c>, <c>transmit</c>
/// or <c>xmit</c> command.</para>
/// </remarks>
public sealed class FlexMonitor : IFlexMonitor
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeProvider _time;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _referenceWait;
    private FlexClient? _client;
    private FlexMeters? _meters;

    /// <summary>A monitor for the radio at <paramref name="host"/>.</summary>
    public FlexMonitor(string host, int port = 4992, TimeSpan? connectTimeout = null, TimeProvider? time = null, TimeSpan? referenceWait = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = host;
        _port = port;
        _time = time ?? TimeProvider.System;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        _referenceWait = referenceWait ?? TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc />
    public string? Problem { get; private set; }

    /// <inheritdoc />
    public bool Connected => _client?.IsConnected == true;

    /// <inheritdoc />
    public ReferenceReading Reference => _client is null ? ReferenceReading.Unknown : Read(_client.Reference);

    /// <inheritdoc />
    public double? PaTemperatureC =>
        _meters is not null && _meters.TryGet("PATEMP", out FlexMeterReading reading) ? reading.Value : null;

    /// <inheritdoc />
    public async Task<bool> ConnectAsync(CancellationToken cancellation)
    {
        await CloseAsync().ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(_connectTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        try
        {
            _client = await FlexClient.ConnectAsync(_host, _port, cancellation: linked.Token).ConfigureAwait(false);
            _client.Disconnected += () => Problem = "the radio closed the API session";
            await _client.InitUdpAsync(linked.Token).ConfigureAwait(false);

            var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _client.ReferenceChanged += _ => heard.TrySetResult();
            _client.SendCommandNoWait("sub radio all");
            _meters = await FlexMeters.SubscribeAsync(_client, linked.Token).ConfigureAwait(false);

            // The reference arrives as status in answer to the subscription; give it a moment, but
            // an answer that never comes is "unknown", not a failure.
            if (_client.Reference == FlexReferenceStatus.Unknown && _referenceWait > TimeSpan.Zero)
            {
                await Task.WhenAny(heard.Task, Task.Delay(_referenceWait, _time, linked.Token)).ConfigureAwait(false);
            }
            Problem = null;
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            Problem = e is OperationCanceledException
                ? $"no answer from {_host}:{_port.ToString(CultureInfo.InvariantCulture)} within {_connectTimeout.TotalSeconds:0} s"
                : $"{_host}:{_port.ToString(CultureInfo.InvariantCulture)}: {e.Message}";
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
    }

    internal static ReferenceReading Read(FlexReferenceStatus status)
    {
        if (status.State == FlexOscillatorSource.Unknown && status.Setting == FlexOscillatorSource.Unknown)
        {
            return ReferenceReading.Unknown;
        }
        bool gps = status.State == FlexOscillatorSource.Gpsdo && status.Locked;
        return new ReferenceReading(status.Describe(), gps);
    }

    private async Task CloseAsync()
    {
        _meters?.Dispose();
        _meters = null;
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
