using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.Mailcast.Propagation;
using Mailcast.Receiver.Web;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>The receiver and content type 4: decoded, logged once, kept across restarts, never for the BBS, and on the page.</summary>
public class IonosphereTests
{
    private static readonly DateTimeOffset Sounded = new(2026, 10, 6, 14, 30, 0, TimeSpan.Zero);

    private static readonly IonoReading Reading = IonoEvaluator.Evaluate(
        [new IonoSounding("RL052", Sounded, IonoSource.Giro, 6.05, M3000: 3.3)], new IonoSettings(), Sounded.AddMinutes(10));

    private static byte[] Ax25(MailcastFrame frame) => Ax25UiFrame.Build(OnAir.Source, OnAir.Destination, frame.ToBytes());

    [Fact]
    public async Task TheReading_IsShownAndKept_ButNeverWaitsForTheBbs()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Sounded.AddMinutes(12));
        var log = new List<string>();
        var obj = IonoRecord.ToTransferObject(Reading);
        await using (var intake = new Intake(dir.Path, log.Add, new ReceiverStoreOptions { Time = time }))
        {
            Assert.True(intake.Offer(Ax25(obj.Frame(0))));
            Assert.True(intake.Offer(Ax25(obj.Frame(1))));
            await intake.DrainAsync(CancellationToken.None);
            Assert.Empty(intake.Pending());
            Assert.Equal(6.05, intake.Ionosphere!.FoF2);
        }
        Assert.Equal(
            ["Ionosphere: Chilton foF2 6.05 MHz at 14:30 UTC (12 min old). 40 m: closed at 100 km, open from about 570 km."],
            log);

        await using var reopened = new Intake(dir.Path, _ => { }, new ReceiverStoreOptions { Time = time });
        Assert.Equal(Sounded, reopened.Ionosphere!.SoundingTimeUtc);
    }

    [Fact]
    public void ThePage_SaysItInWords_WithTheVerdictAtEachDistance_AndAgesIt()
    {
        Assert.Null(StatusPage.IonoView(null, Sounded));
        var json = JsonSerializer.SerializeToElement(StatusPage.IonoView(Reading, Sounded.AddMinutes(12)), ReceiverConfig.JsonLine);
        Assert.Equal("MARGINAL", json.GetProperty("state").GetString());
        Assert.Equal(12, json.GetProperty("ageMinutes").GetInt32());
        Assert.Equal("Chilton", json.GetProperty("stationName").GetString());
        Assert.Equal(570, json.GetProperty("skipZoneKm").GetInt32());
        Assert.Equal("Ionosphere: Chilton foF2 6.05 MHz at 14:30 UTC (12 min old). 40 m: closed at 100 km, open from about 570 km.", json.GetProperty("words").GetString());
        Assert.Equal("100 km closed, 500 km closed, 1000 km good", json.GetProperty("distanceWords").GetString());
        var distances = json.GetProperty("distances").EnumerateArray().Select(d => (d.GetProperty("km").GetInt32(), d.GetProperty("verdict").GetString())).ToList();
        Assert.Equal([(100, "closed"), (500, "closed"), (1000, "reliable")], distances);

        // An hour on and nothing newer heard: the page says it is too old to go by.
        var old = JsonSerializer.SerializeToElement(StatusPage.IonoView(Reading, Sounded.AddMinutes(70)), ReceiverConfig.JsonLine);
        Assert.Equal("UNKNOWN", old.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, old.GetProperty("distanceWords").ValueKind);
        Assert.Contains("Too old to judge 40 m by", old.GetProperty("words").GetString(), StringComparison.Ordinal);
    }
}
