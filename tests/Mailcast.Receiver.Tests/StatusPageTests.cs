using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Mailcast.Receiver.Delivery;
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

    private static object FeedbackForm(bool enabled, string? callsign) =>
        new { audio = "wav:/nonexistent.wav", type = "linBpq", host = "127.0.0.1", port = 8011, login = "Q0CAST", password = "", command = "BBS", feedback = new { enabled, callsign } };

    [Fact]
    public async Task DailyReport_TurnedOnAndOffFromThePage_IsSavedAndInForceAtOnce()
    {
        using var dir = new TempDirectory();
        var (host, page, http, path) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        // Off with a BBS: the page offers it.
        var shown = await http.GetFromJsonAsync<JsonElement>("api/settings");
        Assert.False(shown.GetProperty("feedback").GetProperty("enabled").GetBoolean());
        var status = await http.GetFromJsonAsync<JsonElement>("api/status");
        Assert.False(status.GetProperty("feedback").GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("bbs").GetProperty("reachable").GetBoolean());

        // From another site it is refused, like any other change of settings.
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "api/settings") { Content = JsonContent.Create(FeedbackForm(true, "G4ABC")) };
        foreign.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(foreign)).StatusCode);
        Assert.Null(ReceiverConfig.Load(path).Feedback);

        var on = await http.PostAsJsonAsync("api/settings", FeedbackForm(true, " g4abc "));

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(new Feedback.FeedbackSettings { Enabled = true, Callsign = "G4ABC" }, ReceiverConfig.Load(path).Feedback);
        Assert.Equal(new Feedback.FeedbackSettings { Enabled = true, Callsign = "G4ABC" }, host.Config.Feedback);
        // No restart: the report service reads the settings in force.
        status = await http.GetFromJsonAsync<JsonElement>("api/status");
        Assert.True(status.GetProperty("feedback").GetProperty("enabled").GetBoolean());
        shown = await http.GetFromJsonAsync<JsonElement>("api/settings");
        Assert.True(shown.GetProperty("feedback").GetProperty("enabled").GetBoolean());
        Assert.Equal("G4ABC", shown.GetProperty("feedback").GetProperty("callsign").GetString());
        // The other settings are as they were.
        Assert.Equal("secret", ReceiverConfig.Load(path).Bbs.Password);

        // Off in one click: no callsign given, the one set is kept for next time.
        var off = await http.PostAsJsonAsync("api/settings", FeedbackForm(false, ""));

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(new Feedback.FeedbackSettings { Enabled = false, Callsign = "G4ABC" }, ReceiverConfig.Load(path).Feedback);
        status = await http.GetFromJsonAsync<JsonElement>("api/status");
        Assert.False(status.GetProperty("feedback").GetProperty("enabled").GetBoolean());

        // Saving the other settings without it leaves it as it is.
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("api/settings", Form())).StatusCode);
        Assert.Equal(new Feedback.FeedbackSettings { Enabled = false, Callsign = "G4ABC" }, ReceiverConfig.Load(path).Feedback);
    }

    [Theory]
    [InlineData("", "Enter your callsign")]
    [InlineData(null, "Enter your callsign")]
    [InlineData("N0CALL", "does not look like a callsign")]
    [InlineData("G4ABC/P", "does not look like a callsign")]
    [InlineData("G4ABC-1", "does not look like a callsign")]
    [InlineData("G4ABCDE", "does not look like a callsign")]
    public async Task DailyReport_WithACallsignItCannotGoFrom_IsRefusedWithAReason(string? callsign, string expected)
    {
        using var dir = new TempDirectory();
        var (host, page, http, path) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        var before = ReceiverConfig.Load(path);

        var answer = await http.PostAsJsonAsync("api/settings", FeedbackForm(true, callsign));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Contains(expected, (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, ReceiverConfig.Load(path));
        Assert.Null(host.Config.Feedback);
    }

    [Fact]
    public async Task DailyReport_OnAPageWithAPassword_NeedsIt()
    {
        using var dir = new TempDirectory();
        var (host, page, http, path) = await StartAsync(dir.Path, password: "letmein");
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var without = await http.PostAsJsonAsync("api/settings", FeedbackForm(true, "G4ABC"));
        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        Assert.Null(ReceiverConfig.Load(path).Feedback);

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String("any:letmein"u8.ToArray()));
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("api/settings", FeedbackForm(true, "G4ABC"))).StatusCode);
        Assert.True(ReceiverConfig.Load(path).Feedback!.Enabled);
    }

    /// <summary>The page offers the daily report only with a BBS to send it through: one that has answered, or not yet failed.</summary>
    [Fact]
    public void DailyReport_IsOfferedOnlyWithABbs()
    {
        var at = DateTimeOffset.Parse("2026-10-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        DeliveryRecord Said(DeliveryVerdict verdict) => new(at, "B1", "Title", verdict, null);
        const string Refused = "cannot connect to 127.0.0.1:8011: Connection refused";

        // Off with a BBS: nothing has failed, or it has answered before.
        Assert.True(StatusPage.BbsReachable([], null, null));
        Assert.True(StatusPage.BbsReachable([Said(DeliveryVerdict.Accepted)], null, Refused));
        Assert.True(StatusPage.BbsReachable([Said(DeliveryVerdict.Deferred)], null, Refused));
        Assert.True(StatusPage.BbsReachable([], new Feedback.SentReport { Answer = Feedback.FeedbackAnswer.Accepted }, Refused));
        // Off without one: it has never answered, and the last session failed.
        Assert.False(StatusPage.BbsReachable([], null, Refused));
        Assert.False(StatusPage.BbsReachable([Said(DeliveryVerdict.NotOffered)], null, Refused));
        Assert.False(StatusPage.BbsReachable([], new Feedback.SentReport { Answer = Feedback.FeedbackAnswer.Failed }, Refused));
    }

    [Fact]
    public async Task DailyReport_Panel_IsOnThePage()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        string html = await http.GetStringAsync("/");

        foreach (string part in new[]
        {
            "id=\"optIn\"", "Help the experiment:", "about 500 bytes a day. Nothing else is sent.", "Send a daily report", "Your callsign",
            "Daily report to M0LTE: on", "Turn off", "docs/receiver.md#sending-a-daily-report",
        })
        {
            Assert.Contains(part, html, StringComparison.Ordinal);
        }
        // Near the top: before the tiles.
        Assert.True(html.IndexOf("id=\"optIn\"", StringComparison.Ordinal) < html.IndexOf("<main>", StringComparison.Ordinal));
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
        Assert.Equal(7052.0, status.GetProperty("audio").GetProperty("dialKHz").GetDouble());
        Assert.Equal(-18, status.GetProperty("level").GetProperty("lowDbFs").GetDouble());
        Assert.Contains("Q0CAST", status.GetProperty("bbs").GetProperty("target").GetString(), StringComparison.Ordinal);

        string html = await http.GetStringAsync("/");
        Assert.Contains("pdn-mailcast receiver", html, StringComparison.Ordinal);
        Assert.DoesNotMatch("[^\\x00-\\x7F]", html);
        foreach (string stale in new[] { "broadcast", "Broadcast", "midday", "7.0497", "once a day", "30 s", "30 second" })
        {
            Assert.DoesNotContain(stale, html, StringComparison.Ordinal);
        }
        Assert.Equal("every hour on the hour, in daylight: from 120 minutes after sunrise to 30 minutes before sunset at IO91lk", status.GetProperty("schedule").GetProperty("words").GetString());
        Assert.Equal(10, status.GetProperty("schedule").GetProperty("toneSeconds").GetInt32());
    }

    private static DateTimeOffset T(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    // During a slot the web SDR listens to, during one it doesn't, between slots, and around midnight.
    [InlineData("2026-10-05T12:05:00Z", "2026-10-05T13:00:00Z", "2026-10-05T12:00:00Z", "2026-10-05T12:00:00Z", true)]
    [InlineData("2026-10-05T13:05:00Z", "2026-10-05T14:00:00Z", "2026-10-05T13:00:00Z", "2026-10-05T14:00:00Z", false)]
    [InlineData("2026-10-05T13:20:00Z", "2026-10-05T14:00:00Z", null, "2026-10-05T14:00:00Z", false)]
    [InlineData("2026-10-05T23:59:00Z", "2026-10-06T00:00:00Z", null, "2026-10-06T00:00:00Z", true)]
    [InlineData("2026-10-06T00:03:00Z", "2026-10-06T01:00:00Z", "2026-10-06T00:00:00Z", "2026-10-06T00:00:00Z", true)]
    public void Schedule_GivesTheNextSlot_AndTheWebSdrsNext(string now, string next, string? recent, string webSlot, bool webOpen)
    {
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", Daylight = null };

        var schedule = JsonSerializer.SerializeToElement(StatusPage.Schedule(config, T(now)), ReceiverConfig.JsonLine);

        Assert.Equal(T(next), schedule.GetProperty("next").GetDateTimeOffset());
        if (recent is null)
        {
            Assert.Equal(JsonValueKind.Null, schedule.GetProperty("recent").ValueKind);
        }
        else
        {
            Assert.Equal(T(recent), schedule.GetProperty("recent").GetDateTimeOffset());
        }
        var web = schedule.GetProperty("webSdr");
        Assert.Equal(T(webSlot), web.GetProperty("slot").GetDateTimeOffset());
        Assert.Equal(webOpen, web.GetProperty("openNow").GetBoolean());
        // Without daylight hours, 12 of the 24, every other hour.
        Assert.Equal(12, web.GetProperty("times").GetArrayLength());
        Assert.Equal("02:00", web.GetProperty("times")[1].GetString());
    }

    [Fact]
    public void Schedule_SoundCard_HasNoWebSdrPart()
    {
        var config = new ReceiverConfig { Audio = "plughw:CARD=Device,DEV=0", Daylight = null };

        var schedule = JsonSerializer.SerializeToElement(StatusPage.Schedule(config, T("2026-10-05T13:20:00Z")), ReceiverConfig.JsonLine);

        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("webSdr").ValueKind);
        Assert.Equal(24, schedule.GetProperty("slotsPerDay").GetInt32());
    }

    [Theory]
    // In daylight on 5 October (09:00 to 17:00), before it, and after the last slot.
    [InlineData("2026-10-05T12:05:00Z", "2026-10-05T13:00:00Z", "2026-10-05T12:00:00Z", "2026-10-05T12:00:00Z", true)]
    [InlineData("2026-10-05T06:00:00Z", "2026-10-05T09:00:00Z", null, "2026-10-05T09:00:00Z", false)]
    [InlineData("2026-10-05T17:20:00Z", "2026-10-06T09:00:00Z", null, "2026-10-06T09:00:00Z", false)]
    public void Schedule_InDaylight_GivesTodaysSlots_AndTheWebSdrSpreadOverThem(string now, string next, string? recent, string webSlot, bool webOpen)
    {
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org" };

        var schedule = JsonSerializer.SerializeToElement(StatusPage.Schedule(config, T(now)), ReceiverConfig.JsonLine);

        Assert.Equal(T(next), schedule.GetProperty("next").GetDateTimeOffset());
        Assert.Equal(recent is null ? JsonValueKind.Null : JsonValueKind.String, schedule.GetProperty("recent").ValueKind);
        Assert.Equal("config", schedule.GetProperty("from").GetString());
        Assert.Equal(9, schedule.GetProperty("slotsPerDay").GetInt32());
        Assert.Equal(["09:00", "10:00", "11:00", "12:00", "13:00", "14:00", "15:00", "16:00", "17:00"], schedule.GetProperty("today").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("IO91lk", schedule.GetProperty("daylight").GetProperty("locator").GetString());
        Assert.Contains("in daylight", schedule.GetProperty("words").GetString(), StringComparison.Ordinal);
        var web = schedule.GetProperty("webSdr");
        Assert.Equal(T(webSlot), web.GetProperty("slot").GetDateTimeOffset());
        Assert.Equal(webOpen, web.GetProperty("openNow").GetBoolean());
        // Every one of the 9, 17:00 too: they all fit in the allowance.
        Assert.Equal(9, web.GetProperty("slotsPerDay").GetInt32());
        Assert.Equal(["09:00", "10:00", "11:00", "12:00", "13:00", "14:00", "15:00", "16:00", "17:00"], web.GetProperty("times").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Schedule_FromTheDirectory_IsUsedAndSaidSo()
    {
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org" };
        var heard = SlotSchedule.From(new Packet.Mailcast.SlotTimetable(new TimeOnly(0, 30), 60, new Packet.Mailcast.DaylightRule("IO91lk", 60, 60)));

        var schedule = JsonSerializer.SerializeToElement(StatusPage.Schedule(config, heard, true, T("2026-10-05T12:05:00Z")), ReceiverConfig.JsonLine);

        Assert.Equal("directory", schedule.GetProperty("from").GetString());
        Assert.Equal(T("2026-10-05T12:30:00Z"), schedule.GetProperty("next").GetDateTimeOffset());
        Assert.Equal("07:30", schedule.GetProperty("today")[0].GetString());
        Assert.Equal("16:30", schedule.GetProperty("today").EnumerateArray().Last().GetString());
    }

    [Fact]
    public async Task ConfiguredDial_ReachesTheWebSdrTuningTheStatusAndTheSettings()
    {
        using var dir = new TempDirectory();
        var config = Config(dir.Path) with { Audio = "ubersdr:wessex.zapto.org", DialKHz = 7053.5 };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        // Never started, so it holds no port; disposing it would make HttpListener bind one.
        var page = new StatusPage(host, null, _ => { });

        await using var pipeline = host.CreatePipeline(AudioSource.Parse(config.Audio), host.Config);
        Assert.Equal(7_053_500, pipeline.DialHz);
        Assert.Equal(7_053_500, pipeline.WebSdrTuning.FrequencyHz);
        Assert.Equal("Upper", pipeline.WebSdrTuning.Sideband.ToString());

        var status = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine);
        Assert.Equal(7053.5, status.GetProperty("audio").GetProperty("dialKHz").GetDouble());
        Assert.Equal(7055.3, status.GetProperty("audio").GetProperty("centreKHz").GetDouble(), 6);
        Assert.Equal(1800, status.GetProperty("markers").GetProperty("centreHz").GetDouble());

        var settings = JsonSerializer.SerializeToElement(StatusPage.SettingsView(host.Config), ReceiverConfig.JsonLine);
        Assert.Equal(7053.5, settings.GetProperty("dialKHz").GetDouble());

        // The form has no dial: saving the other settings keeps the one in the file.
        var saved = StatusPage.Apply(host.Config, new StatusPage.SettingsForm("ubersdr:wessex.zapto.org", "linBpq", "127.0.0.1", 8011, "Q0CAST", null, "BBS"));
        Assert.Equal(7053.5, saved.DialKHz);
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
