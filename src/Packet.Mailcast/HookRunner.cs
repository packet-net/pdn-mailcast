using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Packet.Mailcast;

/// <summary>How a hook's run ended.</summary>
public enum HookOutcome
{
    /// <summary>It exited with 0.</summary>
    Succeeded,

    /// <summary>It exited with something other than 0.</summary>
    Failed,

    /// <summary>It ran past its timeout, and it and everything it started were killed.</summary>
    TimedOut,

    /// <summary>It could not be started at all.</summary>
    CouldNotStart,

    /// <summary>The caller cancelled it (the receiver is stopping), and it and everything it started were killed.</summary>
    Cancelled,
}

/// <summary>How a hook's run went.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="ExitCode">Its exit code, when it exited by itself.</param>
/// <param name="Why">Why it could not be started, for <see cref="HookOutcome.CouldNotStart"/>.</param>
/// <param name="Took">How long it ran, on the runner's clock.</param>
/// <param name="Timeout">The timeout it ran under.</param>
public sealed record HookResult(HookOutcome Outcome, int? ExitCode, string? Why, TimeSpan Took, TimeSpan Timeout)
{
    /// <summary>Whether it worked: exited with 0.</summary>
    public bool Ok => Outcome == HookOutcome.Succeeded;

    /// <summary>How it went, in words, for the log: "exited with 3 after 1.2 s".</summary>
    public string Describe() => Outcome switch
    {
        HookOutcome.Succeeded => string.Create(CultureInfo.InvariantCulture, $"finished (exit 0) after {Took.TotalSeconds:F1} s"),
        HookOutcome.Failed => string.Create(CultureInfo.InvariantCulture, $"exited with {ExitCode} after {Took.TotalSeconds:F1} s"),
        HookOutcome.TimedOut => string.Create(CultureInfo.InvariantCulture, $"was still running after its {Timeout.TotalSeconds:F0} s timeout, so it was killed with everything it started"),
        HookOutcome.CouldNotStart => $"could not be started: {Why}",
        _ => "was killed, with everything it started, because the receiver is stopping",
    };
}

/// <summary>
/// Runs a <see cref="HookCommand"/>: the program directly, never through a shell, as this
/// process's own user, with this process's environment plus the variables given. Its stdin is
/// closed at once, and its stdout and stderr are logged a line at a time as plain ASCII, up to
/// <see cref="MostLines"/> lines of at most <see cref="MostLineLength"/> characters (the rest is
/// read and dropped). One that runs past its timeout, or is cancelled, is killed together with
/// every process it started that is still its descendant.
/// </summary>
/// <remarks>
/// Timeouts are on the <see cref="TimeProvider"/> given, so a test can run one on a fake clock.
/// Nothing is thrown for a hook that fails: the result says how it went.
/// </remarks>
public sealed class HookRunner
{
    /// <summary>The most lines of a run's output logged, stdout and stderr together.</summary>
    public const int MostLines = 40;

    /// <summary>The most characters of one line logged.</summary>
    public const int MostLineLength = 200;

    /// <summary>How long, after a hook exits, its output is waited for: a background process it left may hold the pipes.</summary>
    public static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);

    /// <summary>How long a killed hook is waited for to be gone.</summary>
    public static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Action<string> _log;

    /// <summary>A runner on <paramref name="time"/>'s clock, logging each line to <paramref name="log"/>.</summary>
    public HookRunner(TimeProvider time, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _time = time;
        _log = log;
    }

    /// <summary>For tests: raised with the process ID once a hook has started.</summary>
    public event Action<int>? Started;

    /// <summary>
    /// Runs <paramref name="command"/> with <paramref name="environment"/> added to this process's
    /// own, logging its output with <paramref name="prefix"/> ("hooks: before") in front, and
    /// returns how it went once it has exited or been killed.
    /// </summary>
    public async Task<HookResult> RunAsync(HookCommand command, IReadOnlyDictionary<string, string> environment, string prefix, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(environment);
        var timeout = command.TimeLimit;
        var start = new ProcessStartInfo(command.Command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (string arg in command.Args ?? [])
        {
            start.ArgumentList.Add(arg);
        }
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        long began = _time.GetTimestamp();
        using var process = new Process { StartInfo = start };
        // Set before the start, so a hook can never outrun its timer.
        using var timer = new CancellationTokenSource(timeout, _time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellation);
        try
        {
            if (!process.Start())
            {
                return new(HookOutcome.CouldNotStart, null, "the system started no process", TimeSpan.Zero, timeout);
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(HookOutcome.CouldNotStart, null, Clean(e.Message), TimeSpan.Zero, timeout);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // It has gone already; its exit says how.
        }
        int lines = 0;
        Task output = Task.WhenAll(
            PumpAsync(process.StandardOutput, prefix, () => Interlocked.Increment(ref lines)),
            PumpAsync(process.StandardError, prefix + " (stderr)", () => Interlocked.Increment(ref lines)));
        Started?.Invoke(process.Id);

        HookOutcome outcome;
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            outcome = process.ExitCode == 0 ? HookOutcome.Succeeded : HookOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            outcome = timer.IsCancellationRequested ? HookOutcome.TimedOut : HookOutcome.Cancelled;
            await KillAsync(process).ConfigureAwait(false);
        }
        var took = _time.GetElapsedTime(began);

        // Whatever it printed, logged before its result is; but a background process it left
        // behind may hold the pipes open for good, so not waited for long.
        using (var grace = new CancellationTokenSource())
        {
            var gone = Task.Delay(OutputGrace, _time, grace.Token);
            if (await Task.WhenAny(output, gone).ConfigureAwait(false) == output)
            {
                await grace.CancelAsync().ConfigureAwait(false);
            }
        }
        if (Volatile.Read(ref lines) > MostLines)
        {
            _log(string.Create(CultureInfo.InvariantCulture, $"{prefix}: ({Volatile.Read(ref lines) - MostLines} more lines not logged)"));
        }
        _ = output.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        return outcome is HookOutcome.Succeeded or HookOutcome.Failed
            ? new(outcome, process.ExitCode, null, took, timeout)
            : new(outcome, null, null, took, timeout);
    }

    /// <summary>Kills the hook and its descendants, and waits a little for it to be gone.</summary>
    private async Task KillAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            // Gone already, or some descendant could not be killed: what could be, was.
        }
        using var limit = new CancellationTokenSource(KillWait, _time);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log($"hooks: WARNING - process {process.Id} would not die after it was killed");
        }
    }

    /// <summary>
    /// Reads one of the hook's streams to its end, logging each line, cut to
    /// <see cref="MostLineLength"/>, while fewer than <see cref="MostLines"/> have been; the rest
    /// is read and dropped, so a chatty hook never blocks on a full pipe.
    /// </summary>
    private async Task PumpAsync(StreamReader reader, string prefix, Func<int> count)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        bool cut = false;
        void Flush()
        {
            if (count() <= MostLines)
            {
                _log($"{prefix}: {Clean(line.ToString())}{(cut ? " ..." : "")}");
            }
            line.Clear();
            cut = false;
        }
        while (true)
        {
            int n = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            foreach (char c in buffer.AsSpan(0, n))
            {
                if (c == '\n')
                {
                    Flush();
                }
                else if (c != '\r')
                {
                    if (line.Length < MostLineLength)
                    {
                        line.Append(c);
                    }
                    else
                    {
                        cut = true;
                    }
                }
            }
        }
        if (line.Length > 0 || cut)
        {
            Flush();
        }
    }

    /// <summary>
    /// Plain printable ASCII, for the log: a journal read under a C locale shows anything else as
    /// escapes. Tabs become spaces, everything else outside ASCII a question mark.
    /// </summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var result = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            result.Append(c is >= ' ' and <= '~' ? c : c == '\t' ? ' ' : '?');
        }
        return result.ToString();
    }
}
