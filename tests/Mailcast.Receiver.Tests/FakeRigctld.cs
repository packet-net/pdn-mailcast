using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Packet.SoundModem.Rig;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// Copied from pdn-soundmodem's tests (tests/Packet.SoundModem.Tests/Rig/FakeRigctld.cs, v0.86.0), with
/// <see cref="Recorded"/> added so a test can put its commands in order with another fake's.
/// An in-process rigctld: a TCP listener on a loopback port the OS chose, speaking the plain text
/// protocol for <c>f F m M t T</c> against a rig that is only a few fields.
/// </summary>
/// <remarks>
/// Every command is recorded, numbered by the connection it arrived on, so a test can say "the
/// first thing the second connection did was T 0". <see cref="Kill"/> closes every open
/// connection, which is what a rigctld that dies looks like from the other end, and
/// <see cref="Accepting"/> false makes it accept and immediately close, which is what one that is
/// starting up, or wedged, looks like.
/// </remarks>
internal sealed class FakeRigctld : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<TcpClient> _open = [];
    private readonly List<(int Connection, string Command)> _commands = [];
    private readonly Task _accepting;
    private int _connections;

    internal FakeRigctld(long dialHz = 14_074_000, string mode = "USB", int passbandHz = 2400)
    {
        DialHz = dialHz;
        Mode = mode;
        PassbandHz = passbandHz;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    internal RigctldEndpoint Endpoint => new("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);

    internal long DialHz { get; set; }

    internal string Mode { get; set; }

    internal int PassbandHz { get; set; }

    internal bool Ptt { get; set; }

    /// <summary>False: accept, then close at once.</summary>
    internal bool Accepting { get; set; } = true;

    /// <summary>Answer an <c>M</c> with a non-zero passband with RPRT -9.</summary>
    internal bool RefusesPassband { get; set; }

    /// <summary>The widest passband the rig will set; wider asks get this.</summary>
    internal int? WidestPassbandHz { get; set; }

    /// <summary>Take every <c>F</c> with RPRT 0 but leave the dial here.</summary>
    internal long? StuckDialHz { get; set; }

    /// <summary>Answer every <c>F</c> with RPRT -11.</summary>
    internal bool RefusesFrequency { get; set; }

    /// <summary>Answer every read with RPRT -5, as a rig that is switched off does.</summary>
    internal bool RigOff { get; set; }

    /// <summary>Answer <c>\chk_vfo</c> with 1, as a rigctld started with <c>--vfo</c> does.</summary>
    internal bool VfoMode { get; set; }

    /// <summary>Never answer <c>\chk_vfo</c>, as an old rigctld that ignores it does.</summary>
    internal bool IgnoresChkVfo { get; set; }

    /// <summary>Answer <c>T 1</c> with RPRT -9 (and key the rig anyway, the worst case).</summary>
    internal bool RefusesKey { get; set; }

    /// <summary>Answer <c>T 0</c> with RPRT -9 and leave the rig keyed.</summary>
    internal bool RefusesUnkey { get; set; }

    /// <summary>A command (by its first word) that, when it arrives, closes the connection
    /// without an answer and without acting on it, once.</summary>
    internal string? DieOn { get; set; }

    /// <summary>The passband an <c>M mode 0</c> sets.</summary>
    internal int NormalPassbandHz { get; set; } = 2400;

    /// <summary>Called with "rig: " and each set command (F, M, T) as it arrives.</summary>
    internal Action<string>? Recorded { get; set; }

    /// <summary>How many connections have been accepted and served.</summary>
    internal int Connections
    {
        get
        {
            lock (_gate)
            {
                return _connections;
            }
        }
    }

    /// <summary>Every command so far, with the 1-based connection it arrived on.</summary>
    internal IReadOnlyList<(int Connection, string Command)> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    /// <summary>The set commands only (F, M, T), in order.</summary>
    internal IReadOnlyList<string> Sets =>
        [.. Commands.Select(c => c.Command).Where(c => c is ['F' or 'M' or 'T', ' ', ..])];

    /// <summary>Closes every open connection, as a rigctld that died would.</summary>
    internal void Kill()
    {
        lock (_gate)
        {
            foreach (TcpClient client in _open)
            {
                try
                {
                    client.Client.LingerState = new LingerOption(true, 0);
                }
                catch (Exception closing) when (closing is ObjectDisposedException or SocketException)
                {
                    // Already on its way out.
                }

                client.Dispose();
            }

            _open.Clear();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!Accepting)
                {
                    client.Dispose();
                    continue;
                }

                int number;
                lock (_gate)
                {
                    number = ++_connections;
                    _open.Add(client);
                }

                _ = ServeAsync(client, number);
            }
        }
        catch (Exception stopped) when (stopped is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client, int number)
    {
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            NetworkStream stream = client.GetStream();
            while (await reader.ReadLineAsync(_stop.Token) is string line)
            {
                string command = line.Trim();
                if (command is ['F' or 'M' or 'T', ' ', ..])
                {
                    Recorded?.Invoke("rig: " + command);
                }

                lock (_gate)
                {
                    _commands.Add((number, command));
                    if (DieOn is string dying && command.Split(' ')[0] == dying)
                    {
                        DieOn = null;
                        client.Client.LingerState = new LingerOption(true, 0);
                        break;
                    }
                }

                if (IgnoresChkVfo && command == "\\chk_vfo")
                {
                    continue;
                }

                byte[] reply = Encoding.ASCII.GetBytes(Answer(command));
                await stream.WriteAsync(reply, _stop.Token);
            }
        }
        catch (Exception gone) when (gone is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
        }
        finally
        {
            lock (_gate)
            {
                _open.Remove(client);
            }

            client.Dispose();
        }
    }

    private string Answer(string command)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lock (_gate)
        {
            if (RigOff && parts is ["f"] or ["m"] or ["t"])
            {
                return "RPRT -5\n";
            }

            switch (parts)
            {
                case ["\\chk_vfo"]:
                    return VfoMode ? "1\n" : "0\n";
                case ["f"]:
                    return string.Create(CultureInfo.InvariantCulture, $"{DialHz}\n");
                case ["m"]:
                    return string.Create(CultureInfo.InvariantCulture, $"{Mode}\n{PassbandHz}\n");
                case ["t"]:
                    return Ptt ? "1\n" : "0\n";
                case ["F", string hz]:
                    if (RefusesFrequency)
                    {
                        return "RPRT -11\n";
                    }

                    DialHz = StuckDialHz ?? long.Parse(hz, CultureInfo.InvariantCulture);
                    return "RPRT 0\n";
                case ["M", string mode, string width]:
                    int passband = int.Parse(width, CultureInfo.InvariantCulture);
                    if (RefusesPassband && passband != 0)
                    {
                        return "RPRT -9\n";
                    }

                    Mode = mode;
                    PassbandHz = passband == 0 ? NormalPassbandHz
                        : WidestPassbandHz is int widest ? Math.Min(passband, widest)
                        : passband;
                    return "RPRT 0\n";
                case ["T", string on]:
                    if (on == "0" && RefusesUnkey)
                    {
                        return "RPRT -9\n";
                    }

                    Ptt = on != "0";
                    return on != "0" && RefusesKey ? "RPRT -9\n" : "RPRT 0\n";
                default:
                    return "RPRT -4\n";
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Kill();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
