using Mailcast.Receiver.Feedback;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The <c>locator</c> setting: where a sound card, pdn-soundmodem or recording receiver is, so it
/// names its hops and its daily report says where it is, as a web SDR's already did.
/// </summary>
public class LocatorTests
{
    [Fact]
    public void Config_LocatorIsOptional_RefusedIfNotOne_AndKeptThroughASave()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");

        File.WriteAllText(path, $$"""{ "audio": "{{Samples.SoundCard}}" }""");
        Assert.Null(ReceiverConfig.Load(path).Locator);

        File.WriteAllText(path, $$"""{ "audio": "{{Samples.SoundCard}}", "locator": "Near Reading" }""");
        var e = Assert.Throws<ConfigException>(() => ReceiverConfig.Load(path));
        Assert.Contains("\"locator\"", e.Message, StringComparison.Ordinal);

        File.WriteAllText(path, $$"""{ "audio": "{{Samples.SoundCard}}", "locator": "JO00fu" }""");
        var config = ReceiverConfig.Load(path);
        Assert.Equal("JO00fu", config.Locator);
        config.Save(path);
        Assert.Equal("JO00fu", ReceiverConfig.Load(path).Locator);

        // Unset, a saved file leaves it out rather than writing null.
        (config with { Locator = null }).Save(path);
        Assert.DoesNotContain("\"locator\": null", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Null(ReceiverConfig.Load(path).Locator);
    }

    [Theory]
    [InlineData("plughw:CARD=Device,DEV=0")]
    [InlineData("soundmodem")]
    [InlineData("wav:/nowhere/recording.wav")]
    public async Task Place_ForAnythingButAWebSdr_IsTheConfiguredLocator_AndGoesInTheReport(string audio)
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = Samples.OnThisMachine(audio), StateDirectory = dir.Path, Locator = "JO00fu" };
        await using var host = new ReceiverHost(config, new FakeTimeProvider(), _ => { });

        Assert.Equal("JO00fu", host.Place?.Locator);
        Assert.Equal("JO00fu", ChannelSummary.Locator(host.Place));
    }

    [Fact]
    public async Task Place_WithoutALocator_OrForAWebSdr_IsNotTheSetting()
    {
        using var dir = new TempDirectory();
        var soundCard = new ReceiverConfig { Audio = Samples.SoundCard, StateDirectory = dir.Path };
        await using (var host = new ReceiverHost(soundCard, new FakeTimeProvider(), _ => { }))
        {
            Assert.Null(host.Place);
        }

        // A web SDR is somewhere else entirely: only where it says it is counts, and it has not said yet.
        var webSdr = soundCard with { Audio = "ubersdr:wessex.zapto.org", Locator = "JO00fu" };
        await using (var host = new ReceiverHost(webSdr, new FakeTimeProvider(), _ => { }))
        {
            Assert.Null(host.Place);
        }
    }

    [Fact]
    public void SettingsForm_SetsKeepsAndClearsTheLocator()
    {
        var current = new ReceiverConfig { Audio = Samples.SoundCard, Locator = "IO91lk" };
        StatusPage.SettingsForm Form(string? locator) =>
            new(Samples.SoundCard, "linBpq", "127.0.0.1", 8011, "Q0CAST", null, "BBS", Locator: locator);

        Assert.Equal("IO91lk", StatusPage.Apply(current, Form(null)).Locator);
        Assert.Equal("JO00fu", StatusPage.Apply(current, Form(" JO00fu ")).Locator);
        Assert.Null(StatusPage.Apply(current, Form("")).Locator);
        Assert.Throws<ConfigException>(() => StatusPage.Apply(current, Form("Reading")));
    }
}
