using Mailcast.Receiver.Feedback;
using Mailcast.Receiver.Hooks;
using Mailcast.Receiver.Retune;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

/// <summary>Issue #154: hand edits to the config file are never overwritten, and nothing in it is ignored unseen.</summary>
public class ConfigFileTests
{
    private static string Write(TempDirectory dir, string json)
    {
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string Plain = """{ "audio": "ubersdr:wessex.zapto.org", "bbs": { "password": "secret-pw" } }""";

    [Fact]
    public void TrySave_WritesAnUntouchedFile_AndAgainAfterItsOwnSave()
    {
        using var dir = new TempDirectory();
        string path = Write(dir, Plain);
        var file = ConfigFile.Load(path);
        var log = new List<string>();

        Assert.Equal(ConfigFile.SaveOutcome.Saved, file.TrySave(file.Config with { DialKHz = 7052.3 }, log.Add));
        Assert.Equal(7052.3, ReceiverConfig.Load(path).DialKHz);
        // Its own save is not a hand edit: the next save goes ahead, and nothing is noticed.
        Assert.Equal(ConfigFile.SaveOutcome.Saved, file.TrySave(file.Config with { DialKHz = 7052.0 }, log.Add));
        Assert.False(file.Check(log.Add));
        Assert.False(file.ChangedByHand);
        Assert.Empty(log);
    }

    [Fact]
    public void TrySave_AfterAHandEdit_WritesNothing_AndSaysSoOnce()
    {
        using var dir = new TempDirectory();
        string path = Write(dir, Plain);
        var file = ConfigFile.Load(path);
        string edited = """{ "audio": "ubersdr:wessex.zapto.org", "bbs": { "password": "secret-pw" }, "rig": { "rigctld": "127.0.0.1:4532", "dedicatedRadio": true } }""";
        File.WriteAllText(path, edited);
        var log = new List<string>();

        Assert.Equal(ConfigFile.SaveOutcome.EditedByHand, file.TrySave(file.Config with { DialKHz = 7052.3 }, log.Add));

        Assert.Equal(edited, File.ReadAllText(path));
        Assert.Equal(["config: not saved from the page: the config file was changed by hand since the receiver started; restart the receiver to use it"], log);
        Assert.True(file.ChangedByHand);
        // The once-a-minute check does not say the same edit again.
        Assert.True(file.Check(log.Add));
        Assert.Single(log);
    }

    [Fact]
    public void TrySave_SeesAnEditThatKeepsTheFilesTime()
    {
        using var dir = new TempDirectory();
        string path = Write(dir, Plain);
        var when = File.GetLastWriteTimeUtc(path);
        var file = ConfigFile.Load(path);
        File.WriteAllText(path, Plain.Replace("secret-pw", "secret-px", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, when);

        Assert.Equal(ConfigFile.SaveOutcome.EditedByHand, file.TrySave(file.Config, _ => { }));
    }

    [Fact]
    public void Check_SaysEachHandEditOnce_AndForgetsOneUndone()
    {
        using var dir = new TempDirectory();
        string path = Write(dir, Plain);
        var file = ConfigFile.Load(path);
        var log = new List<string>();

        Assert.False(file.Check(log.Add));
        Assert.Empty(log);

        File.WriteAllText(path, Plain + " ");
        Assert.True(file.Check(log.Add));
        Assert.True(file.Check(log.Add));
        Assert.Equal([ConfigFile.ChangedLine], log);
        Assert.True(file.ChangedByHand);

        File.WriteAllText(path, Plain + "  ");
        Assert.True(file.Check(log.Add));
        Assert.Equal([ConfigFile.ChangedLine, ConfigFile.ChangedLine], log);

        // Put back as it was read: nothing to restart for any more.
        File.WriteAllText(path, Plain);
        Assert.False(file.Check(log.Add));
        Assert.False(file.ChangedByHand);
        Assert.Equal(2, log.Count);
        Assert.Equal("config: the config file was changed by hand; restart the receiver to use it", ConfigFile.ChangedLine);
    }

    [Fact]
    public void Check_PassesOverAFileItCannotRead()
    {
        using var dir = new TempDirectory();
        string path = Write(dir, Plain);
        var file = ConfigFile.Load(path);
        var log = new List<string>();
        File.Delete(path); // unreadable, as a file an editor on Windows holds open is

        Assert.False(file.Check(log.Add));
        Assert.False(file.ChangedByHand);
        Assert.Empty(log);

        // Readable again as it was: still nothing to say.
        File.WriteAllText(path, Plain);
        Assert.False(file.Check(log.Add));
        Assert.Empty(log);
    }

    [Fact]
    public void TrySave_OfAFileItCannotRead_WritesNothing_AndAsksToTryAgain()
    {
        using var dir = new TempDirectory();
        string path = Write(dir, Plain);
        var file = ConfigFile.Load(path);
        var log = new List<string>();
        File.Delete(path);

        Assert.Equal(ConfigFile.SaveOutcome.Unreadable, file.TrySave(file.Config with { DialKHz = 7052.3 }, log.Add));

        Assert.False(File.Exists(path));
        Assert.Equal(["config: not saved from the page: the config file could not be read to check it has not been edited by hand; try again in a moment"], log);
        Assert.False(file.ChangedByHand);
        Assert.Equal("Couldn't read the config file to check it hasn't been edited by hand; try again in a moment", ConfigFile.Unreadable);

        // Once it can be read again, unchanged, the save goes through.
        File.WriteAllText(path, Plain);
        Assert.Equal(ConfigFile.SaveOutcome.Saved, file.TrySave(file.Config with { DialKHz = 7052.3 }, log.Add));
        Assert.Single(log);
    }

    [Fact]
    public async Task RunAsync_NoticesAHandEditWithinAMinute()
    {
        using var dir = new TempDirectory();
        string path = Write(dir, Plain);
        var file = ConfigFile.Load(path);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
        var said = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var run = file.RunAsync(clock, line => said.TrySetResult(line), stop.Token);

        File.WriteAllText(path, Plain + "\n");
        clock.Advance(ConfigFile.CheckEvery);

        Assert.Equal(ConfigFile.ChangedLine, await said.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public void UnknownKeys_AreTheTopLevelKeysThatAreNotSettings()
    {
        string text = """
            {
              // a comment
              "audio": "ubersdr:wessex.zapto.org",
              "rgi": { "rigctld": "127.0.0.1:4532" },
              "Hooks": {},
              "bbs": { "rig": "nested keys are the setting's own business" },
              "locator": null,
              "retune": false,
              "slotUtc": "00:00",
              "webSdrSlotsPerDay": 8,
              "trustedKeys": [],
            }
            """;

        Assert.Equal(["rgi", "Hooks"], ReceiverConfig.UnknownKeys(text));
    }

    [Fact]
    public void UnknownKeys_TheExampleHasNone()
    {
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "receiver.example.json"));

        Assert.Empty(ReceiverConfig.UnknownKeys(text));
    }

    [Fact]
    public void UnknownKeys_EverySettingSavedIsKnown()
    {
        var config = new ReceiverConfig
        {
            Locator = "IO91",
            Retune = true,
            Sources = CallsignList.Parse(["GB7RDG"]),
            Rig = new RigSettings(),
            Bpq = new BpqNodeSettings(),
            Hooks = new HooksSettings(),
            Feedback = new FeedbackSettings(),
            TrustedKeys = [new TrustedKeySettings()],
        };

        Assert.Empty(ReceiverConfig.UnknownKeys(System.Text.Encoding.UTF8.GetString(config.ToFile())));
    }

    [Fact]
    public async Task StartUp_SaysWhichFile_EachUnknownKey_AndWhatIsInForce()
    {
        using var dir = new TempDirectory();
        string path = Write(dir, $$"""
            {
              "audio": "ubersdr:wessex.zapto.org",
              "stateDirectory": "{{dir.Path.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "daylight": null,
              "rgi": { "rigctld": "127.0.0.1:4532" },
              "webSdrSlotsPerDay": 8,
              "feedback": { "enabled": true, "callsign": "g8bpq" }
            }
            """);
        var file = ConfigFile.Load(path);
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 6, 30, 0, TimeSpan.Zero));
        await using var host = new ReceiverHost(file.Config, clock, log.Enqueue) { ConfigFile = file };
        using var stop = new CancellationTokenSource();

        var run = host.RunAsync(stop.Token);
        await stop.CancelAsync();
        await run;

        Assert.Contains($"config: read {path}", log);
        Assert.Single(log, l => l == "config: \"rgi\" is not a setting and is ignored");
        Assert.DoesNotContain(log, l => l.Contains("\"webSdrSlotsPerDay\" is not a setting", StringComparison.Ordinal));
        Assert.Single(log, l => l.StartsWith("config: webSdrSlotsPerDay is no longer used", StringComparison.Ordinal));
        Assert.Contains("config: rig retuning off (no \"rig\"), hooks off (no \"hooks\"), daily report on as G8BPQ", log);
        Assert.All(log, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), l));
    }

    [Fact]
    public void PartsLine_SaysWhatIsOffAsPlainlyAsWhatIsOn()
    {
        var none = new ReceiverConfig();
        Assert.Equal("config: rig retuning off (no \"rig\"), hooks off (no \"hooks\"), daily report off (no \"feedback\")", ConfigFile.PartsLine(none));

        var shared = none with
        {
            Rig = new RigSettings(),
            Bpq = new BpqNodeSettings(),
            Hooks = new HooksSettings { Before = new HookCommand { Command = "/bin/true" } },
            Feedback = new FeedbackSettings { Enabled = false, Callsign = "G4ABC" },
        };
        Assert.Equal("config: rig retuning on, with LinBPQ held off the air through \"bpq\", hooks on, daily report off", ConfigFile.PartsLine(shared));

        var dedicated = none with { Rig = new RigSettings { DedicatedRadio = true }, Hooks = new HooksSettings() };
        Assert.Equal("config: rig retuning on (a dedicated radio), hooks off (\"hooks\" has no \"before\" or \"after\"), daily report off (no \"feedback\")", ConfigFile.PartsLine(dedicated));

        var window = none with { Audio = "soundmodem", Retune = true };
        Assert.StartsWith("config: retuning through pdn-soundmodem's receive window on (\"retune\"), ", ConfigFile.PartsLine(window), StringComparison.Ordinal);
    }

    [Fact]
    public void Changes_SayWhatChanged_AndNeverAPassword()
    {
        var before = new ReceiverConfig
        {
            Bbs = new BbsSettings { Password = "old-bbs-pw" },
            Web = new WebSettings { Password = "old-page-pw" },
        };
        Assert.Null(ConfigFile.Changes(before, before));

        var after = before with
        {
            DialKHz = 7052.07,
            Bbs = before.Bbs with { Password = "new-bbs-pw", Port = 8012 },
            Web = before.Web with { Password = "new-page-pw" },
            Feedback = new FeedbackSettings { Enabled = true, Callsign = "G4ABC" },
        };
        string said = ConfigFile.Changes(before, after)!;

        Assert.Equal("dial 7052.00 -> 7052.07 kHz, BBS port 8011 -> 8012, BBS password changed, page password changed, daily report on as G4ABC", said);
        Assert.DoesNotContain("pw", said, StringComparison.Ordinal);
        Assert.Equal("daily report off", ConfigFile.Changes(after, after with { Feedback = after.Feedback! with { Enabled = false } }));
        Assert.Equal("locator none -> IO91lk", ConfigFile.Changes(before, before with { Locator = "IO91lk" }));
    }
}
