using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Mailcast.Receiver.Web;

namespace Mailcast.Receiver.Tests;

public class StatusPageTests
{
    private static ReceiverConfig Config(string dir, int port = 1, bool lan = false, string password = "") => new()
    {
        Audio = "wav:/nonexistent.wav",
        StateDirectory = dir,
        Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "secret" },
        Web = new WebSettings { Port = port, Lan = lan, Password = password },
    };

    /// <summary>
    /// A page on a port nothing else has. HttpListener cannot bind port 0 and say which port it
    /// got, and pdn-soundmodem's waterfall needs HttpListener, so this asks the system for a
    /// free port and, in the rare case another process takes it first, asks again.
    /// </summary>
    private static async Task<(ReceiverHost Host, StatusPage Page, HttpClient Http, string Path)> StartAsync(string dir, bool lan = false, string password = "")
    {
        string path = System.IO.Path.Combine(dir, "receiver.json");
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var config = Config(dir, port, lan, password);
            config.Save(path);
            var host = new ReceiverHost(config, TimeProvider.System, _ => { });
            var page = new StatusPage(host, path, _ => { });
            try
            {
                page.Start();
                return (host, page, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") }, path);
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                await page.DisposeAsync();
                await host.DisposeAsync();
            }
        }
    }

    private static object Form(string host = "127.0.0.1", int port = 8011, string password = "", string audio = "ubersdr:wessex.zapto.org") =>
        new { audio, type = "linBpq", host, port, login = "q0cast", password, command = "BBS" };

    [Fact]
    public async Task Settings_SavedFromThePage_AreWrittenBackAndPutInForce()
    {
        using var dir = new TempDirectory();
        var (host, page, http, path) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var shown = await http.GetFromJsonAsync<JsonElement>("api/settings");
        Assert.True(shown.GetProperty("bbs").GetProperty("passwordSet").GetBoolean());
        Assert.DoesNotContain("secret", shown.GetRawText(), StringComparison.Ordinal);

        // A new port is a different BBS: the password has to be given again.
        var refused = await http.PostAsJsonAsync("api/settings", Form(port: 8012));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("password again", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var answer = await http.PostAsJsonAsync("api/settings", Form(port: 8011));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var saved = ReceiverConfig.Load(path);
        Assert.Equal(8011, saved.Bbs.Port);
        Assert.Equal("Q0CAST", saved.Bbs.Login);
        Assert.Equal("secret", saved.Bbs.Password);
        Assert.Equal("ubersdr:wessex.zapto.org", saved.Audio);
        Assert.Equal(saved, host.Config);
    }

    [Fact]
    public async Task Settings_ThatCannotWork_AreRefusedWithAReason()
    {
        using var dir = new TempDirectory();
        var (host, page, http, path) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        var before = ReceiverConfig.Load(path);

        var answer = await http.PostAsJsonAsync("api/settings", Form(audio: ""));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Contains("audio", (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, ReceiverConfig.Load(path));
    }

    [Fact]
    public async Task Status_DescribesTheReceiverAndPageIsServed()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var status = await http.GetFromJsonAsync<JsonElement>("api/status");
        Assert.Equal(1800, status.GetProperty("markers").GetProperty("centreHz").GetDouble());
        Assert.Equal(-18, status.GetProperty("level").GetProperty("lowDbFs").GetDouble());
        Assert.Contains("Q0CAST", status.GetProperty("bbs").GetProperty("target").GetString(), StringComparison.Ordinal);

        string html = await http.GetStringAsync("/");
        Assert.Contains("pdn-mailcast receiver", html, StringComparison.Ordinal);
        Assert.DoesNotMatch("[^\\x00-\\x7F]", html);
    }

    [Fact]
    public async Task Post_FromAnotherSiteOrNotJson_IsRefused()
    {
        using var dir = new TempDirectory();
        var (host, page, http, path) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        var before = ReceiverConfig.Load(path);

        var plain = await http.PostAsync("api/settings", new StringContent(JsonSerializer.Serialize(Form()), System.Text.Encoding.UTF8, "text/plain"));
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "api/settings") { Content = JsonContent.Create(Form()) };
        foreign.Headers.Add("Origin", "http://evil.example");
        var crossSite = await http.SendAsync(foreign);
        var huge = await http.PostAsync("api/settings", new StringContent(new string(' ', StatusPage.MaxFormBytes + 1), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        Assert.Equal(before, ReceiverConfig.Load(path));
    }

    [Fact]
    public async Task PageWithAPassword_AsksForIt()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path, password: "letmein");
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var without = await http.GetAsync("api/status");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String("any:wrong"u8.ToArray()));
        var wrong = await http.GetAsync("api/status");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String("any:letmein"u8.ToArray()));
        var right = await http.GetAsync("api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        Assert.Contains("Basic", without.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Theory]
    [InlineData("localhost:8130", true)]
    [InlineData("127.0.0.1:8130", true)]
    [InlineData("[::1]:8130", true)]
    [InlineData("rebound.example:8130", false)]
    [InlineData("192.168.1.5:8130", false)]
    public void LoopbackHost_IsRecognised(string host, bool loopback)
    {
        Assert.Equal(loopback, StatusPage.IsLoopbackHost(host));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8130", "127.0.0.1:8130", true)]
    [InlineData("http://evil.example", "127.0.0.1:8130", false)]
    [InlineData("null", "127.0.0.1:8130", false)]
    public void Origin_MustBeThisPage(string origin, string host, bool same)
    {
        Assert.Equal(same, StatusPage.SameOrigin(origin, host));
    }
}
