using System.Net;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast.Discovery;

namespace Packet.Mailcast.Tests;

/// <summary>Issue #218: multicast DNS service discovery, the part that is ours.</summary>
public class DiscoveryTests
{
    private sealed class Network
    {
        private readonly List<Member> _members = [];

        public bool Broken { get; set; }

        public Member Join(IPAddress? address = null)
        {
            var m = new Member(this, address ?? IPAddress.Parse("192.168.1.20"));
            lock (_members)
            {
                _members.Add(m);
            }
            return m;
        }

        public sealed class Member : IMdnsTransport
        {
            private readonly Network net;
            private readonly IPAddress address;
            private readonly System.Threading.Channels.Channel<(byte[] Packet, IPAddress From)> _in = System.Threading.Channels.Channel.CreateUnbounded<(byte[], IPAddress)>();

            public Member(Network net, IPAddress address)
            {
                this.net = net;
                this.address = address;
                // In order, as a real network keeps a sender's packets.
                _ = Task.Run(async () =>
                {
                    await foreach (var (packet, from) in _in.Reader.ReadAllAsync())
                    {
                        Received?.Invoke(packet, from);
                    }
                });
            }

            public event Action<byte[], IPAddress>? Received;

            public void Start()
            {
                if (net.Broken)
                {
                    throw new MdnsUnavailableException("no network here can multicast");
                }
            }

            public void Send(byte[] packet)
            {
                Member[] all;
                lock (net._members)
                {
                    all = [.. net._members];
                }
                foreach (var m in all)
                {
                    m._in.Writer.TryWrite((packet, address));
                }
            }

            public void Send(Func<IPAddress, byte[]> packetForNetwork) => Send(packetForNetwork(address));

            public void Inject(byte[] packet, IPAddress from) => Received?.Invoke(packet, from);

            public void Dispose() => _in.Writer.TryComplete();
        }
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (int i = 0; i < 250 && !condition(); i++)
        {
            await Task.Delay(20);
        }
        Assert.True(condition(), what);
    }

    private static readonly LocalNetworks Lan = new(() => [(IPAddress.Loopback, 8), (IPAddress.Parse("10.0.0.0"), 8), (IPAddress.Parse("192.168.1.0"), 24)]);

    private static readonly string[] Text = ["txtvers=1", "v=0.1.0", "api=1"];

    [Fact]
    public void AMessage_RoundTrips_WithEveryKindOfRecord()
    {
        var message = new DnsMessage { IsResponse = true };
        message.Records.Add(new DnsRecord { Name = ReceiverService.TypeName, Type = DnsRecord.TypePtr, Ttl = 120, Target = "M0LTE on box._pdn-mailcast._tcp.local" });
        message.Records.Add(new DnsRecord { Name = "M0LTE on box._pdn-mailcast._tcp.local", Type = DnsRecord.TypeSrv, Ttl = 120, Port = 8130, Target = "box-mailcast.local" });
        message.Records.Add(new DnsRecord { Name = "M0LTE on box._pdn-mailcast._tcp.local", Type = DnsRecord.TypeTxt, Ttl = 120, Text = Text });
        message.Records.Add(new DnsRecord { Name = "box-mailcast.local", Type = DnsRecord.TypeA, Ttl = 120, Address = IPAddress.Parse("10.45.0.10") });

        var read = DnsMessage.Parse(message.ToBytes())!;

        Assert.True(read.IsResponse);
        Assert.Equal(4, read.Records.Count);
        Assert.Equal("M0LTE on box._pdn-mailcast._tcp.local", read.Records[0].Target);
        Assert.Equal(8130, read.Records[1].Port);
        Assert.Equal("box-mailcast.local", read.Records[1].Target);
        Assert.Equal(Text, read.Records[2].Text);
        Assert.Equal(IPAddress.Parse("10.45.0.10"), read.Records[3].Address);
        Assert.Equal(120u, read.Records[0].Ttl);
    }

    [Fact]
    public void AQuestion_RoundTrips()
    {
        var q = new DnsMessage();
        q.Questions.Add(new DnsQuestion(ReceiverService.TypeName, DnsRecord.TypePtr));
        var read = DnsMessage.Parse(q.ToBytes())!;
        Assert.False(read.IsResponse);
        Assert.Equal(new DnsQuestion("_pdn-mailcast._tcp.local", DnsRecord.TypePtr), Assert.Single(read.Questions));
    }

    [Fact]
    public void ACompressedName_IsRead_AndGarbageIsRefusedNotThrown()
    {
        // A response with one PTR whose owner is "a.local" and whose target is "b" then a pointer to "local" (offset 14).
        byte[] packet =
        [
            0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0,
            1, (byte)'a', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0,
            0, 12, 0, 1, 0, 0, 0, 120, 0, 4, 1, (byte)'b', 0xC0, 14,
        ];
        var read = DnsMessage.Parse(packet)!;
        Assert.Equal("a.local", read.Records[0].Name);
        Assert.Equal("b.local", read.Records[0].Target);

        byte[] loop = [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1];
        Assert.Null(DnsMessage.Parse(loop));
        var random = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[random.Next(0, 80)];
            random.NextBytes(junk);
            DnsMessage.Parse(junk); // never throws
        }
        Assert.Null(DnsMessage.Parse([1, 2, 3]));
    }

    [Fact]
    public async Task ABrowser_FindsAnAnnouncedService_WithItsAddressPortAndText_AndSeesItGo()
    {
        var net = new Network();
        var lines = new List<string>();
        using var announcer = new MdnsAnnouncer(net.Join(IPAddress.Parse("10.45.0.77")), "M0LTE on box", 8130, Text, lines.Add, networks: Lan);
        using var browser = new MdnsBrowser(net.Join(), ReceiverService.TypeName, lines.Add, networks: Lan);
        var found = new List<FoundService>();
        var lost = new List<string>();
        browser.Found += f => { lock (found) { found.Add(f); } };
        browser.Lost += n => { lock (lost) { lost.Add(n); } };
        Assert.True(announcer.Start());
        Assert.True(browser.Start());

        await Eventually(() => browser.Current.Count == 1, "found");

        var service = Assert.Single(browser.Current);
        Assert.Equal("M0LTE on box", service.Name);
        Assert.Equal(IPAddress.Parse("10.45.0.77"), service.Address);
        Assert.Equal(8130, service.Port);
        Assert.Equal("1", service.Get("api"));
        Assert.Equal("0.1.0", service.Get("v"));
        Assert.Null(service.Get("pw"));
        lock (found)
        {
            Assert.Single(found); // one event, however many announcements
        }

        announcer.Dispose();
        await Eventually(() => { lock (lost) { return lost.Count == 1; } }, "lost on goodbye");
        Assert.Empty(browser.Current);
        lock (lost)
        {
            Assert.Equal(["M0LTE on box"], lost);
        }
        Assert.Empty(lines);
    }

    [Fact]
    public async Task AServiceThatJustStopsBeingAnnounced_IsLostWhenItsRecordRunsOut()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var net = new Network();
        var member = net.Join();
        using var browser = new MdnsBrowser(member, ReceiverService.TypeName, _ => { }, time, Lan);
        browser.Start();
        var message = new DnsMessage { IsResponse = true };
        string instance = "rx._pdn-mailcast._tcp.local";
        message.Records.Add(new DnsRecord { Name = ReceiverService.TypeName, Type = DnsRecord.TypePtr, Ttl = 120, Target = instance });
        message.Records.Add(new DnsRecord { Name = instance, Type = DnsRecord.TypeSrv, Ttl = 120, Port = 8130, Target = "h.local" });
        member.Inject(message.ToBytes(), IPAddress.Parse("10.0.0.5"));
        Assert.Single(browser.Current);

        for (int i = 0; i < 400 && browser.Current.Count > 0; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }

        Assert.Empty(browser.Current);
    }

    [Fact]
    public void WithoutMulticast_BothSidesSayOneShortAsciiLine_AndDoNothing()
    {
        var net = new Network { Broken = true };
        var lines = new List<string>();
        using var announcer = new MdnsAnnouncer(net.Join(), "x", 8130, Text, lines.Add, networks: Lan);
        using var browser = new MdnsBrowser(net.Join(), ReceiverService.TypeName, lines.Add, networks: Lan);

        Assert.False(announcer.Start());
        Assert.False(browser.Start());

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.All(l, c => Assert.InRange(c, ' ', '~')));
        Assert.All(lines, l => Assert.StartsWith("discovery: cannot ", l, StringComparison.Ordinal));
        Assert.Empty(browser.Current);
    }

    [Fact]
    public void AnInstanceName_IsMadeOfPlainLettersAndFitsALabel()
    {
        Assert.Equal("M0LTE on my-box", ReceiverService.Label("M0LTE on my-box"));
        Assert.Equal("receiver", ReceiverService.Label("  .. "));
        Assert.Equal("a b", ReceiverService.Label("a.b"));
        Assert.True(ReceiverService.Label(new string('x', 200)).Length <= 40);
    }

    [Fact]
    public async Task OnRealSockets_AnAnnouncementIsFound()
    {
        // Needs a machine whose networks multicast (a container or CI runner often cannot), so it runs only where asked:
        // PDN_MAILCAST_REAL_MDNS=1 dotnet test ... --filter OnRealSockets. Another port than 5353 keeps it clear of Avahi.
        if (Environment.GetEnvironmentVariable("PDN_MAILCAST_REAL_MDNS") != "1")
        {
            return;
        }
        int port = 20000 + Random.Shared.Next(20000);
        using var browserSide = new UdpMdnsTransport(port);
        using var announcerSide = new UdpMdnsTransport(port);
        var lines = new List<string>();
        using var browser = new MdnsBrowser(browserSide, ReceiverService.TypeName, lines.Add);
        using var announcer = new MdnsAnnouncer(announcerSide, "socket test", 8130, Text, lines.Add);
        Assert.True(browser.Start());
        Assert.True(announcer.Start());

        for (int i = 0; i < 250 && browser.Current.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        var found = Assert.Single(browser.Current);
        Assert.Equal("socket test", found.Name);
        Assert.Equal(8130, found.Port);
    }

    [Fact]
    public void AName_OverTheLimitOf255Octets_IsRefused()
    {
        var message = new DnsMessage();
        message.Questions.Add(new DnsQuestion(string.Join('.', Enumerable.Repeat(new string('a', 60), 5)), DnsRecord.TypePtr));
        Assert.Null(DnsMessage.Parse(message.ToBytes()));
    }

    private static byte[] Announcement(string instance, string host, ushort port, string address, string? srvName = null, string typeName = ReceiverService.TypeName)
    {
        var m = new DnsMessage { IsResponse = true };
        m.Records.Add(new DnsRecord { Name = typeName, Type = DnsRecord.TypePtr, Ttl = 120, Target = instance });
        m.Records.Add(new DnsRecord { Name = srvName ?? instance, Type = DnsRecord.TypeSrv, Ttl = 120, Port = port, Target = host });
        m.Records.Add(new DnsRecord { Name = host, Type = DnsRecord.TypeA, Ttl = 120, Address = IPAddress.Parse(address) });
        return m.ToBytes();
    }

    [Fact]
    public void TheBrowser_BelievesOnlyLocalSources_LocalAddresses_AndNamesUnderItsType()
    {
        var net = new Network();
        var member = net.Join();
        using var browser = new MdnsBrowser(member, ReceiverService.TypeName, _ => { }, networks: Lan);
        browser.Start();

        member.Inject(Announcement("a._pdn-mailcast._tcp.local", "h.local", 8130, "10.1.1.1"), IPAddress.Parse("8.8.8.8")); // from far away
        Assert.Empty(browser.Current);
        member.Inject(Announcement("b._pdn-mailcast._tcp.local", "h.local", 8130, "8.8.4.4"), IPAddress.Parse("10.2.2.2")); // points far away
        Assert.Equal(IPAddress.Parse("10.2.2.2"), Assert.Single(browser.Current).Address); // the A record was not believed; the sender is
        member.Inject(Announcement("c.other._tcp.local", "h.local", 8130, "10.1.1.1", typeName: "_other._tcp.local"), IPAddress.Parse("10.2.2.3"));
        member.Inject(Announcement("d._pdn-mailcast._tcp.local", "h.local", 8130, "10.1.1.1", srvName: "e.other._tcp.local"), IPAddress.Parse("10.2.2.3"));
        Assert.Single(browser.Current);

        // At most MdnsBrowser.MaxInstances.
        for (int i = 0; i < 100; i++)
        {
            member.Inject(Announcement($"n{i}._pdn-mailcast._tcp.local", $"h{i}.local", 8130, "10.1.1.1"), IPAddress.Parse("10.2.2.9"));
        }
        Assert.True(browser.Current.Count <= MdnsBrowser.MaxInstances);
    }

    [Fact]
    public async Task TheAnnouncer_SaysOnEachNetworkOnlyTheAddressItHasThere()
    {
        var net = new Network();
        var listener = net.Join();
        var heard = new List<DnsMessage>();
        listener.Received += (p, _) => { if (DnsMessage.Parse(p) is { } m) { lock (heard) { heard.Add(m); } } };
        using var announcer = new MdnsAnnouncer(net.Join(IPAddress.Parse("192.168.1.20")), "x", 8130, Text, _ => { }, networks: Lan);
        announcer.Start();
        await Eventually(() => { lock (heard) { return heard.Count > 0; } }, "an announcement");

        lock (heard)
        {
            var a = heard[0].Records.Where(r => r.Type == DnsRecord.TypeA).ToList();
            Assert.Equal(IPAddress.Parse("192.168.1.20"), Assert.Single(a).Address);
        }
    }
}
