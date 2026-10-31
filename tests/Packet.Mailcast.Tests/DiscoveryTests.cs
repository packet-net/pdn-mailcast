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

        public sealed class Member(Network net, IPAddress address) : IMdnsTransport
        {
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
                    _ = Task.Run(() => m.Received?.Invoke(packet, address));
                }
            }

            public void Inject(byte[] packet, IPAddress from) => Received?.Invoke(packet, from);

            public void Dispose()
            {
            }
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
        using var announcer = new MdnsAnnouncer(net.Join(IPAddress.Parse("10.45.0.77")), "M0LTE on box", 8130, Text, lines.Add, addresses: () => [IPAddress.Parse("10.45.0.77")]);
        using var browser = new MdnsBrowser(net.Join(), ReceiverService.TypeName, lines.Add);
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
        using var browser = new MdnsBrowser(member, ReceiverService.TypeName, _ => { }, time);
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
        using var announcer = new MdnsAnnouncer(net.Join(), "x", 8130, Text, lines.Add);
        using var browser = new MdnsBrowser(net.Join(), ReceiverService.TypeName, lines.Add);

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
    public async Task OnRealSockets_AnAnnouncementIsFound_WhereMulticastWorks()
    {
        // Another port than 5353 keeps this clear of Avahi and of other test runs. A machine whose networks cannot multicast
        // (a container, a CI runner) cannot run this; the transport says so and the test ends there.
        int port = 20000 + Random.Shared.Next(20000);
        using var browserSide = new UdpMdnsTransport(port);
        using var announcerSide = new UdpMdnsTransport(port);
        var lines = new List<string>();
        using var browser = new MdnsBrowser(browserSide, ReceiverService.TypeName, lines.Add);
        using var announcer = new MdnsAnnouncer(announcerSide, "socket test", 8130, Text, lines.Add);
        if (!browser.Start() || !announcer.Start())
        {
            Assert.All(lines, l => Assert.StartsWith("discovery: cannot ", l, StringComparison.Ordinal));
            return;
        }

        for (int i = 0; i < 150 && browser.Current.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        // Multicast can be offered by the system and filtered in between; only a find is checked, not its absence.
        if (browser.Current.Count == 1)
        {
            Assert.Equal("socket test", browser.Current[0].Name);
            Assert.Equal(8130, browser.Current[0].Port);
        }
    }
}
