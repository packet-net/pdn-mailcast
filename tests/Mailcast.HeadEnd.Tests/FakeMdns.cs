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

    private sealed class Member(FakeMdnsNetwork net, IPAddress address) : IMdnsTransport
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

        public void Dispose()
        {
            lock (net._members)
            {
                net._members.Remove(this);
            }
        }
    }
}
