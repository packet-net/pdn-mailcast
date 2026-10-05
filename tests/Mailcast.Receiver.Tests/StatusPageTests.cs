using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Mailcast.Receiver.Web;

namespace Mailcast.Receiver.Tests;

public class StatusPageTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ReceiverConfig Config(string dir) => new()
    {
        Audio = "wav:/nonexistent.wav",
        StateDirectory = dir,
        Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "secret" },
        Web = new WebSettings { Port = FreePort() },
    };

    [Fact]
    public async Task Settings_SavedFromThePage_AreWrittenBackAndPutInForce()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        var config = Config(dir.Path);
        config.Save(path);
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        await using var page = new StatusPage(host, path, _ => { });
        page.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{config.Web.Port}/") };

        var shown = await http.GetFromJsonAsync<JsonElement>("api/settings");
        Assert.True(shown.GetProperty("bbs").GetProperty("passwordSet").GetBoolean());
        Assert.DoesNotContain("secret", shown.GetRawText(), StringComparison.Ordinal);

        var answer = await http.PostAsJsonAsync("api/settings", new
        {
            audio = "ubersdr:wessex.zapto.org", type = "linBpq", host = "127.0.0.1", port = 8012, login = "q0cast", password = "", command = "BBS",
        });

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var saved = ReceiverConfig.Load(path);
        Assert.Equal(8012, saved.Bbs.Port);
        Assert.Equal("Q0CAST", saved.Bbs.Login);
        Assert.Equal("secret", saved.Bbs.Password);
        Assert.Equal("ubersdr:wessex.zapto.org", saved.Audio);
        Assert.Equal(saved, host.Config);
    }

    [Fact]
    public async Task Settings_ThatCannotWork_AreRefusedWithAReason()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        var config = Config(dir.Path);
        config.Save(path);
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        await using var page = new StatusPage(host, path, _ => { });
        page.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{config.Web.Port}/") };

        var answer = await http.PostAsJsonAsync("api/settings", new
        {
            audio = "", type = "linBpq", host = "127.0.0.1", port = 8011, login = "Q0CAST", password = "", command = "BBS",
        });

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Contains("audio", (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(config, ReceiverConfig.Load(path));
    }

    [Fact]
    public async Task Status_DescribesTheReceiverAndPageIsServed()
    {
        using var dir = new TempDirectory();
        var config = Config(dir.Path);
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        await using var page = new StatusPage(host, null, _ => { });
        page.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{config.Web.Port}/") };

        var status = await http.GetFromJsonAsync<JsonElement>("api/status");
        Assert.Equal(1800, status.GetProperty("markers").GetProperty("centreHz").GetDouble());
        Assert.Equal(-18, status.GetProperty("level").GetProperty("lowDbFs").GetDouble());
        Assert.Contains("Q0CAST", status.GetProperty("bbs").GetProperty("target").GetString(), StringComparison.Ordinal);

        string html = await http.GetStringAsync("/");
        Assert.Contains("pdn-mailcast receiver", html, StringComparison.Ordinal);
        Assert.DoesNotMatch("[^\\x00-\\x7F]", html);
    }
}
