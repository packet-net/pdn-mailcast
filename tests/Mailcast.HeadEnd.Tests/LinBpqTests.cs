using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// The intake against a real LinBPQ in docker, configured as docs/headend.md says: bulletins
/// posted on the BBS reach the head end, personal mail and local bulletins do not.
/// </summary>
[Trait("Category", "Docker")]
public sealed class LinBpqTests
{
    /// <summary>m0lte/linbpq, LinBPQ 6.0.25.41, the image the receiver's end-to-end test uses.</summary>
    private const string Image = "m0lte/linbpq@sha256:b01155258c50c0a3f039bc7e3db92e669af505fa95328470980d977d54dd6903";

    [Fact]
    public async Task Intake_TakesTheBulletinsLinBpqQueuesForThePartner()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string fixture = Path.Combine(AppContext.BaseDirectory, "LinBpq");
        string id = (await Docker(timeout.Token, "create", "-p", "127.0.0.1::8011", "-p", "127.0.0.1::8010", Image)).Trim();
        try
        {
            await Docker(timeout.Token, "cp", Path.Combine(fixture, "bpq32.cfg"), $"{id}:/data/bpq32.cfg");
            await Docker(timeout.Token, "cp", Path.Combine(fixture, "linmail.cfg"), $"{id}:/data/linmail.cfg");
            await Docker(timeout.Token, "start", id);
            int fbbPort = await Port(id, 8011, timeout.Token);
            int telnetPort = await Port(id, 8010, timeout.Token);

            using var sysop = await Sysop.LogInAsync(telnetPort, timeout.Token);
            string ww = await sysop.PostAsync("SB ALL @ WW", "Mailcast WW test", "to everyone", timeout.Token);
            string gbr = await sysop.PostAsync("SB NEWS @ GBR", "Mailcast GBR test", "to the UK", timeout.Token);
            string euro = await sysop.PostAsync("SB RSGB @ EURO", "Mailcast EURO test", "to Europe", timeout.Token);
            string local = await sysop.PostAsync("SB LOCAL @ GB7TST", "Local notice", "stays here", timeout.Token);
            string personal = await sysop.PostAsync("SP G4XYZ @ GB7XYZ.#12.GBR.EURO", "Personal", "for one person", timeout.Token);
            Assert.Contains("Q0HEAD 3 Msgs", await sysop.CommandAsync("FWD QUEUE", timeout.Token), StringComparison.Ordinal);

            using var state = new TempDirectory();
            var journal = new MemoryJournal();
            var store = new RotationStore(state.Path, Compression.Default, new ScheduleOptions(), journal);
            var config = new FbbIntakeConfig
            {
                Host = "127.0.0.1",
                Port = fbbPort,
                PartnerCallsign = "Q0HEAD",
                User = "Q0HEAD",
                Password = "headend-test",
                Login = FbbIntakeConfig.LinBpqFbbPort,
                SessionTimeoutSeconds = 60,
            };
            using var intake = new FbbIntake(config, store, new IntakePolicy(32 * 1024), journal, TimeProvider.System);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var result = await intake.CollectAsync(today, timeout.Token);

            Assert.True(result.Problem is null, $"{result.Problem}\n{string.Join('\n', journal.Lines)}");
            Assert.Equal(3, result.Accepted);
            Assert.Equal(0, result.Refused);
            var directory = store.Plan(BroadcastScheduler.Midnight(today).AddHours(12), 1, Compression.Default, new ScheduleOptions()).Directory;
            Assert.Equal(new[] { ww, gbr, euro }.Order(), directory.Entries.Select(e => e.Bid).Order());
            Assert.DoesNotContain(local, directory.Entries.Select(e => e.Bid));
            Assert.DoesNotContain(personal, directory.Entries.Select(e => e.Bid));

            // LinBPQ counts them delivered: nothing left for the partner, and a second pass takes nothing.
            Assert.DoesNotContain("Q0HEAD", await sysop.CommandAsync("FWD QUEUE", timeout.Token), StringComparison.Ordinal);
            var again = await intake.CollectAsync(today, timeout.Token);
            Assert.Null(again.Problem);
            Assert.Equal(0, again.Accepted);
        }
        finally
        {
            await Docker(CancellationToken.None, "rm", "-f", id);
        }
    }

    /// <summary>
    /// Issue #136: the report reader logs in as Q0CAST, set up as a receiver's login is on GB7RDG
    /// (a BBS user with a forwarding record), lists the bulletins to MCAST and notes each report
    /// whose sender is the callsign in its title. Nothing on the BBS changes: the reports are
    /// still there to read.
    /// </summary>
    [Fact]
    public async Task ReportReader_ListsTheReportsToMcast_AsAReceiversLogin()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string fixture = Path.Combine(AppContext.BaseDirectory, "LinBpq");
        string id = (await Docker(timeout.Token, "create", "-p", "127.0.0.1::8011", "-p", "127.0.0.1::8010", Image)).Trim();
        try
        {
            await Docker(timeout.Token, "cp", Path.Combine(fixture, "bpq32.cfg"), $"{id}:/data/bpq32.cfg");
            await Docker(timeout.Token, "cp", Path.Combine(fixture, "linmail.cfg"), $"{id}:/data/linmail.cfg");
            await Docker(timeout.Token, "start", id);
            int fbbPort = await Port(id, 8011, timeout.Token);
            int telnetPort = await Port(id, 8010, timeout.Token);

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            using var sysop = await Sysop.LogInAsync(telnetPort, timeout.Token);
            await sysop.PostAsync("SB MCAST @ GB7TST.#42.GBR.EURO", $"MCR G4TST {today.AddDays(-1):yyyy-MM-dd}", "MCR1 0.16.0 IO91lk sc 3/3 0 0 0", timeout.Token);
            // Someone else's callsign in the title: not the sender's, so not counted.
            await sysop.PostAsync("SB MCAST @ GB7TST.#42.GBR.EURO", $"MCR G8BPQ {today.AddDays(-1):yyyy-MM-dd}", "MCR1 0.16.0 IO91lk sc 3/3 0 0 0", timeout.Token);
            await sysop.PostAsync("SB ALL @ WW", "Not a report", "hello", timeout.Token);
            // Older reports, enough that the listing runs past a page.
            for (int back = 2; back <= 12; back++)
            {
                await sysop.PostAsync("SB MCAST @ GB7TST.#42.GBR.EURO", $"MCR G4TST {today.AddDays(-back):yyyy-MM-dd}", "MCR1 0.16.0 IO91lk sc 3/3 0 0 0", timeout.Token);
            }
            // Q0CAST with paging on (10 lines a page, LinBPQ's least), as a user might have left it.
            await SetPageLengthAsync(fbbPort, "Q0CAST", "cast-test", 10, timeout.Token);

            using var state = new TempDirectory();
            var journal = new MemoryJournal();
            var log = new ReportLog(state.Path, journal);
            var config = new ReportsReaderConfig { Host = "127.0.0.1", Port = fbbPort, User = "Q0CAST", Password = "cast-test" };
            using var reader = new ReportReader(config, log, journal, TimeProvider.System);
            var result = await reader.CollectAsync(today, timeout.Token);

            Assert.True(result.Problem is null, $"{result.Problem}\n{string.Join('\n', journal.Lines)}");
            var heard = Assert.Single(log.All);
            Assert.Equal(("G4TST", today.AddDays(-1)), (heard.Key, heard.Value.Day));
            Assert.Equal($"reports={today.AddDays(-1):yyyy-MM-dd}:G4TST", log.For(today)!.ToField());
            Assert.Contains(journal.Lines, l => l.StartsWith("reports: 1 new of 12 listed", StringComparison.Ordinal));
            Assert.DoesNotContain(journal.Lines, l => l.Contains("cast-test", StringComparison.Ordinal));

            // Still on the BBS for anyone to read.
            Assert.Contains("MCR G4TST", await sysop.CommandAsync("L> MCAST", timeout.Token), StringComparison.Ordinal);

            // A wrong password is a problem in the journal, which never gives the password.
            using var wrong = new ReportReader(config with { Password = "not-it" }, new ReportLog(state.Path, journal, readOnly: true), journal, TimeProvider.System);
            var refused = await wrong.CollectAsync(today, timeout.Token);
            Assert.NotNull(refused.Problem);
            Assert.Contains("password", refused.Problem, StringComparison.Ordinal);
            Assert.DoesNotContain("not-it", refused.Problem, StringComparison.Ordinal);
        }
        finally
        {
            await Docker(CancellationToken.None, "rm", "-f", id);
        }
    }

    /// <summary>Logs in on the FBBPORT and sets the user's page length with LinBPQ's OP command.</summary>
    private static async Task SetPageLengthAsync(int port, string user, string password, int lines, CancellationToken cancellation)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, cancellation);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{user}\r{password}\rBBS\r"), cancellation);
        string said = await ReadUntilAsync(stream, "de GB7TST>", cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"OP {lines}\r"), cancellation);
        said = await ReadUntilAsync(stream, "de GB7TST>", cancellation);
        Assert.DoesNotContain("too short", said, StringComparison.Ordinal);
        await stream.WriteAsync("B\r"u8.ToArray(), cancellation);
    }

    private static async Task<string> ReadUntilAsync(NetworkStream stream, string marker, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            int n = await stream.ReadAsync(buffer, cancellation);
            if (n == 0)
            {
                break;
            }
            text.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }
        return text.ToString();
    }

    /// <summary>The sysop on LinBPQ's ordinary telnet port, in the BBS.</summary>
    private sealed class Sysop : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        private Sysop(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<Sysop> LogInAsync(int port, CancellationToken cancellation)
        {
            while (true)
            {
                try
                {
                    var client = new TcpClient();
                    await client.ConnectAsync("127.0.0.1", port, cancellation);
                    var sysop = new Sysop(client);
                    await sysop.UntilAsync("user:", cancellation);
                    await sysop.SendAsync("sysop", cancellation);
                    await sysop.UntilAsync("password:", cancellation);
                    await sysop.SendAsync("sysop", cancellation);
                    await sysop.UntilAsync("\r", cancellation);
                    await sysop.SendAsync("BBS", cancellation);
                    await sysop.UntilAsync("de GB7TST>", cancellation);
                    return sysop;
                }
                catch (Exception e) when (e is SocketException or IOException)
                {
                    // LinBPQ is still starting.
                    await Task.Delay(500, cancellation);
                }
            }
        }

        /// <summary>Posts a message and returns its BID.</summary>
        public async Task<string> PostAsync(string command, string title, string text, CancellationToken cancellation)
        {
            await SendAsync(command, cancellation);
            await UntilAsync("Title", cancellation);
            await SendAsync(title, cancellation);
            await UntilAsync("/ex", cancellation);
            await SendAsync(text, cancellation);
            await SendAsync("/EX", cancellation);
            string answer = await UntilAsync("de GB7TST>", cancellation);
            int at = answer.IndexOf("Bid:", StringComparison.Ordinal);
            return answer[(at + 4)..].Trim().Split(' ')[0];
        }

        public async Task<string> CommandAsync(string command, CancellationToken cancellation)
        {
            await SendAsync(command, cancellation);
            return await UntilAsync("de GB7TST>", cancellation);
        }

        private async Task SendAsync(string line, CancellationToken cancellation)
        {
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), cancellation);
            // LinBPQ reads a message's last line and its /EX apart only if they arrive apart.
            await Task.Delay(300, cancellation);
        }

        private async Task<string> UntilAsync(string marker, CancellationToken cancellation)
        {
            var text = new StringBuilder();
            var buffer = new byte[4096];
            while (!text.ToString().Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                int n = await _stream.ReadAsync(buffer, cancellation);
                if (n == 0)
                {
                    throw new IOException($"LinBPQ closed the connection before \"{marker}\"; had: {text}");
                }
                text.Append(Encoding.Latin1.GetString(buffer, 0, n));
            }
            return text.ToString();
        }

        public void Dispose() => _client.Dispose();
    }

    private static async Task<int> Port(string id, int containerPort, CancellationToken cancellation)
    {
        string mapping = (await Docker(cancellation, "port", id, $"{containerPort}/tcp")).Trim().Split('\n')[0];
        return int.Parse(mapping[(mapping.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
    }

    private static async Task<string> Docker(CancellationToken cancellation, params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("cannot start docker");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {string.Join(' ', args)} failed: {await error}");
        }
        return await output;
    }
}
