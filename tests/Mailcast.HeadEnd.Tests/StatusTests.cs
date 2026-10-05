using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Status;

namespace Mailcast.HeadEnd.Tests;

public class StatusTests
{
    private static SlotReport Report => new()
    {
        Day = new DateOnly(2026, 10, 5),
        Start = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 10, 5, 12, 21, 40, TimeSpan.Zero),
        Outcome = SlotOutcome.Aborted,
        Reason = "PA temperature 71.0 C passed the 70.0 C limit",
        FramesPlanned = 171,
        FramesSent = 120,
        FramesQueued = 120,
        Bursts = 18,
        BulletinsInRotation = 34,
        ToneSent = true,
        Reference = "GPS locked (GPSDO locked)",
        PaTemperatureMaxC = 71,
    };

    [Fact]
    public void Store_KeepsTheLastSlotAcrossARestart()
    {
        using var dir = new TempDirectory();
        var first = new StatusStore(dir.Path, TimeProvider.System);
        first.RecordSlot(Report);
        first.RecordIntake("FBB", new IntakeResult(3, 1, null));

        var second = new StatusStore(dir.Path, TimeProvider.System);
        Assert.Equal(Report, second.LastSlot);
        var json = JsonNode.Parse(second.Render())!;
        Assert.Equal("Aborted", json["lastSlot"]!["outcome"]!.GetValue<string>());
        Assert.Equal(120, json["lastSlot"]!["framesSent"]!.GetValue<int>());
        Assert.Equal(71, json["lastSlot"]!["paTemperatureMaxC"]!.GetValue<double>());
        Assert.Equal("GPS locked (GPSDO locked)", json["lastSlot"]!["reference"]!.GetValue<string>());
    }

    [Fact]
    public async Task Server_ServesTheStatusDocument()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var status = new StatusStore(null, TimeProvider.System);
        status.RecordSlot(Report);
        status.SetBulletinsHeld(34);
        await using var server = new StatusServer("127.0.0.1", port, status);
        server.Start();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var json = JsonNode.Parse(await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/status")))!;
        Assert.Equal("pdn-mailcast-headend", json["service"]!.GetValue<string>());
        Assert.Equal(34, json["bulletinsHeld"]!.GetValue<int>());
        Assert.Equal(171, json["lastSlot"]!["framesPlanned"]!.GetValue<int>());
        var missing = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/other"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Server_PostRun_StartsAOneOffSlot_OrSaysWhyNot()
    {
        int port = FreePort();
        var asked = new List<string>();
        bool busy = false;
        var status = new StatusStore(null, TimeProvider.System);
        await using var server = new StatusServer("127.0.0.1", port, status, who =>
        {
            asked.Add(who);
            return busy
                ? new Service.RunNowAnswer(false, null, "a slot is running")
                : new Service.RunNowAnswer(true, new DateTimeOffset(2026, 10, 5, 12, 24, 0, TimeSpan.Zero), null);
        });
        server.Start();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var url = new Uri($"http://127.0.0.1:{port}/run");

        using var first = new HttpRequestMessage(HttpMethod.Post, url);
        first.Headers.Add(StatusServer.RequestedByHeader, "tf with --run-now");
        var accepted = await http.SendAsync(first);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var json = JsonNode.Parse(await accepted.Content.ReadAsStringAsync())!;
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 24, 0, TimeSpan.Zero), json["slot"]!.GetValue<DateTimeOffset>());
        Assert.StartsWith("tf with --run-now (127.0.0.1:", asked.Single(), StringComparison.Ordinal);

        busy = true;
        var conflict = await http.PostAsync(url, null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("a slot is running", JsonNode.Parse(await conflict.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
        Assert.StartsWith("someone (127.0.0.1:", asked[1], StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.GetAsync(url)).StatusCode);
        Assert.Equal(2, asked.Count);

        // Without a way to start one, there is no /run at all.
        int other = FreePort();
        await using var readOnly = new StatusServer("127.0.0.1", other, status);
        readOnly.Start();
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync(new Uri($"http://127.0.0.1:{other}/run"), null)).StatusCode);
    }

    [Fact]
    public void SlotsToday_CountsEachSlotOnceByItsLatestRun_AndSurvivesARestart()
    {
        using var dir = new TempDirectory();
        var time = new VirtualTime(new DateTimeOffset(2026, 10, 5, 15, 30, 0, TimeSpan.Zero));
        var store = new StatusStore(dir.Path, time);
        SlotReport At(int hour, int minute, SlotOutcome outcome) => new()
        {
            Slot = new DateTimeOffset(2026, 10, 5, hour, 0, 0, TimeSpan.Zero),
            Day = new DateOnly(2026, 10, 5),
            End = new DateTimeOffset(2026, 10, 5, hour, minute, 0, TimeSpan.Zero),
            Outcome = outcome,
        };
        store.RecordSlot(At(12, 3, SlotOutcome.Completed));
        store.RecordSlot(At(13, 1, SlotOutcome.Skipped));
        store.RecordSlot(At(13, 7, SlotOutcome.Completed)); // the retry
        store.RecordSlot(At(14, 2, SlotOutcome.Aborted));
        store.RecordSlot(At(15, 0, SlotOutcome.Skipped));
        var yesterday = At(23, 5, SlotOutcome.Completed) with { Slot = new DateTimeOffset(2026, 10, 4, 23, 0, 0, TimeSpan.Zero), Day = new DateOnly(2026, 10, 4) };
        store.RecordSlot(yesterday);
        Assert.Equal(new SlotsToday(new DateOnly(2026, 10, 5), 4, 2, 1, 1), store.SlotsToday);

        var reopened = new StatusStore(dir.Path, time);
        Assert.Equal(store.SlotsToday, reopened.SlotsToday);
        var json = JsonNode.Parse(reopened.Render())!;
        Assert.Equal(4, json["slotsToday"]!["slots"]!.GetValue<int>());
        Assert.Equal(2, json["slotsToday"]!["completed"]!.GetValue<int>());
    }
}

