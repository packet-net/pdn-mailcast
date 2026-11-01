using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

/// <summary>Issue #224: stations listened for by hand until they are heard or advertised.</summary>
public class ListenForTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 5, 0, TimeSpan.Zero);

    private static ListenForEntry Entry(string call = "GM8BPQ", int minute = 30, int every = 60, long hz = 7_047_000) =>
        new() { Callsign = call, Minute = minute, EveryMinutes = every, FrequencyHz = hz, Sideband = "usb" };

    private static ReceiverConfig Config(string dir, params ListenForEntry[] entries) => new()
    {
        Audio = "ubersdr:wessex.zapto.org",
        StateDirectory = dir,
        Stations = StationFixture.EverySlot,
        ListenFor = entries.Length == 0 ? null : entries,
    };

    private static async Task HearAsync(ReceiverHost host, ScheduleOptions options, DateTimeOffset slot, int bulletin)
    {
        foreach (var frame in Samples.Frames([Samples.Bulletin(bulletin)], options, slot))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnEntry_IsFollowedAtItsSlots_AndMarkedAddedByHand()
    {
        using var dir = new TempDirectory();
        await using var host = new ReceiverHost(Config(dir.Path, Entry()), new FakeTimeProvider(Now), _ => { });

        var listened = host.Listening.Single(l => l.Station == "GM8BPQ");
        Assert.True(listened.Followed);
        Assert.Equal("added by hand", listened.Source);
        Assert.Equal(7_047_000, listened.FrequencyHz);
        Assert.Equal(["GB7RDG", "GM8BPQ"], host.Followed.Stations.Select(s => s.Station));
        Assert.Equal("GM8BPQ", host.Followed.StationAt(new DateTimeOffset(2026, 10, 31, 11, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task AnEntry_IsNotFollowed_WhenTheReceiverCannotTuneThere_OrItsSlotsAreTooClose()
    {
        using var dir = new TempDirectory();
        var config = Config(dir.Path, Entry("M0FAR", hz: 10_489_750_000), Entry("M0FST", every: 30));
        await using var host = new ReceiverHost(config, new FakeTimeProvider(Now), _ => { });

        Assert.False(host.Listening.Single(l => l.Station == "M0FAR").Followed);
        Assert.Contains("above HF", host.Listening.Single(l => l.Station == "M0FAR").NotFollowedBecause, StringComparison.Ordinal);
        Assert.Contains("every 30 minutes", host.Listening.Single(l => l.Station == "M0FST").NotFollowedBecause, StringComparison.Ordinal);
        Assert.Equal(["GB7RDG"], host.Followed.Stations.Select(s => s.Station));
    }

    [Fact]
    public async Task Caps_AtMostSevenStations_AndHandEntriesComeBeforeAdverts()
    {
        var xyz = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        // Four by hand (added in order), five adverts, the fixture station and M0XYZ: seven places in all, the adverts that lose are the last.
        var hand = Enumerable.Range(1, 4).Select(i => Entry($"G0HA{i}", minute: 10 + i)).ToArray();
        var config = Config(dir.Path, hand) with { TrustedKeys = [new TrustedKeySettings { Label = "M0XYZ", PublicKey = xyz.PublicKeyBase64 }] };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(Now), _ => { });
        await HearAsync(host, ScheduleOptions.Hourly with
        {
            Timetable = new SlotTimetable(new TimeOnly(0, 5), 60),
            Signer = xyz,
            Station = "M0XYZ",
            Adverts = [.. Enumerable.Range(1, 5).Select(i => new StationAdvert($"G0AA{i}", new SlotTimetable(new TimeOnly(0, 30 + i), 60), 7_053_800, Sideband.Usb))],
        }, Now, 1);

        var listening = host.Listening.ToDictionary(l => l.Station);
        Assert.Equal(["M0XYZ", "GB7RDG", "G0HA1", "G0HA2", "G0HA3", "G0HA4", "G0AA1"], host.Followed.Stations.Select(s => s.Station));
        Assert.Contains("at most 7 stations, and others were heard of first", listening["G0AA2"].NotFollowedBecause, StringComparison.Ordinal);
        Assert.Equal("added by hand", listening["G0HA4"].Source);
        Assert.Equal("an advert", listening["G0AA1"].Source);
    }

    [Fact]
    public async Task WhenItsOwnDirectoryIsHeard_TheEntryGoes_WithOneLogLine_AndItsOwnTimetableIsUsed()
    {
        var bpq = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        using var dir = new TempDirectory();
        var log = new List<string>();
        var config = Config(dir.Path, Entry("GM8BPQ", minute: 20), Entry("M0OTH", minute: 50)) with
        {
            TrustedKeys = [new TrustedKeySettings { Label = "GM8BPQ", PublicKey = bpq.PublicKeyBase64 }],
        };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(Now), line => { lock (log) { log.Add(line); } });
        Assert.Equal("added by hand", host.Listening.Single(l => l.Station == "GM8BPQ").Source);

        await HearAsync(host, ScheduleOptions.Hourly with { Timetable = new SlotTimetable(new TimeOnly(0, 40), 60), Signer = bpq, Station = "GM8BPQ" }, Now.AddMinutes(30), 1);

        // The config is left alone; the entry is retired in the state file and no longer used.
        Assert.Equal(["GM8BPQ", "M0OTH"], host.Config.ListenFor!.Select(e => e.Callsign));
        Assert.StartsWith("its own signed directory was heard", host.ListenForState.Get(host.Config.ListenFor![0])!.Retired, StringComparison.Ordinal);
        Assert.Null(host.ListenForState.Get(host.Config.ListenFor![1])!.Retired);
        var own = host.Listening.Single(l => l.Station == "GM8BPQ");
        Assert.Equal("its own directory", own.Source);
        Assert.Equal(40, own.Timetable.Anchor.Minute);
        lock (log)
        {
            string line = Assert.Single(log, l => l.StartsWith("listen-for: GM8BPQ was heard", StringComparison.Ordinal));
            Assert.All(line, c => Assert.True(c < 128));
        }
    }

    [Fact]
    public async Task AnEntryNeverHeard_IsRemovedAfterSevenDays_WithALogLineAndANote()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        var log = new List<string>();
        await using var host = new ReceiverHost(Config(dir.Path, Entry()), time, line => { lock (log) { log.Add(line); } });

        time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        host.ExpireListenFor();
        Assert.Null(host.ListenForState.Get(host.Config.ListenFor![0])!.Retired);

        time.Advance(TimeSpan.FromMinutes(1));
        host.ExpireListenFor();
        host.ExpireListenFor();
        Assert.Single(host.Config.ListenFor!);
        Assert.Equal("not heard in 7 days", host.ListenForState.Get(host.Config.ListenFor![0])!.Retired);
        Assert.DoesNotContain(host.Listening, l => l.Station == "GM8BPQ");
        lock (log)
        {
            Assert.Single(log, l => l.StartsWith("listen-for: GM8BPQ was not heard in 7 days", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheAddedTime_SurvivesARestart_AndTheConfigFileIsNeverWritten()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, "{ \"audio\": \"ubersdr:wessex.zapto.org\" } // my notes\n");
        var time = new FakeTimeProvider(Now);
        var config = Config(dir.Path, Entry());
        await using (var first = new ReceiverHost(config, time, _ => { }))
        {
            Assert.Equal(Now, first.ListenForState.Get(config.ListenFor![0])!.AddedUtc);
        }
        time.Advance(TimeSpan.FromDays(3));
        await using var second = new ReceiverHost(config, time, _ => { });
        Assert.Equal(Now, second.ListenForState.Get(config.ListenFor![0])!.AddedUtc);
        Assert.EndsWith("// my notes\n", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfigFile_RoundTrips_AndRejectsABadEntry()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        Config(dir.Path, Entry() with { Locator = "IO87", Daylight = new DaylightSettings { Locator = "IO87" } }).Save(path);
        string text = File.ReadAllText(path);
        Assert.Contains("\"listenFor\"", text, StringComparison.Ordinal);
        var back = ReceiverConfig.Load(path).ListenFor!.Single();
        Assert.Equal("GM8BPQ", back.Callsign);
        Assert.Equal("IO87", back.Daylight!.Locator);

        var bad = Assert.Throws<ConfigException>(() => Config(dir.Path, Entry(hz: 0)).Validate());
        Assert.Contains("\"listenFor\" entry 1", bad.Message, StringComparison.Ordinal);
                Assert.Throws<ConfigException>(() => Config(dir.Path, Entry(), Entry()).Validate());
        Assert.Throws<ConfigException>(() => Config(dir.Path, Entry(every: 7)).Validate());
    }

    [Fact]
    public async Task TheStartUpLines_SayEachStationsSlotsAndWhereTheyCameFrom()
    {
        using var dir = new TempDirectory();
        await using var plain = new ReceiverHost(Config(dir.Path), new FakeTimeProvider(Now), _ => { });
        var lines = plain.SlotLines().ToList();
        Assert.Equal("stations: fixture lists GB7RDG", lines[0]);
        lines = [.. lines.Where(l => l.StartsWith("slots:", StringComparison.Ordinal))];
        Assert.Single(lines);
        Assert.Equal("slots: GB7RDG's slots are every hour on the hour (from the stations file)", lines[0]);

        await using var shifted = new ReceiverHost(Config(dir.Path) with { Stations = StationFixture.Custom(minute: 30) }, new FakeTimeProvider(Now), _ => { });
        lines = [.. shifted.SlotLines().Where(l => l.StartsWith("slots:", StringComparison.Ordinal))];
        Assert.Equal("slots: GB7RDG's slots are every hour at 30 minutes past (from the stations file)", lines[0]);
        Assert.All(lines, l => Assert.All(l, c => Assert.True(c < 128)));
    }

    private static async Task<(ReceiverHost Host, StatusPage Page, HttpClient Http)> StartAsync(string dir, FakeTimeProvider time, string password = "")
    {
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var config = Config(dir) with { Web = new WebSettings { Port = port, Password = password } };
            var host = new ReceiverHost(config, time, _ => { });
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

    private static HttpRequestMessage Post(string url, object body, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return request;
    }

    private static readonly object Add = new { callsign = "gm8bpq", minute = 30, everyMinutes = 60, frequencyHz = 7_047_000, sideband = "usb", locator = "IO87" };

    [Fact]
    public async Task ThePage_AddsAndRemovesAnEntry_AndShowsItAsAddedByHand()
    {
        using var dir = new TempDirectory();
        var (host, page, http) = await StartAsync(dir.Path, new FakeTimeProvider(Now));
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Post("api/stations/listen-for", new { callsign = "GM8BPQ", minute = 30 }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post("api/stations/listen-for", Add))).StatusCode);
        var bad = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "api/stations/listen-for") { Content = new StringContent("{nope", System.Text.Encoding.UTF8, "application/json") });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Could not read the request", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var noLocator = await http.SendAsync(Post("api/stations/listen-for", new { callsign = "M0NOL", minute = 5, frequencyHz = 7_040_000, sideband = "usb", daylightOnly = true }));
        Assert.Equal(HttpStatusCode.BadRequest, noLocator.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post("api/stations/listen-for", new { callsign = "M0EVR", minute = 5, frequencyHz = 7_040_000, sideband = "usb" }))).StatusCode);
        Assert.Null(host.Config.ListenFor!.Single(e => e.Callsign == "M0EVR").Daylight);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post("api/stations/listen-for/remove", new { callsign = "M0EVR" }))).StatusCode);

        var status = await http.GetFromJsonAsync<JsonElement>("api/status");
        var row = status.GetProperty("listening").EnumerateArray().Single(l => l.GetProperty("station").GetString() == "GM8BPQ");
        Assert.True(row.GetProperty("byHand").GetBoolean());
        Assert.True(row.GetProperty("followed").GetBoolean());
        var entry = status.GetProperty("listenFor").EnumerateArray().Single();
        Assert.Equal("GM8BPQ", entry.GetProperty("callsign").GetString());
        Assert.Equal(Now, entry.GetProperty("addedUtc").GetDateTimeOffset());
        Assert.Contains("daylight", entry.GetProperty("slots").GetString(), StringComparison.Ordinal); // a locator means daylight

        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Post("api/stations/listen-for/remove", new { callsign = "G0NOP" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post("api/stations/listen-for/remove", new { callsign = "GM8BPQ" }))).StatusCode);
        Assert.Null(host.Config.ListenFor);
        Assert.DoesNotContain(host.Listening, l => l.Station == "GM8BPQ");
    }

    [Fact]
    public async Task ThePage_RefusesACrossSitePost_AndOneWithoutThePassword()
    {
        using var dir = new TempDirectory();
        var (host, page, http) = await StartAsync(dir.Path, new FakeTimeProvider(Now), password: "letmein");
        await using var _h = host;
        await using var _p = page;
        using var _c = http;

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post("api/stations/listen-for", Add))).StatusCode);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String("any:letmein"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post("api/stations/listen-for", Add, "http://evil.example"))).StatusCode);
        Assert.Null(host.Config.ListenFor);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post("api/stations/listen-for", Add))).StatusCode);
        Assert.Single(host.Config.ListenFor!);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post("api/stations/listen-for/remove", new { callsign = "GM8BPQ" }, "http://evil.example"))).StatusCode);
        Assert.Single(host.Config.ListenFor!);
    }
}
