using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Packet.Mailcast.Discovery;

namespace Mailcast.HeadEnd.Relay;

/// <summary>How a receiver was found.</summary>
public enum ReceiverSource
{
    /// <summary>On the head end's own machine, on the receiver's usual port.</summary>
    ThisMachine,

    /// <summary>On the network, announced by multicast DNS.</summary>
    Network,
}

/// <summary>What the status document says of one receiver.</summary>
public sealed record ReceiverStatus(string Name, string Url, string Found, string? Version, string State, DateTimeOffset? LastRead, int Objects, string? Problem);

/// <summary>Settings of the pool that tests change; the defaults are what runs.</summary>
public sealed class ReceiverPoolOptions
{
    /// <summary>The receiver's usual port, tried on 127.0.0.1.</summary>
    public int LocalPort { get; init; } = ReceiverService.DefaultPort;

    /// <summary>How often receivers are read.</summary>
    public TimeSpan ReadEvery { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Failed reads in a row before a receiver on this machine is counted lost.</summary>
    public int LocalFailuresToLose { get; init; } = 3;

    /// <summary>Makes the network side of multicast DNS; null leaves the network alone (only this machine is looked at).</summary>
    public Func<IMdnsTransport>? Transport { get; init; } = static () => new UdpMdnsTransport();

    /// <summary>The networks this machine is on, to believe only announcements from them; the network cards' by default.</summary>
    public LocalNetworks? Networks { get; init; }

    /// <summary>Whether an address belongs to this machine; the network cards' own addresses by default.</summary>
    public Func<IPAddress, bool>? IsOwnAddress { get; init; }
}

/// <summary>
/// The receivers the head end reads (issue #218), with no setting: first one on this machine on the receiver's usual
/// port, then any that announce themselves on the local network (<c>_pdn-mailcast._tcp</c>, multicast DNS). They come
/// and go as they start and stop, and everything read goes into one <see cref="ObjectCatalogue"/>. A line in the
/// journal for each receiver found or lost. Multicast not working here is said once, and a receiver on this machine is
/// still found. Multicast does not cross routers or VLANs; a receiver beyond one is not found.
/// </summary>
/// <remarks>
/// Reading is in the background only and nothing waits for it: receivers are read in parallel, each request with its own short
/// timeout, and a receiver that keeps failing is asked less often. At most <see cref="MaxReceivers"/> are followed, and
/// <see cref="MaxPerAddress"/> at one address, because anything on the network can announce itself.
/// </remarks>
public sealed class ReceiverPool : IDisposable
{
    /// <summary>The most receivers followed.</summary>
    public const int MaxReceivers = 16;

    /// <summary>The most receivers followed at one address.</summary>
    public const int MaxPerAddress = 2;

    /// <summary>The most a response may be: bigger is refused.</summary>
    public const long MaxResponseBytes = 16L * 1024 * 1024;

    /// <summary>The longest wait before a receiver that keeps failing is asked again.</summary>
    public static readonly TimeSpan LongestBackoff = TimeSpan.FromMinutes(10);

    private const string LocalKey = "local";

    private sealed class Link(string key, string instance, string name, ReceiverSource source, Uri url, string endpoint, ReceiverReader reader)
    {
        public readonly string Key = key;
        public readonly string Instance = instance;
        public string Name = name;
        public readonly ReceiverSource Source = source;
        public readonly Uri Url = url;
        public readonly string Endpoint = endpoint;
        public readonly ReceiverReader Reader = reader;
        public string? Version;
        public int Failures;
        public string? Problem;
        public string? ReportedProblem;
        public DateTimeOffset? LastRead;
        public DateTimeOffset NextTry;
    }

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IJournal _journal;
    private readonly TimeProvider _time;
    private readonly ReceiverPoolOptions _options;
    private readonly LocalNetworks _networks;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Link> _links = [];
    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly SemaphoreSlim _kick = new(0, 1);
    private MdnsBrowser? _browser;
    private string? _refusedLine;

    /// <summary>Makes a pool; <see cref="RunAsync"/> runs it.</summary>
    /// <param name="catalogue">Where what is read goes.</param>
    /// <param name="journal">Where found and lost go.</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">Settings; the defaults run.</param>
    /// <param name="http">The client to read with; one with a 5 s timeout for each request and a 16 MiB limit on a response by default.</param>
    public ReceiverPool(ObjectCatalogue catalogue, IJournal journal, TimeProvider time, ReceiverPoolOptions? options = null, HttpClient? http = null)
    {
        Catalogue = catalogue;
        _journal = journal;
        _time = time;
        _options = options ?? new ReceiverPoolOptions();
        _networks = _options.Networks ?? new LocalNetworks();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = MaxResponseBytes };
        _ownsHttp = http is null;
        catalogue.Evicted += OnEvicted;
    }

    /// <summary>What the receivers have given.</summary>
    public ObjectCatalogue Catalogue { get; }

    /// <summary>The receivers now followed.</summary>
    public IReadOnlyList<ReceiverStatus> Receivers
    {
        get
        {
            lock (_gate)
            {
                return [.. _links.Values.OrderBy(l => l.Name, StringComparer.Ordinal).Select(l => new ReceiverStatus(
                    l.Name, l.Url.ToString(), l.Source == ReceiverSource.ThisMachine ? "this machine" : "network", l.Version,
                    l.Problem is null ? (l.LastRead is null ? "found" : "ok") : "not readable", l.LastRead, l.Reader.ObjectsKnown, l.Problem))];
            }
        }
    }

    /// <summary>The part of the status document about receivers and what they have given.</summary>
    public object StatusView()
    {
        var totals = Catalogue.Totals();
        return new
        {
            receivers = Receivers,
            objects = totals.Objects,
            complete = totals.Complete,
            pieces = totals.Pieces,
            piecesHeardOnAir = totals.OnAirPieces,
            megabytes = Math.Round(totals.Bytes / 1048576.0, 1),
        };
    }

    private void OnEvicted(ulong id)
    {
        Link[] links;
        lock (_gate)
        {
            links = [.. _links.Values];
        }
        foreach (var l in links)
        {
            l.Reader.Forget(id);
        }
    }

    /// <summary>Looks for receivers and reads them until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        if (_options.Transport is { } make)
        {
            try
            {
                var browser = new MdnsBrowser(make(), ReceiverService.TypeName, _journal.Write, _time, _networks);
                browser.Found += OnFound;
                browser.Lost += OnLost;
                _browser = browser;
                browser.Start();
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidOperationException or ObjectDisposedException)
            {
                _journal.Write($"discovery: cannot look for receivers on the network ({ReceiverService.Safe(e.Message)}); only one on this machine is found");
            }
        }
        string? said = null;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await ReadNowAsync(cancellation).ConfigureAwait(false);
                await WaitForNextReadAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // nothing here may stop the head end: say it once and go on
            catch (Exception e)
#pragma warning restore CA1031
            {
                string line = $"receivers: reading failed ({ReceiverService.Safe(e.Message)}); carrying on";
                if (line != said)
                {
                    said = line;
                    _journal.Write(line);
                }
                await Task.Delay(_options.ReadEvery, _time, cancellation).ConfigureAwait(false);
            }
        }
    }

    // The next read comes after ReadEvery, or at once when a receiver has just been found.
    private async Task WaitForNextReadAsync(CancellationToken cancellation)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var delay = Task.Delay(_options.ReadEvery, _time, either.Token);
        var kicked = _kick.WaitAsync(either.Token);
        await Task.WhenAny(delay, kicked).ConfigureAwait(false);
        await either.CancelAsync().ConfigureAwait(false);
        foreach (var t in new[] { delay, kicked })
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The one that lost.
            }
        }
        cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Looks on this machine for a receiver if none is followed there, then reads every receiver followed, all at once. Never
    /// throws for a receiver that cannot be read; the problem is on the receiver's status. A read already going is waited for.
    /// </summary>
    public async Task ReadNowAsync(CancellationToken cancellation)
    {
        await _reading.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await ProbeThisMachineAsync(cancellation).ConfigureAwait(false);
            Link[] links;
            lock (_gate)
            {
                links = [.. _links.Values];
            }
            await Task.WhenAll(links.Select(l => ReadAsync(l, cancellation))).ConfigureAwait(false);
        }
        finally
        {
            _reading.Release();
        }
    }

    private async Task ProbeThisMachineAsync(CancellationToken cancellation)
    {
        string endpoint = EndpointOf(IPAddress.Loopback, _options.LocalPort);
        lock (_gate)
        {
            if (_links.Values.Any(l => l.Endpoint == endpoint))
            {
                return;
            }
        }
        var url = new Uri($"http://127.0.0.1:{_options.LocalPort}/");
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            probe.CancelAfter(TimeSpan.FromSeconds(3));
            // The index of one object: the shape of the answer says it is a receiver and not some other web page.
            await new ReceiverApiClient(_http, url).GetObjectsAsync(0, 1, probe.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ReceiverReadException or OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return; // nothing there, or not a receiver
        }
        Add(new Link(LocalKey, "", "this machine", ReceiverSource.ThisMachine, url, endpoint, NewReader(url)), null);
    }

    private ReceiverReader NewReader(Uri url) => new(new ReceiverApiClient(_http, url), Catalogue, _time);

    private void OnFound(FoundService found)
    {
        if (found.Get("api") is { } api && api != ReceiverService.ApiVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            _journal.Write($"receiver: {ReceiverService.Safe(found.Name)} at {found.Address}:{found.Port} speaks API version {ReceiverService.Safe(api)}, which this head end does not; not read");
            return;
        }
        string name = ReceiverService.Safe(found.Name);
        string key = "net:" + found.Name;
        string endpoint = EndpointOf(found.Address, found.Port);
        string? version = found.Get("v") is { } v ? ReceiverService.Safe(v) : null;
        var url = new Uri($"http://{found.Address}:{found.Port}/");
        Link? old;
        lock (_gate)
        {
            _links.TryGetValue(key, out old);
            if (old is not null && old.Endpoint == endpoint)
            {
                old.Version = version ?? old.Version;
                return;
            }
            if (old is null && _links.Values.FirstOrDefault(l => l.Endpoint == endpoint) is { } same)
            {
                // The receiver the local probe already follows (or another name for one followed), heard again over the network.
                same.Version = version ?? same.Version;
                if (same.Source == ReceiverSource.ThisMachine)
                {
                    same.Name = name;
                }
                return;
            }
            if (old is not null)
            {
                _links.Remove(key); // announced again somewhere else: the old link is stale
            }
        }
        if (old is not null)
        {
            _journal.Write($"receiver: {name} moved from {old.Url} to {url}");
        }
        Add(new Link(key, found.Name, name, ReceiverSource.Network, url, endpoint, NewReader(url)) { Version = version }, old is null ? version : null, quiet: old is not null);
    }

    private void OnLost(string name)
    {
        Link? gone;
        lock (_gate)
        {
            if (!_links.Remove("net:" + name, out gone))
            {
                return;
            }
        }
        _journal.Write($"receiver: lost {gone.Name} at {gone.Url} (it stopped announcing itself)");
    }

    private void Add(Link link, string? version, bool quiet = false)
    {
        string? refused = null;
        lock (_gate)
        {
            string host = link.Endpoint[..link.Endpoint.LastIndexOf(':')];
            if (_links.ContainsKey(link.Key) || _links.Values.Any(l => l.Endpoint == link.Endpoint))
            {
                return; // already followed, perhaps found another way while this was being looked at
            }
            if (_links.Count >= MaxReceivers)
            {
                refused = $"receiver: not following {link.Name} at {link.Url}: already following {MaxReceivers}";
            }
            else if (_links.Values.Count(l => l.Endpoint[..l.Endpoint.LastIndexOf(':')] == host) >= MaxPerAddress)
            {
                refused = $"receiver: not following {link.Name} at {link.Url}: already following {MaxPerAddress} at that address";
            }
            else
            {
                _links[link.Key] = link;
            }
        }
        if (refused is not null)
        {
            if (refused != _refusedLine)
            {
                _refusedLine = refused;
                _journal.Write(refused);
            }
            return;
        }
        try
        {
            _kick.Release();
        }
        catch (SemaphoreFullException)
        {
            // A read is already asked for.
        }
        if (!quiet)
        {
            _journal.Write($"receiver: found {link.Name} at {link.Url} ({(link.Source == ReceiverSource.ThisMachine ? "on this machine" : "on the network")}{(version is null ? "" : ", version " + version)})");
        }
    }

    private async Task ReadAsync(Link link, CancellationToken cancellation)
    {
        var now = _time.GetUtcNow();
        if (link.NextTry > now)
        {
            return; // failing: asked less often
        }
        try
        {
            ReadResult result;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(90)); // a whole read, many requests, still ends
                try
                {
                    result = await link.Reader.ReadAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new ReceiverReadException("it did not finish in time");
                }
            }
            lock (_gate)
            {
                link.LastRead = _time.GetUtcNow();
                link.Failures = 0;
                link.Problem = null;
                link.NextTry = default;
            }
            if (link.ReportedProblem is not null)
            {
                link.ReportedProblem = null;
                _journal.Write($"receiver: {link.Name} at {link.Url} can be read again");
            }
            if (result.NewPieces > 0 || result.NewObjects > 0)
            {
                _journal.Write($"receiver: {link.Name} gave {result.NewPieces} new piece(s) and {result.NewObjects} rebuilt object(s)");
            }
        }
        catch (ReceiverReadException e)
        {
            bool lost = false;
            string problem = ReceiverService.Safe(e.Message);
            lock (_gate)
            {
                link.Failures++;
                link.Problem = problem;
                if (link.Failures >= 3)
                {
                    // Failing for good: ask less and less often, up to every 10 minutes.
                    double factor = Math.Pow(2, Math.Min(link.Failures - 3, 10));
                    link.NextTry = _time.GetUtcNow() + TimeSpan.FromTicks((long)Math.Min(_options.ReadEvery.Ticks * factor, LongestBackoff.Ticks));
                }
                if (link.Source == ReceiverSource.ThisMachine && link.Failures >= _options.LocalFailuresToLose)
                {
                    _links.Remove(link.Key);
                    lost = true;
                }
            }
            if (lost)
            {
                _journal.Write($"receiver: lost {link.Name} at {link.Url} ({problem})");
            }
            else if (link.ReportedProblem != problem && link.Failures >= 3)
            {
                link.ReportedProblem = problem;
                _journal.Write($"receiver: cannot read {link.Name} at {link.Url}: {problem}");
            }
        }
    }

    // This machine's own address and a port are one receiver however it was reached.
    private string EndpointOf(IPAddress address, int port) =>
        (IPAddress.IsLoopback(address) || IsOwn(address) ? "127.0.0.1" : address.ToString()) + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private bool IsOwn(IPAddress address) =>
        _options.IsOwnAddress is { } own ? own(address) : LocalNetworks.FromInterfaces().Any(n => n.Address.Equals(address));

    /// <inheritdoc />
    public void Dispose()
    {
        Catalogue.Evicted -= OnEvicted;
        if (_browser is { } browser)
        {
            browser.Found -= OnFound;
            browser.Lost -= OnLost;
            browser.Dispose();
        }
        if (_ownsHttp)
        {
            _http.Dispose();
        }
        _reading.Dispose();
        _kick.Dispose();
    }
}
