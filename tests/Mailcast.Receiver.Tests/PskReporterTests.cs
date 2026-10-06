using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.Mailcast.Propagation;
using Mailcast.Receiver.Web;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>The receiver and the PSK Reporter reading (content type 4, source 3): decoded, logged once, kept across restarts, never for the BBS, and in the propagation tile.</summary>
public class PskReporterTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 6, 14, 0, 0, TimeSpan.Zero);

    /// <summary>Nothing under 250 km on 40 m, 41 spots further out.</summary>
    private static readonly PskReading Reading = PskEvaluator.Evaluate(Spots(), Observed, PskEvaluator.Window);

    private static IEnumerable<PskSpot> Spots()
    {
        for (int i = 0; i < 25; i++)
        {
            yield return new PskSpot(i, Observed.AddMinutes(-4), PskBand.Forty, 520 + i, -11, $"S{i % 12}", $"S{(i + 1) % 12}");
        }
        for (int i = 0; i < 16; i++)
        {
            yield return new PskSpot(100 + i, Observed.AddMinutes(-4), PskBand.Forty, 760 + i, -13, $"T{i % 9}", $"T{(i + 1) % 9}");
        }
    }

    private static byte[] Ax25(MailcastFrame frame) => Ax25UiFrame.Build(Samples.Source, OnAir.Destination, frame.ToBytes());

    [Fact]
    public async Task TheReading_IsLoggedOnceAndKept_ButNeverWaitsForTheBbs()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Observed.AddMinutes(3));
        var log = new List<string>();
        var obj = PskRecord.ToTransferObject(Reading);
        await using (var intake = new Intake(dir.Path, log.Add, new ReceiverStoreOptions { Time = time }))
        {
            Assert.True(intake.Offer(Ax25(obj.Frame(0))));
            Assert.True(intake.Offer(Ax25(obj.Frame(1))));
            await intake.DrainAsync(CancellationToken.None);
            Assert.Empty(intake.Pending());
            Assert.Equal(25, intake.PskReporter!.Forty!.At(500)!.Spots);
            Assert.Null(intake.Ionosphere);
        }
        Assert.Equal(
            ["40 m FT8/FT4/WSPR spots (PSK Reporter, last 30 min to 14:00 UTC, 3 min ago): open at 500 km (25 spots, 12 stations, median -11 dB) and 1000 km (16 spots, 9 stations, median -13 dB), nothing under 250 km despite 41 spots further out."],
            log);

        await using var reopened = new Intake(dir.Path, _ => { }, new ReceiverStoreOptions { Time = time });
        Assert.Equal(Observed, reopened.PskReporter!.ObservedUtc);
    }

    [Fact]
    public void TheTile_SaysItInPlainWords_WithTheCountsAtEachDistance_AndAgesIt()
    {
        Assert.Null(StatusPage.PskView(null, Observed));
        var json = JsonSerializer.SerializeToElement(StatusPage.PskView(Reading, Observed.AddMinutes(5)), ReceiverConfig.JsonLine);
        Assert.Equal("MARGINAL", json.GetProperty("state").GetString());
        Assert.Equal(5, json.GetProperty("ageMinutes").GetInt32());
        Assert.Equal(30, json.GetProperty("windowMinutes").GetInt32());
        Assert.Equal("FT8 spots: open from about 520 km", json.GetProperty("headline").GetString());
        Assert.Equal(
            "40 m FT8/FT4/WSPR spots (PSK Reporter, last 30 min to 14:00 UTC, 5 min ago): open at 500 km (25 spots, 12 stations, median -11 dB) and 1000 km (16 spots, 9 stations, median -13 dB), nothing under 250 km despite 41 spots further out.",
            json.GetProperty("words").GetString());
        Assert.Equal(
            "100 km closed (0 spots), 500 km open (25 spots, 12 stations, median -11 dB), 1000 km open (16 spots, 9 stations, median -13 dB)",
            json.GetProperty("distanceWords").GetString());
        var distances = json.GetProperty("distances").EnumerateArray()
            .Select(d => (d.GetProperty("km").GetInt32(), d.GetProperty("verdict").GetString(), d.GetProperty("spots").GetInt32()))
            .ToList();
        Assert.Equal([(100, "closed", 0), (500, "open", 25), (1000, "open", 16)], distances);
        Assert.Equal("otherDistances", json.GetProperty("distances")[0].GetProperty("closedBy").GetString());

        // An hour on and nothing newer heard: too old to go by.
        var old = JsonSerializer.SerializeToElement(StatusPage.PskView(Reading, Observed.AddMinutes(60)), ReceiverConfig.JsonLine);
        Assert.Equal("UNKNOWN", old.GetProperty("state").GetString());
        Assert.Equal("No fresh PSK Reporter reading", old.GetProperty("headline").GetString());
        Assert.Equal(JsonValueKind.Null, old.GetProperty("distanceWords").ValueKind);
        Assert.EndsWith("too old to judge 40 m by.", old.GetProperty("words").GetString(), StringComparison.Ordinal);

        // A head end whose feed was down says so.
        var down = JsonSerializer.SerializeToElement(StatusPage.PskView(PskEvaluator.Evaluate(Spots(), Observed, TimeSpan.Zero), Observed), ReceiverConfig.JsonLine);
        Assert.Equal("No verdict: the PSK Reporter feed was down", down.GetProperty("headline").GetString());
        Assert.Equal(JsonValueKind.Null, down.GetProperty("distanceWords").ValueKind);
    }
}
