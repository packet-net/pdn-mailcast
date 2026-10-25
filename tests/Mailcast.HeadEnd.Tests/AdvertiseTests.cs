using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Tests;

/// <summary>A head end telling listeners about other stations (issue #178), and naming itself.</summary>
public class AdvertiseTests
{
    private static string Config(string advertise) => $$"""
        {
          "callsign": "M0LTE",
          "station": { "apiKey": "k" },
          "slot": { "timeUtc": "00:00", "everyMinutes": 60 },
          "advertise": [{{advertise}}]
        }
        """;

    private const string Hf = """{ "callsign": "G8BPQ", "slot": { "timeUtc": "00:30", "everyMinutes": 60 }, "frequencyHz": 7053800, "sideband": "usb" }""";
    private const string Qo100 = """{ "callsign": "G8BPQ", "slot": { "timeUtc": "00:15", "everyMinutes": 60, "daylight": { "locator": "IO93fb" } }, "frequencyHz": 10489750000 }""";

    [Fact]
    public void EveryDirectory_NamesTheHeadEnd_AndCarriesTheAdverts_OneStationOnTwoFrequencies()
    {
        var config = HeadEndConfig.Parse(Config(Hf + "," + Qo100));
        var options = config.ToScheduleOptions();
        Assert.Equal("M0LTE", options.Station);
        Assert.Equal(2, options.Adverts.Count);
        Assert.Equal("advertising: G8BPQ slots=00:30/60 on 7.0538 MHz USB; G8BPQ slots=00:15/60 daylight=IO93fb/120/30 on 10489.7500 MHz USB", Program.DescribeAdverts(config));

        using var state = new TempDirectory();
        var store = new RotationStore(state.Path, Compression.Default, options, new MemoryJournal());
        store.Offer(Bulletins.Make(1, 2000), new DateOnly(2026, 10, 25));
        var plan = new StoreSlotPlanner(store, Compression.Default, options, config.ToWaveforms(Mailcast.HeadEnd.Slot.LinearAirtime.Measure), config.ToSlotSettings(), config.FillLimitAfter).Plan(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        var directory = BroadcastDirectory.Parse(plan.Broadcast!.Directory.Serialize());
        Assert.Equal("M0LTE", directory.Station);
        Assert.Equal(options.Adverts, directory.Adverts);
    }

    [Theory]
    [InlineData(Hf + "," + Hf, "lists G8BPQ on 7053800 Hz twice")]
    [InlineData("""{ "callsign": "g8bpq", "frequencyHz": 7053800 }""", "is not a callsign")]
    [InlineData("""{ "callsign": "G8BPQ" }""", "\"frequencyHz\" is required")]
    [InlineData("""{ "callsign": "G8BPQ", "frequencyHz": 7053800, "sideband": "am" }""", "usb or lsb")]
    [InlineData("""{ "callsign": "G8BPQ", "frequencyHz": 7053800, "slot": { "timeUtc": "half past" } }""", "is not HH:mm")]
    public void Advertise_SaysWhatIsWrong(string advertise, string says)
    {
        var e = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(Config(advertise)));
        Assert.Contains(says, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAdverts_SaysSo() =>
        Assert.Equal("advertising: no other stations (\"advertise\" in the configuration)", Program.DescribeAdverts(HeadEndConfig.Parse(Config(""))));
}
