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
                await container.WaitForLoginAsync(cancellation);
            }
            catch (OperationCanceledException e) when (cancellation.IsCancellationRequested)
            {
                // Whenever the deadline passes mid-try, so that a timeout always says why.
                string logs = await LinBpqContainer.DockerAsync(CancellationToken.None, "logs", id);
                throw new OperationCanceledException($"Q0CAST never logged in to FBB; {e.Message}; container log: {logs}", e, cancellation);
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
