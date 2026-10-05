using System.Text.Json;
using Mailcast.Receiver.Retune;
using Mailcast.Receiver.Web;

namespace Mailcast.Receiver.Tests;

/// <summary>The page's view of the shared radio: there when retuning, and never the sysop password.</summary>
public sealed class RetuneStatusTests
{
    /// <summary>
    /// A port nothing has: the page is never started here, but HttpListener still binds its
    /// prefix when it is closed, so the default 8130 would clash with anything else on it.
    /// </summary>
    private static WebSettings FreeWeb()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new WebSettings { Port = port };
    }

    [Fact]
    public async Task Status_ShowsTheRetunerAndNeverThePassword()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig
        {
            Audio = "plughw:CARD=Device,DEV=0",
            StateDirectory = dir.Path,
            Rig = new RigSettings { Rigctld = "127.0.0.1:4532" },
            Bpq = new BpqNodeSettings { User = "sysop", Password = "s3cret-sysop", HfPort = 2 },
            Web = FreeWeb(),
        };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        var page = new StatusPage(host, null, _ => { });
        using var closing = new PageCloser(page);

        string status = JsonSerializer.Serialize(page.Status(), ReceiverConfig.JsonLine);
        string settings = JsonSerializer.Serialize(StatusPage.SettingsView(config), ReceiverConfig.JsonLine);

        using var json = JsonDocument.Parse(status);
        var retune = json.RootElement.GetProperty("retune");
        Assert.Equal("idle", retune.GetProperty("stage").GetString());
        Assert.Equal("127.0.0.1:4532", retune.GetProperty("rigctld").GetString());
        Assert.Equal("LinBPQ's node at 127.0.0.1:8010 as sysop, port 2", retune.GetProperty("linBpq").GetString());
        Assert.DoesNotContain("s3cret-sysop", status, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-sysop", settings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_WithoutRig_HasNoRetuneBlock()
    {
        using var dir = new TempDirectory();
        await using var host = new ReceiverHost(new ReceiverConfig { Audio = "wav:/nonexistent.wav", StateDirectory = dir.Path, Web = FreeWeb() }, TimeProvider.System, _ => { });
        var page = new StatusPage(host, null, _ => { });
        using var closing = new PageCloser(page);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page.Status(), ReceiverConfig.JsonLine));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("retune").ValueKind);
    }

    /// <summary>
    /// Disposes a page that was never started. HttpListener binds its prefix even then, so a
    /// port another test run took in the meantime makes it throw; that says nothing about this.
    /// </summary>
    private sealed class PageCloser(StatusPage page) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                page.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (System.Net.HttpListenerException)
            {
            }
        }
    }
}
