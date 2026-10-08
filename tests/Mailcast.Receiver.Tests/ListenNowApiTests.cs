using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Mailcast.Receiver.Web;

namespace Mailcast.Receiver.Tests;

/// <summary>Issue #53's POST /api/listen-now and the "listenNow" part of /api/status.</summary>
public class ListenNowApiTests
{
    // 09:20: the web SDR's next window is 09:58, for the 10:00 slot, 38 minutes away.
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 20, 0, TimeSpan.Zero);

    private static ReceiverConfig Config(string dir, int webPort, string password = "") => new()
    {
        Audio = "ubersdr:wessex.zapto.org",
        StateDirectory = dir,
        Daylight = null,
        Web = new WebSettings { Port = webPort, Password = password },
    };

    private static async Task<(ReceiverHost Host, StatusPage Page, HttpClient Http)> StartAsync(string dir, FakeTimeProvider time, string password = "")
    {
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var host = new ReceiverHost(Config(dir, port, password), time, _ => { });
            var page = new StatusPage(host, null, _ => { });
            try
            {
                page.Start();
                return (host, page, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                await page.DisposeAsync();
                await host.DisposeAsync();
            }
        }
    }

    private static HttpRequestMessage ListenNow(string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/listen-now") { Content = JsonContent.Create(new { }) };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return request;
    }

    private static async Task<JsonElement> StatusAsync(HttpClient http) => await http.GetFromJsonAsync<JsonElement>("api/status");

    [Fact]
    public async Task ListenNowView_BetweenWindows_SaysItCanBeUsed()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var ln = (await StatusAsync(http)).GetProperty("listenNow");
        Assert.Equal(3, ln.GetProperty("usesLeft").GetInt32());
        Assert.Equal(3, ln.GetProperty("usesMax").GetInt32());
        Assert.True(ln.GetProperty("canUse").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ln.GetProperty("until").ValueKind);
        Assert.Equal(JsonValueKind.Null, ln.GetProperty("problem").ValueKind);
    }

    [Fact]
    public async Task Post_Opens_ACountedSession_ShownInStatus()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var answer = await http.SendAsync(ListenNow());
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var body = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        var until = body.GetProperty("until").GetDateTimeOffset();
        Assert.Equal(time.GetUtcNow() + ListenNowService.Duration, until);

        var ln = (await StatusAsync(http)).GetProperty("listenNow");
        Assert.Equal(until, ln.GetProperty("until").GetDateTimeOffset());
        Assert.Equal(2, ln.GetProperty("usesLeft").GetInt32());
    }

    [Fact]
    public async Task Post_WithAWindowTooClose_IsRefused_AndSaysWhy()
    {
        using var dir = new TempDirectory();
        // 09:56: the 10:00 slot's window opens at 09:58, under 5 minutes away.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 9, 56, 0, TimeSpan.Zero));
        var (host, page, http) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var answer = await http.SendAsync(ListenNow());
        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        var body = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("wait for that instead", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_ThreeTimes_ThenRefusedForToday()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(ListenNow())).StatusCode);
            time.Advance(ListenNowService.Duration);
        }

        var refused = await http.SendAsync(ListenNow());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("used 3 times today", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_FromAnotherSite_IsRefused()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var crossSite = await http.SendAsync(ListenNow(origin: "http://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Equal(3, (await StatusAsync(http)).GetProperty("listenNow").GetProperty("usesLeft").GetInt32());
    }

    [Fact]
    public async Task Post_NeedsThePagePassword()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http) = await StartAsync(dir.Path, time, password: "letmein");
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(ListenNow())).StatusCode);

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String("any:letmein"u8.ToArray()));
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(ListenNow())).StatusCode);
    }
}
