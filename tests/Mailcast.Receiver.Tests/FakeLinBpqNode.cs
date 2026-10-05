using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// An in-process LinBPQ node telnet port, as LinBPQ 6.0.25 answers it (checked against the real
/// one in <c>LinBpqNodeTests</c>): the user and password prompts, the welcome, PASSWORD for sysop
/// status, and XMITOFF for the ports it has.
/// </summary>
/// <remarks>
/// <see cref="Kill"/> closes every session, keeping each port's XMITOFF, as a dropped telnet
/// session does; <see cref="Restart"/> also clears XMITOFF, as a LinBPQ restart does; and
/// <see cref="Accepting"/> false accepts and closes at once, as a LinBPQ that is down looks.
/// </remarks>
internal sealed class FakeLinBpqNode : IAsyncDisposable
{
    public const string User = "sysop";
    public const string Password = "s3cret-sysop";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<TcpClient> _open = [];
    private readonly List<string> _commands = [];
    private readonly Dictionary<int, int> _xmitOff = [];
    private readonly Dictionary<int, string> _names = [];
    private readonly List<FakeSession> _sessions = [];
    private readonly Task _accepting;
    private int _connections;

    public FakeLinBpqNode(params int[] ports)
    {
        foreach (int port in ports.Length == 0 ? [1, 2] : ports)
        {
            _xmitOff[port] = 0;
            _names[port] = port == 1 ? "Telnet" : $"HF through QtSoundModem {port}";
        }
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Whether the login has sysop status (SYSOP on its USER= line).</summary>
    public bool Sysop { get; set; } = true;

    /// <summary>False: accept, then close at once.</summary>
    public bool Accepting { get; set; } = true;

    /// <summary>A command that, once, is carried out and then the session closed without an answer.</summary>
    public string? DropAfter { get; set; }

    /// <summary>With <see cref="DropAfter"/>: stop accepting connections as the session closes.</summary>
    public bool RefuseAfterDrop { get; set; }

    /// <summary>A command that, once, is carried out and answered with something that is not LinBPQ's answer.</summary>
    public string? GarbleAfter { get; set; }

    /// <summary>
    /// As LinBPQ's IDLETIME running out: each logged-in session is told "Disconnected from Node -
    /// Telnet Session kept" and keeps its socket, but loses sysop status.
    /// </summary>
    public void IdleOut()
    {
        List<FakeSession> sessions;
        lock (_gate)
        {
            sessions = [.. _sessions];
        }
        foreach (var session in sessions.Where(x => x.State == 2))
        {
            session.Authorised = false;
            session.Send("*** Disconnected from Stream 1\r\nDisconnected from Node - Telnet Session kept\r\n");
        }
    }

    /// <summary>Every logged-in session loses sysop status without a word, as one whose idle-out notice went astray.</summary>
    public void ForgetSysop()
    {
        lock (_gate)
        {
            foreach (var session in _sessions)
            {
                session.Authorised = false;
            }
        }
    }

    private sealed class FakeSession(NetworkStream stream)
    {
        public int State { get; set; }

        public bool Authorised { get; set; }

        public void Send(string text)
        {
            lock (this)
            {
                try
                {
                    stream.Write(Encoding.ASCII.GetBytes(text));
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                }
            }
        }
    }

    /// <summary>Called with "bpq: " and each node command as it arrives.</summary>
    public Action<string>? Recorded { get; set; }

    /// <summary>Every node command so far (never the login).</summary>
    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    public int Connections
    {
        get
        {
            lock (_gate)
            {
                return _connections;
            }
        }
    }

    /// <summary>A port's XMITOFF, as LinBPQ holds it.</summary>
    public int XmitOff(int port)
    {
        lock (_gate)
        {
            return _xmitOff[port];
        }
    }

    public void SetXmitOff(int port, int value)
    {
        lock (_gate)
        {
            _xmitOff[port] = value;
        }
    }

    public Retune.BpqNodeSettings Settings(int hfPort = 2, string user = User, string password = Password) =>
        new() { Host = "127.0.0.1", Port = Port, User = user, Password = password, HfPort = hfPort };

    /// <summary>Closes every session; XMITOFF stays as it was.</summary>
    public void Kill()
    {
        lock (_gate)
        {
            foreach (var client in _open)
            {
                try
                {
                    client.Client.LingerState = new LingerOption(true, 0);
                }
                catch (Exception e) when (e is ObjectDisposedException or SocketException)
                {
                }
                client.Dispose();
            }
            _open.Clear();
        }
    }

    /// <summary>As LinBPQ restarting: every session closed and every XMITOFF back to 0.</summary>
    public void Restart()
    {
        Kill();
        lock (_gate)
        {
            foreach (int port in _xmitOff.Keys.ToList())
            {
                _xmitOff[port] = 0;
            }
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!Accepting)
                {
                    client.Dispose();
                    continue;
                }
                lock (_gate)
                {
                    _connections++;
                    _open.Add(client);
                }
                _ = ServeAsync(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        FakeSession? session = null;
        try
        {
            var stream = client.GetStream();
            session = new FakeSession(stream);
            lock (_gate)
            {
                _sessions.Add(session);
            }
            stream.Write([0xFF, 0xFB, 0x03, 0xFF, 0xFB, 0x01]);
            session.Send("user:");
            var line = new StringBuilder();
            var buffer = new byte[256];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }
                for (int i = 0; i < read; i++)
                {
                    char c = (char)buffer[i];
                    if (c == '\n')
                    {
                        continue;
                    }
                    if (c != '\r')
                    {
                        line.Append(c);
                        continue;
                    }
                    string input = line.ToString();
                    line.Clear();
                    switch (session.State)
                    {
                        case 0:
                            if (input == User)
                            {
                                session.State = 1;
                                session.Send("password:");
                            }
                            else
                            {
                                session.Send("user:");
                            }
                            break;
                        case 1:
                            session.Send("\r\n");
                            if (input == Password)
                            {
                                session.State = 2;
                                session.Send("Connected to GB7TST's Telnet Server\r\n\r\n");
                            }
                            else
                            {
                                session.Send("password:");
                            }
                            break;
                        default:
                            Recorded?.Invoke("bpq: " + input);
                            lock (_gate)
                            {
                                _commands.Add(input);
                            }
                            bool authorised = session.Authorised;
                            string reply = Answer(input, ref authorised);
                            session.Authorised = authorised;
                            if (input == DropAfter)
                            {
                                DropAfter = null;
                                Accepting = !RefuseAfterDrop;
                                client.Client.LingerState = new LingerOption(true, 0);
                                return;
                            }
                            if (input == GarbleAfter)
                            {
                                GarbleAfter = null;
                                reply = "Eh?";
                            }
                            session.Send("TST:GB7TST} " + reply + "\r\n");
                            break;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
        }
        finally
        {
            lock (_gate)
            {
                _open.Remove(client);
                if (session is not null)
                {
                    _sessions.Remove(session);
                }
            }
            client.Dispose();
        }
    }

    private string Answer(string input, ref bool authorised)
    {
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
            case ["PORTS"]:
                lock (_gate)
                {
                    return "Ports\r\n" + string.Join("", _names.OrderBy(p => p.Key).Select(p => $"  {p.Key} {p.Value,-30}\r\n")).TrimEnd('\r', '\n');
                }
            case ["PASSWORD"]:
                if (Sysop)
                {
                    authorised = true;
                    return "Ok";
                }
                return "1 1 1 1 1";
            case ["XMITOFF", ..]:
                if (!authorised)
                {
                    return "Command requires SYSOP status - enter password";
                }
                if (parts.Length < 2 || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out int port))
                {
                    return "Invalid Port Number";
                }
                lock (_gate)
                {
                    if (!_xmitOff.TryGetValue(port, out int was))
                    {
                        return "Invalid Port Number";
                    }
                    if (parts.Length < 3)
                    {
                        return $"XMITOFF {was}";
                    }
                    int now = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    _xmitOff[port] = now;
                    return $"XMITOFF was {was} now {now}";
                }
            default:
                return "Invalid command - Enter ? for command list";
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
