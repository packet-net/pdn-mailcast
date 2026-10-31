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

    /// <summary>Whether an address belongs to this machine; the network's own addresses by default.</summary>
    public Func<IPAddress, bool>? IsOwnAddress { get; init; }
}

/// <summary>
/// The receivers the head end reads (issue #218), with no setting: first one on this machine on the receiver's usual
/// port, then any that announce themselves on the local network (<c>_pdn-mailcast._tcp</c>, multicast DNS). They come
/// and go as they start and stop, and everything read goes into one <see cref="ObjectCatalogue"/>. A line in the
/// journal for each receiver found or lost. Multicast not working here is said once, and a receiver on this machine
/// is still found. Multicast does not cross routers or VLANs; a receiver beyond one is not found.
/// </summary>
public sealed class ReceiverPool : IDisposable
{
    private sealed class Link(string key, string name, ReceiverSource source, Uri url, ReceiverReader reader)
    {
        public readonly string Key = key;
        public string Name = name;
        public readonly ReceiverSource Source = source;
        public readonly Uri Url = url;
        public readonly ReceiverReader Reader = reader;
        public string? Version;
        public int Failures;
        public string? Problem;
        public string? ReportedProblem;
        public DateTimeOffset? LastRead;
    }

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IJournal _journal;
    private readonly TimeProvider _time;
    private readonly ReceiverPoolOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Link> _links = [];
    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly SemaphoreSlim _kick = new(0, 1);
    private MdnsBrowser? _browser;

    /// <summary>Makes a pool; <see cref="RunAsync"/> runs it.</summary>
    /// <param name="catalogue">Where what is read goes.</param>
    /// <param name="journal">Where found and lost go.</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">Settings; the defaults run.</param>
    /// <param name="http">The client to read with; one with a 15 s timeout by default.</param>
    public ReceiverPool(ObjectCatalogue catalogue, IJournal journal, TimeProvider time, ReceiverPoolOptions? options = null, HttpClient? http = null)
    {
        Catalogue = catalogue;
        _journal = journal;
        _time = time;
        _options = options ?? new ReceiverPoolOptions();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = http is null;
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

    /// <summary>Looks for receivers and reads them until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        if (_options.Transport is { } make)
        {
            var browser = new MdnsBrowser(make(), ReceiverService.TypeName, _journal.Write, _time);
            browser.Found += OnFound;
            browser.Lost += OnLost;
            _browser = browser;
            browser.Start();
        }
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await ReadNowAsync(cancellation).ConfigureAwait(false);
                await WaitForNextReadAsync(cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
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
    /// Looks on this machine for a receiver if none is followed there, then reads every receiver followed. Never throws for a
    /// receiver that cannot be read; the problem is on the receiver's status. A read already going is waited for.
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
            foreach (var link in links)
            {
                await ReadAsync(link, cancellation).ConfigureAwait(false);
            }
        }
        finally
        {
            _reading.Release();
        }
    }

    private async Task ProbeThisMachineAsync(CancellationToken cancellation)
    {
        string key = KeyOf(IPAddress.Loopback, _options.LocalPort);
        lock (_gate)
        {
            if (_links.ContainsKey(key))
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
        catch (ReceiverReadException e) when (e.NeedsPassword)
        {
            // It is one, but not one we can read; follow it so the status says why.
        }
        catch (Exception e) when (e is ReceiverReadException or OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return; // nothing there, or not a receiver
        }
        Add(key, "this machine", ReceiverSource.ThisMachine, url, null);
    }

    private void OnFound(FoundService found)
    {
        if (found.Get("api") is { } api && api != ReceiverService.ApiVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            _journal.Write($"receiver: {found.Name} at {found.Address}:{found.Port} speaks API version {api}, which this head end does not; not read");
            return;
        }
        string key = KeyOf(found.Address, found.Port);
        lock (_gate)
        {
            // The same receiver the local probe already follows, heard again over the network.
            if (_links.TryGetValue(key, out var existing))
            {
                existing.Version = found.Get("v") ?? existing.Version;
                if (existing.Source == ReceiverSource.ThisMachine)
                {
                    existing.Name = found.Name;
                }
                return;
            }
        }
        Add(key, found.Name, ReceiverSource.Network, new Uri($"http://{(found.Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{found.Address}]" : found.Address.ToString())}:{found.Port}/"), found.Get("v"));
    }

    private void OnLost(string name)
    {
        Link? gone = null;
        lock (_gate)
        {
            gone = _links.Values.FirstOrDefault(l => l.Source == ReceiverSource.Network && l.Name == name);
            if (gone is not null)
            {
                _links.Remove(gone.Key);
            }
        }
        if (gone is not null)
        {
            _journal.Write($"receiver: lost {gone.Name} at {gone.Url} (it stopped announcing itself)");
        }
    }

    private void Add(string key, string name, ReceiverSource source, Uri url, string? version)
    {
        var link = new Link(key, name, source, url, new ReceiverReader(new ReceiverApiClient(_http, url), Catalogue, _time)) { Version = version };
        lock (_gate)
        {
            if (!_links.TryAdd(key, link))
            {
                return;
            }
        }
        try
        {
            _kick.Release();
        }
        catch (SemaphoreFullException)
        {
            // A read is already asked for.
        }
        _journal.Write($"receiver: found {name} at {url} ({(source == ReceiverSource.ThisMachine ? "on this machine" : "on the network")}{(version is null ? "" : ", version " + version)})");
    }

    private async Task ReadAsync(Link link, CancellationToken cancellation)
    {
        try
        {
            var result = await link.Reader.ReadAsync(cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                link.LastRead = _time.GetUtcNow();
                link.Failures = 0;
                link.Problem = null;
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
            lock (_gate)
            {
                link.Failures++;
                link.Problem = e.Message;
                if (link.Source == ReceiverSource.ThisMachine && !e.NeedsPassword && link.Failures >= _options.LocalFailuresToLose)
                {
                    _links.Remove(link.Key);
                    lost = true;
                }
            }
            if (lost)
            {
                _journal.Write($"receiver: lost {link.Name} at {link.Url} ({e.Message})");
            }
            else if (link.ReportedProblem != e.Message && (e.NeedsPassword || link.Failures >= 3))
            {
                link.ReportedProblem = e.Message;
                _journal.Write($"receiver: cannot read {link.Name} at {link.Url}: {e.Message}");
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // Not expected from the client (it turns timeouts into read errors); treated as nothing happening.
        }
    }

    // This machine's own address and a port are one receiver however it was reached.
    private string KeyOf(IPAddress address, int port) =>
        (IPAddress.IsLoopback(address) || IsOwn(address) ? "127.0.0.1" : address.ToString()) + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private bool IsOwn(IPAddress address) =>
        _options.IsOwnAddress is { } own ? own(address) : MdnsAnnouncer.OwnAddresses().Contains(address);

    /// <inheritdoc />
    public void Dispose()
    {
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
