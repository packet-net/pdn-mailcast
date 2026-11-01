using Packet.Mailcast;

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
        Assert.True(config.Web.Lan);
        Assert.Null(config.DialKHz);
        Assert.Equal(7052.0, config.ResolvedDialKHz);
        Assert.Null(config.Sources);
        Assert.False(config.WebSdrSlotsPerDayIgnored);
        Assert.Empty(config.SlotSettingsIgnored);
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
    public void Slots_ComeFromTheStationsFile_NotTheConfig()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "audio": "ubersdr:wessex.zapto.org" }""");

        var config = ReceiverConfig.Load(path);

        Assert.Equal(new SlotSchedule(new TimeOnly(0, 0), 60, StationFixture.Daylight), Fixture.Slots(config));
        Assert.Equal(ReceiverConfig.MostWebSdrSlotsPerDay, ListeningWindow.WebSdrSlots(Fixture.Slots(config)).Count);
        Assert.Empty(config.SlotSettingsIgnored);
    }

    [Fact]
    public void OldSlotSettings_AreIgnoredNotRefused_NamedInTheirOrder_AndLeftOutWhenSaved()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """
            {
              // as the example was when the slots were one station's
              "audio": "ubersdr:wessex.zapto.org",
              "slotUtc": "12:00",
              "minute": 30,
              "everyMinutes": 120,
              "daylight": { "locator": "IO92", "afterSunriseMinutes": 60, "beforeSunsetMinutes": 0 },
            }
            """);

        var config = ReceiverConfig.Load(path);

        Assert.Equal(["minute", "slotUtc", "everyMinutes", "daylight"], config.SlotSettingsIgnored);
        // The slots are the stations file's, whatever the old settings said.
        Assert.Equal(new SlotSchedule(new TimeOnly(0, 0), 60, StationFixture.Daylight), Fixture.Slots(config));
        Assert.Empty(ReceiverConfig.UnknownKeys(File.ReadAllText(path)));

        config.Save(path);
        string saved = File.ReadAllText(path);
        foreach (string key in new[] { "slotUtc", "minute", "everyMinutes", "daylight" })
        {
            Assert.DoesNotContain(key, saved, StringComparison.Ordinal);
        }
        Assert.Empty(ReceiverConfig.Load(path).SlotSettingsIgnored);
    }

    [Theory]
    [InlineData("8")]
    [InlineData("0")]
    [InlineData("24")]
    [InlineData("null")]
    [InlineData("\"lots\"")]
    public void WebSdrSlotsPerDay_FromAnOldFile_IsIgnoredNotRefused_AndLeftOutWhenSaved(string value)
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, $$"""{ "audio": "ubersdr:wessex.zapto.org", "webSdrSlotsPerDay": {{value}} }""");

        var config = ReceiverConfig.Load(path);

        Assert.True(config.WebSdrSlotsPerDayIgnored);
        Assert.Equal(ReceiverConfig.MostWebSdrSlotsPerDay, ListeningWindow.WebSdrSlots(Fixture.Slots(config)).Count);
        config.Save(path);
        Assert.DoesNotContain("webSdrSlotsPerDay", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(ReceiverConfig.Load(path).WebSdrSlotsPerDayIgnored);
    }

    [Fact]
    public void Dial_LeftOut_IsTheFirstStationsCentreLess1800Hz_AndNotSavedBack()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "audio": "ubersdr:wessex.zapto.org" }""");

        var config = ReceiverConfig.Load(path);

        Assert.Null(config.DialKHz);
        Assert.Equal(7052.0, config.ResolvedDialKHz);
        Assert.Equal(7_052_000, config.DialHz);
        Assert.Equal(7_053_800, config.CentreHz);
        Assert.Equal(1800, config.AudioCentreHz);
        config.Save(path);
        Assert.DoesNotContain("dialKHz", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Dial_LeftOut_WithNoStation_IsRefusedForASoundCardAndSdrConnect_ButNotForAWebSdrOrSoundModem()
    {
        var none = new ReceiverConfig { Stations = StationsFile.Empty };

        Assert.Null(none.ResolvedDialKHz);
        foreach (string audio in new[] { Samples.SoundCard, "sdrconnect:192.168.1.20" })
        {
            var e = Assert.Throws<ConfigException>(() => (none with { Audio = audio }).Validate());
            Assert.Equal("\"dialKHz\" is not set and the stations file lists no station to take it from: set \"dialKHz\" in the config file, the USB dial of your radio in kHz", e.Message);
        }
        (none with { Audio = "ubersdr:wessex.zapto.org" }).Validate();
        (none with { Audio = "soundmodem" }).Validate();
        (none with { Audio = Samples.SoundCard, DialKHz = 7052 }).Validate();
    }

    [Fact]
    public void Dial_Explicit7052_StillWorks_WithTheSameAudioCentre()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "dialKHz": 7052 }""");

        var config = ReceiverConfig.Load(path);

        Assert.Equal(7052.0, config.DialKHz);
        Assert.Equal(7_053_800, config.CentreHz);
        Assert.Equal(1800, config.AudioCentreHz);
    }

    [Fact]
    public void Dial_7052_3_IsTheNarrowFilterAlternative_WithTheSameFixedCentre()
    {
        // issue: a sound card behind a 2.4 kHz or narrower rig filter does better on 7052.3 (the
        // filter study in mailcast-test/filter-study-2026-10-08: G4WNC kept 30 of 36 frames
        // through a 2.1 kHz DSP filter on 7052.3, against 16 of 36 on 7052). The transmitter
        // itself never moves, so the true centre (CentreHz) is the same at either dial; only
        // where it falls in the receiver's own audio (AudioCentreHz) changes.
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "dialKHz": 7052.3 }""");

        var config = ReceiverConfig.Load(path);

        Assert.Equal(7052.3, config.DialKHz);
        Assert.Equal(7_053_800, config.CentreHz);
        Assert.Equal(1500, config.AudioCentreHz, 3);
    }

    [Fact]
    public void Dial_IsReadFromTheFileAndSavedBack()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        // An arbitrary dial inside the accepted range (not one of the two commonly recommended),
        // as a "measure or type" dial would be.
        File.WriteAllText(path, """{ "dialKHz": 7052.6 }""");

        var config = ReceiverConfig.Load(path);
        Assert.Equal(7052.6, config.DialKHz);
        // The signal's true centre never depends on the dial: the transmitter does not move.
        Assert.Equal(7_053_800, config.CentreHz);
        Assert.Equal(1200, config.AudioCentreHz, 3);

        config.Save(path);
        Assert.Contains("\"dialKHz\": 7052.6", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("dialHz", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(7052.6, ReceiverConfig.Load(path).DialKHz);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        var config = new ReceiverConfig
        {
            Audio = Samples.SoundCard,
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
    [InlineData("""{ "dialKHz": 7.052 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": 0 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": -7052 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": 7052000 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": 7051.79 }""", "dialKHz")]
    [InlineData("""{ "dialKHz": 7052.81 }""", "dialKHz")]
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
    [InlineData("ubersdr:wessex.zapto.org", AudioSourceKind.UberSdr)]
    [InlineData("plughw:CARD=Device,DEV=0", AudioSourceKind.Alsa)]
    [InlineData("wav:/tmp/slot.wav", AudioSourceKind.Wav)]
    public void AudioSetting_IsReadAsItsKind(string setting, AudioSourceKind kind)
    {
        Assert.Equal(kind, AudioSource.Parse(setting, windows: false).Kind);
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
        var config = Load($$"""{ "audio": "{{Samples.SoundCard}}" }""");

        Assert.Null(config.Rig);
        Assert.Null(config.Bpq);
    }

    [Fact]
    public void RigWithBpq_Loads()
    {
        var config = Load($$"""{ "audio": "{{Samples.SoundCard}}", "rig": { "rigctld": "127.0.0.1:4532" }, {{Bpq}} }""");

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
        Assert.Contains("\"hooks\".\"before\"", e.Message, StringComparison.Ordinal);

        var dedicated = Load("""{ "rig": { "rigctld": "127.0.0.1:4532", "dedicatedRadio": true } }""");
        Assert.True(dedicated.Rig!.DedicatedRadio);
        Assert.Null(dedicated.Bpq);
    }

    [Fact]
    public void RigWithHooksBefore_NeedsNoBpq_ButAnAfterAloneIsNotEnough()
    {
        var hooked = Load("""{ "rig": { "rigctld": "127.0.0.1:4532" }, "hooks": { "before": { "command": "/bin/true" } } }""");
        Assert.False(hooked.Rig!.DedicatedRadio);
        Assert.Null(hooked.Bpq);
        Assert.Equal("config: rig retuning on, with the radio kept quiet by \"hooks\".\"before\", hooks on, daily report off (no \"feedback\")", ConfigFile.PartsLine(hooked));

        var e = Assert.Throws<ConfigException>(() => Load("""{ "rig": { "rigctld": "127.0.0.1:4532" }, "hooks": { "after": { "command": "/bin/true" } } }"""));
        Assert.Contains("\"hooks\".\"before\"", e.Message, StringComparison.Ordinal);
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
