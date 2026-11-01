namespace Packet.Mailcast.Tests;

/// <summary>The stations file: what a receiver trusts and follows before it has heard anything.</summary>
public class StationsFileTests
{
    private static string Station(string extra = "", string callsign = "G4ABC") => $$"""
        {
          "callsign": "{{callsign}}",
          "publicKey": "{{StationFixture.KeyBase64}}",
          "slots": { "minute": 30, "everyMinutes": 120 },
          "frequencyHz": 7047000,
          "sideband": "lsb",
          "locator": "IO83"{{extra}}
        }
        """;

    private static string File(string station, string report = "\"MCAST@HOST.#1.GBR.EURO\"") => $$"""{ "report": {{report}}, "stations": [ {{station}} ] }""";

    [Fact]
    public void AStation_IsReadWithEverythingItGives()
    {
        var file = StationsFile.Parse(StationFixture.Json(formerly: "OLDCALL"), "stations.json");

        Assert.Equal(StationFixture.Report, file.Report);
        var station = Assert.Single(file.Stations);
        Assert.Equal(StationFixture.Callsign, station.Callsign);
        Assert.Equal(StationFixture.Key.KeyId, station.Key.KeyId);
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60, StationFixture.Daylight), station.Slots);
        Assert.Equal(7_053_800, station.FrequencyHz);
        Assert.Equal(Sideband.Usb, station.Sideband);
        Assert.Equal("IO91lk", station.Locator);
        Assert.Equal(["OLDCALL"], station.Formerly);
        Assert.Equal(new StationDetails(7_053_800, Sideband.Usb, "IO91lk"), station.Details);
        Assert.Equal(new StationAdvert(StationFixture.Callsign, station.Slots, 7_053_800, Sideband.Usb, "IO91lk"), station.ToAdvert());
    }

    [Fact]
    public void DaylightAndFormerly_MayBeLeftOut_AndTheSidebandIsLower_WhenSaid()
    {
        var file = StationsFile.Parse(File(Station()), "stations.json");

        var station = Assert.Single(file.Stations);
        Assert.Null(station.Slots.Daylight);
        Assert.Equal(new TimeOnly(0, 30), station.Slots.Anchor);
        Assert.Equal(120, station.Slots.EveryMinutes);
        Assert.Equal(Sideband.Lsb, station.Sideband);
        Assert.Empty(station.Formerly);
        Assert.Equal(("MCAST", "HOST.#1.GBR.EURO"), StationsFile.SplitAddress(file.Report));
    }

    [Fact]
    public void AnEmptyList_IsAllowed_AndTheReportIsOptional()
    {
        var file = StationsFile.Parse("""{ "stations": [] }""", "stations.json");

        Assert.Empty(file.Stations);
        Assert.Null(file.Report);
        Assert.Null(file.First);
        Assert.Null(file.Legacy);
    }

    [Fact]
    public void Current_ReadsAFormerNameAsTheStationThatListsIt_AndLegacyIsTheFirstThatListsOne()
    {
        var file = StationsFile.Parse($$"""
            { "stations": [ {{Station(callsign: "G4ABC")}}, {{Station(""","formerly":["GB7XYZ","OLD"]""", "G4DEF")}} ] }
            """, "stations.json");

        Assert.Equal("G4DEF", file.Current("GB7XYZ"));
        Assert.Equal("G4DEF", file.Current("old"));
        Assert.Equal("G4ABC", file.Current("G4ABC"));
        Assert.Equal("G9ZZZ", file.Current("G9ZZZ"));
        Assert.Equal("G4DEF", file.Legacy);
        Assert.Same(file.Stations[0], file.First);
        Assert.Same(file.Stations[1], file.Find("G4DEF"));
        Assert.Null(file.Find("G9ZZZ"));
    }

    [Theory]
    [InlineData("MCAST@GB7RDG.#42.GBR.EURO", true)]
    [InlineData("BULL@HOST", true)]
    [InlineData("MCAST", false)]
    [InlineData("@HOST", false)]
    [InlineData("MCAST@", false)]
    [InlineData("MCAST@A@B", false)]
    [InlineData("MCAST@HOST NAME", false)]
    [InlineData("TOOLONGNAME@HOST", false)]
    [InlineData(null, false)]
    public void ReportAddresses_NeedABulletinAnAtAndAHost(string? address, bool ok) =>
        Assert.Equal(ok, StationsFile.SplitAddress(address) is not null);

    [Theory]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("{ ", "not valid JSON")]
    [InlineData("{ }", "no \"stations\" list")]
    [InlineData("""{ "stations": {} }""", "\"stations\" must be a list")]
    [InlineData("""{ "stations": [], "report": "nonsense" }""", "\"report\" must be an address")]
    [InlineData("""{ "stations": [ 5 ] }""", "station 1 must be an object")]
    [InlineData("""{ "stations": [ {} ] }""", "station 1 has no \"callsign\"")]
    [InlineData("""{ "stations": [ { "callsign": "g4abc" } ] }""", "is not a callsign")]
    [InlineData("""{ "stations": [ { "callsign": "G4ABC" } ] }""", "G4ABC) has no \"publicKey\"")]
    [InlineData("""{ "stations": [ { "callsign": "G4ABC", "publicKey": "AAAA" } ] }""", "\"publicKey\": ")]
    public void AFileThatCannotWork_IsRefused_InOneLineSayingWhatAndWhere(string json, string mentioned)
    {
        var e = Assert.Throws<FormatException>(() => StationsFile.Parse(json, "stations.json"));

        Assert.Contains(mentioned, e.Message, StringComparison.Ordinal);
        Assert.Contains("stations.json", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', e.Message);
    }

    [Theory]
    [InlineData("\"slots\": { \"minute\": 0, \"everyMinutes\": 60 }, \"frequencyHz\": 0, \"locator\": \"IO83\"", "needs \"frequencyHz\"")]
    [InlineData("\"slots\": { \"minute\": 0, \"everyMinutes\": 60 }, \"frequencyHz\": 7047000", "needs a \"locator\"")]
    [InlineData("\"slots\": { \"minute\": 0, \"everyMinutes\": 60 }, \"frequencyHz\": 7047000, \"locator\": \"zz\"", "not a 4 or 6 character Maidenhead locator")]
    [InlineData("\"slots\": { \"minute\": 0, \"everyMinutes\": 60 }, \"frequencyHz\": 7047000, \"locator\": \"IO83\", \"sideband\": \"am\"", "usb or lsb")]
    [InlineData("\"frequencyHz\": 7047000, \"locator\": \"IO83\"", "no \"slots\"")]
    [InlineData("\"slots\": { \"everyMinutes\": 60 }, \"frequencyHz\": 7047000, \"locator\": \"IO83\"", "need a \"minute\"")]
    [InlineData("\"slots\": { \"minute\": 60, \"everyMinutes\": 60 }, \"frequencyHz\": 7047000, \"locator\": \"IO83\"", "\"minute\" is 0 to 59")]
    [InlineData("\"slots\": { \"minute\": 0 }, \"frequencyHz\": 7047000, \"locator\": \"IO83\"", "need \"everyMinutes\"")]
    [InlineData("\"slots\": { \"minute\": 0, \"everyMinutes\": 7 }, \"frequencyHz\": 7047000, \"locator\": \"IO83\"", "must divide a day")]
    [InlineData("\"slots\": { \"minute\": 0, \"everyMinutes\": 60, \"daylight\": { \"locator\": \"zz\", \"afterSunriseMinutes\": 1, \"beforeSunsetMinutes\": 1 } }, \"frequencyHz\": 7047000, \"locator\": \"IO83\"", "\"daylight\": ")]
    [InlineData("\"slots\": { \"minute\": 0, \"everyMinutes\": 60, \"daylight\": { \"locator\": \"IO83\" } }, \"frequencyHz\": 7047000, \"locator\": \"IO83\"", "needs \"afterSunriseMinutes\"")]
    [InlineData("\"slots\": { \"minute\": 0, \"everyMinutes\": 60 }, \"frequencyHz\": 7047000, \"locator\": \"IO83\", \"formerly\": [1]", "\"formerly\" must be a list of names")]
    public void AStationThatCannotWork_IsRefused_NamingIt(string rest, string mentioned)
    {
        string json = $$"""{ "stations": [ { "callsign": "G4ABC", "publicKey": "{{StationFixture.KeyBase64}}", {{rest}} } ] }""";

        var e = Assert.Throws<FormatException>(() => StationsFile.Parse(json, "stations.json"));

        Assert.Contains(mentioned, e.Message, StringComparison.Ordinal);
        Assert.Contains("G4ABC", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', e.Message);
    }

    [Fact]
    public void ACallsignTwice_IsRefused()
    {
        var e = Assert.Throws<FormatException>(() => StationsFile.Parse($$"""{ "stations": [ {{Station()}}, {{Station()}} ] }""", "stations.json"));

        Assert.Contains("lists G4ABC twice", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOfAnOperator_BesideTheConfig_IsUsedInsteadOfTheShippedOne_AndMayBeEmpty()
    {
        using var dir = new TempDirectory();
        string shipped = Path.Combine(dir.Path, "shipped.json");
        System.IO.File.WriteAllText(shipped, StationFixture.Json());
        string config = Path.Combine(dir.Path, "receiver.json");

        // Nothing beside the config: the shipped one.
        Assert.Equal([StationFixture.Callsign], StationsFile.Locate(config, shipped).Stations.Select(s => s.Callsign));

        // The operator's wins, and may list nothing.
        System.IO.File.WriteAllText(Path.Combine(dir.Path, StationsFile.FileName), """{ "stations": [] }""");
        var own = StationsFile.Locate(config, shipped);
        Assert.Empty(own.Stations);
        Assert.Equal(Path.Combine(dir.Path, StationsFile.FileName), own.Path);

        // A bad one is a problem to be told about, not a quiet fall back to the shipped one.
        System.IO.File.WriteAllText(Path.Combine(dir.Path, StationsFile.FileName), "{ nope");
        Assert.Throws<FormatException>(() => StationsFile.Locate(config, shipped));
    }

    [Fact]
    public void WithNeitherFile_ThereAreNoStations()
    {
        using var dir = new TempDirectory();

        var file = StationsFile.Locate(Path.Combine(dir.Path, "receiver.json"), Path.Combine(dir.Path, "missing.json"));

        Assert.Empty(file.Stations);
        Assert.Null(file.Report);
    }
}
