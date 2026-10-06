using System.Text.Json;
using Mailcast.Receiver.Web;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>The callsigns broadcast frames are accepted from: the config's "sources".</summary>
public class SourcesTests
{
    private static byte[] Payload() => Samples.Frames([Samples.Bulletin(1)])[0].AsSpan(16).ToArray();

    private static ReceiverConfig LoadJson(TempDirectory dir, string json)
    {
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, json);
        return ReceiverConfig.Load(path);
    }

    [Fact]
    public void Default_IsGb7rdgAndM0lte()
    {
        Assert.Equal(["GB7RDG", "M0LTE"], ReceiverConfig.DefaultSources);
        Assert.Null(new ReceiverConfig().Sources);
        Assert.Equal(ReceiverConfig.DefaultSources, new ReceiverConfig().AcceptedSources);
    }

    [Theory]
    [InlineData("GB7RDG")]
    [InlineData("GB7RDG-3")]
    [InlineData("M0LTE")]
    [InlineData("M0LTE-15")]
    public void Frames_FromGb7rdgOrM0lte_AnySsid_AreAccepted(string source)
    {
        byte[] frame = Ax25UiFrame.Build(source, OnAir.Destination, Payload());

        Assert.True(BroadcastFrame.TryGetPayload(frame, ReceiverConfig.DefaultSources, out var payload, out string? other));
        Assert.Equal(Payload(), payload.ToArray());
        Assert.Null(other);
    }

    [Theory]
    [InlineData("G4ABC", "G4ABC")]
    [InlineData("M0LTF-1", "M0LTF")]
    [InlineData("GB7RD", "GB7RD")]
    public void Frames_FromAnyoneElse_AreRefused_AndSayWho(string source, string expected)
    {
        byte[] frame = Ax25UiFrame.Build(source, OnAir.Destination, Payload());

        Assert.False(BroadcastFrame.TryGetPayload(frame, ReceiverConfig.DefaultSources, out _, out string? other));
        Assert.Equal(expected, other);
    }

    [Fact]
    public void Frames_ToAnotherDestination_AreNotCountedAsAnotherSource()
    {
        Assert.False(BroadcastFrame.TryGetPayload(Ax25UiFrame.Build("G4ABC", "ID", Payload()), ReceiverConfig.DefaultSources, out _, out string? other));
        Assert.Null(other);
    }

    [Fact]
    public async Task Intake_AcceptsItsSources_AndLogsEachOtherSourceOnce_NamingTheList()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        await using var intake = new Intake(dir.Path, line => { lock (log) { log.Add(line); } });
        byte[] payload = Payload();

        Assert.True(intake.Offer(Ax25UiFrame.Build("M0LTE-1", OnAir.Destination, payload)));
        Assert.False(intake.Offer(Ax25UiFrame.Build("G4ABC", OnAir.Destination, payload)));
        Assert.False(intake.Offer(Ax25UiFrame.Build("G4ABC-2", OnAir.Destination, payload)));

        string[] lines;
        lock (log)
        {
            lines = [.. log.Where(l => l.Contains("G4ABC", StringComparison.Ordinal))];
        }
        string line = Assert.Single(lines);
        Assert.Contains("not from an accepted source (GB7RDG, M0LTE)", line, StringComparison.Ordinal);

        // Changed while running, as the settings page does.
        intake.Sources = CallsignList.Parse(["G4ABC"]);
        Assert.True(intake.Offer(Ax25UiFrame.Build("G4ABC-2", OnAir.Destination, payload)));
        Assert.False(intake.Offer(Ax25UiFrame.Build("GB7RDG", OnAir.Destination, payload)));
        Assert.Equal(2, intake.FramesHeard);
    }

    [Fact]
    public void Config_WithoutTheKey_UsesTheDefault_AndSavingLeavesItOut()
    {
        using var dir = new TempDirectory();
        var config = LoadJson(dir, """{ "audio": "ubersdr:wessex.zapto.org" }""");

        Assert.Null(config.Sources);
        Assert.Equal(ReceiverConfig.DefaultSources, config.AcceptedSources);
        Assert.Contains("no \"sources\"", ReceiverHost.SourcesLine(config), StringComparison.Ordinal);
        Assert.Contains("GB7RDG, M0LTE", ReceiverHost.SourcesLine(config), StringComparison.Ordinal);

        string path = Path.Combine(dir.Path, "receiver.json");
        config.Save(path);
        Assert.DoesNotContain("sources", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(config, ReceiverConfig.Load(path));
    }

    [Fact]
    public void Config_Sources_AreUpperCased_WithoutSsids_Once_AndSurviveASave()
    {
        using var dir = new TempDirectory();
        var config = LoadJson(dir, """{ "sources": [" m0lte-2 ", "G4ABC", "M0LTE"] }""");

        Assert.Equal(["M0LTE", "G4ABC"], config.AcceptedSources);
        Assert.Contains("frames are accepted from M0LTE, G4ABC", ReceiverHost.SourcesLine(config), StringComparison.Ordinal);

        string path = Path.Combine(dir.Path, "receiver.json");
        config.Save(path);
        Assert.Contains("\"sources\": [", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(config, ReceiverConfig.Load(path));
    }

    [Theory]
    [InlineData("""[]""", "empty")]
    [InlineData("""null""", "null")]
    [InlineData("""[""]""", "not a callsign")]
    [InlineData("""["GB7RDGX"]""", "not a callsign")]
    [InlineData("""["M0 LTE"]""", "not a callsign")]
    [InlineData("""["M0LTE-16"]""", "not a callsign")]
    [InlineData("""["M0LTE-"]""", "not a callsign")]
    [InlineData("""["-1"]""", "not a callsign")]
    [InlineData("""["G4/ABC"]""", "not a callsign")]
    [InlineData("""[7]""", "must be a list of callsigns")]
    [InlineData("""[null]""", "must be a list of callsigns")]
    [InlineData("\"GB7RDG\"", "must be a list of callsigns")]
    public void Config_Sources_ThatCannotWork_AreRefusedWithAReason(string value, string reason)
    {
        using var dir = new TempDirectory();

        var e = Assert.Throws<ConfigException>(() => LoadJson(dir, $$"""{ "sources": {{value}} }"""));
        Assert.Contains("\"sources\"", e.Message, StringComparison.Ordinal);
        Assert.Contains(reason, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Example_SeedsGb7rdgAndM0lte()
    {
        var config = ReceiverConfig.Load(Path.Combine(AppContext.BaseDirectory, "receiver.example.json"));

        Assert.NotNull(config.Sources);
        Assert.Equal(ReceiverConfig.DefaultSources, config.Sources);
    }

    [Fact]
    public void SettingsPage_ShowsTheSources_AndCanChangeThem()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { StateDirectory = dir.Path, Audio = "wav:/nonexistent.wav" };
        var form = new StatusPage.SettingsForm("ubersdr:wessex.zapto.org", "linBpq", "127.0.0.1", 8011, "Q0CAST", null, "BBS");

        var view = JsonSerializer.SerializeToElement(StatusPage.SettingsView(config), ReceiverConfig.JsonLine);
        Assert.Equal(["GB7RDG", "M0LTE"], view.GetProperty("sources").EnumerateArray().Select(e => e.GetString()));
        Assert.False(view.GetProperty("sourcesSet").GetBoolean());

        // Left out of the form, a config without "sources" stays without it.
        Assert.Null(StatusPage.Apply(config, form).Sources);

        var changed = StatusPage.Apply(config, form with { Sources = ["m0lte-1"] });
        Assert.Equal(["M0LTE"], changed.Sources!);
        Assert.Equal(changed.Sources, StatusPage.Apply(changed, form).Sources);

        Assert.Contains("empty", Assert.Throws<ConfigException>(() => StatusPage.Apply(config, form with { Sources = [] })).Message, StringComparison.Ordinal);
        Assert.Contains("not a callsign", Assert.Throws<ConfigException>(() => StatusPage.Apply(config, form with { Sources = ["NOT A CALL"] })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_GivesTheIntakeTheConfigsSources_AndANewConfigs()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { StateDirectory = dir.Path, Audio = "wav:/nonexistent.wav", Sources = CallsignList.Parse(["G4ABC"]) };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });

        Assert.Equal(["G4ABC"], host.Intake.Sources);
        host.Reconfigure(config with { Sources = null });
        Assert.Equal(ReceiverConfig.DefaultSources, host.Intake.Sources);
    }
}
