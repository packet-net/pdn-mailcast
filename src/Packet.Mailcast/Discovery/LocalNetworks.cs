using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Packet.Mailcast.Discovery;

/// <summary>
/// The IPv4 networks this machine is on, to tell whether an address is one a packet may honestly come from or point to:
/// mDNS is link-local, so anything claiming to be from, or to be at, an address outside these networks is ignored.
/// </summary>
public sealed class LocalNetworks
{
    private readonly Func<IReadOnlyList<(IPAddress Address, int PrefixLength)>> _read;
    private readonly Lock _gate = new();
    private IReadOnlyList<(IPAddress Address, int PrefixLength)> _cached = [];
    private long _readAt;
    private bool _have;

    /// <summary>Makes one; the network cards' addresses (loopback included) by default, a function for tests.</summary>
    public LocalNetworks(Func<IReadOnlyList<(IPAddress Address, int PrefixLength)>>? read = null) => _read = read ?? FromInterfaces;

    /// <summary>Whether the address is on a network this machine is on.</summary>
    public bool IsLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        IReadOnlyList<(IPAddress Address, int PrefixLength)> networks;
        lock (_gate)
        {
            if (!_have || Environment.TickCount64 - _readAt > 10_000)
            {
                _cached = _read();
                _readAt = Environment.TickCount64;
                _have = true;
            }
            networks = _cached;
        }
        uint a = Value(address);
        foreach (var (own, prefix) in networks)
        {
            uint mask = prefix <= 0 ? 0 : prefix >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);
            if ((Value(own) & mask) == (a & mask))
            {
                return true;
            }
        }
        return false;
    }

    private static uint Value(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    /// <summary>The addresses and prefix lengths of the cards that are up, loopback's 127.0.0.0/8 included.</summary>
    public static IReadOnlyList<(IPAddress Address, int PrefixLength)> FromInterfaces() =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(u => (u.Address, IPAddress.IsLoopback(u.Address) ? 8 : u.PrefixLength)),
    ];
}
