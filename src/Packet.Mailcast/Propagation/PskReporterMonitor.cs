using System.Globalization;
using System.Net.Sockets;

namespace Packet.Mailcast.Propagation;

/// <summary>How the PSK Reporter feed is doing, for the head end's status.</summary>
/// <param name="Connected">Subscribed and hearing from the broker now.</param>
/// <param name="Since">When the current connection was made, or the last one lost.</param>
/// <param name="LastMessage">When the broker last sent a spot.</param>
/// <param name="SpotsHeld">Spots held for the window.</param>
/// <param name="Messages">Messages taken since the head end started.</param>
/// <param name="Reconnects">Connections made after the first.</param>
/// <param name="Problem">What went wrong last, while it is going wrong; null when connected.</param>
/// <param name="Dropped">Spots not kept because the most that are held already were.</param>
public sealed record PskFeedStatus(bool Connected, DateTimeOffset? Since, DateTimeOffset? LastMessage, int SpotsHeld, long Messages, int Reconnects, string? Problem, long Dropped = 0);

/// <summary>
/// Keeps the PSK Reporter reading up to date in the background: subscribes to PSK Reporter's
/// public MQTT feed for 40 and 80 m spots with both ends in the UK or Ireland, and holds the last
/// <see cref="PskEvaluator.Window"/> of them. <see cref="Current"/> never waits for the network,
/// and a feed that fails only ever makes the reading UNKNOWN.
/// </summary>
/// <remarks>
/// <para>QoS 0 with a clean session, so the broker keeps nothing for us. A lost connection is
/// made again after 5 s, then 10, 20 and so on up to <see cref="MostBackoff"/>, and from the start
/// again once a connection has lasted a minute and brought spots. No word from the broker for
/// one and a half keep-alives (a PINGREQ goes every half) counts as lost. Narrow topics keep it
/// to a couple of spots a second. At most <see cref="MostSpots"/> spots and about
/// <see cref="MostBytes"/> are held: when full, new spots are dropped until the once-a-minute
/// prune, which also trims to nine tenths of either, oldest first, so a flood costs O(1) a spot.</para>
/// </remarks>
public sealed class PskReporterMonitor
{
    /// <summary>PSK Reporter's public broker.</summary>
    public const string DefaultHost = "mqtt.pskreporter.info";

    /// <summary>Its plain MQTT port.</summary>
    public const int DefaultPort = 1883;

    /// <summary>The MQTT keep-alive asked for.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(60);

    /// <summary>How long connecting and subscribing may take.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The first wait after a failure; it doubles each time.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between attempts.</summary>
    public static readonly TimeSpan MostBackoff = TimeSpan.FromMinutes(5);

    /// <summary>A connection that lasts this long starts the backoff afresh.</summary>
    public static readonly TimeSpan Settled = TimeSpan.FromMinutes(1);

    /// <summary>A drop shorter than this does not make the reading UNKNOWN on its own.</summary>
    public static readonly TimeSpan Blip = TimeSpan.FromMinutes(2);

    /// <summary>The most spots held: far more than half an hour of UK and Irish 40 and 80 m.</summary>
    public const int MostSpots = 20_000;

    /// <summary>The most memory the held spots may take, roughly, in octets.</summary>
    public const long MostBytes = 4 * 1024 * 1024;

    /// <summary>How often the held spots are pruned.</summary>
    public static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly string _clientId;
    private readonly string _where;
    private readonly Lock _gate = new();
    private readonly List<PskSpot> _spots = [];
    private readonly HashSet<long> _seen = [];
    private readonly List<(DateTimeOffset From, DateTimeOffset To)> _up = [];
    private DateTimeOffset? _connectedSince;
    private DateTimeOffset? _downSince;
    private DateTimeOffset? _lastMessage;
    private DateTimeOffset _lastPrune;
    private long _messages;
    private int _connections;
    private string? _problem;
    private int _failures;
    private long _bytes;
    private long _dropped;
    private bool _full;
    private int _sessionSpots;
    private volatile bool _wantedFailing;

    /// <summary>A monitor that connects to <paramref name="host"/>, or with <paramref name="connect"/>, for tests.</summary>
    /// <param name="callsign">The head end's callsign, in the MQTT client id with a random suffix.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">Where it says it connected, lost the feed or got it back.</param>
    /// <param name="connect">Opens a connection to the broker; null uses TCP to <paramref name="host"/>.</param>
    /// <param name="host">The broker.</param>
    /// <param name="port">Its port.</param>
    public PskReporterMonitor(string callsign, TimeProvider time, Action<string>? log = null, Func<CancellationToken, Task<Stream>>? connect = null, string host = DefaultHost, int port = DefaultPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callsign);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? (_ => { });
        _where = string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
        _connect = connect ?? (cancellation => Tcp(host, port, cancellation));
        string call = new([.. callsign.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).Take(8)]);
        _clientId = string.Create(CultureInfo.InvariantCulture, $"mailcast-{call}-{Random.Shared.Next(0x10000):x4}");
        _lastPrune = time.GetUtcNow();
    }

    /// <summary>The MQTT client id: <c>mailcast-CALL-xxxx</c>, a new suffix each start.</summary>
    public string ClientId => _clientId;

    /// <summary>
    /// The reading as of now, from the spots held: <see cref="PskReading.None"/> until the feed
    /// has first come up, UNKNOWN while it is down. Never waits, never throws.
    /// </summary>
    public PskReading Current
    {
        get
        {
            DateTimeOffset now = _time.GetUtcNow();
            PskSpot[] spots;
            TimeSpan up;
            bool connected;
            lock (_gate)
            {
                if (_connections == 0)
                {
                    return PskReading.None;
                }
                spots = [.. _spots];
                up = UpDuring(now - PskEvaluator.Window, now);
                connected = _connectedSince is not null || (_downSince is { } down && now - down < Blip);
            }
            return PskEvaluator.Evaluate(spots, now, up, connected);
        }
    }

    /// <summary>Roughly what the held spots take, octets (see <see cref="Cost"/>).</summary>
    internal long HeldBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>How the feed is doing.</summary>
    public PskFeedStatus Feed
    {
        get
        {
            lock (_gate)
            {
                return new PskFeedStatus(_connectedSince is not null, _connectedSince ?? _downSince, _lastMessage, _spots.Count, _messages, Math.Max(0, _connections - 1), _connectedSince is null ? _problem : null, _dropped);
            }
        }
    }

    /// <summary>
    /// Stays subscribed while <paramref name="wanted"/> says a reading is wanted (near a slot, say),
    /// until cancelled, connecting again after any failure. Nothing it meets ends it but cancellation.
    /// </summary>
    public async Task RunAsync(Func<DateTimeOffset, bool>? wanted, CancellationToken cancellation)
    {
        bool Wanted()
        {
            try
            {
                bool want = wanted?.Invoke(_time.GetUtcNow()) ?? true;
                _wantedFailing = false;
                return want;
            }
#pragma warning disable CA1031 // observe only: a schedule that cannot be read keeps the feed, it never stops the loop
            catch (Exception e)
#pragma warning restore CA1031
            {
                if (!_wantedFailing)
                {
                    _wantedFailing = true;
                    _log($"pskreporter: cannot tell whether a slot is near ({Plain(e.Message)}); staying subscribed");
                }
                return true;
            }
        }
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (!Wanted())
                {
                    _failures = 0;
                    await Task.Delay(TimeSpan.FromMinutes(1), _time, cancellation).ConfigureAwait(false);
                    continue;
                }
                DateTimeOffset started = _time.GetUtcNow();
                string? problem = null;
                try
                {
                    if (await SessionAsync(Wanted, cancellation).ConfigureAwait(false))
                    {
                        _failures = 0;
                        _log("pskreporter: unsubscribed, no slot within the hour");
                        continue; // not wanted any more
                    }
                    problem = "the broker closed the connection";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // a reading is observe only: nothing here may stop the head end
                catch (Exception e)
#pragma warning restore CA1031
                {
                    problem = Plain(e is OperationCanceledException ? "no answer in time" : e.Message);
                }
                Down(problem);
                if (_time.GetUtcNow() - started >= Settled && _sessionSpots > 0)
                {
                    // A connection that lasted and brought spots: start the backoff afresh. One
                    // that took the subscription and then sent nothing does not count.
                    _failures = 0;
                }
                _failures++;
                TimeSpan wait = Backoff(_failures);
                if (_failures == 1)
                {
                    _log(string.Create(CultureInfo.InvariantCulture, $"pskreporter: {problem}; trying again in {wait.TotalSeconds:0} s, and less often while it fails"));
                }
                await Task.Delay(wait, _time, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>The wait after the <paramref name="failures"/>th failure in a row: 5 s, 10, 20, up to <see cref="MostBackoff"/>.</summary>
    public static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(FirstBackoff.TotalSeconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 20)), MostBackoff.TotalSeconds));

    /// <summary>Takes one message from the feed: kept if it is a spot this reading counts, new, and inside the window.</summary>
    internal bool Offer(ReadOnlySpan<byte> payload)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            _messages++;
            _lastMessage = now;
        }
        if (!PskFeed.TryParse(payload, out var spot))
        {
            return false;
        }
        Interlocked.Increment(ref _sessionSpots);
        return Add(spot);
    }

    /// <summary>Roughly what holding a spot costs, octets: the entry, its place in the set, and its two callsigns.</summary>
    internal static long Cost(PskSpot spot) => 96 + (24 + (2L * spot.Sender.Length)) + (24 + (2L * spot.Receiver.Length));

    /// <summary>Holds a spot, unless it is a repeat, outside the window, or there is no room until the next prune. O(1) but for that prune, once a minute.</summary>
    internal bool Add(PskSpot spot)
    {
        DateTimeOffset now = _time.GetUtcNow();
        bool startedDropping = false, kept = false;
        lock (_gate)
        {
            if (now - _lastPrune >= PruneEvery)
            {
                Prune(now);
            }
            if (spot.Time <= now - PskEvaluator.Window || spot.Time > now + PskEvaluator.MostAhead || _seen.Contains(spot.Sequence))
            {
                return false;
            }
            long cost = Cost(spot);
            if (_spots.Count >= MostSpots || _bytes + cost > MostBytes)
            {
                _dropped++;
                startedDropping = !_full;
                _full = true;
            }
            else
            {
                _seen.Add(spot.Sequence);
                _spots.Add(spot);
                _bytes += cost;
                kept = true;
            }
        }
        if (startedDropping)
        {
            _log(string.Create(CultureInfo.InvariantCulture, $"pskreporter: holding as many spots as it will ({MostSpots}, about {MostBytes / (1024 * 1024)} MB); new ones are dropped until older ones go"));
        }
        return kept;
    }

    /// <summary>Once a minute: spots out of the window go, then if still above nine tenths of the limits the oldest, in one pass.</summary>
    private void Prune(DateTimeOffset now)
    {
        _lastPrune = now;
        DateTimeOffset oldest = now - PskEvaluator.Window;
        _spots.RemoveAll(s => s.Time <= oldest);
        long bytes = _spots.Sum(Cost);
        if (_spots.Count > MostSpots * 9 / 10 || bytes > MostBytes * 9 / 10)
        {
            _spots.Sort((a, b) => a.Time.CompareTo(b.Time));
            int drop = 0;
            while (drop < _spots.Count && (_spots.Count - drop > MostSpots * 9 / 10 || bytes > MostBytes * 9 / 10))
            {
                bytes -= Cost(_spots[drop]);
                drop++;
            }
            _spots.RemoveRange(0, drop);
        }
        _bytes = bytes;
        _full = false;
        _seen.Clear();
        foreach (var s in _spots)
        {
            _seen.Add(s.Sequence);
        }
        _up.RemoveAll(u => u.To <= oldest);
    }

    /// <summary>One connection: true when it ended because a reading was no longer wanted; throws or returns false when it was lost.</summary>
    private async Task<bool> SessionAsync(Func<bool> wanted, CancellationToken cancellation)
    {
        MqttSubscriber mqtt;
        using (var timeout = new CancellationTokenSource(ConnectTimeout, _time))
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token))
        {
            var stream = await _connect(connecting.Token).ConfigureAwait(false);
            mqtt = new MqttSubscriber(stream);
            try
            {
                await mqtt.ConnectAsync(_clientId, KeepAlive, connecting.Token).ConfigureAwait(false);
                await mqtt.SubscribeAsync(PskFeed.TopicFilters, connecting.Token).ConfigureAwait(false);
            }
            catch
            {
                await mqtt.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        await using (mqtt.ConfigureAwait(false))
        {
            Up();
            _sessionSpots = 0;
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var state = new SessionState(_time.GetUtcNow());
            Task watchdog = WatchAsync(mqtt, state, wanted, session);
            try
            {
                while (true)
                {
                    var packet = await mqtt.ReadAsync(session.Token).ConfigureAwait(false);
                    state.Heard = _time.GetUtcNow();
                    if (packet.Type == MqttSubscriber.Publish)
                    {
                        Offer(packet.Payload.Span);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && state.Ended is not null)
            {
                if (state.Ended == "unwanted")
                {
                    await mqtt.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                    Down(null);
                    return true;
                }
                throw new IOException(state.Ended);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Stopping: say goodbye, briefly, by the wall clock whatever clock the rest runs on.
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await mqtt.DisconnectAsync(quick.Token).ConfigureAwait(false);
                throw;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
            finally
            {
                await session.CancelAsync().ConfigureAwait(false);
                try
                {
                    await watchdog.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // ended with the session
                }
            }
        }
    }

    private sealed class SessionState(DateTimeOffset start)
    {
        public DateTimeOffset Heard { get; set; } = start;

        public DateTimeOffset Pinged { get; set; } = start;

        public string? Ended { get; set; }
    }

    private async Task WatchAsync(MqttSubscriber mqtt, SessionState state, Func<bool> wanted, CancellationTokenSource session)
    {
        while (!session.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _time, session.Token).ConfigureAwait(false);
            DateTimeOffset now = _time.GetUtcNow();
            if (!wanted())
            {
                state.Ended = "unwanted";
            }
            else if (now - state.Heard >= KeepAlive * 1.5)
            {
                state.Ended = string.Create(CultureInfo.InvariantCulture, $"no word from the broker for {(now - state.Heard).TotalSeconds:0} s");
            }
            if (state.Ended is not null)
            {
                await session.CancelAsync().ConfigureAwait(false);
                return;
            }
            if (now - state.Pinged >= KeepAlive / 2)
            {
                state.Pinged = now;
                try
                {
                    await mqtt.SendPingAsync(session.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    // the read loop will find out
                }
            }
        }
    }

    private void Up()
    {
        DateTimeOffset now = _time.GetUtcNow();
        bool again;
        int count;
        lock (_gate)
        {
            _connectedSince = now;
            _downSince = null;
            _problem = null;
            _connections++;
            count = _connections;
            again = _failures > 0;
        }
        if (count == 1)
        {
            _log(string.Create(CultureInfo.InvariantCulture, $"pskreporter: subscribed at {_where} as {_clientId}, {PskFeed.TopicFilters.Count} topics: 40 and 80 m with both ends in the UK or Ireland"));
        }
        else if (again)
        {
            _log(string.Create(CultureInfo.InvariantCulture, $"pskreporter: subscribed again at {_where}"));
        }
    }

    private void Down(string? problem)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_connectedSince is { } since)
            {
                _up.Add((since, now));
                _downSince = now;
            }
            _connectedSince = null;
            _problem = problem;
            _up.RemoveAll(u => u.To <= now - PskEvaluator.Window);
        }
    }

    private TimeSpan UpDuring(DateTimeOffset from, DateTimeOffset to)
    {
        TimeSpan total = TimeSpan.Zero;
        foreach (var (start, end) in _up)
        {
            total += Overlap(start, end);
        }
        if (_connectedSince is { } since)
        {
            total += Overlap(since, to);
        }
        return total;

        TimeSpan Overlap(DateTimeOffset start, DateTimeOffset end)
        {
            var a = start > from ? start : from;
            var b = end < to ? end : to;
            return b > a ? b - a : TimeSpan.Zero;
        }
    }

    private static async Task<Stream> Tcp(string host, int port, CancellationToken cancellation)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, cancellation).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string Plain(string text) => new([.. text.Select(c => c is < ' ' or > '~' ? '?' : c)]);
}
