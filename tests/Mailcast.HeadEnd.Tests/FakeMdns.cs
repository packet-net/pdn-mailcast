using System.Net;
using Packet.Mailcast.Discovery;

namespace Mailcast.HeadEnd.Tests;

/// <summary>A network for multicast DNS in memory: whatever one member sends, every member (itself too, as multicast loops back) gets.</summary>
internal sealed class FakeMdnsNetwork
{
    private readonly List<Member> _members = [];

    public bool Broken { get; set; }

    public IMdnsTransport Join(IPAddress? address = null)
    {
        var m = new Member(this, address ?? IPAddress.Loopback);
        lock (_members)
        {
            _members.Add(m);
        }
        return m;
    }

    private sealed class Member : IMdnsTransport
    {
        private readonly FakeMdnsNetwork net;
        private readonly IPAddress address;
        private readonly System.Threading.Channels.Channel<(byte[] Packet, IPAddress From)> _in = System.Threading.Channels.Channel.CreateUnbounded<(byte[], IPAddress)>();

        public Member(FakeMdnsNetwork net, IPAddress address)
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

        public void Dispose()
        {
            lock (net._members)
            {
                net._members.Remove(this);
            }
            _in.Writer.TryComplete();
        }
    }
}
