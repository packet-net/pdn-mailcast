using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace Packet.Mailcast.Tests;

/// <summary>
/// The hook runner against tiny real shell scripts. Timeouts are on a fake clock that only the
/// test moves, so nothing depends on how fast the machine is; the real-time bounds are only so a
/// broken build fails rather than hangs.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class HookRunnerTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 5, 11, 57, 0, TimeSpan.Zero));
    private readonly ConcurrentQueue<string> _log = new();

    public void Dispose() => _dir.Dispose();

    private HookRunner Runner() => new(_clock, _log.Enqueue);

    private HookCommand Script(string body, int timeoutSeconds = 30, params string[] args)
    {
        string path = Path.Combine(_dir.Path, $"hook-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new HookCommand { Command = path, Args = args, TimeoutSeconds = timeoutSeconds };
    }

    private static readonly IReadOnlyDictionary<string, string> NoVariables = new Dictionary<string, string>();

    [Fact]
    public async Task Succeeds_WithTheVariablesAndArgumentsAsGiven_NoShellInBetween()
    {
        var hook = Script("""
            echo "hook=$MAILCAST_HOOK slot=$MAILCAST_SLOT_UTC dial=$MAILCAST_DIAL_KHZ centre=$MAILCAST_CENTRE_KHZ ok=${MAILCAST_BEFORE_OK-none}"
            for a in "$@"; do echo "arg=[$a]"; done
            """, args: ["two words", "$HOME", "*", ""]);
        var variables = SlotHookEnvironment.For("before", new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), 7052.0, 7053.8);

        var result = await Runner().RunAsync(hook, variables, "hooks: before").WaitAsync(Bound);

        Assert.Equal(HookOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Ok);
        Assert.Equal(
            [
                "hooks: before: hook=before slot=2026-10-05T12:00:00Z dial=7052.0 centre=7053.8 ok=none",
                "hooks: before: arg=[two words]",
                "hooks: before: arg=[$HOME]",
                "hooks: before: arg=[*]",
                "hooks: before: arg=[]",
            ],
            _log);
    }

    [Fact]
    public void Environment_ForAfter_SaysWhetherBeforeWorked()
    {
        var slot = new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.FromHours(1));

        var after = SlotHookEnvironment.For("after", slot, 7052.25, 7054.05, beforeOk: false);

        Assert.Equal("after", after["MAILCAST_HOOK"]);
        Assert.Equal("2026-10-05T12:00:00Z", after["MAILCAST_SLOT_UTC"]);
        Assert.Equal("7052.25", after["MAILCAST_DIAL_KHZ"]);
        Assert.Equal("7054.05", after["MAILCAST_CENTRE_KHZ"]);
        Assert.Equal("0", after["MAILCAST_BEFORE_OK"]);
        Assert.Equal("1", SlotHookEnvironment.For("after", slot, 7052, 7053.8, beforeOk: true)["MAILCAST_BEFORE_OK"]);
        Assert.False(SlotHookEnvironment.For("before", slot, 7052, 7053.8).ContainsKey("MAILCAST_BEFORE_OK"));
    }

    [Fact]
    public async Task NonZeroExit_IsAFailure_WithStderrLogged()
    {
        var hook = Script("echo 'ardopcf: not running' >&2\nexit 3");

        var result = await Runner().RunAsync(hook, NoVariables, "hooks: before").WaitAsync(Bound);

        Assert.Equal(HookOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Ok);
        Assert.StartsWith("exited with 3", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("hooks: before (stderr): ardopcf: not running", _log);
    }

    [Fact]
    public async Task MissingOrNotExecutable_CannotStart()
    {
        var missing = new HookCommand { Command = Path.Combine(_dir.Path, "not-there") };
        var plain = Script("exit 0");
        File.SetUnixFileMode(plain.Command, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var a = await Runner().RunAsync(missing, NoVariables, "hooks: before").WaitAsync(Bound);
        var b = await Runner().RunAsync(plain, NoVariables, "hooks: before").WaitAsync(Bound);

        Assert.Equal(HookOutcome.CouldNotStart, a.Outcome);
        Assert.Equal(HookOutcome.CouldNotStart, b.Outcome);
        Assert.StartsWith("could not be started: ", a.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdinIsClosed_SoAHookThatReadsItDoesNotHang()
    {
        var hook = Script("cat >/dev/null\necho read to the end");

        var result = await Runner().RunAsync(hook, NoVariables, "hooks: before").WaitAsync(Bound);

        Assert.True(result.Ok);
        Assert.Contains("hooks: before: read to the end", _log);
    }

    [Fact]
    public async Task Timeout_KillsTheHookAndWhatItStarted()
    {
        string pids = Path.Combine(_dir.Path, "pids");
        var hook = Script($"sleep 1000 &\necho \"$$ $!\" > '{pids}.tmp'\nmv '{pids}.tmp' '{pids}'\nwait", timeoutSeconds: 5);

        var run = Runner().RunAsync(hook, NoVariables, "hooks: before");
        int[] both = await PidsAsync(pids);
        Assert.All(both, pid => Assert.True(Alive(pid), $"process {pid} should be running yet"));

        // Not yet: one second short of the timeout, it runs on.
        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(run.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var result = await run.WaitAsync(Bound);

        Assert.Equal(HookOutcome.TimedOut, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Contains("5 s timeout", result.Describe(), StringComparison.Ordinal);
        foreach (int pid in both)
        {
            await GoneAsync(pid);
        }
    }

    [Fact]
    public async Task Cancelled_KillsTheHookAndWhatItStarted()
    {
        string pids = Path.Combine(_dir.Path, "pids");
        var hook = Script($"sleep 1000 &\necho \"$$ $!\" > '{pids}.tmp'\nmv '{pids}.tmp' '{pids}'\nwait");
        using var stop = new CancellationTokenSource();

        var run = Runner().RunAsync(hook, NoVariables, "hooks: before", stop.Token);
        int[] both = await PidsAsync(pids);
        await stop.CancelAsync();
        var result = await run.WaitAsync(Bound);

        Assert.Equal(HookOutcome.Cancelled, result.Outcome);
        foreach (int pid in both)
        {
            await GoneAsync(pid);
        }
    }

    [Fact]
    public async Task Output_IsPlainAsciiAndCapped()
    {
        var hook = Script("""
            printf 'caf\303\251\tok\r\n'
            head -c 5000 /dev/zero | tr '\0' x; echo
            i=0; while [ $i -lt 100 ]; do echo "line $i"; i=$((i+1)); done
            printf 'no newline at the end'
            """);

        var result = await Runner().RunAsync(hook, NoVariables, "hooks: after").WaitAsync(Bound);

        Assert.True(result.Ok);
        var lines = _log.ToList();
        Assert.Equal("hooks: after: caf? ok", lines[0]);
        Assert.Equal("hooks: after: " + new string('x', HookRunner.MostLineLength) + " ...", lines[1]);
        Assert.Equal(HookRunner.MostLines + 1, lines.Count);
        Assert.Equal("hooks: after: (63 more lines not logged)", lines[^1]);
        Assert.All(lines, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), l));
    }

    /// <summary>The hook's own PID and its child's, once it has written them.</summary>
    private static async Task<int[]> PidsAsync(string path)
    {
        var until = DateTime.UtcNow + Bound;
        while (!File.Exists(path))
        {
            Assert.True(DateTime.UtcNow < until, "the hook never started its child");
            await Task.Delay(20);
        }
        return [.. (await File.ReadAllTextAsync(path)).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse)];
    }

    /// <summary>Running: there, and not a zombie waiting to be reaped.</summary>
    private static bool Alive(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task GoneAsync(int pid)
    {
        var until = DateTime.UtcNow + Bound;
        while (Alive(pid))
        {
            Assert.True(DateTime.UtcNow < until, $"process {pid} is still running");
            await Task.Delay(20);
        }
    }
}
