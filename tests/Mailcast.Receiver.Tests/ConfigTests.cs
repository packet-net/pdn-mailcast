namespace Mailcast.Receiver.Tests;

public class ConfigTests
{
    [Fact]
    public void Example_LoadsWithItsSettings()
    {
        var config = ReceiverConfig.Load(Path.Combine(AppContext.BaseDirectory, "receiver.example.json"));

        Assert.Equal("ubersdr:wessex.zapto.org", config.Audio);
        Assert.Equal(BbsKind.LinBpq, config.Bbs.Type);
        Assert.Equal(8011, config.Bbs.Port);
        Assert.Equal(8130, config.Web.Port);
        Assert.Equal("Q0CAST", config.Bbs.Login);
        Assert.False(config.Web.Lan);
        Assert.Equal(7052.0, config.DialKHz);
        Assert.Equal("00:00", config.SlotUtc);
        Assert.Equal(60, config.EveryMinutes);
        Assert.Equal(8, config.WebSdrSlotsPerDay);
        Assert.False(config.SlotUtcWithoutEveryMinutes);
    }

    [Fact]
    public void Slots_DefaultToEveryHourOnTheHour()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "audio": "ubersdr:wessex.zapto.org" }""");

        var config = ReceiverConfig.Load(path);

        Assert.Equal(new SlotSchedule(new TimeOnly(0, 0), 60, Packet.Mailcast.DaylightRule.Gb7rdg), config.Schedule);
        Assert.Equal(8, config.WebSdrSlots.Count);
        Assert.False(config.SlotUtcWithoutEveryMinutes);
    }

    [Fact]
    public void OldConfig_WithOnlySlotUtc_IsHourlyFromThatTime()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """
            {
              // as the example was before hourly slots
              "audio": "ubersdr:wessex.zapto.org",
              "slotUtc": "12:00",
            }
            """);

        var config = ReceiverConfig.Load(path);

        Assert.True(config.SlotUtcWithoutEveryMinutes);
        Assert.Equal(new SlotSchedule(new TimeOnly(12, 0), 60, Packet.Mailcast.DaylightRule.Gb7rdg), config.Schedule);
        // The same slots as 00:00 every hour, and the web SDR listens to the same ones too.
        Assert.Equal(new SlotSchedule(new TimeOnly(0, 0), 60).FromAnchor.Order(), config.Schedule.FromAnchor.Order());
        Assert.Equal(new ReceiverConfig().WebSdrSlots, config.WebSdrSlots);

        // Saved, it says so, and no note is needed any more.
        config.Save(path);
        Assert.Contains("\"everyMinutes\": 60", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(config with { SlotUtcWithoutEveryMinutes = false }, ReceiverConfig.Load(path));
    }

    [Fact]
    public void SlotUtcWithEveryMinutes_IsTakenAsGiven()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "slotUtc": "00:30", "everyMinutes": 120, "webSdrSlotsPerDay": 12 }""");

        var config = ReceiverConfig.Load(path);

        Assert.False(config.SlotUtcWithoutEveryMinutes);
        Assert.Equal(12, config.Schedule.SlotsPerDay);
        Assert.Equal(12, config.WebSdrSlots.Count);
        config.Save(path);
        Assert.Contains("\"everyMinutes\": 120", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("slotStart", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(config, ReceiverConfig.Load(path));
    }

    [Fact]
    public void Dial_DefaultsTo7052kHz_WithTheSignal1800HzAbove()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "audio": "ubersdr:wessex.zapto.org" }""");

        var config = ReceiverConfig.Load(path);

        Assert.Equal(7052.0, config.DialKHz);
        Assert.Equal(7_052_000, config.DialHz);
        Assert.Equal(7_053_800, config.CentreHz);
    }

    [Fact]
    public void Dial_IsReadFromTheFileAndSavedBack()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "dialKHz": 7049.7 }""");

        var config = ReceiverConfig.Load(path);
        Assert.Equal(7049.7, config.DialKHz);
        Assert.Equal(7_051_500, config.CentreHz, 3);

        config.Save(path);
        Assert.Contains("\"dialKHz\": 7049.7", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("dialHz", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(7049.7, ReceiverConfig.Load(path).DialKHz);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        var config = new ReceiverConfig
        {
            Audio = "plughw:CARD=Device,DEV=0",
            Bbs = new BbsSettings { Type = BbsKind.Fbb, Host = "10.0.0.2", Port = 6300, Login = "Q0CAST", Password = "pw" },
            Web = new WebSettings { Port = 9000, Lan = true, Password = "page" },
            StateDirectory = dir.Path,
        };

        config.Save(path);

        Assert.Equal(config, ReceiverConfig.Load(path));
        Assert.Contains("\"fbb\"", File.ReadAllText(path), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(path));
        }
    }

    [Theory]
    [InlineData("""{ "audio": "" }""", "audio")]
    [InlineData("""{ "audio": "wav:" }""", "wav:")]
    [InlineData("""{ "bbs": { "port": 70000 } }""", "port")]
    [InlineData("""{ "bbs": { "login": "two words" } }""", "login")]
    [InlineData("""{ "audio": """, "JSON")]
    [InlineData("""{ "audio": null }""", "audio")]
    [InlineData("""{ "bbs": null }""", "bbs")]
    [InlineData("""{ "bbs": { "host": null } }""", "bbs")]
    [InlineData("""{ "web": { "lan": true } }""", "password")]
    [InlineData("""{ "slotUtc": "noon" }""", "slotUtc")]
    [InlineData("""{ "slotUtc": "noon", "everyMinutes": 60 }""", "slotUtc")]
    [InlineData("""{ "everyMinutes": 0 }""", "everyMinutes")]
    [InlineData("""{ "everyMinutes": -60 }""", "everyMinutes")]
    [InlineData("""{ "everyMinutes": 10 }""", "everyMinutes")]
    [InlineData("""{ "everyMinutes": 50 }""", "everyMinutes")]
    [InlineData("""{ "everyMinutes": 2880 }""", "everyMinutes")]
    [InlineData("""{ "webSdrSlotsPerDay": 0 }""", "webSdrSlotsPerDay")]
    [InlineData("""{ "webSdrSlotsPerDay": 13 }""", "3 hours")]
    [InlineData("""{ "webSdrSlotsPerDay": 24 }""", "webSdrSlotsPerDay")]
    [InlineData("""{ "dialKHz": 7.052 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": 0 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": -7052 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": 7052000 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": 1799.9 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": 30000.1 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": "7052" }""", "JSON")]
    public void BadSetting_SaysWhich(string json, string mentioned)
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, json);

        var e = Assert.Throws<ConfigException>(() => ReceiverConfig.Load(path));

        Assert.Contains(mentioned, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(180)]
    [InlineData(1440)]
    public void EveryMinutes_ThatDividesADay_IsAccepted(int minutes)
    {
        new ReceiverConfig { EveryMinutes = minutes, Daylight = null }.Validate();
        new ReceiverConfig { EveryMinutes = minutes, SlotUtc = "12:00" }.Validate();
    }

    [Fact]
    public void Daylight_DefaultsToGb7rdgs_CanBeTurnedOff_AndIsSavedBack()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "daylight": { "locator": "IO92", "afterSunriseMinutes": 60, "beforeSunsetMinutes": 0 } }""");
        var config = ReceiverConfig.Load(path);
        Assert.Equal(new Packet.Mailcast.DaylightRule("IO92", 60, 0), config.Schedule.Daylight);
        config.Save(path);
        Assert.Equal(config, ReceiverConfig.Load(path));

        File.WriteAllText(path, """{ "daylight": null }""");
        Assert.Null(ReceiverConfig.Load(path).Schedule.Daylight);
        Assert.Equal(Packet.Mailcast.DaylightRule.Gb7rdg, new ReceiverConfig().Schedule.Daylight);
        Assert.Equal(Packet.Mailcast.DaylightRule.Gb7rdg, ReceiverConfig.Load(Path.Combine(AppContext.BaseDirectory, "receiver.example.json")).Schedule.Daylight);
    }

    [Theory]
    [InlineData("""{ "daylight": { "locator": "nowhere" } }""", "locator")]
    [InlineData("""{ "daylight": { "afterSunriseMinutes": 900 } }""", "afterSunriseMinutes")]
    [InlineData("""{ "everyMinutes": 1440 }""", "no slot in a whole year")]
    public void Daylight_ThatCannotWork_IsRefused(string json, string mentioned)
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, json);
        var e = Assert.Throws<ConfigException>(() => ReceiverConfig.Load(path));
        Assert.Contains(mentioned, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ubersdr:wessex.zapto.org", AudioSourceKind.UberSdr)]
    [InlineData("plughw:CARD=Device,DEV=0", AudioSourceKind.Alsa)]
    [InlineData("wav:/tmp/slot.wav", AudioSourceKind.Wav)]
    public void AudioSetting_IsReadAsItsKind(string setting, AudioSourceKind kind)
    {
        Assert.Equal(kind, AudioSource.Parse(setting).Kind);
    }
}
