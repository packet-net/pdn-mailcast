using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Mailcast.Receiver.Delivery;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

public class StatusPageTests
{
    private static ReceiverConfig Config(string dir, int port = 1, bool lan = false, string password = "") => new()
    {
        Audio = "wav:/nonexistent.wav",
        StateDirectory = dir,
        Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "secret-pw" },
        Web = new WebSettings { Port = port, Lan = lan, Password = password },
    };

    /// <summary>
    /// A page on a port nothing else has. HttpListener cannot bind port 0 and say which port it
    /// got, and pdn-soundmodem's waterfall needs HttpListener, so this asks the system for a
    /// free port and, in the rare case another process takes it first, asks again.
    /// </summary>
    private static async Task<(ReceiverHost Host, StatusPage Page, HttpClient Http, string Path)> StartAsync(
        string dir, bool lan = false, string password = "", Func<BbsSettings, Task<BbsLoginTestResult>>? testBbsLogin = null, Action<string>? log = null)
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
            var page = new StatusPage(host, ConfigFile.Load(path), log ?? (_ => { })) { TestBbsLogin = testBbsLogin };
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
    public async Task Settings_AfterAHandEdit_AreNotSaved_AndThePageSaysToRestart()
    {
        // Issue #154: a "rig" added by hand was lost at the next save from the page.
        using var dir = new TempDirectory();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var (host, page, http, path) = await StartAsync(dir.Path, log: log.Enqueue);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        Assert.False((await http.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("configChanged").GetBoolean());
        string edited = File.ReadAllText(path).Replace("\"audio\"", "\"rig\": { \"rigctld\": \"127.0.0.1:4532\", \"dedicatedRadio\": true },\n  \"audio\"", StringComparison.Ordinal);
        File.WriteAllText(path, edited);

        var answer = await http.PostAsJsonAsync("api/settings", Form(audio: "ubersdr:other.example"));

        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        var error = (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Equal("The config file was edited by hand since the receiver started; restart the receiver to use it, then make this change again.", error);
        Assert.Equal(edited, File.ReadAllText(path));
        Assert.Equal("wav:/nonexistent.wav", host.Config.Audio);
        Assert.Single(log, l => l.StartsWith("config: not saved from the page:", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.StartsWith("config: saved from the page", StringComparison.Ordinal));
        Assert.True((await http.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("configChanged").GetBoolean());
    }

    [Fact]
    public async Task Settings_SavedFromThePage_SayWhatChanged_WithNoPassword()
    {
        using var dir = new TempDirectory();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var (host, page, http, _) = await StartAsync(dir.Path, log: log.Enqueue);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var answer = await http.PostAsJsonAsync("api/settings", Form(audio: "ubersdr:other.example", password: "another-pw"));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("config: saved from the page: audio wav:/nonexistent.wav -> ubersdr:other.example, BBS login Q0CAST -> q0cast, BBS password changed", log);
        Assert.DoesNotContain(log, l => l.Contains("pw", StringComparison.Ordinal));

        // The daily report turned on is said once, in that same kind of line.
        var on = await http.PostAsJsonAsync("api/settings", new
        {
            audio = "ubersdr:other.example", type = "linBpq", host = "127.0.0.1", port = 8011, login = "q0cast", password = "", command = "BBS",
            feedback = new { enabled = true, callsign = "g4abc" },
        });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(["config: saved from the page: daily report on as G4ABC"], log.Where(l => l.Contains("daily report", StringComparison.Ordinal)));
    }

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
        Assert.DoesNotContain("secret-pw", shown.GetRawText(), StringComparison.Ordinal);

        // A new port is a different BBS: the password has to be given again.
        var refused = await http.PostAsJsonAsync("api/settings", Form(port: 8012));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("password again", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var answer = await http.PostAsJsonAsync("api/settings", Form(port: 8011));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var saved = ReceiverConfig.Load(path);
        Assert.Equal(8011, saved.Bbs.Port);
        // Issue #134: LinBPQ's USER= logins are case-sensitive, so the login is saved as typed.
        Assert.Equal("q0cast", saved.Bbs.Login);
        Assert.Equal("secret-pw", saved.Bbs.Password);
        Assert.Equal("ubersdr:wessex.zapto.org", saved.Audio);
        Assert.Equal(saved, host.Config);
    }

    /// <summary>Issue #134: LinBPQ's USER= logins are case-sensitive, so its login is kept as typed; FBB's is upper-cased.</summary>
    [Theory]
    [InlineData(BbsKind.LinBpq, " m7taw ", "m7taw")]
    [InlineData(BbsKind.LinBpq, "M7TAW", "M7TAW")]
    [InlineData(BbsKind.Fbb, "q0cast", "Q0CAST")]
    public void BbsLogin_KeepsLinBpqsCase_UpperCasesFbbs(BbsKind kind, string typed, string saved) =>
        Assert.Equal(saved, StatusPage.BbsLogin(kind, typed));

    /// <summary>Issue #48: the status page's slim list of recent slots, and the full drill-down for one of them.</summary>
    [Fact]
    public async Task SlotFrames_Api_ReturnsWhatWasHeardForThatSlot()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);

        var status = await http.GetFromJsonAsync<JsonElement>("api/status");
        var heardSlots = status.GetProperty("heardSlots").EnumerateArray().ToList();
        var slim = Assert.Single(heardSlots);
        Assert.True(slim.GetProperty("frames").GetInt32() > 0);
        // A string, not a number: a real slot's ticks (about 6.4e17) are past JavaScript's safe
        // integer range, so as a number the page would round them to a slot that is not held.
        var ticksJson = slim.GetProperty("slotTicks");
        Assert.Equal(JsonValueKind.String, ticksJson.ValueKind);
        string slotTicks = ticksJson.GetString()!;
        var held = Assert.Single(host.FrameHistory.Slots).Slot;
        Assert.Equal(held.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), slotTicks);
        Assert.True(held.UtcTicks > (1L << 53)); // real size: the case that broke in the browser
        // What a browser would have sent had it been a number: a double's nearest value.
        long rounded = (long)(double)held.UtcTicks;
        if (rounded != held.UtcTicks)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"api/slots/{rounded}/frames")).StatusCode);
        }

        var detail = await http.GetFromJsonAsync<JsonElement>($"api/slots/{slotTicks}/frames");
        var bursts = detail.GetProperty("bursts").EnumerateArray().ToList();
        Assert.NotEmpty(bursts);
        var pieces = bursts[0].GetProperty("pieces").EnumerateArray().ToList();
        Assert.NotEmpty(pieces);
        Assert.True(pieces[0].TryGetProperty("what", out _));
        Assert.True(pieces[0].TryGetProperty("status", out _));
        Assert.True(pieces[0].TryGetProperty("crcGood", out _));
        Assert.Contains(bursts, b => b.GetProperty("pieces").EnumerateArray()
            .Any(p => p.GetProperty("status").GetString() == "completedObject"));
    }

    /// <summary>
    /// The embedded WaterfallWebServer answers /channel-audio for any program on the same
    /// machine, keyless, with the audio this receiver feeds it; on the receiver's own page that
    /// is surprising rather than useful, since a pdn-soundmodem audio source pointed back at this
    /// port would read the receiver's own feed. pdn-soundmodem has no option to turn it off
    /// (filed as packet-net/pdn-soundmodem#683), so the receiver blocks the path itself, ahead of
    /// the waterfall server, even with one attached. /rig-window already 404s on its own: this
    /// receiver never sets WaterfallOptions.ReceiveWindowRig.
    /// </summary>
    [Fact]
    public async Task Waterfall_DoesNotServeChannelAudioOrRigWindow()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        // Without a waterfall attached yet, the paths must still be refused, not answered with
        // "no audio yet" as any other /waterfall/ path would be.
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("waterfall/channel-audio")).StatusCode);

        await using var pipeline = host.CreatePipeline(AudioSource.Parse(host.Config.Audio), host.Config);
        page.Attach(pipeline);

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("waterfall/channel-audio")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("waterfall/rig-window")).StatusCode);
    }

    /// <summary>Issue #48: anything in the slot's place that is not a tick count a date can have is a 400.</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("12x4")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("-639271010995923120")]
    [InlineData("3155378976000000000")] // one tick past DateTimeOffset.MaxValue
    [InlineData("9223372036854775807")] // long.MaxValue
    [InlineData("99999999999999999999")] // past long
    public async Task SlotFrames_Api_400sForASlotThatIsNotATickCount(string raw)
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var answer = await http.GetAsync($"api/slots/{raw}/frames");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
    }

    /// <summary>Issue #48: a slot never heard (or scrolled out of the last few) is a 404, not an error.</summary>
    [Fact]
    public async Task SlotFrames_Api_404sForASlotNeverHeard()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var answer = await http.GetAsync($"api/slots/{DateTimeOffset.UtcNow.UtcTicks}/frames");

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
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
        // Issue #99: nothing has actually been tried yet, so it is not known, not "fine".
        Assert.Equal(JsonValueKind.Null, status.GetProperty("bbs").GetProperty("reachable").ValueKind);

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
        Assert.Equal("secret-pw", ReceiverConfig.Load(path).Bbs.Password);

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

    /// <summary>
    /// The page offers the daily report only with a BBS to send it through: one that has
    /// answered. Issue #99: before anything has actually been tried (a delivery session or a
    /// Test BBS login), it is null, not true, not known yet rather than "fine".
    /// </summary>
    [Fact]
    public void DailyReport_IsOfferedOnlyWithABbs()
    {
        var at = DateTimeOffset.Parse("2026-10-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        DeliveryRecord Said(DeliveryVerdict verdict) => new(at, "B1", "Title", verdict, null);
        const string Refused = "cannot connect to 127.0.0.1:8011: Connection refused";

        // Nothing tried yet: not known, whichever way lastFailure happens to sit.
        Assert.Null(StatusPage.BbsReachable(false, [], null, null, null));
        Assert.Null(StatusPage.BbsReachable(false, [], null, null, null));

        // Tried, and it has answered before, or just now: true.
        Assert.True(StatusPage.BbsReachable(true, [Said(DeliveryVerdict.Accepted)], null, Refused, null));
        Assert.True(StatusPage.BbsReachable(true, [Said(DeliveryVerdict.Deferred)], null, Refused, null));
        Assert.True(StatusPage.BbsReachable(true, [], new Feedback.SentReport { Answer = Feedback.FeedbackAnswer.Accepted }, Refused, null));
        Assert.True(StatusPage.BbsReachable(false, [], null, null, BbsLoginTestOutcome.Ok));

        // Tried, and it never got that far: false.
        Assert.False(StatusPage.BbsReachable(true, [], null, Refused, null));
        Assert.False(StatusPage.BbsReachable(true, [Said(DeliveryVerdict.NotOffered)], null, Refused, null));
        Assert.False(StatusPage.BbsReachable(true, [], new Feedback.SentReport { Answer = Feedback.FeedbackAnswer.Failed }, Refused, null));
        Assert.False(StatusPage.BbsReachable(false, [], null, null, BbsLoginTestOutcome.Unreachable));
        Assert.False(StatusPage.BbsReachable(false, [], null, null, BbsLoginTestOutcome.WrongPassword));
    }

    [Fact]
    public async Task Globe_IsClosedByDefault_AndItsCoastlinesAreServed()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        string html = await http.GetStringAsync("/");
        Assert.Contains("<details id=\"globeDetails\">", html, StringComparison.Ordinal);
        Assert.Contains("Where it bounced", html, StringComparison.Ordinal);

        using var globe = JsonDocument.Parse(await http.GetStringAsync("/globe.json"));
        Assert.True(globe.RootElement.GetProperty("near").GetArrayLength() > 10);
        Assert.True(globe.RootElement.GetProperty("world").GetArrayLength() > 100);
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
            "id=\"optIn\"", "Help the experiment:", "as a public bulletin to <span id=\"optInAddress\">", "about 500 bytes a day. Nothing else is sent.", "Send a daily report", "Your callsign",
            "Daily report, a public bulletin to ${f.to}: on", "Turn off", "docs/receiver.md#sending-a-daily-report",
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
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org", Stations = StationFixture.EverySlot };

        var schedule = JsonSerializer.SerializeToElement(StatusPage.Schedule(config, Fixture.Followed(config), T(now)), ReceiverConfig.JsonLine);

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
        var config = new ReceiverConfig { Audio = Samples.SoundCard, Stations = StationFixture.EverySlot };

        var schedule = JsonSerializer.SerializeToElement(StatusPage.Schedule(config, Fixture.Followed(config), T("2026-10-05T13:20:00Z")), ReceiverConfig.JsonLine);

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

        var schedule = JsonSerializer.SerializeToElement(StatusPage.Schedule(config, Fixture.Followed(config), T(now)), ReceiverConfig.JsonLine);

        Assert.Equal(T(next), schedule.GetProperty("next").GetDateTimeOffset());
        Assert.Equal(recent is null ? JsonValueKind.Null : JsonValueKind.String, schedule.GetProperty("recent").ValueKind);
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
    public void Schedule_OfSeveralStations_UsesTheTimetableGiven()
    {
        var config = new ReceiverConfig { Audio = "ubersdr:wessex.zapto.org" };
        var heard = SlotSchedule.From(new Packet.Mailcast.SlotTimetable(new TimeOnly(0, 30), 60, new Packet.Mailcast.DaylightRule("IO91lk", 60, 60)));

        var schedule = JsonSerializer.SerializeToElement(StatusPage.Schedule(config, Fixture.Of(heard), T("2026-10-05T12:05:00Z")), ReceiverConfig.JsonLine);

        Assert.Equal(T("2026-10-05T12:30:00Z"), schedule.GetProperty("next").GetDateTimeOffset());
        Assert.Equal("07:30", schedule.GetProperty("today")[0].GetString());
        Assert.Equal("16:30", schedule.GetProperty("today").EnumerateArray().Last().GetString());
    }

    [Fact]
    public async Task ConfiguredDial_ReachesTheWebSdrTuningTheStatusAndTheSettings()
    {
        using var dir = new TempDirectory();
        // An unusual but accepted dial (audio centre 1300 Hz, inside the 1000-2000 Hz this
        // receiver can use), as a measured or typed dial would be.
        var config = Config(dir.Path) with { Audio = "ubersdr:wessex.zapto.org", DialKHz = 7052.5 };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        // Never started, so it holds no port; disposing it would make HttpListener bind one.
        var page = new StatusPage(host, null, _ => { });

        await using var pipeline = host.CreatePipeline(AudioSource.Parse(config.Audio), host.Config);
        Assert.Equal(7_052_500, pipeline.DialHz);
        Assert.Equal(7_052_500, pipeline.WebSdrTuning.FrequencyHz);
        Assert.Equal("Upper", pipeline.WebSdrTuning.Sideband.ToString());

        var status = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine);
        Assert.Equal(7052.5, status.GetProperty("audio").GetProperty("dialKHz").GetDouble());
        // The signal's true centre never moves with the dial: the transmitter does not.
        Assert.Equal(7053.8, status.GetProperty("audio").GetProperty("centreKHz").GetDouble(), 6);
        // ...but where it falls in this receiver's own audio does: 1300 Hz at this (unusual) dial.
        Assert.Equal(1300, status.GetProperty("markers").GetProperty("centreHz").GetDouble(), 3);

        var settings = JsonSerializer.SerializeToElement(StatusPage.SettingsView(host.Config), ReceiverConfig.JsonLine);
        Assert.Equal(7052.5, settings.GetProperty("dialKHz").GetDouble());

        // The form has no dial: saving the other settings keeps the one in the file.
        var saved = StatusPage.Apply(host.Config, new StatusPage.SettingsForm("ubersdr:wessex.zapto.org", "linBpq", "127.0.0.1", 8011, "Q0CAST", null, "BBS"));
        Assert.Equal(7052.5, saved.DialKHz);

        // A sound card's own dial (typed or measured) writes dialKHz directly through the form.
        var narrowed = StatusPage.Apply(host.Config, new StatusPage.SettingsForm(Samples.SoundCard, "linBpq", "127.0.0.1", 8011, "Q0CAST", null, "BBS", DialKHz: 7052.3));
        Assert.Equal(7052.3, narrowed.DialKHz);
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
    public async Task TestBbs_GoodPassword_SaysOkAndNeverReturnsIt()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await using var bbs = new FakeBbs(); // default password "secret-pw", matching Config()'s saved Bbs.Password

        // Issue #99: not known yet, before the login is tested.
        var before = await http.GetFromJsonAsync<JsonElement>("api/status");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("bbs").GetProperty("reachable").ValueKind);

        var answer = await http.PostAsJsonAsync("api/bbs/test", new { host = "127.0.0.1", port = bbs.Port });
        string body = await answer.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.DoesNotContain("secret-pw", body, StringComparison.Ordinal);
        // Issue #99: a successful "Test BBS login" counts as having tried, so the daily report's opt-in can show.
        var after = await http.GetFromJsonAsync<JsonElement>("api/status");
        Assert.True(after.GetProperty("bbs").GetProperty("reachable").GetBoolean());
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("ok", json.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("detail").ValueKind);
        // No mail was exchanged.
        Assert.Empty(bbs.Taken);
    }

    [Fact]
    public async Task TestBbs_WrongPassword_SaysSo()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await using var bbs = new FakeBbs();

        var answer = await http.PostAsJsonAsync("api/bbs/test", new { host = "127.0.0.1", port = bbs.Port, password = "nope" });

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var json = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("wrongPassword", json.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task TestBbs_NotSetUpAsAPartner_SaysSo()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await using var bbs = new FakeBbs { NotAPartner = true };

        var answer = await http.PostAsJsonAsync("api/bbs/test", new { host = "127.0.0.1", port = bbs.Port });

        var json = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("notForwardingPartner", json.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task TestBbs_NothingListening_SaysItCannotReachIt()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var answer = await http.PostAsJsonAsync("api/bbs/test", new { host = "127.0.0.1", port = closedPort, password = "secret-pw" });

        var json = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unreachable", json.GetProperty("outcome").GetString());
        // Issue #99: a failed "Test BBS login" is itself a thing tried, so it is false, not null.
        var status = await http.GetFromJsonAsync<JsonElement>("api/status");
        Assert.False(status.GetProperty("bbs").GetProperty("reachable").GetBoolean());
    }

    [Fact]
    public async Task TestBbs_IsRateLimited()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await using var bbs = new FakeBbs();

        var first = await http.PostAsJsonAsync("api/bbs/test", new { host = "127.0.0.1", port = bbs.Port });
        var second = await http.PostAsJsonAsync("api/bbs/test", new { host = "127.0.0.1", port = bbs.Port });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
    }

    [Fact]
    public async Task TestBbs_FromAnotherSite_IsRefused()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        using var foreign = new HttpRequestMessage(HttpMethod.Post, "api/bbs/test") { Content = JsonContent.Create(new { host = "127.0.0.1", port = 1 }) };
        foreign.Headers.Add("Origin", "http://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(foreign)).StatusCode);
    }

    [Fact]
    public async Task TestBbs_OnAPageWithAPassword_NeedsIt()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path, password: "letmein");
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var without = await http.PostAsJsonAsync("api/bbs/test", new { host = "127.0.0.1", port = 1 });

        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(70000)]
    public async Task TestBbs_PortOutOfRange_IsRefusedCleanly_NotLeftToThrow(int port)
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var answer = await http.PostAsJsonAsync("api/bbs/test", new { host = "127.0.0.1", port, password = "secret-pw" });

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        var json = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("TCP port", json.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad\u0000host")]
    [InlineData("bad\u0001host")]
    public async Task TestBbs_HostInsane_IsRefusedCleanly(string host)
    {
        using var dir = new TempDirectory();
        var (hostApp, page, http, _) = await StartAsync(dir.Path);
        await using var _h = hostApp;
        await using var _p = page;
        using var _c = http;

        var answer = await http.PostAsJsonAsync("api/bbs/test", new { host, port = 8011, password = "secret-pw" });

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
    }

    [Fact]
    public async Task TestBbs_HostTooLong_IsRefusedCleanly()
    {
        using var dir = new TempDirectory();
        var (hostApp, page, http, _) = await StartAsync(dir.Path);
        await using var _h = hostApp;
        await using var _p = page;
        using var _c = http;

        var answer = await http.PostAsJsonAsync("api/bbs/test", new { host = new string('a', 300), port = 8011, password = "secret-pw" });

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
    }

    /// <summary>An empty host, like an empty login or command, keeps the one already saved: it is never left empty, so this is not the same path as <see cref="TestBbs_HostInsane_IsRefusedCleanly"/>.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TestBbs_HostLeftEmpty_KeepsTheSavedOne(string host)
    {
        using var dir = new TempDirectory();
        var (hostApp, page, http, _) = await StartAsync(dir.Path);
        await using var _h = hostApp;
        await using var _p = page;
        using var _c = http;
        await using var bbs = new FakeBbs(); // the saved host (127.0.0.1) and password (secret-pw) reach it fine

        var answer = await http.PostAsJsonAsync("api/bbs/test", new { host, port = bbs.Port, password = "secret-pw" });

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var json = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task TestBbs_LoginThrowsUnexpectedly_StillAnswersWithNoSecretInIt()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path, testBbsLogin: _ => throw new InvalidOperationException("boom, with the password s3cr3t in it"));
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        var answer = await http.PostAsJsonAsync("api/bbs/test", new { host = "127.0.0.1", port = 8011, password = "secret-pw" });
        string body = await answer.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.DoesNotContain("s3cr3t", body, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-pw", body, StringComparison.Ordinal);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("unexpectedReply", json.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("detail").ValueKind);
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

    /// <summary>
    /// Issue #68: the newest directory heard (<c>Samples.Day</c>, 2026-10-04) is days behind the
    /// clock, as for a weak station it never stops being heard. The page's own status says so
    /// plainly, naming the stale date, rather than leaving a caller (or the heading built from
    /// it) to think the list below is of today.
    /// </summary>
    [Fact]
    public async Task Status_NewestDirectoryHeardIsNotToday_NamesTheStaleDate()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var (host, page) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)])) // directory day defaults to Samples.Day, 2026-10-04
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);

        var status = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine);
        var directory = status.GetProperty("directory");
        Assert.Equal("2026-10-04", directory.GetProperty("date").GetString());
        Assert.True(directory.GetProperty("stale").GetBoolean());
    }

    /// <summary>Issue #68: once the clock catches up to the directory's own date, it is not stale.</summary>
    [Fact]
    public async Task Status_NewestDirectoryHeardIsToday_IsNotStale()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var (host, page) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        foreach (var frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);

        var status = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine);
        Assert.False(status.GetProperty("directory").GetProperty("stale").GetBoolean());
    }

    /// <summary>A receiver on a fake clock, its page bound to a free port (see <see cref="StartAsync"/> above for why it may ask twice).</summary>
    private static async Task<(ReceiverHost Host, StatusPage Page)> StartAsync(string dir, TimeProvider time)
    {
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var host = new ReceiverHost(Config(dir, port), time, _ => { });
            var page = new StatusPage(host, null, _ => { });
            try
            {
                page.Start();
                return (host, page);
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                await page.DisposeAsync();
                await host.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Keys_ListedWithTheirSource_AddedAndForgottenFromThePage()
    {
        // Issue #146: one table of trusted keys; a page-added key can be forgotten, a built-in one cannot.
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        async Task<JsonElement[]> Keys() =>
            [.. (await http.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("stations").GetProperty("keys").EnumerateArray()];

        var builtIn = Assert.Single(await Keys());
        Assert.Equal("GB7RDG", builtIn.GetProperty("station").GetString());
        Assert.Equal("stations file", builtIn.GetProperty("source").GetString());
        Assert.False(builtIn.GetProperty("canForget").GetBoolean());
        var refused = await http.PostAsJsonAsync("api/keys/forget", new { keyId = builtIn.GetProperty("keyId").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var added = Packet.Mailcast.DirectorySigner.FromPem(Packet.Mailcast.DirectorySigner.NewKeyPem());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/keys/add", new { station = "not a call", publicKey = added.PublicKeyBase64 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/keys/add", new { station = "G4ABC", publicKey = "AAAA" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/keys/add", new { station = "G4ABC", publicKey = Convert.ToBase64String(StationFixture.Key.PublicKey) })).StatusCode); // already trusted
        Assert.Single(await Keys());

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("api/keys/add", new { station = "g4abc", publicKey = added.PublicKeyBase64 })).StatusCode);
        var keys = await Keys();
        Assert.Equal(2, keys.Length);
        var row = keys.Single(k => k.GetProperty("station").GetString() == "G4ABC");
        Assert.Equal("added on this page", row.GetProperty("source").GetString());
        Assert.Equal(Packet.Mailcast.KeyId.Format(added.KeyId), row.GetProperty("keyId").GetString());
        Assert.True(row.GetProperty("canForget").GetBoolean());

        // Kept across a restart, in the store.
        Assert.Contains(host.Intake.Pins(), p => p.Station == "G4ABC" && p.Added);

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("api/keys/forget", new { keyId = Packet.Mailcast.KeyId.Format(added.KeyId) })).StatusCode);
        Assert.Single(await Keys());
    }

    [Fact]
    public async Task Keys_Changes_NeedTheSignInAndRefuseOtherSites()
    {
        using var dir = new TempDirectory();
        var (host, page, http, _) = await StartAsync(dir.Path, password: "letmein");
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("api/keys/add", new { station = "G4ABC", publicKey = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("api/keys/forget", new { keyId = "0000000000000000" })).StatusCode);

        using var foreign = new HttpRequestMessage(HttpMethod.Post, "api/keys/forget") { Content = JsonContent.Create(new { keyId = "0000000000000000" }) };
        foreign.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(foreign)).StatusCode);
    }
}
