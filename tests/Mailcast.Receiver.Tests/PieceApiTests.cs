using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mailcast.RaptorQ;
using Microsoft.Extensions.Time.Testing;
using Mailcast.Receiver.Web;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

/// <summary>Issue #214: the API for objects and pieces.</summary>
public class PieceApiTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 20, 0, TimeSpan.Zero);

    private static async Task<(ReceiverHost Host, StatusPage Page, HttpClient Http)> StartAsync(string dir, string password = "")
    {
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir, Daylight = null, Web = new WebSettings { Port = port, Password = password } };
            var host = new ReceiverHost(config, new FakeTimeProvider(Start), _ => { });
            var page = new StatusPage(host, null, _ => { }) { FailureDelay = TimeSpan.Zero };
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

    private static TransferObject Obj(int seed = 1, int size = 2000)
    {
        var content = new byte[size];
        new Random(seed).NextBytes(content);
        return TransferObject.ForContent(0x71, content, default, false, Compression.NoDictionary, Compression.Default, symbolSize: 240);
    }

    private static HttpRequestMessage Post(object body, string? key = null, string header = "Bearer", string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/pieces") { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            if (header == "Bearer")
            {
                request.Headers.Add("Authorization", "Bearer " + key);
            }
            else
            {
                request.Headers.Add("X-Api-Key", key);
            }
        }
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return request;
    }

    private static string[] Frames(TransferObject o, int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => Convert.ToHexStringLower(o.Frame((uint)i).ToBytes()))];

    [Fact]
    public async Task Post_IsRefusedWithoutAKey_WithAClearMessage()
    {
        using var dir = new TempDirectory();
        var (host, page, http) = await StartAsync(dir.Path);
        await using var _h = host; await using var _p = page; using var _c = http;
        var answer = await http.SendAsync(Post(new { frames = Frames(Obj(), 0, 1) }, "whatever"));
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Contains("no API key", await answer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheKey_IsMadeOnThePage_AndNeededToPost()
    {
        using var dir = new TempDirectory();
        var (host, page, http) = await StartAsync(dir.Path);
        await using var _h = host; await using var _p = page; using var _c = http;
        Assert.Equal(JsonValueKind.Null, (await http.GetFromJsonAsync<JsonElement>("api/key")).GetProperty("key").ValueKind);
        var made = await (await http.PostAsync("api/key/new", null)).Content.ReadFromJsonAsync<JsonElement>();
        string key = made.GetProperty("key").GetString()!;
        Assert.Equal(key, (await http.GetFromJsonAsync<JsonElement>("api/key")).GetProperty("key").GetString());
        Assert.True((await http.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("sharing").GetProperty("keySet").GetBoolean());

        var body = new { frames = Frames(Obj(), 0, 1) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post(body, "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post(body, key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post(body, key, "X-Api-Key"))).StatusCode);

        string newer = (await (await http.PostAsync("api/key/new", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post(body, key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post(body, newer))).StatusCode);
        await http.PostAsync("api/key/clear", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post(body, newer))).StatusCode);
    }

    [Fact]
    public async Task Post_FromAnotherSite_IsRefused_EvenWithTheKey()
    {
        using var dir = new TempDirectory();
        var (host, page, http) = await StartAsync(dir.Path);
        await using var _h = host; await using var _p = page; using var _c = http;
        string key = host.Intake.NewApiKey();
        var answer = await http.SendAsync(Post(new { frames = Frames(Obj(), 0, 1) }, key, origin: "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>("api/objects")).GetProperty("objects").EnumerateArray());
    }

    [Fact]
    public async Task Post_GivesAnOutcomePerFrame_AndTheIndexPiecesAndDataFollow()
    {
        using var dir = new TempDirectory();
        var (host, page, http) = await StartAsync(dir.Path);
        await using var _h = host; await using var _p = page; using var _c = http;
        string key = host.Intake.NewApiKey();
        var obj = Obj();
        int k = obj.SourceSymbols;
        var frames = Frames(obj, 0, k + 1).Concat(["zz", "00"]).Append(Frames(obj, 0, 1)[0]).ToArray();
        var answer = await http.SendAsync(Post(new { source = "my tool", frames }, key));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var body = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("my_tool", body.GetProperty("source").GetString());
        var results = body.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(frames.Length, results.Count);
        Assert.Equal("stored", results[0].GetProperty("outcome").GetString());
        Assert.Equal(ObjectId.Format(obj.ObjectId), results[0].GetProperty("objectId").GetString());
        Assert.Equal("notAFrame", results[^3].GetProperty("outcome").GetString()); // not hex
        Assert.Equal("notAFrame", results[^2].GetProperty("outcome").GetString()); // not a frame
        Assert.Equal("duplicate", results[^1].GetProperty("outcome").GetString()); // the rebuild comes once, after the whole post

        string id = ObjectId.Format(obj.ObjectId);
        var index = await http.GetFromJsonAsync<JsonElement>("api/objects");
        var row = Assert.Single(index.GetProperty("objects").EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetString());
        Assert.Equal(0x71, row.GetProperty("type").GetInt32());
        Assert.True(row.GetProperty("complete").GetBoolean());
        Assert.Equal(k, row.GetProperty("k").GetInt32());
        Assert.Equal(obj.Oti.TransferLength, row.GetProperty("oti").GetProperty("transferLength").GetInt64());
        Assert.True(row.GetProperty("outside").GetInt32() >= k);
        long next = index.GetProperty("next").GetInt64();
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>($"api/objects?since={next}")).GetProperty("objects").EnumerateArray());

        var pieces = await http.GetFromJsonAsync<JsonElement>($"api/objects/{id}/pieces?limit=2");
        Assert.Equal(2, pieces.GetProperty("pieces").GetArrayLength());
        Assert.True(pieces.GetProperty("more").GetBoolean());
        var first = pieces.GetProperty("pieces")[0];
        Assert.Equal("outside", first.GetProperty("from").GetString());
        Assert.Equal("my_tool", first.GetProperty("label").GetString());
        Assert.True(MailcastFrame.TryParse(Convert.FromHexString(first.GetProperty("frame").GetString()!), out var parsed) && parsed!.ObjectId == obj.ObjectId);
        var rest = await http.GetFromJsonAsync<JsonElement>($"api/objects/{id}/pieces?since={pieces.GetProperty("next").GetInt64()}");
        Assert.Equal(row.GetProperty("held").GetInt32() - 2, rest.GetProperty("pieces").GetArrayLength());

        var data = await http.GetAsync($"api/objects/{id}/data");
        Assert.Equal(HttpStatusCode.OK, data.StatusCode);
        Assert.Equal(obj.Bytes.ToArray(), await data.Content.ReadAsByteArrayAsync());
        Assert.Equal("0", string.Join(',', data.Headers.GetValues("X-Mailcast-Dictionary")));
    }

    [Fact]
    public async Task Data_Is404ForAnObjectNotCompleteOrNotHeld_AndBadRequestsAreSaid()
    {
        using var dir = new TempDirectory();
        var (host, page, http) = await StartAsync(dir.Path);
        await using var _h = host; await using var _p = page; using var _c = http;
        string key = host.Intake.NewApiKey();
        var obj = Obj(2);
        await http.SendAsync(Post(new { frames = Frames(obj, 0, 2) }, key));
        string id = ObjectId.Format(obj.ObjectId);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"api/objects/{id}/data")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/objects/0000000000000001/data")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/objects/0000000000000001/pieces")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/objects/nonsense/pieces")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("api/objects?since=abc")).StatusCode);
        var row = (await http.GetFromJsonAsync<JsonElement>("api/objects")).GetProperty("objects")[0];
        Assert.False(row.GetProperty("complete").GetBoolean());
        Assert.Equal(2, row.GetProperty("held").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Post(new { nothing = 1 }, key))).StatusCode);
        var many = new { frames = Enumerable.Repeat("00", StatusPage.MaxPostedFrames + 1).ToArray() };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.SendAsync(Post(many, key))).StatusCode);
        var plain = new HttpRequestMessage(HttpMethod.Post, "api/pieces") { Content = new StringContent("[]", Encoding.UTF8, "text/plain") };
        plain.Headers.Add("X-Api-Key", key);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await http.SendAsync(plain)).StatusCode);
        // A bare list is accepted too.
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post(Frames(obj, 2, 1), key))).StatusCode);
    }

    [Fact]
    public async Task WithAPagePassword_TheObjectReadsAndTheReferenceStayOpen_ButTheRestNeedsIt()
    {
        using var dir = new TempDirectory();
        var (host, page, http) = await StartAsync(dir.Path, password: "secret");
        await using var _h = host; await using var _p = page; using var _c = http;
        string key = host.Intake.NewApiKey();
        var obj = Obj();
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post(new { frames = Frames(obj, 0, obj.SourceSymbols) }, key))).StatusCode);
        string id = ObjectId.Format(obj.ObjectId);
        foreach (string open in new[] { "api/objects", $"api/objects/{id}/pieces", $"api/objects/{id}/data", "api/openapi.json", "api/docs" })
        {
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(open)).StatusCode);
        }
        // The password still guards the mail, the settings, the status and the key.
        foreach (string closed in new[] { "api/mail", "api/settings", "api/status", "api/key" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(closed)).StatusCode);
        }
    }

    [Fact]
    public async Task OutsidePieces_AreNotASlotsFrames_AndAreCountedApart()
    {
        using var dir = new TempDirectory();
        var (host, page, http) = await StartAsync(dir.Path);
        await using var _h = host; await using var _p = page; using var _c = http;
        string key = host.Intake.NewApiKey();
        long heard = host.Intake.FramesHeard;
        var counted = 0;
        host.Intake.OutsidePieces += n => counted += n;
        await http.SendAsync(Post(new { frames = Frames(Obj(3), 0, 3) }, key));
        Assert.Equal(heard, host.Intake.FramesHeard);
        Assert.Equal(0, host.Intake.FramesStored);
        Assert.Equal(3, counted);
        var sharing = (await http.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("sharing");
        Assert.Equal(3, sharing.GetProperty("outsidePieces").GetInt32());
        Assert.Equal(1, sharing.GetProperty("objects").GetInt32());
    }
}

public class OpenApiTests
{
    [Fact]
    public async Task TheDocument_IsOpenApi31_AndListsEveryPublicRoute_AndTheDocsPageWorksOffline()
    {
        using var dir = new TempDirectory();
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Daylight = null, Web = new WebSettings { Port = port } };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 9, 20, 0, TimeSpan.Zero)), _ => { });
        await using var page = new StatusPage(host, null, _ => { });
        page.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        var doc = await http.GetFromJsonAsync<JsonElement>("api/openapi.json");
        Assert.StartsWith("3.1.", doc.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        var paths = doc.GetProperty("paths");
        foreach (var (method, path) in StatusPage.PublicApiRoutes)
        {
            Assert.True(paths.TryGetProperty(path, out var item), path);
            Assert.True(item.TryGetProperty(method.ToLowerInvariant(), out _), method + " " + path);
        }
        Assert.Equal(StatusPage.PublicApiRoutes.Length, paths.EnumerateObject().Sum(p => p.Value.EnumerateObject().Count()));
        Assert.True(doc.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearerKey", out _));

        // Each listed route is really handled (anything but the generic 404 for a path nobody serves).
        foreach (var (method, path) in StatusPage.PublicApiRoutes)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path.Replace("{id}", "0000000000000001"));
            var answer = await http.SendAsync(request);
            string body = await answer.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Not found.", body, StringComparison.Ordinal);
        }

        var docs = await http.GetStringAsync("api/docs");
        Assert.Contains("openapi.json", docs, StringComparison.Ordinal);
        Assert.Contains("\"withDefaultFonts\":false", docs, StringComparison.Ordinal);
        Assert.Contains("\"showDeveloperTools\":\"never\"", docs, StringComparison.Ordinal);
        foreach (var example in doc.GetProperty("paths").GetProperty("/api/objects").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("example").GetProperty("objects").EnumerateArray())
        {
            var oti = ObjectTransmissionInformation.Read(Convert.FromHexString(example.GetProperty("oti").GetProperty("hex").GetString()!));
            Assert.Equal(example.GetProperty("oti").GetProperty("transferLength").GetInt64(), oti.TransferLength);
            Assert.Equal(example.GetProperty("oti").GetProperty("symbolSize").GetInt32(), oti.SymbolSize);
        }
        var script = await http.GetAsync("api/docs/scalar.js");
        Assert.Equal(System.Net.HttpStatusCode.OK, script.StatusCode);
        Assert.True((await script.Content.ReadAsByteArrayAsync()).Length > 1_000_000);
    }
}


public class HostCheckTests
{
    [Theory]
    [InlineData("192.168.1.5:8080", true)]
    [InlineData("[::1]:8080", true)]
    [InlineData("localhost", true)]
    [InlineData("evil.example", false)]
    [InlineData("claude-code.evil.example", false)]
    public void ForChangesOnlyAnAddressLocalhostOrTheMachinesNameIsAnswered(string host, bool ok) => Assert.Equal(ok, StatusPage.IsOwnHost(host));

    [Fact]
    public void TheMachinesExactName_OrWithDotLocal_IsAnswered_NoOtherSuffix()
    {
        string me = Environment.MachineName;
        Assert.True(StatusPage.IsOwnHost(me.ToUpperInvariant() + ":8080"));
        Assert.True(StatusPage.IsOwnHost(me + ".local"));
        Assert.False(StatusPage.IsOwnHost(me + ".evil.example"));
        Assert.False(StatusPage.IsOwnHost(me + ".lan"));
        Assert.False(StatusPage.IsOwnHost("x" + me));
    }

    [Fact]
    public async Task AnOtherName_CanViewButNotChangeOrReadTheKey()
    {
        using var dir = new TempDirectory();
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", StateDirectory = dir.Path, Daylight = null, Web = new WebSettings { Port = port, Lan = true } };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 9, 20, 0, TimeSpan.Zero)), _ => { });
        await using var page = new StatusPage(host, null, _ => { });
        page.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        HttpRequestMessage As(HttpMethod method, string path)
        {
            var r = new HttpRequestMessage(method, path);
            r.Headers.Host = "claude-code.evil.example:" + port;
            return r;
        }
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(As(HttpMethod.Get, "api/status"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(As(HttpMethod.Get, "api/objects"))).StatusCode);
        var refused = await http.SendAsync(As(HttpMethod.Get, "api/key"));
        Assert.Equal((HttpStatusCode)421, refused.StatusCode);
        Assert.Contains("IP address", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        foreach (string path in new[] { "api/key/new", "api/key/clear", "api/mail/resend", "api/keys/add", "api/settings" })
        {
            Assert.Equal((HttpStatusCode)421, (await http.SendAsync(As(HttpMethod.Post, path))).StatusCode);
        }
        Assert.Null(host.Intake.ApiKey());
        // Posting pieces is exempt: it needs the key, which says so.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(As(HttpMethod.Post, "api/pieces"))).StatusCode);
        // Signing in and out are exempt (no password is set here, so they just answer).
        Assert.NotEqual((HttpStatusCode)421, (await http.SendAsync(As(HttpMethod.Post, "login"))).StatusCode);
        Assert.NotEqual((HttpStatusCode)421, (await http.SendAsync(As(HttpMethod.Post, "logout"))).StatusCode);
        // By address, changing works.
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("api/key/new", null)).StatusCode);
    }
}
