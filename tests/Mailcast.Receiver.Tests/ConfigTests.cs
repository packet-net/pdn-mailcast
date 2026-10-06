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
        Assert.Null(config.WebSdrSlotsPerDay);
        Assert.False(config.SlotUtcWithoutEveryMinutes);
        Assert.Equal(new ArchiveSettings { Days = 30, MaxMegabytes = 50 }, config.Archive);
    }

    [Theory]
    [InlineData("""{ "archive": { "days": -1 } }""")]
    [InlineData("""{ "archive": { "maxMegabytes": -5 } }""")]
    [InlineData("""{ "archive": { "days": 99999 } }""")]
    [InlineData("""{ "archive": null }""")]
    public void Archive_ThatCannotWork_IsRefusedWithAReason(string json)
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, json);

        var e = Assert.Throws<ConfigException>(() => ReceiverConfig.Load(path));
        Assert.Contains("archive", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_LeftOut_Keeps30DaysAnd50Megabytes_AndZeroKeepsNone()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "audio": "ubersdr:wessex.zapto.org" }""");
        Assert.Equal(new ArchiveSettings { Days = 30, MaxMegabytes = 50 }, ReceiverConfig.Load(path).Archive);

        File.WriteAllText(path, """{ "archive": { "days": 0 } }""");
        Assert.Equal(new ArchiveSettings { Days = 0, MaxMegabytes = 50 }, ReceiverConfig.Load(path).Archive);
    }

    [Fact]
    public void Slots_DefaultToEveryHourOnTheHour()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "audio": "ubersdr:wessex.zapto.org" }""");

        var config = ReceiverConfig.Load(path);

        Assert.Equal(new SlotSchedule(new TimeOnly(0, 0), 60, Packet.Mailcast.DaylightRule.Gb7rdg), config.Schedule);
        Assert.Equal(ReceiverConfig.MostWebSdrSlotsPerDay, config.WebSdrSlots.Count);
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
    public void WebSdrSlotsPerDay_LeftOut_StaysLeftOutWhenSaved_AndGivenIsKept()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, "{}");

        var config = ReceiverConfig.Load(path);
        config.Save(path);

        Assert.Null(config.WebSdrSlotsPerDay);
        Assert.DoesNotContain("webSdrSlotsPerDay", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Null(ReceiverConfig.Load(path).WebSdrSlotsPerDay);

        File.WriteAllText(path, """{ "webSdrSlotsPerDay": 8 }""");
        ReceiverConfig.Load(path).Save(path);
        Assert.Equal(8, ReceiverConfig.Load(path).WebSdrSlotsPerDay);
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

public class RetuneConfigTests
{
    private static ReceiverConfig Load(string json)
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, json);
        return ReceiverConfig.Load(path);
    }

    private const string Bpq = """
        "bpq": { "host": "127.0.0.1", "port": 8010, "user": "sysop", "password": "pw", "hfPort": 2 }
        """;

    [Fact]
    public void Default_HasNoRigAndNoBpq()
    {
        var config = Load("""{ "audio": "plughw:CARD=Device,DEV=0" }""");

        Assert.Null(config.Rig);
        Assert.Null(config.Bpq);
    }

    [Fact]
    public void RigWithBpq_Loads()
    {
        var config = Load($$"""{ "audio": "plughw:CARD=Device,DEV=0", "rig": { "rigctld": "127.0.0.1:4532" }, {{Bpq}} }""");

        Assert.Equal(new Packet.SoundModem.Rig.RigctldEndpoint("127.0.0.1", 4532), config.Rig!.Endpoint);
        Assert.False(config.Rig.DedicatedRadio);
        Assert.Equal(2, config.Bpq!.HfPort);
        Assert.Equal(15, config.Bpq.DrainSeconds);
        Assert.Null(config.Bpq.ExpectedPortId);
        Assert.Equal("sysop", config.Bpq.User);
        Assert.DoesNotContain("pw", config.Bpq.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RigWithoutBpq_IsRefusedUnlessTheRadioIsDedicated()
    {
        var e = Assert.Throws<ConfigException>(() => Load("""{ "rig": { "rigctld": "127.0.0.1:4532" } }"""));
        Assert.Contains("\"bpq\"", e.Message, StringComparison.Ordinal);
        Assert.Contains("\"dedicatedRadio\": true", e.Message, StringComparison.Ordinal);

        var dedicated = Load("""{ "rig": { "rigctld": "127.0.0.1:4532", "dedicatedRadio": true } }""");
        Assert.True(dedicated.Rig!.DedicatedRadio);
        Assert.Null(dedicated.Bpq);
    }

    [Fact]
    public void BpqWithoutRig_IsRefused()
    {
        var e = Assert.Throws<ConfigException>(() => Load($$"""{ {{Bpq}} }"""));
        Assert.Contains("\"rig\" is not", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "rig": { "rigctld": "127.0.0.1:99999", "dedicatedRadio": true } }""", "\"rig\".\"rigctld\"")]
    [InlineData("""{ "rig": { "rigctld": "", "dedicatedRadio": true } }""", "\"rig\".\"rigctld\"")]
    [InlineData("""{ "rig": {}, "bpq": { "user": "sysop", "password": "pw" } }""", "\"bpq\".\"hfPort\"")]
    [InlineData("""{ "rig": {}, "bpq": { "user": "", "password": "pw", "hfPort": 2 } }""", "\"bpq\".\"user\"")]
    [InlineData("""{ "rig": {}, "bpq": { "user": "sysop", "password": "", "hfPort": 2 } }""", "\"bpq\".\"password\"")]
    [InlineData("""{ "rig": {}, "bpq": { "user": "sysop", "password": "pw", "hfPort": 2, "port": 0 } }""", "\"bpq\".\"port\"")]
    [InlineData("""{ "rig": {}, "bpq": { "user": "sysop", "password": "pw", "hfPort": 2, "drainSeconds": 41 } }""", "\"bpq\".\"drainSeconds\" 41 must be from 0 to 40")]
    [InlineData("""{ "rig": {}, "bpq": { "user": "sysop", "password": "pw", "hfPort": 2, "expectedPortId": " " } }""", "\"bpq\".\"expectedPortId\"")]
    public void BadSettings_AreRefusedSayingWhich(string json, string which)
    {
        var e = Assert.Throws<ConfigException>(() => Load(json));
        Assert.Contains(which, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Saved_KeepsRigAndBpq()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, $$"""{ "rig": { "rigctld": "127.0.0.1:4532" }, {{Bpq}} }""");
        var config = ReceiverConfig.Load(path);

        config.Save(path);

        var again = ReceiverConfig.Load(path);
        Assert.Equal(config.Rig, again.Rig);
        Assert.Equal(config.Bpq, again.Bpq);
    }
}
