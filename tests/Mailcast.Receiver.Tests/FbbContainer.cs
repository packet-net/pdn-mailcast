using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// A real Linux FBB (xfbbd 7.0.11, Ubuntu 24.04's fbb package) in docker, built from Fbb/ and set
/// up as docs/receiver.md tells a sysop to set one up: a telnet port, and the user Q0CAST with
/// the B and M flags and a password. Only the end-to-end tests (Category=Docker) use it.
/// </summary>
/// <remarks>
/// There is no published FBB image to pull, so the image is built here, from a pinned Ubuntu and
/// a pinned package version. Docker's cache makes every build after the first a no-op. The tag
/// carries a hash of Fbb/, so branches sharing a runner never test each other's image.
/// </remarks>
internal sealed partial class FbbContainer : IAsyncDisposable
{
    /// <summary>The image built from Fbb/: <c>pdn-mailcast-test-fbb:7.0.11-</c> and 12 hex digits of its hash.</summary>
    public static readonly string Image = "pdn-mailcast-test-fbb:7.0.11-" + FixtureHash(Path.Combine(AppContext.BaseDirectory, "Fbb"));

    public const string Login = "Q0CAST";

    /// <summary>FBB keeps 12 characters of a password, so a longer one could never log in.</summary>
    public const string Password = "mailcast12";

    /// <summary>A plain Telnet user with the M flag but not B: not a forwarding partner, however it answers.</summary>
    public const string PlainLogin = "Q1CAST";

    public const string PlainPassword = "mailcast12";

    private FbbContainer(string id, int telnetPort)
    {
        Id = id;
        TelnetPort = telnetPort;
    }

    public string Id { get; }

    /// <summary>FBB's telnet port (6300 inside) on the host's loopback.</summary>
    public int TelnetPort { get; }

    public static async Task<FbbContainer> StartAsync(CancellationToken cancellation)
    {
        await LinBpqContainer.DockerAsync(cancellation, "build", "-q", "-t", Image, Path.Combine(AppContext.BaseDirectory, "Fbb"));
        string id = (await LinBpqContainer.DockerAsync(cancellation, "create", "-p", "127.0.0.1::6300", Image)).Trim();
        try
        {
            await LinBpqContainer.DockerAsync(cancellation, "start", id);
            string mapping = (await LinBpqContainer.DockerAsync(cancellation, "port", id, "6300/tcp")).Trim().Split('\n')[0];
            var container = new FbbContainer(id, int.Parse(mapping[(mapping.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture));
            try
            {
                await container.WaitForSetupAsync(cancellation);
                await container.WaitForLoginAsync(cancellation);
            }
            catch (OperationCanceledException e) when (cancellation.IsCancellationRequested)
            {
                // Whenever the deadline passes mid-try, so that a timeout always says why.
                string logs = await LinBpqContainer.DockerAsync(CancellationToken.None, "logs", id);
                throw new OperationCanceledException($"FBB's container never finished starting up; {e.Message}; container log: {logs}", e, cancellation);
            }
            return container;
        }
        catch
        {
            await LinBpqContainer.DockerAsync(CancellationToken.None, "rm", "-f", id);
            throw;
        }
    }

    /// <summary>
    /// Waits for start.sh's own log line for <see cref="PlainLogin"/>, which only ever prints
    /// after its "EU Q0CAST ..." console command and echo have both already completed (start.sh
    /// runs its lines in order, and <c>set -e</c> would stop it dead if an earlier one failed), so
    /// seeing it means both users it creates really exist.
    /// </summary>
    /// <remarks>
    /// Without this, a test using <see cref="PlainLogin"/> straight after <see cref="StartAsync"/>
    /// returns could race ahead of start.sh's second "EU" console command and see FBB answer
    /// "Unregistered callsign" for it (issue #113): <see cref="WaitForLoginAsync"/> below only ever
    /// proved <see cref="Login"/> was ready, never <see cref="PlainLogin"/>, and the two "EU"
    /// commands are seconds apart even on an otherwise idle host, since fbb-console paces every
    /// line it sends with its own fixed sleeps.
    /// </remarks>
    private async Task WaitForSetupAsync(CancellationToken cancellation)
    {
        const string marker = "mailcast: " + PlainLogin + " set up";
        while (true)
        {
            string logs = await LinBpqContainer.DockerAsync(cancellation, "logs", Id);
            if (logs.Contains(marker, StringComparison.Ordinal))
            {
                return;
            }
            await Task.Delay(500, cancellation);
        }
    }

    /// <summary>
    /// Waits until Q0CAST can log in and FBB gives its SID, as it does to a forwarding partner.
    /// Until the container's start script has set Q0CAST up, FBB answers "Unregistered callsign".
    /// </summary>
    /// <remarks>
    /// Each try gets ten seconds, as for LinBPQ: a probe giving up and trying again, not anything
    /// measured.
    /// </remarks>
    private async Task WaitForLoginAsync(CancellationToken cancellation)
    {
        string lastSeen = "";
        while (true)
        {
            if (cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException($"last seen: {lastSeen}", null, cancellation);
            }
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            attempt.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", TelnetPort, attempt.Token);
                var stream = client.GetStream();
                await ReadUntilAsync(stream, "allsign :", attempt.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($".{Login}\r"), attempt.Token);
                lastSeen = await ReadUntilAsync(stream, ":", attempt.Token);
                if (lastSeen.Contains("assword", StringComparison.OrdinalIgnoreCase))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"{Password}\r"), attempt.Token);
                    lastSeen = await ReadUntilAsync(stream, ">", attempt.Token);
                    if (lastSeen.Contains("[FBB-", StringComparison.Ordinal))
                    {
                        return;
                    }
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

    /// <summary>
    /// Completes <see cref="PlainLogin"/>'s first-ever registration over telnet (first name,
    /// city, home BBS, zip code, as FBB's own new-user prompt asks for) and logs off with B.
    /// Afterwards a dot-login to it reaches a bare prompt too, the same shape a real forwarding
    /// partner's does, which is issue #108's coordinator follow-up: whether that bare prompt can
    /// still be told apart from a real partner's. See <see cref="ProbeHandshakeAsync"/> and
    /// BbsLoginTestFbbTests.cs, which use this to set up that case against a real FBB.
    /// </summary>
    public async Task RegisterPlainUserAsync(CancellationToken cancellation)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", TelnetPort, cancellation);
        var stream = client.GetStream();
        await ReadUntilAsync(stream, "allsign :", cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($".{PlainLogin}\r"), cancellation);
        await ReadUntilAsync(stream, "assword :", cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{PlainPassword}\r"), cancellation);
        await ReadUntilAsync(stream, "first name", cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("TestUser\r"), cancellation);
        await ReadUntilAsync(stream, "ZIP code !", cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("Reading\r"), cancellation);
        await ReadUntilAsync(stream, "HomeBBS", cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GB7TST\r"), cancellation);
        await ReadUntilAsync(stream, "ZIP code", cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RG11AA\r"), cancellation);
        await ReadUntilAsync(stream, ">", cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("B\r"), cancellation);
        // Give FBB a moment to count this as a clean, finished session before the socket closes.
        await Task.Delay(500, cancellation);
    }

    /// <summary>
    /// Logs in as <paramref name="login"/> and, if the reply ends at a bare prompt, sends the
    /// same harmless SID-and-FF handshake a real delivery session opens with when it has nothing
    /// to send (<see cref="Mailcast.Receiver.Delivery.BbsClient"/>'s Run.FeedAsync): our SID, then
    /// FF, meaning "nothing to propose". Returns FBB's reply to that handshake, or null if the
    /// login never reached a bare prompt to send it to. Used to confirm, against a real FBB, that
    /// a correctly set-up partner and an already-registered non-partner answer identically (issue
    /// #108's coordinator follow-up); see BbsLoginTestFbbTests.cs.
    /// </summary>
    public async Task<string?> ProbeHandshakeAsync(string login, string password, CancellationToken cancellation)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", TelnetPort, cancellation);
        var stream = client.GetStream();
        await ReadUntilAsync(stream, "allsign :", cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($".{login}\r"), cancellation);
        await ReadUntilAsync(stream, "assword :", cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{password}\r"), cancellation);
        string loginReply = await ReadUntilAsync(stream, ">", cancellation);
        if (!loginReply.TrimEnd().EndsWith('>'))
        {
            return null;
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes("[PDN-MAILCASTTEST-B1FHM$]\r\n"), cancellation);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("FF\r\n"), cancellation);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        idle.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        var reply = new StringBuilder();
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, idle.Token);
                if (read == 0)
                {
                    break;
                }
                reply.Append(Encoding.Latin1.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // The idle timeout, not the caller's: whatever arrived in the 5 s is the answer.
        }
        return reply.ToString();
    }

    /// <summary>
    /// The first 12 hex digits of a SHA-256 over every file in <paramref name="dir"/>, each name
    /// and then its contents, in name order.
    /// </summary>
    private static string FixtureHash(string dir)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\n"));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
    }

    /// <summary>
    /// The bulletins in the BBS, each as FBB's sysop sees it on the console: its line in LL (list),
    /// and R (read). FBB's own notices to the sysop, such as "UNKNOWN ROUTE" for a bulletin to an
    /// @ it has no forwarding route for, are private messages and left out.
    /// </summary>
    public async Task<IReadOnlyList<(string Listing, string Text)>> ReadAllBulletinsAsync(CancellationToken cancellation)
    {
        // As in LinBPQ, L lists only what is new since the last L; LL lists the last n, new or not.
        string listing = await ConsoleAsync(cancellation, "LL 1000");
        var messages = new List<(string, string)>();
        foreach (Match line in BulletinLine().Matches(listing))
        {
            string text = await ConsoleAsync(cancellation, "R " + line.Groups[1].Value);
            messages.Add((line.Value.Trim(), text));
        }
        return messages;
    }

    /// <summary>One session on FBB's sysop console, typing <paramref name="lines"/>; see Fbb/fbb-console.</summary>
    private async Task<string> ConsoleAsync(CancellationToken cancellation, params string[] lines)
    {
        string text = await LinBpqContainer.DockerAsync(cancellation, ["exec", Id, "fbb-console", .. lines]);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>A bulletin's line in FBB's list: its number, then a type of B.</summary>
    [GeneratedRegex(@"^(\d+)\s+B\S*\s.*$", RegexOptions.Multiline)]
    private static partial Regex BulletinLine();

    private static async Task<string> ReadUntilAsync(NetworkStream stream, string marker, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (!text.ToString().Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            int read = await stream.ReadAsync(buffer, cancellation);
            if (read == 0)
            {
                throw new IOException($"FBB closed the connection before \"{marker}\"; had: {text}");
            }
            text.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }
        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await LinBpqContainer.DockerAsync(CancellationToken.None, "rm", "-f", Id);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
