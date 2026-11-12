using System.Globalization;
using System.Net.Sockets;

namespace Mailcast.HeadEnd.Service;

/// <summary>
/// Claims "this callsign on this frequency" for as long as the head end runs, so that two head ends
/// on one machine never send as the same station on the same frequency: they would send the same
/// pieces of every bulletin (the piece order is keyed by callsign and frequency), and a listener
/// hearing both would gain nothing from the second. A second one stops at start-up and says what to
/// change. The claim is a Linux abstract socket name, which needs no writable directory (each
/// instance has its own state directory and a private /tmp) and which the system lets go when the
/// process ends, however it ends. Elsewhere there is no check.
/// </summary>
public sealed class StationLock : IDisposable
{
    private readonly Socket? _socket;

    private StationLock(Socket? socket) => _socket = socket;

    /// <summary>The abstract socket name for a callsign and frequency.</summary>
    internal static string Name(string callsign, long? frequencyHz) => string.Create(CultureInfo.InvariantCulture,
        $"\0pdn-mailcast-headend/{callsign.ToUpperInvariant()}/{(frequencyHz is { } hz ? hz.ToString(CultureInfo.InvariantCulture) : "-")}");

    /// <summary>
    /// Claims <paramref name="callsign"/> on <paramref name="frequencyHz"/> (null for a station that
    /// gives none). Null, with <paramref name="problem"/> saying in one line what to change, when
    /// another head end on this machine holds it.
    /// </summary>
    public static StationLock? Take(string callsign, long? frequencyHz, out string? problem)
    {
        problem = null;
        if (!OperatingSystem.IsLinux())
        {
            return new StationLock(null);
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(Name(callsign, frequencyHz)));
            return new StationLock(socket);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            socket.Dispose();
            problem = frequencyHz is { } hz
                ? string.Create(CultureInfo.InvariantCulture, $"another head end on this machine already sends as {callsign.ToUpperInvariant()} on {hz} Hz, so the two would send the same pieces; give this one its own \"frequencyHz\" or \"callsign\"")
                : $"another head end on this machine already sends as {callsign.ToUpperInvariant()} with no \"frequencyHz\", so the two would send the same pieces; give each its own \"frequencyHz\"";
            return null;
        }
        catch (SocketException)
        {
            // Not able to make the claim at all (no Unix sockets here): carry on unchecked rather than refuse to run.
            socket.Dispose();
            return new StationLock(null);
        }
    }

    public void Dispose() => _socket?.Dispose();
}
