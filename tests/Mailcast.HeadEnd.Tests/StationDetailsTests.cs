using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Tests;

/// <summary>A head end saying where its own signal is and where it is (issue #179).</summary>
public class StationDetailsTests
{
    private static string Config(string extra) => $$"""
        {
          "callsign": "G8BPQ",
          "station": { "apiKey": "k" },
          "slot": { "minute": 30, "everyMinutes": 60 }{{extra}}
        }
        """;

    [Fact]
    public void EveryDirectory_CarriesTheStationsFrequencySidebandAndLocator()
    {
        var config = HeadEndConfig.Parse(Config(", \"frequencyHz\": 7047000, \"sideband\": \"usb\", \"locator\": \"IO83\""));
        var options = config.ToScheduleOptions();
        Assert.Equal(new StationDetails(7_047_000, Sideband.Usb, "IO83"), options.Details);
        Assert.Equal("this station: 7.0470 MHz USB, locator IO83 in every directory", Program.DescribeSelf(config));

        using var state = new TempDirectory();
        var store = new RotationStore(state.Path, Compression.Default, options, new MemoryJournal());
        store.Offer(Bulletins.Make(1, 2000), new DateOnly(2026, 10, 26));
        var plan = new StoreSlotPlanner(store, Compression.Default, options, config.ToWaveforms(Mailcast.HeadEnd.Slot.LinearAirtime.Measure), config.ToSlotSettings(), config.FillLimitAfter).Plan(new DateTimeOffset(2026, 10, 26, 10, 30, 0, TimeSpan.Zero));
        var directory = BroadcastDirectory.Parse(plan.Broadcast!.Directory.Serialize());
        Assert.Equal(new StationDetails(7_047_000, Sideband.Usb, "IO83"), directory.Details);
    }

    [Fact]
    public void Sideband_IsWrittenWheneverTheFrequencyIs()
    {
        var usb = HeadEndConfig.Parse(Config(", \"frequencyHz\": 7047000")).ToDetails();
        Assert.Equal(Sideband.Usb, usb!.Sideband);
        var lsb = HeadEndConfig.Parse(Config(", \"frequencyHz\": 7047000, \"sideband\": \"lsb\"")).ToDetails();
        Assert.Equal(Sideband.Lsb, lsb!.Sideband);
        Assert.Null(HeadEndConfig.Parse(Config(", \"locator\": \"IO83\"")).ToDetails()!.Sideband);
    }

    [Fact]
    public void WithNoneGiven_TheDirectoryCarriesNothingNew_AsBefore()
    {
        var config = HeadEndConfig.Parse(Config(""));
        Assert.Null(config.ToScheduleOptions().Details);
        Assert.Contains("no \"frequencyHz\" or \"locator\" given", Program.DescribeSelf(config), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""", "frequencyHz": 0""", "\"frequencyHz\" must be")]
    [InlineData(", \"frequencyHz\": 7047000, \"sideband\": \"am\"", "usb or lsb")]
    [InlineData(", \"sideband\": \"usb\"", "\"sideband\" needs \"frequencyHz\"")]
    [InlineData(", \"locator\": \"ZZ\"", "not a 4 or 6 character Maidenhead locator")]
    public void WhatIsWrong_IsSaid(string extra, string says)
    {
        var e = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(Config(extra)));
        Assert.Contains(says, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdvertCarriesItsLocator_AndAWrongOneIsRefused()
    {
        var config = HeadEndConfig.Parse("""
            { "callsign": "M0LTE", "station": { "apiKey": "k" },
              "advertise": [ { "callsign": "G8BPQ", "frequencyHz": 7047000, "locator": "IO83" } ] }
            """);
        Assert.Equal("IO83", Assert.Single(config.ToScheduleOptions().Adverts).Locator);
        var e = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""
            { "callsign": "M0LTE", "station": { "apiKey": "k" },
              "advertise": [ { "callsign": "G8BPQ", "frequencyHz": 7047000, "locator": "nowhere" } ] }
            """));
        Assert.Contains("not a 4 or 6 character Maidenhead locator", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2_400_100_000L, "10489.6 MHz")]
    [InlineData(2_400_000_000L, "10489.5 MHz")]
    [InlineData(2_430_000_000L, "10489.5 to 10490.0 MHz")]
    [InlineData(2_450_000_000L, "10489.5 to 10490.0 MHz")]
    public void AUplinkFrequency_IsRefused_NamingTheDownlink(long hz, string says)
    {
        var own = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(Config($", \"frequencyHz\": {hz}")));
        Assert.Contains("QO-100's uplink band", own.Message, StringComparison.Ordinal);
        Assert.Contains(says, own.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', own.Message.Trim());

        var advert = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(Config($$""", "advertise": [ { "callsign": "G8BPQ", "slot": { "minute": 30, "everyMinutes": 60 }, "frequencyHz": {{hz}}, "sideband": "usb" } ]""")));
        Assert.Contains("\"advertise\" entry 1", advert.Message, StringComparison.Ordinal);
        Assert.Contains(says, advert.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10_489_700_000L)]
    [InlineData(2_399_999_999L)]
    [InlineData(2_450_000_001L)]
    [InlineData(7_053_800L)]
    public void ADownlinkOrOtherFrequency_IsAccepted(long hz)
    {
        Assert.Equal(hz, HeadEndConfig.Parse(Config($", \"frequencyHz\": {hz}")).FrequencyHz);
    }
}
