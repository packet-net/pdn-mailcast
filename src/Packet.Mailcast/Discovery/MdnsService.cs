using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Packet.Mailcast.Discovery;

/// <summary>What a receiver announces about itself, and what a head end reads back (issue #218).</summary>
public static class ReceiverService
{
    /// <summary>The DNS-SD service type.</summary>
    public const string Type = "_pdn-mailcast._tcp";

    /// <summary>The service type as mDNS names it.</summary>
    public const string TypeName = Type + ".local";

    /// <summary>
    /// The version of the receiver's object and piece API (<c>/api/objects</c>), announced as the TXT key <c>api</c>.
    /// 1: the API of issue #214. A head end that does not know a receiver's number leaves that receiver alone.
    /// </summary>
    public const int ApiVersion = 1;

    /// <summary>The receiver's default web port, tried on the head end's own machine first.</summary>
    public const int DefaultPort = 8130;

    /// <summary>Makes a DNS label of a name: letters, digits, hyphen and underscore and space, at most 40.</summary>
    public static string Label(string? name)
    {
        var sb = new StringBuilder();
        foreach (char c in name ?? "")
        {
            sb.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' ? c : ' ');
            if (sb.Length >= 40)
            {
                break;
            }
        }
        string text = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? "receiver" : text;
    }

    /// <summary>Text with anything outside printable ASCII made a question mark, for a log line.</summary>
    public static string Plain(string text) => new([.. text.Select(c => c is >= ' ' and <= '~' ? c : '?')]);

    /// <summary>What came from the network, made safe to print: plain ASCII and at most 60 characters.</summary>
    public static string Safe(string? text)
    {
        string plain = Plain(text ?? "");
        return plain.Length <= 60 ? plain : plain[..60];
    }

    /// <summary>
    /// Four hex digits that tell this machine from another with the same name (a Raspberry Pi called "raspberrypi", say): from its
    /// name and its network cards' hardware addresses. Used in the instance name and the host name, which must be unique on the network.
    /// </summary>
    public static string MachineSuffix()
    {
        var macs = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel))
            .Select(n => n.GetPhysicalAddress().ToString())
            .Where(m => m.Length > 0)
            .Order(StringComparer.Ordinal);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + string.Join(',', macs)));
        return Convert.ToHexStringLower(hash.AsSpan(0, 2));
    }

    /// <summary>The TXT strings a receiver announces.</summary>
    public static IReadOnlyList<string> Text(string version) => ["txtvers=1", "v=" + version, "api=" + ApiVersion];
}

/// <summary>
/// Announces one service on the local network by multicast DNS (RFC 6762, 6763): answers questions about
/// it, says so unprompted at start and every minute (a record's life is two), and says goodbye when
/// disposed. On each network it says only the address it has there. A failure to use multicast is reported
/// once through the log and the announcer then does nothing; it never throws.
/// </summary>
/// <remarks>
/// Deviation from RFC 6762, on purpose: the RFC has a host announce twice at start and then only answer queries, with a
/// querier asking again at growing intervals. Here the announcement is repeated every minute and a browser asks every 30
/// seconds, whatever it has heard. That costs a few small packets a minute and helps where a firewall drops the query or the
/// response in one direction, or the network was not up when the other side started.
/// </remarks>
public sealed class MdnsAnnouncer : IDisposable
{
    /// <summary>How often the service is announced again.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(60);

    private const uint Ttl = 120;
    private readonly IMdnsTransport _transport;
    private readonly string _instance;
    private readonly string _host;
    private readonly int _port;
    private readonly IReadOnlyList<string> _text;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly LocalNetworks _networks;
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset _lastAnswer = DateTimeOffset.MinValue;
    private bool _running;

    /// <summary>Makes an announcer; <see cref="Start"/> begins.</summary>
    /// <param name="transport">The network.</param>
    /// <param name="instance">The instance name, one DNS label (see <see cref="ReceiverService.Label"/>), unique on the network.</param>
    /// <param name="port">The TCP port of the service.</param>
    /// <param name="text">TXT strings.</param>
    /// <param name="log">One plain line at a time.</param>
    /// <param name="time">The clock.</param>
    /// <param name="networks">The networks this machine is on, to ignore queries from elsewhere; the cards' by default.</param>
    public MdnsAnnouncer(IMdnsTransport transport, string instance, int port, IReadOnlyList<string> text, Action<string> log, TimeProvider? time = null, LocalNetworks? networks = null)
    {
        _transport = transport;
        _instance = instance;
        _port = port;
        _text = text;
        _log = log;
        _time = time ?? TimeProvider.System;
        _networks = networks ?? new LocalNetworks();
        _host = ReceiverService.Label(Environment.MachineName).Replace(' ', '-').ToLowerInvariant() + "-" + ReceiverService.MachineSuffix() + "-mailcast.local";
    }

    /// <summary>The full name of the instance: <c>NAME._pdn-mailcast._tcp.local</c>.</summary>
    public string InstanceName => _instance + "." + ReceiverService.TypeName;

    /// <summary>Starts announcing. False if multicast cannot be used here (one line said); the announcer then does nothing.</summary>
    public bool Start()
    {
        try
        {
            _transport.Received += OnReceived;
            _transport.Start();
        }
        catch (MdnsUnavailableException e)
        {
            _transport.Received -= OnReceived;
            _log($"discovery: cannot announce this receiver on the network ({ReceiverService.Plain(e.Message)}); head ends on other machines will not find it");
            return false;
        }
        _running = true;
        _ = Task.Run(() => AnnounceLoopAsync(_stop.Token));
        return true;
    }

    private async Task AnnounceLoopAsync(CancellationToken stop)
    {
        try
        {
            foreach (var wait in new[] { 0, 1, 2 })
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), _time, stop).ConfigureAwait(false);
                Send(Ttl);
            }
            using var timer = new PeriodicTimer(Every, _time);
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                Send(Ttl);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private void OnReceived(byte[] packet, IPAddress from)
    {
        if (!_running || !_networks.IsLocal(from) || DnsMessage.Parse(packet) is not { IsResponse: false } query)
        {
            return;
        }
        bool asked = query.Questions.Any(q =>
            (q.Type is DnsRecord.TypePtr or DnsRecord.TypeAny && string.Equals(q.Name, ReceiverService.TypeName, StringComparison.OrdinalIgnoreCase))
            || (q.Type is DnsRecord.TypeSrv or DnsRecord.TypeTxt or DnsRecord.TypeAny && string.Equals(q.Name, InstanceName, StringComparison.OrdinalIgnoreCase))
            || (q.Type is DnsRecord.TypeA or DnsRecord.TypeAny && string.Equals(q.Name, _host, StringComparison.OrdinalIgnoreCase)));
        if (!asked)
        {
            return;
        }
        // At most one answer a second, whoever asks: many browsers starting together get the one multicast.
        var now = _time.GetUtcNow();
        lock (_stop)
        {
            if (now - _lastAnswer < TimeSpan.FromSeconds(1))
            {
                return;
            }
            _lastAnswer = now;
        }
        Send(Ttl);
    }

    private void Send(uint ttl) => _transport.Send(address =>
    {
        var message = new DnsMessage { IsResponse = true };
        message.Records.Add(new DnsRecord { Name = ReceiverService.TypeName, Type = DnsRecord.TypePtr, Ttl = ttl, Target = InstanceName });
        message.Records.Add(new DnsRecord { Name = InstanceName, Type = DnsRecord.TypeSrv, Ttl = ttl, Port = (ushort)_port, Target = _host });
        message.Records.Add(new DnsRecord { Name = InstanceName, Type = DnsRecord.TypeTxt, Ttl = ttl, Text = _text });
        message.Records.Add(new DnsRecord { Name = _host, Type = DnsRecord.TypeA, Ttl = ttl, Address = address });
        return message.ToBytes();
    });

    /// <inheritdoc />
    public void Dispose()
    {
        if (_running)
        {
            _running = false;
            _stop.Cancel();
            Send(0); // goodbye, so head ends drop it at once
        }
        _transport.Received -= OnReceived;
        _transport.Dispose();
        _stop.Dispose();
    }
}

/// <summary>A service found on the network.</summary>
/// <param name="Name">The instance name, such as <c>M0XYZ-rx</c>.</param>
/// <param name="Address">The address to reach it on.</param>
/// <param name="Port">Its TCP port.</param>
/// <param name="Text">Its TXT strings.</param>
public sealed record FoundService(string Name, IPAddress Address, int Port, IReadOnlyList<string> Text)
{
    /// <summary>A TXT value, such as <c>v</c> or <c>api</c>; null if absent.</summary>
    public string? Get(string key)
    {
        string prefix = key + "=";
        return Text.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }
}

/// <summary>
/// Looks for a service by multicast DNS and keeps the set that is there: asks at start and every
/// <see cref="Every"/>, listens always, and raises <see cref="Found"/> when an instance appears (or its address or
/// port changes) and <see cref="Lost"/> when it says goodbye or stops being announced for its record's life.
/// Only packets from, and addresses on, the networks this machine is on are believed, only records under the service
/// type are kept, and at most <see cref="MaxInstances"/> instances.
/// </summary>
/// <remarks>The fixed re-query every 30 s deviates from RFC 6762 on purpose; see <see cref="MdnsAnnouncer"/>.</remarks>
public sealed class MdnsBrowser : IDisposable
{
    /// <summary>How often it asks.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    /// <summary>The most instances kept.</summary>
    public const int MaxInstances = 64;

    private readonly IMdnsTransport _transport;
    private readonly string _typeName;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly LocalNetworks _networks;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _instances = new(StringComparer.OrdinalIgnoreCase); // instance full name -> expiry
    private readonly Dictionary<string, (string Host, ushort Port)> _srv = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<IPAddress>> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _from = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FoundService> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private bool _running;

    /// <summary>Makes a browser for a service type such as <see cref="ReceiverService.TypeName"/>.</summary>
    public MdnsBrowser(IMdnsTransport transport, string typeName, Action<string> log, TimeProvider? time = null, LocalNetworks? networks = null)
    {
        _transport = transport;
        _typeName = typeName;
        _log = log;
        _time = time ?? TimeProvider.System;
        _networks = networks ?? new LocalNetworks();
    }

    /// <summary>An instance appeared, or its address or port changed. May be raised on any thread.</summary>
    public event Action<FoundService>? Found;

    /// <summary>An instance went; the argument is its name. May be raised on any thread.</summary>
    public event Action<string>? Lost;

    /// <summary>What is there now.</summary>
    public IReadOnlyList<FoundService> Current
    {
        get
        {
            lock (_gate)
            {
                return [.. _known.Values];
            }
        }
    }

    /// <summary>Starts looking. False if multicast cannot be used here (one line said); it then finds nothing.</summary>
    public bool Start()
    {
        try
        {
            _transport.Received += OnReceived;
            _transport.Start();
        }
        catch (MdnsUnavailableException e)
        {
            _transport.Received -= OnReceived;
            _log($"discovery: cannot look for receivers on the network ({ReceiverService.Plain(e.Message)}); only one on this machine is found");
            return false;
        }
        _running = true;
        _ = Task.Run(() => AskLoopAsync(_stop.Token));
        return true;
    }

    private async Task AskLoopAsync(CancellationToken stop)
    {
        try
        {
            var query = new DnsMessage();
            query.Questions.Add(new DnsQuestion(_typeName, DnsRecord.TypePtr));
            byte[] bytes = query.ToBytes();
            int round = 0;
            while (!stop.IsCancellationRequested)
            {
                _transport.Send(bytes);
                Expire();
                await Task.Delay(round++ < 2 ? TimeSpan.FromSeconds(1) : Every, _time, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private void OnReceived(byte[] packet, IPAddress from)
    {
        if (!_running || !_networks.IsLocal(from) || DnsMessage.Parse(packet) is not { IsResponse: true } message)
        {
            return;
        }
        var now = _time.GetUtcNow();
        string underType = "." + _typeName;
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gone = new List<string>();
        lock (_gate)
        {
            foreach (var r in message.Records)
            {
                switch (r.Type)
                {
                    case DnsRecord.TypePtr when string.Equals(r.Name, _typeName, StringComparison.OrdinalIgnoreCase) && r.Target is { } instance
                        && instance.EndsWith(underType, StringComparison.OrdinalIgnoreCase):
                        if (r.Ttl == 0)
                        {
                            _instances.Remove(instance);
                            gone.Add(instance);
                        }
                        else if (_instances.ContainsKey(instance) || _instances.Count < MaxInstances)
                        {
                            _instances[instance] = now + TimeSpan.FromSeconds(r.Ttl);
                            _from[instance] = from;
                            changed.Add(instance);
                        }
                        break;
                    case DnsRecord.TypeSrv when r.Ttl > 0 && r.Target is { } host && r.Name.EndsWith(underType, StringComparison.OrdinalIgnoreCase) && (_srv.ContainsKey(r.Name) || _srv.Count < MaxInstances):
                        _srv[r.Name] = (host, r.Port);
                        _from[r.Name] = from;
                        changed.Add(r.Name);
                        break;
                    case DnsRecord.TypeTxt when r.Ttl > 0 && r.Name.EndsWith(underType, StringComparison.OrdinalIgnoreCase) && (_txt.ContainsKey(r.Name) || _txt.Count < MaxInstances):
                        _txt[r.Name] = [.. r.Text.Take(16)];
                        changed.Add(r.Name);
                        break;
                }
            }
            // Addresses only for hosts a kept SRV names, and only if on one of our networks (a packet cannot point us elsewhere).
            foreach (var r in message.Records)
            {
                if (r.Type == DnsRecord.TypeA && r.Address is { } address && _networks.IsLocal(address) && _srv.Values.Any(v => string.Equals(v.Host, r.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (r.Ttl == 0)
                    {
                        _hosts.GetValueOrDefault(r.Name)?.Remove(address);
                    }
                    else
                    {
                        if (!_hosts.TryGetValue(r.Name, out var set))
                        {
                            _hosts[r.Name] = set = [];
                        }
                        if (set.Count < 8)
                        {
                            set.Add(address);
                        }
                        foreach (var i in _srv.Where(v => string.Equals(v.Value.Host, r.Name, StringComparison.OrdinalIgnoreCase)).Select(v => v.Key))
                        {
                            changed.Add(i);
                        }
                    }
                }
            }
        }
        foreach (string instance in gone)
        {
            Forget(instance);
        }
        foreach (string instance in changed)
        {
            Resolve(instance);
        }
    }

    private void Resolve(string instance)
    {
        FoundService? found;
        lock (_gate)
        {
            if (!_instances.ContainsKey(instance) || !_srv.TryGetValue(instance, out var srv))
            {
                return;
            }
            // An address of the host on a network this machine is on; failing that, the one the announcement came from.
            IPAddress? address = _hosts.TryGetValue(srv.Host, out var set) ? set.FirstOrDefault(_networks.IsLocal) : null;
            address ??= _from.GetValueOrDefault(instance);
            if (address is null)
            {
                return;
            }
            string name = instance[..^(_typeName.Length + 1)];
            found = new FoundService(name, address, srv.Port, _txt.TryGetValue(instance, out var text) ? text : []);
            if (_known.TryGetValue(instance, out var old) && old.Address.Equals(found.Address) && old.Port == found.Port && old.Text.SequenceEqual(found.Text))
            {
                return;
            }
            _known[instance] = found;
        }
        Found?.Invoke(found);
    }

    private void Forget(string instance)
    {
        string? name = null;
        lock (_gate)
        {
            if (_known.Remove(instance))
            {
                name = instance[..^(_typeName.Length + 1)];
            }
            if (_srv.Remove(instance, out var srv) && !_srv.Values.Any(v => string.Equals(v.Host, srv.Host, StringComparison.OrdinalIgnoreCase)))
            {
                _hosts.Remove(srv.Host);
            }
            _txt.Remove(instance);
            _from.Remove(instance);
        }
        if (name is not null)
        {
            Lost?.Invoke(name);
        }
    }

    private void Expire()
    {
        var now = _time.GetUtcNow();
        string[] expired;
        lock (_gate)
        {
            expired = [.. _instances.Where(i => i.Value <= now).Select(i => i.Key)];
            foreach (string e in expired)
            {
                _instances.Remove(e);
            }
            // Records of instances that were never announced by PTR, or are long gone, do not stay.
            foreach (string stray in _srv.Keys.Concat(_txt.Keys).Where(k => !_instances.ContainsKey(k) && !expired.Contains(k, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                _srv.Remove(stray);
                _txt.Remove(stray);
                _from.Remove(stray);
            }
        }
        foreach (string e in expired)
        {
            Forget(e);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _running = false;
        _stop.Cancel();
        _transport.Received -= OnReceived;
        _transport.Dispose();
        _stop.Dispose();
    }
}
