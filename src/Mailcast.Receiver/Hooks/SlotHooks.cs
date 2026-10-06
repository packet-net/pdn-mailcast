using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailcast.Receiver.Retune;
using Packet.Mailcast;

namespace Mailcast.Receiver.Hooks;

/// <summary>
/// The config's <c>hooks</c>: a program run before each slot the receiver listens to, and one
/// run after it, for a station that shares its radio with something else (stopping Ardopcf on
/// another machine over ssh, say, and starting it again afterwards). Either may be left out;
/// any other key is refused.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HooksSettings
{
    /// <summary>Run before each slot's window, so that it has finished by the time the window opens.</summary>
    public HookCommand? Before { get; init; }

    /// <summary>Run after each slot's window, whenever <see cref="Before"/> was started.</summary>
    public HookCommand? After { get; init; }

    internal void Validate()
    {
        if (Before?.Problem() is { } before)
        {
            throw new ConfigException($"\"hooks\".\"before\": {before}");
        }
        if (After?.Problem() is { } after)
        {
            throw new ConfigException($"\"hooks\".\"after\": {after}");
        }
    }
}

/// <summary>How a window's start went: whether whatever "before" was to stop is stopped.</summary>
public enum BeforeOutcome
{
    /// <summary>"before" worked, or there is none.</summary>
    Ok,

    /// <summary>"before" failed: what it was to stop may still be running.</summary>
    Failed,

    /// <summary>
    /// Nothing was run: an earlier window, someone else's (the retuner's, before the audio
    /// source was changed, say), has not had its "after" yet. Only its owner runs that, once it
    /// has put its radio back.
    /// </summary>
    Busy,
}

/// <summary>A slot's listening window, which the hooks run around.</summary>
/// <param name="Opens">When listening starts.</param>
/// <param name="Closes">When it ends.</param>
/// <param name="Slot">The slot's start.</param>
public readonly record struct HookWindow(DateTimeOffset Opens, DateTimeOffset Closes, DateTimeOffset Slot);

/// <summary>
/// Runs the config's <c>hooks</c> around the slots: "before" ahead of each window, timed to have
/// finished (or been killed at its timeout) by the window's opening, and "after" once the window
/// is over, always, if "before" was started: after a failure, as the receiver stops, and after a
/// restart in the middle of a window, from the note in the state directory.
/// </summary>
/// <remarks>
/// <para>A web SDR's windows are its listening windows; a sound card's are the same around every
/// slot (<see cref="ReceiverConfig.WebSdrBefore"/> before to <see cref="ReceiverConfig.WebSdrAfter"/>
/// after). Both run in <see cref="RunAsync"/>. When the receiver retunes a radio, the
/// <see cref="Retuner"/> runs them instead, in order with its own steps: "before" first, and only
/// if it worked is LinBPQ held off and the radio tuned; "after" once the radio is back and
/// LinBPQ released.</para>
/// <para>The note, <see cref="FileName"/>, is written before "before" starts and removed once
/// "after" has run.</para>
/// </remarks>
public sealed class SlotHooks : IDisposable
{
    /// <summary>The note's name in the state directory.</summary>
    public const string FileName = "hooks.json";

    /// <summary>The longest wait on the clock before looking at it again.</summary>
    public static readonly TimeSpan Check = TimeSpan.FromSeconds(60);

    private readonly Func<ReceiverConfig> _config;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly string _file;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _gate = new();
    private readonly object _loop = new();
    private Owed? _owed;
    private bool _recovered;

    /// <summary>What the note in the state directory holds.</summary>
    /// <param name="Slot">The slot "before" was run for.</param>
    /// <param name="LastSlot">The last slot of the window, when slots follow on so closely that one window covers them.</param>
    /// <param name="BeforeOk">Whether "before" worked, once it has finished.</param>
    /// <param name="Written">When the note was written.</param>
    public sealed record Note(DateTimeOffset Slot, DateTimeOffset LastSlot, bool? BeforeOk, DateTimeOffset Written);

    /// <summary>An "after" owed: for which slot, to whom (null: whoever runs next), and how "before" went.</summary>
    private sealed record Owed(DateTimeOffset Slot, object? Owner, bool BeforeOk);

    /// <summary>The hooks in <paramref name="config"/> (read afresh each time), on <paramref name="time"/>'s clock.</summary>
    public SlotHooks(Func<ReceiverConfig> config, TimeProvider time, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _time = time;
        _log = log;
        _file = Path.Combine(config().StateDirectory, FileName);
        Runner = new HookRunner(time, line => log(Ascii.Clean(line)));
    }

    /// <summary>What starts the programs.</summary>
    public HookRunner Runner { get; }

    /// <summary>The note's path.</summary>
    public string NotePath => _file;

    private HooksSettings? Settings => _config().Hooks;

    /// <summary>Whether the config has a hook at all.</summary>
    public bool Configured => Settings is { } s && (s.Before is not null || s.After is not null);

    /// <summary>How much earlier than the window's opening "before" is started: its timeout, so it is done by then.</summary>
    public TimeSpan BeforeLead => Settings?.Before?.TimeLimit ?? TimeSpan.Zero;

    /// <summary>The slot the note from last time was for, if there was one: that slot is not run again.</summary>
    public DateTimeOffset? RecoveredSlot { get; private set; }

    /// <summary>The slot whose "after" is owed, if any.</summary>
    public DateTimeOffset? OwedSlot
    {
        get
        {
            lock (_gate)
            {
                return _owed?.Slot;
            }
        }
    }

    /// <summary>For tests: raised with the delay each time <see cref="RunAsync"/> has set a timer on the clock.</summary>
    internal event Action<TimeSpan>? Waiting;

    /// <summary>
    /// At start-up, once: a note left by a receiver that stopped part way through a window means
    /// "after" is owed for it. Says so in the log; whichever runs the hooks then runs it.
    /// </summary>
    public void Recover()
    {
        lock (_gate)
        {
            if (_recovered)
            {
                return;
            }
            _recovered = true;
        }
        if (!File.Exists(_file))
        {
            return;
        }
        Note note;
        try
        {
            note = JsonSerializer.Deserialize<Note>(File.ReadAllText(_file), ReceiverConfig.Json) ?? new Note(default, default, null, default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            note = new Note(default, default, null, default);
        }
        string which = note.Slot == default ? "a slot" : $"the {Hhmm(note.Slot)} UTC slot on {note.Slot.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        RecoveredSlot = note.LastSlot == default ? null : note.LastSlot;
        if (Settings?.After is null)
        {
            _log($"hooks: found {_file}: the receiver stopped during {which} last time, but there is no \"after\" command in the config to run for it now");
            DeleteNote();
            return;
        }
        lock (_gate)
        {
            _owed = new Owed(note.Slot, null, note.BeforeOk ?? false);
        }
        _log($"hooks: the receiver stopped during {which} last time, after starting its \"before\" command; running \"after\" now (once the radio is back, if it was retuned), and not listening to that slot again");
    }

    /// <summary>
    /// Runs "before" for <paramref name="slot"/>, having written the note first, and returns
    /// whether it worked; <see cref="BeforeOutcome.Ok"/> when there is no "before". Its failure is
    /// logged once, with <paramref name="consequence"/> on the end. From then on "after" is owed,
    /// to <paramref name="owner"/> alone. If <paramref name="owner"/>'s own window is still open,
    /// "before" is not run again and this says how it went; if anyone else's is (or one from last
    /// time), nothing is run and this returns <see cref="BeforeOutcome.Busy"/>.
    /// </summary>
    public async Task<BeforeOutcome> BeforeAsync(DateTimeOffset slot, object owner, string consequence, CancellationToken cancellation)
    {
        if (!Configured)
        {
            return BeforeOutcome.Ok;
        }
        await _running.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_owed is { } owed)
                {
                    return owed.Owner != owner ? BeforeOutcome.Busy
                        : owed.BeforeOk ? BeforeOutcome.Ok : BeforeOutcome.Failed;
                }
            }
            var config = _config();
            var before = config.Hooks?.Before;
            WriteNote(new Note(slot, slot, null, _time.GetUtcNow()));
            bool ok = true;
            if (before is not null)
            {
                _log($"hooks: running \"before\" for the {Hhmm(slot)} UTC slot: {before.Describe()}");
                var result = await Runner.RunAsync(before, Environment(SlotHookEnvironment.BeforeName, slot, config, null), "hooks: before", cancellation).ConfigureAwait(false);
                ok = result.Ok;
                _log(ok
                    ? $"hooks: \"before\" {result.Describe()}"
                    : $"hooks: WARNING - \"before\" for the {Hhmm(slot)} UTC slot {result.Describe()}{consequence}");
            }
            lock (_gate)
            {
                _owed = new Owed(slot, owner, ok);
            }
            WriteNote(new Note(slot, slot, ok, _time.GetUtcNow()));
            return ok ? BeforeOutcome.Ok : BeforeOutcome.Failed;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>When a following slot keeps the window open: the note says so, so a restart in it does not run that slot again.</summary>
    public void Extend(DateTimeOffset lastSlot)
    {
        DateTimeOffset slot;
        bool ok;
        lock (_gate)
        {
            if (_owed is not { } owed)
            {
                return;
            }
            slot = owed.Slot;
            ok = owed.BeforeOk;
        }
        WriteNote(new Note(slot, lastSlot, ok, _time.GetUtcNow()));
    }

    /// <summary>
    /// Whether "after" is owed to <paramref name="owner"/>, or, with <paramref name="unowned"/>,
    /// owed to nobody in particular (a note from last time).
    /// </summary>
    public bool AfterOwedTo(object owner, bool unowned)
    {
        lock (_gate)
        {
            return _owed is { } owed && (owed.Owner == owner || (unowned && owed.Owner is null));
        }
    }

    /// <summary>
    /// Runs "after", if it is owed to <paramref name="owner"/> (or to nobody, with
    /// <paramref name="unowned"/>; a null <paramref name="owner"/> runs it whoever it is owed
    /// to). It runs to its end or its timeout, whatever else is stopping. The note is removed only
    /// if it exited with 0: otherwise (it failed, timed out or was killed, by systemd stopping the
    /// receiver, say) the note stays, so the next start-up runs it again. <paramref name="why"/>
    /// is said in the log line.
    /// </summary>
    public async Task AfterAsync(object? owner, bool unowned, string why = "")
    {
        await _running.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Owed owed;
            lock (_gate)
            {
                if (_owed is not { } o || !(o.Owner == owner || (unowned && o.Owner is null) || owner is null))
                {
                    return;
                }
                owed = o;
            }
            var config = _config();
            bool done = true;
            if (config.Hooks?.After is { } after)
            {
                string which = owed.Slot == default ? "a slot" : $"the {Hhmm(owed.Slot)} UTC slot";
                _log($"hooks: running \"after\" for {which}{why}: {after.Describe()}");
                var result = await Runner.RunAsync(after, Environment(SlotHookEnvironment.AfterName, owed.Slot, config, owed.BeforeOk), "hooks: after", CancellationToken.None).ConfigureAwait(false);
                done = result.Ok;
                _log(done
                    ? $"hooks: \"after\" {result.Describe()}"
                    : $"hooks: WARNING - \"after\" for {which} {result.Describe()}; keeping {_file}, so it is run again when the receiver next starts");
            }
            lock (_gate)
            {
                _owed = null;
            }
            if (done)
            {
                DeleteNote();
            }
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>As the receiver stops: "after", if it is still owed to anyone.</summary>
    public Task FinishAsync() => AfterAsync(null, unowned: true, " as the receiver stops");

    /// <summary>
    /// Runs the hooks around each window <paramref name="windowAt"/> gives (the window in
    /// progress at a time, or else the next; null for none), until <paramref name="cancellation"/>
    /// is cancelled, while <paramref name="retunerRuns"/> says the retuner is not running them
    /// itself. "after" still owed when it is cancelled is left for <see cref="FinishAsync"/>.
    /// </summary>
    public async Task RunAsync(Func<bool> retunerRuns, Func<DateTimeOffset, HookWindow?> windowAt, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(retunerRuns);
        ArgumentNullException.ThrowIfNull(windowAt);
        Recover();
        DateTimeOffset? finished = RecoveredSlot;
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                bool retuner = retunerRuns();
                if (!retuner && AfterOwedTo(_loop, unowned: true))
                {
                    await AfterAsync(_loop, unowned: true).ConfigureAwait(false);
                    continue;
                }
                var now = _time.GetUtcNow();
                if (retuner || !Configured || Next(windowAt, now, finished) is not { } window)
                {
                    await WaitAsync(Check, cancellation).ConfigureAwait(false);
                    continue;
                }
                var startAt = window.Opens - BeforeLead;
                if (now < startAt)
                {
                    await WaitAsync(Shorter(startAt - now, Check), cancellation).ConfigureAwait(false);
                    continue;
                }

                if (await BeforeAsync(window.Slot, _loop, "; listening anyway", cancellation).ConfigureAwait(false) == BeforeOutcome.Busy)
                {
                    // The retuner's window from before the audio source changed: it runs that
                    // "after" itself, once its radio is back.
                    await WaitAsync(Check, cancellation).ConfigureAwait(false);
                    continue;
                }
                var closes = window.Closes;
                var last = window.Slot;
                while (true)
                {
                    now = _time.GetUtcNow();
                    if (now >= closes)
                    {
                        // A following window whose "before" would start before this one closes keeps it open.
                        if (!retunerRuns() && Next(windowAt, closes, null) is { } following && following.Opens - BeforeLead <= closes)
                        {
                            closes = following.Closes;
                            last = following.Slot;
                            Extend(last);
                            _log($"hooks: the {Hhmm(last)} UTC slot follows straight on, so \"after\" waits until {Hhmm(closes)} UTC");
                            continue;
                        }
                        break;
                    }
                    await WaitAsync(Shorter(closes - now, Check), cancellation).ConfigureAwait(false);
                }
                finished = last;
                await AfterAsync(_loop, unowned: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>The window in progress at <paramref name="at"/> or the next, skipping any slot up to <paramref name="finished"/>.</summary>
    private static HookWindow? Next(Func<DateTimeOffset, HookWindow?> windowAt, DateTimeOffset at, DateTimeOffset? finished)
    {
        var window = windowAt(at);
        for (int i = 0; i < 1000 && window is { } w && finished is { } f && w.Slot <= f; i++)
        {
            window = windowAt(w.Closes);
        }
        return window is { } found && finished is { } done && found.Slot <= done ? null : window;
    }

    private static IReadOnlyDictionary<string, string> Environment(string hook, DateTimeOffset slot, ReceiverConfig config, bool? beforeOk) =>
        SlotHookEnvironment.For(hook, slot, config.DialKHz, config.CentreHz / 1000, beforeOk);

    private void WriteNote(Note note)
    {
        try
        {
            InterlockFile.WriteDurably(_file, JsonSerializer.SerializeToUtf8Bytes(note, ReceiverConfig.Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without it, a receiver that stops in the window does not run "after" when it starts
            // again: whatever "before" stopped stays stopped, which is the safe way round.
            _log($"hooks: WARNING - cannot write {_file} ({Ascii.Clean(e.Message)}); if the receiver stops during this window, \"after\" will not be run for it when it starts again");
        }
    }

    private void DeleteNote()
    {
        try
        {
            File.Delete(_file);
            File.Delete(_file + ".tmp");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"hooks: WARNING - cannot remove {_file} ({Ascii.Clean(e.Message)}); the next start-up runs \"after\" again");
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellation)
    {
        var tick = Task.Delay(delay, _time, cancellation);
        Waiting?.Invoke(delay);
        await tick.ConfigureAwait(false);
    }

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? (a < TimeSpan.Zero ? TimeSpan.Zero : a) : b;

    private static string Hhmm(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public void Dispose() => _running.Dispose();
}
