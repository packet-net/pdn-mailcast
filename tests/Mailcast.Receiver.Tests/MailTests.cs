using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mailcast.Core;
using Mailcast.Receiver.Delivery;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>The receiver's own copies of the mail: kept after the BBS answers, shown on the page, and sent again.</summary>
public class MailTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A BBS that answers each BID as the test says, at once.</summary>
    private sealed class AnsweringBbs(Func<Bulletin, DeliveryOutcome> answer) : IBbsSession
    {
        public Task<SessionReport> DeliverAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation) =>
            Task.FromResult(new SessionReport(true, null, [.. bulletins.Select(answer)], 0));
    }

    private static async Task RebuildAsync(Intake intake, params Bulletin[] bulletins)
    {
        foreach (var frame in Samples.Frames(bulletins))
        {
            intake.Offer(frame);
        }
        await intake.DrainAsync(CancellationToken.None);
    }

    private static ReceiverConfig Config(string dir, int webPort, int bbsPort, string password = "") => new()
    {
        Audio = "wav:/nonexistent.wav",
        StateDirectory = dir,
        Bbs = new BbsSettings { Host = "127.0.0.1", Port = bbsPort, Login = "Q0CAST", Password = "secret" },
        Web = new WebSettings { Port = webPort, Password = password },
    };

    /// <summary>A receiver on a fake clock with its page on a free port; see StatusPageTests for why it may ask twice.</summary>
    private static async Task<(ReceiverHost Host, StatusPage Page, HttpClient Http, List<string> Log)> StartAsync(
        string dir, FakeTimeProvider time, int bbsPort = 8011, string password = "")
    {
        var log = new List<string>();
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var host = new ReceiverHost(Config(dir, port, bbsPort, password), time, line => { lock (log) { log.Add(line); } });
            var page = new StatusPage(host, null, line => { lock (log) { log.Add(line); } });
            try
            {
                page.Start();
                return (host, page, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") }, log);
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                await page.DisposeAsync();
                await host.DisposeAsync();
            }
        }
    }

    private static HttpRequestMessage Resend(string id, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/mail/resend") { Content = JsonContent.Create(new { id }) };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return request;
    }

    private static async Task<JsonElement[]> MailAsync(HttpClient http) =>
        [.. (await http.GetFromJsonAsync<JsonElement>("api/mail"))!.GetProperty("items").EnumerateArray()];

    [Fact]
    public async Task EachFinalAnswer_IsArchivedWithTheVerdict_AndAnAnswerForLaterStaysWaiting()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        await using var intake = new Intake(dir.Path, _ => { }, new ReceiverStoreOptions { Time = time });
        await RebuildAsync(intake, Samples.Bulletin(1), Samples.Bulletin(2), Samples.Bulletin(3), Samples.Bulletin(4));
        var bbs = new AnsweringBbs(b => b.Bid switch
        {
            "1_GB7RDG" => new DeliveryOutcome(b.Bid, DeliveryVerdict.Accepted),
            "2_GB7RDG" => new DeliveryOutcome(b.Bid, DeliveryVerdict.AlreadyHad),
            "3_GB7RDG" => new DeliveryOutcome(b.Bid, DeliveryVerdict.Refused, "FS R: not wanted here"),
            _ => new DeliveryOutcome(b.Bid, DeliveryVerdict.Deferred),
        });
        var service = new DeliveryService(intake, bbs, new DeliveryLedger(dir.Path), time, _ => { });

        Assert.NotNull(await service.DeliverPendingAsync(CancellationToken.None)); // 4 is to be offered later

        var mail = intake.Mail().NewestFirst.ToDictionary(m => m.Bid);
        Assert.Equal(BbsVerdict.Accepted, mail["1_GB7RDG"].Verdict);
        Assert.Equal(BbsVerdict.AlreadyHad, mail["2_GB7RDG"].Verdict);
        Assert.Equal(BbsVerdict.Refused, mail["3_GB7RDG"].Verdict);
        Assert.Equal("FS R: not wanted here", mail["3_GB7RDG"].Detail);
        Assert.All(mail.Values.Where(m => !m.Waiting), m => Assert.Equal(Start, m.Time));
        Assert.Equal(3, mail.Values.Count(m => !m.Waiting));
        Assert.True(mail["4_GB7RDG"].Waiting);
        Assert.Null(mail["4_GB7RDG"].Verdict);
        Assert.Equal(["4_GB7RDG"], intake.Pending().Select(b => b.Bid));
        var (entry, stored) = intake.ReadMail(mail["1_GB7RDG"].ObjectId)!.Value;
        Assert.Equal(Samples.Bulletin(1).Serialize(), stored);
        Assert.False(entry.Waiting);
    }

    [Fact]
    public async Task SendToBbsAgain_GoesBackThroughTheOutbox_ToARealSession()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        await using var bbs = new FakeBbs();
        var (host, page, http, log) = await StartAsync(dir.Path, time, bbs.Port);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        var bulletin = Samples.Bulletin(7);
        await RebuildAsync(host.Intake, bulletin);

        var waiting = Assert.Single(await MailAsync(http));
        Assert.True(waiting.GetProperty("waiting").GetBoolean());
        Assert.Null(await host.Delivery.DeliverPendingAsync(CancellationToken.None));
        Assert.Single(bbs.Taken);
        var archived = Assert.Single(await MailAsync(http));
        Assert.False(archived.GetProperty("waiting").GetBoolean());
        Assert.Equal("accepted", archived.GetProperty("verdict").GetString());
        string id = archived.GetProperty("id").GetString()!;

        // The BBS loses it; the sysop sends it again from the page.
        bbs.Known.Clear();
        time.Advance(TimeSpan.FromDays(2));
        var answer = await http.SendAsync(Resend(id));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal([bulletin], host.Intake.Pending());
        Assert.True(Assert.Single(await MailAsync(http)).GetProperty("waiting").GetBoolean());
        Assert.Contains(log, l => l.Contains("7_GB7RDG", StringComparison.Ordinal) && l.Contains("put back in the outbox", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(Resend(id))).StatusCode);

        Assert.Null(await host.Delivery.DeliverPendingAsync(CancellationToken.None));

        Assert.Equal(2, bbs.Taken.Count);
        Assert.Equal(bulletin.MessageText, bbs.Taken.Last().Body);
        Assert.Empty(host.Intake.Pending());
        var again = Assert.Single(await MailAsync(http));
        Assert.Equal("accepted", again.GetProperty("verdict").GetString());
        Assert.Equal(Start + TimeSpan.FromDays(2), again.GetProperty("time").GetDateTimeOffset());

        // Not again straight away: each one is a session with the BBS.
        var tooSoon = await http.SendAsync(Resend(id));
        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);
        Assert.Contains("Try again in 120 s", await tooSoon.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        time.Advance(StatusPage.ResendCooldown);

        // Sent again while the BBS still has it, the BBS says so by its BID, and it stays down as accepted.
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Resend(id))).StatusCode);
        Assert.Null(await host.Delivery.DeliverPendingAsync(CancellationToken.None));
        Assert.Equal(2, bbs.Taken.Count);
        var kept = Assert.Single(await MailAsync(http));
        Assert.Equal("accepted", kept.GetProperty("verdict").GetString());
        Assert.Equal("offered again; the BBS already had it", kept.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task HostileBulletin_IsServedAsPlainTextOnly_AndNeverAsMarkup()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        const string Script = "<script>alert(1)</script>";
        const string Image = "</pre><img src=x onerror=\"alert(2)\">&amp;";
        var hostile = new Bulletin('B', "G4ABC", "ALL", "WW", "666_GB7RDG", Script, Start,
            ["R:261004/1200Z 666@GB7RDG.#42.GBR.EURO <b>LinBPQ</b>"], $"\r\n{Image}\r\nCaf\u00e9 \u00e0 la carte\r\n");
        await RebuildAsync(host.Intake, hostile);

        var list = await http.GetAsync("api/mail");
        string json = await list.Content.ReadAsStringAsync();
        Assert.StartsWith("application/json", list.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Equal("nosniff", string.Join(",", list.Headers.GetValues("X-Content-Type-Options")));
        Assert.DoesNotContain("<", json, StringComparison.Ordinal); // escaped by the serializer as <
        var item = Assert.Single(JsonDocument.Parse(json).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(Script, item.GetProperty("title").GetString());

        var raw = await http.GetAsync($"api/mail/{item.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", raw.Content.Headers.ContentType!.ToString());
        Assert.Equal("nosniff", string.Join(",", raw.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("sandbox", string.Join(",", raw.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        string text = await raw.Content.ReadAsStringAsync();
        Assert.Equal(Bulletin.TextEncoding.GetString(hostile.Serialize()), text); // the whole bulletin, unchanged
        Assert.Contains("Title: " + Script, text, StringComparison.Ordinal);
        Assert.Contains("<b>LinBPQ</b>", text, StringComparison.Ordinal);
        Assert.Contains("Caf\u00e9", text, StringComparison.Ordinal); // Latin-1 on the air, UTF-8 to the browser

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/mail/0000000000000001")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/mail/..%2F..%2Freceiver.json")).StatusCode);
    }

    [Fact]
    public void Page_PutsNothingFromABulletinInAsHtml()
    {
        string html = PageSource();
        int start = html.IndexOf("/* ---------- mail ---------- */", StringComparison.Ordinal);
        int end = html.IndexOf("/* ----------", start + 1, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        string mail = html[start..end];

        // The mail section builds its table and viewer from elements, each field set as
        // textContent, so a bulletin's text can never become markup or script.
        foreach (string sink in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "srcdoc" })
        {
            Assert.DoesNotContain(sink, mail, StringComparison.Ordinal);
        }
        Assert.Contains("e.textContent = String(text)", mail, StringComparison.Ordinal);
        Assert.Contains("$(\"viewText\").textContent = r.text", mail, StringComparison.Ordinal);
    }

    private static string PageSource()
    {
        using var stream = typeof(StatusPage).Assembly.GetManifestResourceStream("Mailcast.Receiver.Web.index.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task SendToBbsAgain_FromAnotherSiteOrNotJsonOrTooBig_IsRefused()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, log) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        var bulletin = Samples.Bulletin(8);
        await RebuildAsync(host.Intake, bulletin);
        host.Intake.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);
        string id = Assert.Single(await MailAsync(http)).GetProperty("id").GetString()!;

        var crossSite = await http.SendAsync(Resend(id, origin: "http://evil.example"));
        var plain = await http.PostAsync("api/mail/resend", new StringContent($"{{\"id\":\"{id}\"}}", System.Text.Encoding.UTF8, "text/plain"));
        var form = await http.PostAsync("api/mail/resend", new FormUrlEncodedContent([new("id", id)]));
        var huge = await http.PostAsync("api/mail/resend", new StringContent($"{{\"id\":\"{id}\"{new string(' ', StatusPage.MaxFormBytes)}}}", System.Text.Encoding.UTF8, "application/json"));
        var asGet = await http.GetAsync("api/mail/resend");
        var nonsense = await http.PostAsJsonAsync("api/mail/resend", new { id = "../outbox" });

        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asGet.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Empty(host.Intake.Pending());
        Assert.False(Assert.Single(await MailAsync(http)).GetProperty("waiting").GetBoolean());
        Assert.DoesNotContain(log, l => l.Contains("put back in the outbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MailAndResend_NeedThePagePassword()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time, password: "letmein");
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        var bulletin = Samples.Bulletin(9);
        await RebuildAsync(host.Intake, bulletin);
        host.Intake.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);
        string id = ObjectId.Format(Assert.Single(host.Intake.Mail().NewestFirst).ObjectId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/mail")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"api/mail/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Resend(id))).StatusCode);
        Assert.Empty(host.Intake.Pending());

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String("any:letmein"u8.ToArray()));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/mail")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"api/mail/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Resend(id))).StatusCode);
        Assert.Equal([bulletin], host.Intake.Pending());
    }

    [Fact]
    public async Task MailList_IsNewestFirst_AndPaged()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        for (int i = 1; i <= 5; i++)
        {
            await RebuildAsync(host.Intake, Samples.Bulletin(i));
            if (i <= 3)
            {
                host.Intake.Acknowledge(Samples.Bulletin(i).Bid, BbsVerdict.Accepted);
            }
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var first = await http.GetFromJsonAsync<JsonElement>("api/mail?limit=2");
        var second = await http.GetFromJsonAsync<JsonElement>("api/mail?offset=2&limit=2");
        var last = await http.GetFromJsonAsync<JsonElement>("api/mail?offset=4&limit=2");

        Assert.Equal(5, first.GetProperty("total").GetInt32());
        Assert.Equal(2, first.GetProperty("waiting").GetInt32());
        Assert.Equal(3, first.GetProperty("archived").GetInt32());
        string[] Bids(JsonElement e) => [.. e.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("bid").GetString()!)];
        Assert.Equal(["5_GB7RDG", "4_GB7RDG"], Bids(first));
        Assert.Equal(["3_GB7RDG", "2_GB7RDG"], Bids(second));
        Assert.Equal(["1_GB7RDG"], Bids(last));
        var item = first.GetProperty("items")[0];
        Assert.Equal("G4ABC", item.GetProperty("from").GetString());
        Assert.Equal("ALL", item.GetProperty("to").GetString());
        Assert.Equal("GBR", item.GetProperty("at").GetString());
        Assert.Equal(Samples.Bulletin(5).Serialize().Length, item.GetProperty("size").GetInt32());
        Assert.Equal("waiting for the BBS", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InstallFromBeforeTheArchive_KeepsItsOutbox_AndStartsArchiving()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var bulletin = Samples.Bulletin(11);
        // An install of 0.3.0: a bulletin waiting in the outbox, its done marker, a delivery record, no archive folder.
        await using (var old = new Intake(dir.Path, _ => { }, new ReceiverStoreOptions { Time = time, ArchiveRetention = TimeSpan.Zero }))
        {
            await RebuildAsync(old, bulletin);
        }
        new DeliveryLedger(dir.Path).Record(bulletin, new DeliveryOutcome(bulletin.Bid, DeliveryVerdict.Deferred), Start);
        string store = Path.Combine(dir.Path, "store");
        Assert.False(Directory.Exists(Path.Combine(store, "archive")));
        File.WriteAllText(Path.Combine(dir.Path, "receiver.json"), """{ "audio": "wav:/nonexistent.wav", "stateDirectory": "unused" }""");
        var config = ReceiverConfig.Load(Path.Combine(dir.Path, "receiver.json"));
        Assert.Equal(30, config.Archive.Days);
        Assert.Equal(50, config.Archive.MaxMegabytes);

        var log = new List<string>();
        await using var host = new ReceiverHost(config with { StateDirectory = dir.Path }, time, log.Add);
        var waiting = Assert.Single(host.Intake.Mail().NewestFirst);
        Assert.True(waiting.Waiting);
        Assert.Equal(bulletin, Assert.Single(host.Intake.Pending()));

        host.Intake.Acknowledge(bulletin.Bid, BbsVerdict.Accepted);

        Assert.Empty(host.Intake.Pending());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(store, "archive"), "*.mail"));
        Assert.Equal(BbsVerdict.Accepted, Assert.Single(host.Intake.Mail().NewestFirst).Verdict);
        Assert.DoesNotContain(log, l => l.Contains("quarantined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnreadableArchiveFile_IsQuarantinedAndLogged_AndTheReceiverStarts()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var good = Samples.Bulletin(12);
        await using (var first = new Intake(dir.Path, _ => { }, new ReceiverStoreOptions { Time = time }))
        {
            await RebuildAsync(first, good);
            first.Acknowledge(good.Bid, BbsVerdict.Accepted);
        }
        string archive = Path.Combine(dir.Path, "store", "archive");
        File.WriteAllBytes(Path.Combine(archive, "00000000000000ff.mail"), [0xff, 0xfe, 0x00, 0x0a, 0x0a, 0x01]);

        var log = new List<string>();
        await using var host = new ReceiverHost(Config(dir.Path, 1, 8011), time, log.Add);

        Assert.Equal([good.Bid], host.Intake.Mail().NewestFirst.Select(m => m.Bid));
        Assert.True(File.Exists(Path.Combine(dir.Path, "store", "quarantine", "00000000000000ff.mail")));
        Assert.Contains(log, l => l.StartsWith("store: archive file 00000000000000ff.mail unreadable, quarantined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusAndMailRequests_ReadNoFiles()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await RebuildAsync(host.Intake, Samples.Bulletin(21), Samples.Bulletin(22), Samples.Bulletin(23));
        host.Intake.Acknowledge("21_GB7RDG", BbsVerdict.Accepted);
        host.Intake.Acknowledge("22_GB7RDG", BbsVerdict.Refused, "FS R");
        long before = host.Intake.StoreDiskReads;

        for (int i = 0; i < 5; i++)
        {
            _ = JsonSerializer.Serialize(page.Status(), ReceiverConfig.JsonLine);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/status")).StatusCode);
            var list = await http.GetFromJsonAsync<JsonElement>("api/mail?offset=1&limit=1");
            Assert.Equal(3, list.GetProperty("total").GetInt32());
            Assert.Equal(1, list.GetProperty("waiting").GetInt32());
            Assert.Equal(1, (await http.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("bbs").GetProperty("waiting").GetInt32());
            Assert.Single(host.Intake.Pending());
        }

        Assert.Equal(before, host.Intake.StoreDiskReads);
    }

    [Fact]
    public async Task ArchiveFileThatCannotBeRead_IsAnErrorOnThePage_NotAHangUp()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await RebuildAsync(host.Intake, Samples.Bulletin(24));
        host.Intake.Acknowledge("24_GB7RDG", BbsVerdict.Accepted);
        string id = ObjectId.Format(Assert.Single(host.Intake.Mail().NewestFirst).ObjectId);
        string file = Path.Combine(dir.Path, "store", "archive", id + ".mail");
        File.Delete(file);
        Directory.CreateDirectory(file); // reading it now fails as a file system error, not as bad content

        var raw = await http.GetAsync($"api/mail/{id}");
        var resend = await http.SendAsync(Resend(id));

        Assert.Equal(HttpStatusCode.InternalServerError, raw.StatusCode);
        Assert.Contains("Cannot read that bulletin", await raw.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, resend.StatusCode);
        Assert.Contains("Cannot put it back", (await resend.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/mail")).StatusCode);
    }
}
