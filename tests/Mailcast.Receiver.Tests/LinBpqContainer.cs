using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// A real LinBPQ with its mail, in docker, set up from LinBpq/bpq32.cfg and LinBpq/linmail.cfg as
/// README.md tells a sysop to set one up. Only the end-to-end tests (Category=Docker) use it.
/// </summary>
internal sealed partial class LinBpqContainer : IAsyncDisposable
{
    /// <summary>m0lte/linbpq, LinBPQ 6.0.25.41, as published from packet-net's fork on 2026-09-29.</summary>
    public const string Image = "m0lte/linbpq@sha256:b01155258c50c0a3f039bc7e3db92e669af505fa95328470980d977d54dd6903";

    public const string Login = "Q0CAST";
    public const string Password = "mailcast-test";

    private LinBpqContainer(string id, int fbbPort, int telnetPort)
    {
        Id = id;
        FbbPort = fbbPort;
        TelnetPort = telnetPort;
    }

    public string Id { get; }

    /// <summary>The FBBPORT on the host's loopback.</summary>
    public int FbbPort { get; }

    /// <summary>The ordinary telnet port, for reading the BBS as its sysop.</summary>
    public int TelnetPort { get; }

    public static async Task<LinBpqContainer> StartAsync(CancellationToken cancellation)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "LinBpq");
        string id = (await DockerAsync(cancellation, "create", "-p", "127.0.0.1::8011", "-p", "127.0.0.1::8010", Image)).Trim();
        try
        {
            await DockerAsync(cancellation, "cp", Path.Combine(fixture, "bpq32.cfg"), $"{id}:/data/bpq32.cfg");
            await DockerAsync(cancellation, "cp", Path.Combine(fixture, "linmail.cfg"), $"{id}:/data/linmail.cfg");
            await DockerAsync(cancellation, "start", id);
            int fbb = await PortAsync(id, 8011, cancellation);
            int telnet = await PortAsync(id, 8010, cancellation);
            var container = new LinBpqContainer(id, fbb, telnet);
            await container.WaitForMailAsync(cancellation);
            return container;
        }
        catch
        {
            await DockerAsync(CancellationToken.None, "rm", "-f", id);
            throw;
        }
    }

    /// <summary>Waits until BPQMail answers on the FBBPORT with its SID.</summary>
    /// <remarks>
    /// Each try gets ten seconds: while LinBPQ is starting, a connection can be accepted (by
    /// docker's proxy, or by the node before its mail is up) and then answered with nothing.
    /// That is a probe giving up and trying again, not anything measured.
    /// </remarks>
    private async Task WaitForMailAsync(CancellationToken cancellation)
    {
        string lastSeen = "";
        while (true)
        {
            if (cancellation.IsCancellationRequested)
            {
                string logs = await DockerAsync(CancellationToken.None, "logs", Id);
                throw new OperationCanceledException($"LinBPQ's mail never answered on its FBBPORT; last seen: {lastSeen}; container log: {logs}");
            }
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            attempt.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", FbbPort, attempt.Token);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{Login}\r{Password}\rBBS\r"), attempt.Token);
                lastSeen = await ReadUntilAsync(stream, ">", attempt.Token);
                if (lastSeen.Contains("[BPQ-", StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (Exception e) when (e is SocketException or IOException || (e is OperationCanceledException && !cancellation.IsCancellationRequested))
            {
                lastSeen = e.Message;
            }
            try
            {
                await Task.Delay(1000, cancellation);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>The messages in the BBS, each as LinBPQ's sysop sees it: its line in L (list), and R (read).</summary>
    public async Task<IReadOnlyList<(string Listing, string Text)>> ReadAllMessagesAsync(CancellationToken cancellation)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", TelnetPort, cancellation);
        var stream = client.GetStream();
        await ReadUntilAsync(stream, "user:", cancellation);
        await stream.WriteAsync("sysop\r"u8.ToArray(), cancellation);
        await ReadUntilAsync(stream, "password:", cancellation);
        await stream.WriteAsync("sysop\r"u8.ToArray(), cancellation);
        await ReadUntilAsync(stream, "\r", cancellation);
        await stream.WriteAsync("BBS\r"u8.ToArray(), cancellation);
        await ReadUntilAsync(stream, "de GB7TST>", cancellation);
        // L lists only what is new since the last L; LL lists the last n, new or not.
        await stream.WriteAsync("LL 1000\r"u8.ToArray(), cancellation);
        string listing = await ReadUntilAsync(stream, "de GB7TST>", cancellation);
        var lines = ListingNumber().Matches(listing)
            .Select(m => (Number: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Line: m.Value.Trim()))
            .OrderBy(m => m.Number)
            .ToList();
        var messages = new List<(string, string)>();
        foreach (var (number, line) in lines)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"R {number}\r"), cancellation);
            messages.Add((line, await ReadUntilAsync(stream, "de GB7TST>", cancellation)));
        }
        await stream.WriteAsync("B\r"u8.ToArray(), cancellation);
        return messages;
    }

    [GeneratedRegex(@"^\s*(\d+)\s+\d\d-.*$", RegexOptions.Multiline)]
    private static partial Regex ListingNumber();

    private static async Task<string> ReadUntilAsync(NetworkStream stream, string marker, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (!text.ToString().Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            int read = await stream.ReadAsync(buffer, cancellation);
            if (read == 0)
            {
                throw new IOException($"LinBPQ closed the connection before \"{marker}\"; had: {text}");
            }
            text.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static async Task<int> PortAsync(string id, int containerPort, CancellationToken cancellation)
    {
        string mapping = (await DockerAsync(cancellation, "port", id, $"{containerPort}/tcp")).Trim().Split('\n')[0];
        return int.Parse(mapping[(mapping.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
    }

    public static async Task<string> DockerAsync(CancellationToken cancellation, params string[] args)
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

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DockerAsync(CancellationToken.None, "rm", "-f", Id);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
