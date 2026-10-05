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
        Assert.Equal(kind, AudioSource.Parse(setting).Kind);
    }
}
