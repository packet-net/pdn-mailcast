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
}
