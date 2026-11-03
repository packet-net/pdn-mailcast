using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// A real XRouter (505p, ghcr.io/packethacking/xrouter) in docker, with its mailbox as a full BBS
/// (PMSTYPE=4), the receiver's login in USERPASS.SYS and its address in ACCESS.SYS, from XRouter/, as docs/receiver.md tells
/// a sysop to set one up. Only the Docker tests use it.
/// </summary>
internal sealed class XRouterContainer : IAsyncDisposable
{
    public const string Image = "ghcr.io/packethacking/xrouter:505p";

    public const string Login = "Q0CAST";

    public const string Password = "mailcast12";

    private XRouterContainer(string id, int telnetPort)
    {
        Id = id;
        TelnetPort = telnetPort;
    }

    public string Id { get; }

    /// <summary>XRouter's telnet port (23 inside) on the host's loopback.</summary>
    public int TelnetPort { get; }

    public static async Task<XRouterContainer> StartAsync(CancellationToken cancellation)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "XRouter");
        string id = (await LinBpqContainer.DockerAsync(cancellation, "create", "-p", "127.0.0.1::23", Image)).Trim();
        try
        {
            foreach (string file in new[] { "XROUTER.CFG", "USERPASS.SYS", "ACCESS.SYS" })
            {
                await LinBpqContainer.DockerAsync(cancellation, "cp", Path.Combine(fixture, file), $"{id}:/data/{file}");
            }
            await LinBpqContainer.DockerAsync(cancellation, "start", id);
            string mapping = (await LinBpqContainer.DockerAsync(cancellation, "port", id, "23/tcp")).Trim().Split('\n')[0];
            var container = new XRouterContainer(id, int.Parse(mapping[(mapping.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture));
            await container.WaitForPromptAsync(cancellation);
            return container;
        }
        catch
        {
            await LinBpqContainer.DockerAsync(CancellationToken.None, "rm", "-f", id);
            throw;
        }
    }

    /// <summary>Waits until XRouter's telnet port answers with its callsign prompt.</summary>
    private async Task WaitForPromptAsync(CancellationToken cancellation)
    {
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", TelnetPort, cancellation);
                using var stream = client.GetStream();
                var buffer = new byte[512];
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                wait.CancelAfter(TimeSpan.FromSeconds(2));
                int read = await stream.ReadAsync(buffer, wait.Token);
                if (Encoding.Latin1.GetString(buffer, 0, read).Contains("Callsign", StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (Exception e) when (e is SocketException or IOException || (e is OperationCanceledException && !cancellation.IsCancellationRequested))
            {
            }
            await Task.Delay(250, cancellation);
        }
    }

    /// <summary>Every message file in the mailbox's directory, one after another.</summary>
    public Task<string> ReadAllAsync(CancellationToken cancellation) =>
        LinBpqContainer.DockerAsync(cancellation, "exec", Id, "sh", "-c", "cat /data/PMS/0*");

    public async ValueTask DisposeAsync() =>
        await LinBpqContainer.DockerAsync(CancellationToken.None, "rm", "-f", Id);
}
