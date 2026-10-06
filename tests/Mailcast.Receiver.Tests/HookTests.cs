using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Mailcast.Receiver.Hooks;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

/// <summary>Tiny real hook scripts that note each run, with what they were told, in a file.</summary>
[UnsupportedOSPlatform("windows")]
internal static class HookScript
{
    /// <summary>
    /// A script that appends "HOOK SLOT DIAL CENTRE BEFORE_OK" (BEFORE_OK "none" when not given)
    /// to <paramref name="record"/>, runs <paramref name="then"/>, then exits with <paramref name="exit"/>.
    /// </summary>
    public static HookCommand Write(string dir, string name, string record, int exit = 0, int timeoutSeconds = 60, string then = "")
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, $"#!/bin/sh\necho \"$MAILCAST_HOOK $MAILCAST_SLOT_UTC $MAILCAST_DIAL_KHZ $MAILCAST_CENTRE_KHZ ${{MAILCAST_BEFORE_OK-none}}\" >> '{record}'\n{then}\nexit {exit}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new HookCommand { Command = path, TimeoutSeconds = timeoutSeconds };
    }

    /// <summary>The runs noted so far.</summary>
    public static string[] Runs(string record) => File.Exists(record) ? File.ReadAllLines(record) : [];
}

/// <summary>
/// The config's <c>hooks</c>: their validation, the windows they run around, and the runs around
/// a web SDR's and a sound card's windows (the retuner's are in <see cref="RetunerTests"/>). The
/// loop's waits are on a fake clock moved by exactly what it asked for; nothing is timed.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class HookTests
{
    private static DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 10, 5, hour, minute, second, TimeSpan.Zero);

    // ---- validation ----

    private static ConfigException Refused(string json)
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, json);
        return Assert.Throws<ConfigException>(() => ReceiverConfig.Load(path));
    }

    [Fact]
    public void Config_WithHooks_Loads_WithTheDefaults()
    {
        using var dir = new TempDirectory();
        var script = HookScript.Write(dir.Path, "stop-ardop", Path.Combine(dir.Path, "runs"));
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, $$"""
            {
              "hooks": {
                "before": { "command": "{{script.Command}}", "args": ["stop", "ardopcf"], "timeoutSeconds": 45 },
                "after": { "command": "{{script.Command}}" }
              }
            }
            """);

        var config = ReceiverConfig.Load(path);

        Assert.Equal(script.Command, config.Hooks!.Before!.Command);
        Assert.Equal(["stop", "ardopcf"], config.Hooks.Before.Args);
        Assert.Equal(TimeSpan.FromSeconds(45), config.Hooks.Before.TimeLimit);
        Assert.Empty(config.Hooks.After!.Args);
        Assert.Equal(HookCommand.DefaultTimeoutSeconds, config.Hooks.After.TimeoutSeconds);

        // Saved from the page, the hooks stay.
        config.Save(path);
        Assert.Equal(config.Hooks.Before.Args, ReceiverConfig.Load(path).Hooks!.Before!.Args);
    }

    [Fact]
    public void Config_WithoutHooks_RunsNone()
    {
        var config = ReceiverConfig.Load(Path.Combine(AppContext.BaseDirectory, "receiver.example.json"));
        Assert.Null(config.Hooks);
    }

    [Theory]
    [InlineData("""{ "command": "" }""", "\"hooks\".\"before\": \"command\" is empty")]
    [InlineData("""{ "command": "stop-ardop" }""", "\"command\" \"stop-ardop\" is not a full path")]
    [InlineData("""{ "command": "/nonexistent/stop-ardop" }""", "/nonexistent/stop-ardop does not exist")]
    [InlineData("""{ "command": "/" }""", "is a folder, not a program")]
    [InlineData("""{ "command": "SCRIPT", "args": "stop" }""", "\"hooks\" setting that cannot be read (at $.hooks.before.args)")]
    [InlineData("""{ "command": "SCRIPT", "args": ["stop", 3] }""", "\"hooks\" setting that cannot be read (at $.hooks.before.args[1])")]
    [InlineData("""{ "command": "SCRIPT", "args": ["stop", null] }""", "\"args\" has a null in it")]
    [InlineData("""{ "command": "SCRIPT", "args": null }""", "\"args\" is null")]
    [InlineData("""{ "command": "SCRIPT", "timeoutSeconds": 0 }""", "\"timeoutSeconds\" 0 must be from 1 to 300")]
    [InlineData("""{ "command": "SCRIPT", "timeoutSeconds": 301 }""", "\"timeoutSeconds\" 301 must be from 1 to 300")]
    [InlineData("""{ "command": "SCRIPT", "timeoutSeconds": "soon" }""", "(at $.hooks.before.timeoutSeconds)")]
    [InlineData("""{ "command": "SCRIPT", "timeout": 30 }""", "\"timeout\" is not a setting of a hook (at $.hooks.before")]
    public void Config_AHookThatCannotWork_IsRefusedSayingWhy(string before, string expected)
    {
        using var dir = new TempDirectory();
        var script = HookScript.Write(dir.Path, "stop-ardop", Path.Combine(dir.Path, "runs"));

        var e = Refused($$"""{ "hooks": { "before": {{before.Replace("SCRIPT", script.Command, StringComparison.Ordinal)}} } }""");

        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_AHookThatIsNotExecutable_IsRefusedSayingHowToFixIt()
    {
        using var dir = new TempDirectory();
        var script = HookScript.Write(dir.Path, "start-ardop", Path.Combine(dir.Path, "runs"));
        File.SetUnixFileMode(script.Command, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var e = Refused($$"""{ "hooks": { "after": { "command": "{{script.Command}}" } } }""");

        Assert.StartsWith($"\"hooks\".\"after\": \"command\" {script.Command} is not executable by the user {Environment.UserName}, which runs it: make it so, with chmod +x", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_AHookExecutableOnlyByOthers_IsRefused()
    {
        if (Environment.UserName == "root")
        {
            // root may execute anything with any execute bit, so this cannot be shown as root.
            return;
        }
        using var dir = new TempDirectory();
        var script = HookScript.Write(dir.Path, "start-ardop", Path.Combine(dir.Path, "runs"));
        File.SetUnixFileMode(script.Command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        var e = Refused($$"""{ "hooks": { "after": { "command": "{{script.Command}}" } } }""");

        Assert.Contains("is not executable by the user", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_AnUnknownHook_IsRefusedSayingWhatThereIs()
    {
        var e = Refused("""{ "hooks": { "befor": { "command": "/bin/true" } } }""");

        Assert.Contains("\"befor\" is not a setting of \"hooks\": it has \"before\" and \"after\"", e.Message, StringComparison.Ordinal);
    }

    // ---- windows ----

    private static ReceiverHost Host(TempDirectory dir, string audio, bool rig = false) =>
        new(new ReceiverConfig
        {
            Audio = audio,
            StateDirectory = dir.Path,
            Daylight = null,
            WebSdrSlotsPerDay = 8, // every third hour, as the tests below expect
            Rig = rig ? new Retune.RigSettings { DedicatedRadio = true } : null,
        }, new FakeTimeProvider(At(9, 20)), _ => { });

    [Fact]
    public async Task Windows_WebSdr_AreItsListeningWindows()
    {
        using var dir = new TempDirectory();
        await using var host = Host(dir, "ubersdr:wessex.zapto.org");

        // 8 of 24 hourly slots: every third hour, so after 09:00 the next is 12:00.
        Assert.Equal(new HookWindow(At(11, 58), At(12, 12), At(12, 0)), host.HookWindowAt(At(9, 20)));
        Assert.Equal(new HookWindow(At(8, 58), At(9, 12), At(9, 0)), host.HookWindowAt(At(9, 5)));
        Assert.Equal(new HookWindow(At(14, 58), At(15, 12), At(15, 0)), host.HookWindowAt(At(12, 12)));
        Assert.False(host.RetunerRunsHooks);
    }

    [Fact]
    public async Task Windows_SoundCard_AreTheSameAroundEverySlot()
    {
        using var dir = new TempDirectory();
        await using var host = Host(dir, "plughw:CARD=Device,DEV=0");

        Assert.Equal(new HookWindow(At(9, 58), At(10, 12), At(10, 0)), host.HookWindowAt(At(9, 20)));
        Assert.Equal(new HookWindow(At(8, 58), At(9, 12), At(9, 0)), host.HookWindowAt(At(9, 11, 59)));
        Assert.Equal(new HookWindow(At(9, 58), At(10, 12), At(10, 0)), host.HookWindowAt(At(9, 12)));
        Assert.False(host.RetunerRunsHooks);
    }

    [Fact]
    public async Task Windows_RetunedRadio_AreTheRetunersAndARecordingHasNone()
    {
        using var dir = new TempDirectory();
        await using (var retuned = Host(dir, "plughw:CARD=Device,DEV=0", rig: true))
        {
            Assert.True(retuned.RetunerRunsHooks);
        }
        using var other = new TempDirectory();
        await using (var webSdr = Host(other, "ubersdr:wessex.zapto.org", rig: true))
        {
            // The retuner never retunes for a web SDR, so the hooks follow its windows.
            Assert.False(webSdr.RetunerRunsHooks);
        }
        using var third = new TempDirectory();
        await using var recording = Host(third, "wav:/tmp/slot.wav");
        Assert.Null(recording.HookWindowAt(At(9, 20)));
    }

    // ---- runs around a web SDR's and a sound card's windows ----

    /// <summary>A receiver's hooks loop on a fake clock, with scripts noting each run.</summary>
    private sealed class Loop : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly TempDirectory? _ownDir;
        private Task? _run;
        private TimeSpan? _pending;

        public Loop(DateTimeOffset start, string audio = "plughw:CARD=Device,DEV=0", int beforeExit = 0, string? dir = null, int everyMinutes = 60, bool withBefore = true, int afterExit = 0, string afterThen = "")
        {
            if (dir is null)
            {
                _ownDir = new TempDirectory();
                dir = _ownDir.Path;
            }
            Dir = dir;
            Record = Path.Combine(dir, "runs");
            Clock = new FakeTimeProvider(start);
            Config = new ReceiverConfig
            {
                Audio = audio,
                StateDirectory = dir,
                Daylight = null,
                WebSdrSlotsPerDay = 8, // every third hour, as the tests below expect
                EveryMinutes = everyMinutes,
                Hooks = new HooksSettings
                {
                    Before = withBefore ? HookScript.Write(dir, "before.sh", Record, beforeExit) : null,
                    After = HookScript.Write(dir, "after.sh", Record, afterExit, then: afterThen),
                },
            };
            Config.Validate();
            Host = new ReceiverHost(Config, Clock, Log.Enqueue);
            Host.Hooks.Waiting += d => Waits.Writer.TryWrite(d);
        }

        public string Dir { get; }

        public string Record { get; }

        public FakeTimeProvider Clock { get; }

        public ReceiverConfig Config { get; }

        public ReceiverHost Host { get; }

        public ConcurrentQueue<string> Log { get; } = new();

        public Channel<TimeSpan> Waits { get; } = Channel.CreateUnbounded<TimeSpan>();

        public string[] Runs => HookScript.Runs(Record);

        public void Start() => _run = Host.Hooks.RunAsync(() => Host.RetunerRunsHooks, Host.HookWindowAt, _stop.Token);

        public async Task<TimeSpan> NextWait()
        {
            if (_pending is { } pending)
            {
                return pending;
            }
            try
            {
                _pending = await Waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
                return _pending.Value;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"the hooks loop set no timer; at {Clock.GetUtcNow():HH:mm:ss} it said: {string.Join(" | ", Log)}");
            }
        }

        /// <summary>Steps until the clock reads <paramref name="at"/> or later, and the loop is waiting again.</summary>
        public async Task RunTo(DateTimeOffset at)
        {
            while (Clock.GetUtcNow() < at)
            {
                var wait = await NextWait();
                _pending = null;
                Clock.Advance(wait);
            }
            await NextWait();
        }

        /// <summary>Stops the loop as a killed receiver would: nothing more runs.</summary>
        public async Task CrashAsync()
        {
            await _stop.CancelAsync();
            if (_run is not null)
            {
                await _run;
                _run = null;
            }
        }

        /// <summary>Stops the loop as the receiver does: "after", if it is owed, runs on the way out.</summary>
        public async Task StopAsync()
        {
            await CrashAsync();
            await Host.Hooks.FinishAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await CrashAsync();
            await Host.DisposeAsync();
            _ownDir?.Dispose();
        }
    }

    [Fact]
    public async Task SoundCard_BeforeIsDoneByTheWindowsOpeningAndAfterRunsAtItsClose_AroundEverySlot()
    {
        await using var loop = new Loop(At(11, 50));
        loop.Start();

        // "before" has a 60 s timeout, so it starts a minute ahead of the window opening at 11:58.
        await loop.RunTo(At(11, 56));
        Assert.Empty(loop.Runs);
        await loop.RunTo(At(11, 57));
        Assert.Equal(["before 2026-10-05T12:00:00Z 7052.0 7053.8 none"], loop.Runs);
        Assert.True(File.Exists(loop.Host.Hooks.NotePath));

        await loop.RunTo(At(12, 11));
        Assert.Single(loop.Runs);
        await loop.RunTo(At(12, 12));
        Assert.Equal("after 2026-10-05T12:00:00Z 7052.0 7053.8 1", loop.Runs[1]);
        Assert.False(File.Exists(loop.Host.Hooks.NotePath));

        // A sound card hears every slot, so the next is 13:00's.
        await loop.RunTo(At(13, 12));
        Assert.Equal(
            ["before 2026-10-05T13:00:00Z 7052.0 7053.8 none", "after 2026-10-05T13:00:00Z 7052.0 7053.8 1"],
            loop.Runs[2..]);
        Assert.Contains(loop.Log, l => l.StartsWith("hooks: running \"before\" for the 12:00 UTC slot: ", StringComparison.Ordinal));
        Assert.Contains(loop.Log, l => l.StartsWith("hooks: \"after\" finished (exit 0)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WebSdr_OnlyAroundTheSlotsItListensTo()
    {
        await using var loop = new Loop(At(9, 20), audio: "ubersdr:wessex.zapto.org");
        loop.Start();

        await loop.RunTo(At(11, 56));
        Assert.Empty(loop.Runs);
        await loop.RunTo(At(12, 13));
        Assert.Equal(
            ["before 2026-10-05T12:00:00Z 7052.0 7053.8 none", "after 2026-10-05T12:00:00Z 7052.0 7053.8 1"],
            loop.Runs);

        // The next it listens to is 15:00's: nothing for 13:00 or 14:00.
        await loop.RunTo(At(14, 56));
        Assert.Equal(2, loop.Runs.Length);
        await loop.RunTo(At(15, 12));
        Assert.Equal("after 2026-10-05T15:00:00Z 7052.0 7053.8 1", loop.Runs[^1]);
    }

    [Fact]
    public async Task BeforeFails_SaidOnce_AndAfterStillRunsAtTheClose_ToldSo()
    {
        await using var loop = new Loop(At(11, 56, 30), beforeExit: 3);
        loop.Start();

        await loop.RunTo(At(12, 12));

        Assert.Equal(
            ["before 2026-10-05T12:00:00Z 7052.0 7053.8 none", "after 2026-10-05T12:00:00Z 7052.0 7053.8 0"],
            loop.Runs);
        Assert.Single(loop.Log, l => l.Contains("WARNING", StringComparison.Ordinal));
        Assert.Contains("hooks: WARNING - \"before\" for the 12:00 UTC slot exited with 3 after 0.0 s; listening anyway", loop.Log);
    }

    [Fact]
    public async Task OnlyAfter_StillRunsAfterEachWindow()
    {
        await using var loop = new Loop(At(11, 50), withBefore: false);
        loop.Start();

        await loop.RunTo(At(12, 12));

        Assert.Equal(["after 2026-10-05T12:00:00Z 7052.0 7053.8 1"], loop.Runs);
    }

    [Fact]
    public async Task ShuttingDownInTheWindow_RunsAfterOnTheWayOut()
    {
        await using var loop = new Loop(At(11, 56, 30));
        loop.Start();
        await loop.RunTo(At(12, 5));
        Assert.Single(loop.Runs);

        await loop.StopAsync();

        Assert.Equal("after 2026-10-05T12:00:00Z 7052.0 7053.8 1", loop.Runs[^1]);
        Assert.Contains(loop.Log, l => l.StartsWith("hooks: running \"after\" for the 12:00 UTC slot as the receiver stops", StringComparison.Ordinal));
        Assert.False(File.Exists(loop.Host.Hooks.NotePath));

        // Nothing is owed any more: a second stop runs nothing.
        await loop.Host.Hooks.FinishAsync();
        Assert.Equal(2, loop.Runs.Length);
    }

    [Fact]
    public async Task RestartedInTheWindow_RunsAfterAtStartUp_AndLeavesThatSlotAlone()
    {
        using var dir = new TempDirectory();
        await using (var first = new Loop(At(11, 56, 30), dir: dir.Path))
        {
            first.Start();
            await first.RunTo(At(12, 3));
            await first.CrashAsync();
            Assert.True(File.Exists(first.Host.Hooks.NotePath));
        }

        await using var again = new Loop(At(12, 4), dir: dir.Path);
        again.Start();
        await again.NextWait();

        Assert.Equal(
            ["before 2026-10-05T12:00:00Z 7052.0 7053.8 none", "after 2026-10-05T12:00:00Z 7052.0 7053.8 1"],
            again.Runs);
        Assert.Contains(again.Log, l => l.Contains("the receiver stopped during the 12:00 UTC slot on 2026-10-05 last time", StringComparison.Ordinal));
        Assert.False(File.Exists(again.Host.Hooks.NotePath));

        // The rest of 12:00's window is left alone; 13:00's runs as usual.
        await again.RunTo(At(12, 56));
        Assert.Equal(2, again.Runs.Length);
        await again.RunTo(At(13, 12));
        Assert.Equal(["before 2026-10-05T13:00:00Z 7052.0 7053.8 none", "after 2026-10-05T13:00:00Z 7052.0 7053.8 1"], again.Runs[2..]);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "kill -TERM $$")]
    public async Task AfterFailsOrIsKilled_TheNoteStays_SoTheNextStartRunsItAgain(int exit, string then)
    {
        using var dir = new TempDirectory();
        await using (var first = new Loop(At(11, 56, 30), dir: dir.Path, afterExit: exit, afterThen: then))
        {
            first.Start();
            await first.RunTo(At(12, 12));
            Assert.Equal("after 2026-10-05T12:00:00Z 7052.0 7053.8 1", first.Runs[^1]);
            Assert.True(File.Exists(first.Host.Hooks.NotePath));
            Assert.Contains(first.Log, l => l.StartsWith("hooks: WARNING - \"after\" for the 12:00 UTC slot exited with", StringComparison.Ordinal)
                && l.Contains("so it is run again when the receiver next starts", StringComparison.Ordinal));
        }

        await using var again = new Loop(At(12, 20), dir: dir.Path);
        again.Start();
        await again.NextWait();

        Assert.Equal(3, again.Runs.Length);
        Assert.Equal("after 2026-10-05T12:00:00Z 7052.0 7053.8 1", again.Runs[^1]);
        Assert.False(File.Exists(again.Host.Hooks.NotePath));
    }

    [Fact]
    public async Task SlotsCloserThanAWindowAndItsLead_ShareOneBeforeAndOneAfter()
    {
        // Every 15 minutes: 12:00's window closes at 12:12, and 12:15's "before" would start at
        // 12:12 (a minute ahead of its opening at 12:13), so the one window runs on.
        await using var loop = new Loop(At(11, 57, 30), everyMinutes: 15);
        loop.Start();

        await loop.RunTo(At(12, 26));
        Assert.Equal(["before 2026-10-05T12:00:00Z 7052.0 7053.8 none"], loop.Runs);
        Assert.Contains(loop.Log, l => l.Contains("the 12:15 UTC slot follows straight on", StringComparison.Ordinal));
    }
}
