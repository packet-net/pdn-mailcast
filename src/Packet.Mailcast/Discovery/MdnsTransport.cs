using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Packet.Mailcast.Discovery;

/// <summary>Where mDNS messages go and come from: the network, or in tests a stand-in.</summary>
public interface IMdnsTransport : IDisposable
{
    /// <summary>Raised for each packet received, with the address it came from. May be raised on any thread.</summary>
    event Action<byte[], IPAddress>? Received;

    /// <summary>Starts receiving. Throws <see cref="MdnsUnavailableException"/> if multicast cannot be used here.</summary>
    void Start();

    /// <summary>Sends a packet to the mDNS group on every network that can carry it. Never throws for a network that fails.</summary>
    void Send(byte[] packet);

    /// <summary>
    /// Sends to the group on each network that can carry it, a packet made for that network: the function is given the
    /// address this machine has on it (RFC 6762, section 6.2: a host announces on a network only the addresses it has there).
    /// </summary>
    void Send(Func<IPAddress, byte[]> packetForNetwork);
}

/// <summary>Multicast cannot be used on this machine (no capable network, a firewall, a refused socket).</summary>
public sealed class MdnsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Multicast DNS over UDP, IPv4 (224.0.0.251, port 5353): one socket sharing the port with whatever else
/// listens on it (Avahi, Windows' own DNS client), joined to the group on each network that is up and
/// can multicast. A network that is not up yet, or appears later, is joined when next sent on (a browser asks every 30 s and
/// an announcer announces every minute), so a machine whose network comes up after the program does is not given up on.
/// </summary>
public sealed class UdpMdnsTransport(int port = UdpMdnsTransport.StandardPort) : IMdnsTransport
{
    /// <summary>mDNS's port.</summary>
    public const int StandardPort = 5353;

    /// <summary>The IPv4 group address.</summary>
    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    private readonly Lock _gate = new();
    private readonly HashSet<IPAddress> _joined = [];
    private Socket? _socket;
    private bool _disposed;

    /// <inheritdoc />
    public event Action<byte[], IPAddress>? Received;

    /// <inheritdoc />
    public void Start()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if (OperatingSystem.IsLinux())
                {
                    TryReusePort(socket);
                }
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            lock (_gate)
            {
                _socket = socket;
                JoinNewInterfaces(); // none yet is not a failure: Send tries again
            }
            new Thread(() => ReceiveLoop(socket)) { IsBackground = true, Name = "mdns-receive" }.Start();
        }
        catch (SocketException e)
        {
            throw new MdnsUnavailableException(e.Message, e);
        }
    }

    // Linux wants SO_REUSEPORT as well when another program (Avahi) holds the port (level SOL_SOCKET 1, option 15, Linux only).
    private static void TryReusePort(Socket socket)
    {
        try
        {
            socket.SetRawSocketOption(1 /* SOL_SOCKET */, 15 /* SO_REUSEPORT on Linux */, BitConverter.GetBytes(1));
        }
        catch (SocketException)
        {
            // Not there; ReuseAddress alone may do.
        }
    }

    private static IEnumerable<IPAddress> UsableAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                {
                    yield return unicast.Address;
                }
            }
        }
    }

    private void JoinNewInterfaces()
    {
        foreach (var address in UsableAddresses())
        {
            if (_socket is null || _joined.Contains(address))
            {
                continue;
            }
            try
            {
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, address));
                _joined.Add(address);
            }
            catch (SocketException)
            {
                // This network will not take it (a down VPN, a container bridge); the others may.
            }
        }
    }

    /// <inheritdoc />
    public void Send(byte[] packet) => Send(_ => packet);

    /// <inheritdoc />
    public void Send(Func<IPAddress, byte[]> packetForNetwork)
    {
        lock (_gate)
        {
            if (_socket is null || _disposed)
            {
                return;
            }
            JoinNewInterfaces();
            foreach (var address in _joined)
            {
                try
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    _socket.SendTo(packetForNetwork(address), new IPEndPoint(Group, port));
                }
                catch (SocketException)
                {
                    // One network failing must not stop the others.
                }
            }
        }
    }

    private void ReceiveLoop(Socket socket)
    {
        var buffer = new byte[9000];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            try
            {
                int n = socket.ReceiveFrom(buffer, ref from);
                Received?.Invoke(buffer.AsSpan(0, n).ToArray(), ((IPEndPoint)from).Address);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                if (_disposed)
                {
                    return;
                }
                if (e is ObjectDisposedException)
                {
                    return;
                }
                Thread.Sleep(200); // a transient error; the loop ends only when the socket is closed
            }
#pragma warning disable CA1031 // a handler's failure must not end receiving
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Socket? socket;
        lock (_gate)
        {
            _disposed = true;
            socket = _socket;
            _socket = null;
        }
        socket?.Dispose();
    }
}
