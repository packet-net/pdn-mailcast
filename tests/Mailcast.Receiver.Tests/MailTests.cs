using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Packet.Mailcast;
using Packet.SoundModem.Waterfall;
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

    /// <summary>
    /// A part-received object (issue #73's "coming in" row): a bulletin big enough to need more
    /// than one RaptorQ symbol, heard one piece at a time so it never completes, and named by no
    /// directory (built directly from the bulletin, not picked out of a planned day's frames).
    /// </summary>
    private static byte[] FirstPieceOf(Bulletin bulletin)
    {
        var transfer = TransferObject.ForBulletin(bulletin, ZstdDictionary.Gb7rdg1Id, Compression.Default);
        Assert.True(transfer.SourceSymbols > 1);
        return Ax25UiFrame.Build(Samples.Source, OnAir.Destination, transfer.Frame(0).ToBytes());
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
    public async Task Mail_ShowsWhenEachBulletinCompleted_AndItsOwnDeliveryHistory()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await RebuildAsync(host.Intake, Samples.Bulletin(61), Samples.Bulletin(62));
        time.Advance(TimeSpan.FromMinutes(5));
        var service = new DeliveryService(host.Intake, new AnsweringBbs(b => b.Bid == "61_GB7RDG"
            ? new DeliveryOutcome(b.Bid, DeliveryVerdict.Accepted)
            : new DeliveryOutcome(b.Bid, DeliveryVerdict.Deferred)), host.Ledger, time, _ => { });
        await service.DeliverPendingAsync(CancellationToken.None);

        var items = (await MailAsync(http)).ToDictionary(i => i.GetProperty("bid").GetString()!);
        var delivered = items["61_GB7RDG"];
        Assert.Equal(Start, delivered.GetProperty("completed").GetDateTimeOffset());
        Assert.Equal(Start + TimeSpan.FromMinutes(5), delivered.GetProperty("delivered").GetDateTimeOffset());
        // Issue #73: the reader's own delivery history for this BID, not only its final answer.
        var deliveredAttempt = Assert.Single(delivered.GetProperty("attempts").EnumerateArray());
        Assert.Equal(Start + TimeSpan.FromMinutes(5), deliveredAttempt.GetProperty("time").GetDateTimeOffset());
        Assert.Equal("accepted", deliveredAttempt.GetProperty("saidShort").GetString());

        var waiting = items["62_GB7RDG"];
        Assert.Equal(Start, waiting.GetProperty("completed").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("delivered").ValueKind);
        var deferredAttempt = Assert.Single(waiting.GetProperty("attempts").EnumerateArray());
        Assert.Equal("deferred", deferredAttempt.GetProperty("saidShort").GetString());
    }

    /// <summary>
    /// Issue #73 review: the CT 150 survey script (root@10.45.0.235's
    /// /usr/local/bin/mailcast-survey) reads <c>bulletins</c> from <c>GET /api/status</c> for its
    /// bulletins_completed columns, and the receiver's own page used to read <c>deliveries</c>
    /// from there too. Both stay on that endpoint, in their v0.8.7 shape, even though the page
    /// itself now gets the same facts from <c>GET /api/mail</c> instead - this pins that the
    /// fields, and the keys on each of their entries, are still there.
    /// </summary>
    [Fact]
    public async Task Status_KeepsBulletinsAndDeliveriesFields_InTheirOldShape_ForScripts()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await RebuildAsync(host.Intake, Samples.Bulletin(71), Samples.Bulletin(72));
        time.Advance(TimeSpan.FromMinutes(5));
        var service = new DeliveryService(host.Intake, new AnsweringBbs(b => b.Bid == "71_GB7RDG"
            ? new DeliveryOutcome(b.Bid, DeliveryVerdict.Accepted)
            : new DeliveryOutcome(b.Bid, DeliveryVerdict.Deferred)), host.Ledger, time, _ => { });
        await service.DeliverPendingAsync(CancellationToken.None);
        host.Intake.Offer(FirstPieceOf(Samples.Bulletin(970, bodyLines: 200))); // a part-received one too

        var status = await http.GetFromJsonAsync<JsonElement>("api/status");

        var bulletins = status.GetProperty("bulletins").EnumerateArray().ToList();
        Assert.Equal(3, bulletins.Count); // 71, 72 and the part-received one
        var delivered = bulletins.Single(b => b.GetProperty("bid").GetString() == "71_GB7RDG");
        foreach (string key in new[] { "bid", "title", "complete", "received", "needed", "delivery", "deliveryShort", "completedAt", "deliveredAt", "deliveredVerdict", "deliveredDetail" })
        {
            Assert.True(delivered.TryGetProperty(key, out _), $"bulletins[].{key} is missing");
        }
        Assert.True(delivered.GetProperty("complete").GetBoolean());
        Assert.Equal("accepted", delivered.GetProperty("deliveryShort").GetString());
        Assert.Equal(Start, delivered.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(Start + TimeSpan.FromMinutes(5), delivered.GetProperty("deliveredAt").GetDateTimeOffset());
        var partial = bulletins.Single(b => b.GetProperty("bid").ValueKind == JsonValueKind.Null);
        Assert.False(partial.GetProperty("complete").GetBoolean());
        Assert.Equal(1, partial.GetProperty("received").GetInt32());

        var deliveries = status.GetProperty("deliveries").EnumerateArray().ToList();
        Assert.NotEmpty(deliveries);
        foreach (string key in new[] { "time", "bid", "title", "verdict", "said", "saidShort", "detail" })
        {
            Assert.True(deliveries[0].TryGetProperty(key, out _), $"deliveries[].{key} is missing");
        }
        Assert.Contains(deliveries, d => d.GetProperty("bid").GetString() == "71_GB7RDG" && d.GetProperty("saidShort").GetString() == "accepted");
        Assert.Contains(deliveries, d => d.GetProperty("bid").GetString() == "72_GB7RDG" && d.GetProperty("saidShort").GetString() == "deferred");
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

    [Fact]
    public async Task OneDeliverySession_RebuildsTheMailListOnce()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        await using var intake = new Intake(dir.Path, _ => { }, new ReceiverStoreOptions { Time = time });
        await RebuildAsync(intake, Samples.Bulletin(31), Samples.Bulletin(32), Samples.Bulletin(33), Samples.Bulletin(34));
        var service = new DeliveryService(intake, new AnsweringBbs(b => new DeliveryOutcome(b.Bid, DeliveryVerdict.Accepted)), new DeliveryLedger(dir.Path), time, _ => { });
        long before = intake.MailBuilds;

        Assert.Null(await service.DeliverPendingAsync(CancellationToken.None));

        Assert.Equal(before + 1, intake.MailBuilds);
        Assert.Equal(4, intake.Mail().Archived);
        Assert.Equal(0, intake.Mail().Waiting);
    }

    [Fact]
    public async Task SendToBbsAgain_PastFiftyWaiting_IsTooMany()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        var bulletins = Enumerable.Range(40, ReceiverStore.MaxResentWaiting + 1).Select(i => Samples.Bulletin(i, bodyLines: 2)).ToArray();
        await RebuildAsync(host.Intake, bulletins);
        host.Intake.Acknowledge([.. bulletins.Select(b => (b.Bid, BbsVerdict.Accepted, (string?)null))]);
        var ids = host.Intake.Mail().NewestFirst.Select(m => ObjectId.Format(m.ObjectId)).ToList();

        foreach (string id in ids.Take(ReceiverStore.MaxResentWaiting))
        {
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Resend(id))).StatusCode);
        }
        var tooMany = await http.SendAsync(Resend(ids[^1]));

        Assert.Equal(HttpStatusCode.TooManyRequests, tooMany.StatusCode);
        Assert.Contains("50 bulletins sent again are already waiting", await tooMany.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(ReceiverStore.MaxResentWaiting, host.Intake.Mail().Waiting);
    }

    /// <summary>
    /// Issue #73: the combined Bulletins section's data, from the one endpoint - a part-received
    /// object, a delivered bulletin and a refused one, all at once, with the summary counts the
    /// page's own heading is built from.
    /// </summary>
    [Fact]
    public async Task Mail_ShowsAPartReceivedAndADeliveredAndARefusedBulletin_WithSummaryCounts()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await RebuildAsync(host.Intake, Samples.Bulletin(1), Samples.Bulletin(2));
        var service = new DeliveryService(host.Intake, new AnsweringBbs(b => b.Bid == "1_GB7RDG"
            ? new DeliveryOutcome(b.Bid, DeliveryVerdict.Accepted)
            : new DeliveryOutcome(b.Bid, DeliveryVerdict.Refused, "FS R: not wanted here")), host.Ledger, time, _ => { });
        await service.DeliverPendingAsync(CancellationToken.None);
        host.Intake.Offer(FirstPieceOf(Samples.Bulletin(900, bodyLines: 200)));

        var mail = await http.GetFromJsonAsync<JsonElement>("api/mail");
        Assert.Equal(0, mail.GetProperty("waiting").GetInt32());
        Assert.Equal(1, mail.GetProperty("delivered").GetInt32());
        Assert.Equal(1, mail.GetProperty("refused").GetInt32());
        var partial = Assert.Single(mail.GetProperty("partial").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, partial.GetProperty("bid").ValueKind); // not yet named
        Assert.Equal(1, partial.GetProperty("received").GetInt32());
        Assert.True(partial.GetProperty("needed").GetInt32() > 1);

        var items = mail.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("bid").GetString()!);
        Assert.Equal("accepted", items["1_GB7RDG"].GetProperty("verdict").GetString());
        Assert.Equal("refused", items["2_GB7RDG"].GetProperty("verdict").GetString());
        Assert.Equal("FS R: not wanted here", items["2_GB7RDG"].GetProperty("detail").GetString());
    }

    /// <summary>
    /// Issue #73: the part-received list is handed over on the side, not inside the archive's own
    /// paging, so it reads the same at every offset while the archive page underneath it moves on.
    /// </summary>
    [Fact]
    public async Task Mail_PartialRows_AreTheSameOnEveryPage_WhileArchivePagesMoveOn()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await RebuildAsync(host.Intake, Samples.Bulletin(1), Samples.Bulletin(2));
        var service = new DeliveryService(host.Intake, new AnsweringBbs(b => new DeliveryOutcome(b.Bid, DeliveryVerdict.Accepted)), host.Ledger, time, _ => { });
        await service.DeliverPendingAsync(CancellationToken.None);
        host.Intake.Offer(FirstPieceOf(Samples.Bulletin(901, bodyLines: 200)));

        var firstPage = await http.GetFromJsonAsync<JsonElement>("api/mail?offset=0&limit=1");
        var secondPage = await http.GetFromJsonAsync<JsonElement>("api/mail?offset=1&limit=1");
        Assert.Equal(2, firstPage.GetProperty("total").GetInt32());
        string firstBid = Assert.Single(firstPage.GetProperty("items").EnumerateArray()).GetProperty("bid").GetString()!;
        string secondBid = Assert.Single(secondPage.GetProperty("items").EnumerateArray()).GetProperty("bid").GetString()!;
        Assert.NotEqual(firstBid, secondBid);
        Assert.Single(firstPage.GetProperty("partial").EnumerateArray());
        Assert.Single(secondPage.GetProperty("partial").EnumerateArray());
    }

    /// <summary>
    /// Issue #73: a bulletin offered more than once (deferred, then accepted on a later session)
    /// keeps every answer in order in its own "attempts", for the reader to show as its story -
    /// not only the final verdict the Status column already gives.
    /// </summary>
    [Fact]
    public async Task Mail_AttemptHistory_KeepsEveryAnswerInOrder()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await RebuildAsync(host.Intake, Samples.Bulletin(10));
        int calls = 0;
        var bbs = new AnsweringBbs(b => ++calls == 1
            ? new DeliveryOutcome(b.Bid, DeliveryVerdict.Deferred, "offering it again later")
            : new DeliveryOutcome(b.Bid, DeliveryVerdict.Accepted));
        var service = new DeliveryService(host.Intake, bbs, host.Ledger, time, _ => { });

        Assert.NotNull(await service.DeliverPendingAsync(CancellationToken.None)); // deferred first
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(await service.DeliverPendingAsync(CancellationToken.None)); // accepted on the second try

        var item = Assert.Single(await MailAsync(http));
        Assert.Equal("accepted", item.GetProperty("verdict").GetString());
        var attempts = item.GetProperty("attempts").EnumerateArray().ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal("deferred", attempts[0].GetProperty("saidShort").GetString());
        Assert.Equal(Start, attempts[0].GetProperty("time").GetDateTimeOffset());
        Assert.Equal("accepted", attempts[1].GetProperty("saidShort").GetString());
        Assert.Equal(Start + TimeSpan.FromMinutes(10), attempts[1].GetProperty("time").GetDateTimeOffset());
    }

    /// <summary>
    /// Issue #73: a BBS the receiver cannot reach at all (no answer, a login refused, and the
    /// like) is not about any one bulletin, so it stays in the "Your BBS" tile's own data
    /// (<c>bbs.lastFailure</c>) rather than turning up as any bulletin's delivery history. Using
    /// <c>host.Delivery</c> itself, pointed (by <see cref="StartAsync"/>'s default config) at a
    /// BBS port nothing listens on, so the session fails before it offers anything, the same way
    /// <see cref="StatusPageTests.DailyReport_IsOfferedOnlyWithABbs"/> treats that failure.
    /// </summary>
    [Fact]
    public async Task SessionFailure_IsNotTiedToOneBid_AndStaysInTheBbsTile()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        var (host, page, http, _) = await StartAsync(dir.Path, time);
        await using var _h = host;
        await using var _p = page;
        using var _c = http;
        await RebuildAsync(host.Intake, Samples.Bulletin(5));

        string? failure = await host.Delivery.DeliverPendingAsync(CancellationToken.None);
        Assert.NotNull(failure);

        var status = await http.GetFromJsonAsync<JsonElement>("api/status");
        Assert.Equal(failure, status.GetProperty("bbs").GetProperty("lastFailure").GetString());
        var item = Assert.Single(await MailAsync(http));
        Assert.True(item.GetProperty("waiting").GetBoolean());
        Assert.Empty(item.GetProperty("attempts").EnumerateArray());
    }
}
