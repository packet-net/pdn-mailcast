using System.Net;
using Mailcast.Receiver.Discovery;
using Packet.Mailcast.Discovery;

namespace Mailcast.Receiver.Tests;

/// <summary>Issue #218: the receiver announces itself on the network, or says why it does not.</summary>
public class AnnouncementTests
{
    private sealed class Bus
    {
        private readonly List<Side> _sides = [];

        public bool Broken { get; set; }

        public Side Join()
        {
            var s = new Side(this);
            lock (_sides)
            {
                _sides.Add(s);
            }
            return s;
        }

        public sealed class Side : IMdnsTransport
        {
            private readonly Bus bus;
            private readonly System.Threading.Channels.Channel<byte[]> _in = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

            public Side(Bus bus)
            {
                this.bus = bus;
                _ = Task.Run(async () =>
                {
                    await foreach (var packet in _in.Reader.ReadAllAsync())
                    {
                        Received?.Invoke(packet, IPAddress.Loopback);
                    }
                });
            }

            public bool Disposed { get; private set; }

            public event Action<byte[], IPAddress>? Received;

            public void Start()
            {
                if (bus.Broken)
                {
                    throw new MdnsUnavailableException("no network here can multicast");
                }
            }

            public void Send(byte[] packet)
            {
                Side[] all;
                lock (bus._sides)
                {
                    all = [.. bus._sides];
                }
                foreach (var s in all)
                {
                    s._in.Writer.TryWrite(packet);
                }
            }

            public void Send(Func<IPAddress, byte[]> packetForNetwork) => Send(packetForNetwork(IPAddress.Loopback));

            public void Dispose()
            {
                Disposed = true;
                _in.Writer.TryComplete();
            }
        }
    }

    private static async Task<FoundService> FindAsync(Bus bus)
    {
        using var browser = new MdnsBrowser(bus.Join(), ReceiverService.TypeName, _ => { });
        browser.Start();
        for (int i = 0; i < 250 && browser.Current.Count == 0; i++)
        {
            await Task.Delay(20);
        }
        return Assert.Single(browser.Current);
    }

    [Fact]
    public async Task AReceiverOnTheNetwork_IsAnnounced_WithItsPortVersionAndApiVersion()
    {
        var bus = new Bus();
        var lines = new List<string>();
        var config = new ReceiverConfig { Web = new WebSettings { Port = 8133 }, Feedback = new Feedback.FeedbackSettings { Callsign = "m0lte" } };
        using var announcement = ReceiverAnnouncement.Start(config, pageOnLan: true, "0.12.3", bus.Join(), lines.Add);

        Assert.NotNull(announcement);
        var found = await FindAsync(bus);
        Assert.StartsWith("M0LTE on ", found.Name, StringComparison.Ordinal);
        Assert.Equal(8133, found.Port);
        Assert.Equal("0.12.3", found.Get("v"));
        Assert.Equal(ReceiverService.ApiVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), found.Get("api"));
        Assert.Null(found.Get("pw"));
        Assert.Single(lines);
        Assert.StartsWith("discovery: announcing this receiver", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutACallsign_TheInstanceIsNamedByTheComputer()
    {
        var plain = new ReceiverConfig();
        Assert.Equal(ReceiverService.Label(Environment.MachineName) + " " + ReceiverService.MachineSuffix(), ReceiverAnnouncement.InstanceName(plain));
        Assert.DoesNotContain("pw=1", ReceiverAnnouncement.Text("1.0.0")); // the object reads are open whatever the page password
    }

    [Fact]
    public void APageOnlyOnThisMachine_IsNotAnnounced_AndOneLineSaysSo()
    {
        var bus = new Bus();
        var side = bus.Join();
        var lines = new List<string>();
        var announcement = ReceiverAnnouncement.Start(new ReceiverConfig { Web = new WebSettings { Lan = false } }, pageOnLan: false, "1.0.0", side, lines.Add);

        Assert.Null(announcement);
        Assert.True(side.Disposed);
        var line = Assert.Single(lines);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        Assert.Contains("only on this machine", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenMulticastIsUnavailable_TheReceiverCarriesOn_WithOneShortAsciiLine()
    {
        var bus = new Bus { Broken = true };
        var lines = new List<string>();
        var announcement = ReceiverAnnouncement.Start(new ReceiverConfig(), pageOnLan: true, "1.0.0", bus.Join(), lines.Add);

        Assert.Null(announcement);
        var line = Assert.Single(lines);
        Assert.StartsWith("discovery: cannot announce", line, StringComparison.Ordinal);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
    }
}
