using System.Globalization;
using Mailcast.Receiver.Hooks;
using Packet.SoundModem.Rig;

namespace Mailcast.Receiver.Retune;

/// <summary>What the retuner is doing, for the page.</summary>
public enum RetuneStage
{
    /// <summary>Between slots: the radio is the station's, and LinBPQ transmits as normal.</summary>
    Idle,

    /// <summary>LinBPQ's transmit is off, and the rig is not (or no longer) on the bulletin frequency.</summary>
    HoldingTransmitOff,

    /// <summary>The rig is on the bulletin frequency for a slot.</summary>
    Tuned,

    /// <summary>The slot is over and the rig is being put back; LinBPQ stays off until it is.</summary>
    Restoring,
}

/// <summary>
/// Moves a radio shared with packet to the bulletin frequency for each slot and back, holding
/// LinBPQ's transmit off on that radio's port while it is away.
/// </summary>
/// <remarks>
/// <para>For each slot that runs, <see cref="Lead"/> before it, with rigctld connected: check
/// the port with PORTS, read its XMITOFF, write <see cref="InterlockFile"/>, send
/// <c>XMITOFF port 1</c> and see LinBPQ confirm it; wait <see cref="BpqNodeSettings.DrainSeconds"/>
/// for frames already with the TNC (XMITOFF only stops LinBPQ's own queue); check that rigctld
/// reads PTT off; write the file again; then tune the rig to the dial in USB through
/// pdn-soundmodem's <see cref="RigControl"/> until <see cref="After"/> past the slot's start
/// (or a following slot's, when they are that close), checking LinBPQ every
/// <see cref="WindowPoll"/> and at once when its session ends, and renewing the window every
/// <see cref="Check"/>. Then release the window, which puts the rig back, and only once the rig
/// reads where it was before send <c>XMITOFF port 0</c> and delete the file.</para>
/// <para>Every failure goes the safe way: without a confirmed XMITOFF the rig is not tuned; if
/// LinBPQ cannot be seen to hold off during the window, the rig goes back at once; if the rig
/// cannot be put back, LinBPQ stays off, the problem is logged loudly and the restore is retried;
/// and a receiver that dies leaves LinBPQ off rather than on the air on the bulletin frequency.
/// The next start-up finds the file, waits for the rig to be put back (rig control's own restore
/// file says where to) and only then turns LinBPQ's transmit back on.</para>
/// <para>A port the sysop had already turned off is left off afterwards.</para>
/// <para>With <see cref="RigSettings.DedicatedRadio"/> and no <c>bpq</c>, only the tuning is done.</para>
/// <para>With the config's <c>hooks</c>, the retuner runs them too, in order: "before" first,
/// started its timeout earlier than <see cref="Lead"/> so it has finished by then, and only if it
/// worked is LinBPQ held off and the rig tuned; "after" once the rig is back and LinBPQ released,
/// and as the receiver stops.</para>
/// </remarks>
public sealed class Retuner : IAsyncDisposable
{
    /// <summary>How long before a slot LinBPQ is held off and the rig tuned.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(1);

    /// <summary>How long after a slot's start the rig stays on the bulletin frequency.</summary>
    public static TimeSpan After => ReceiverConfig.WebSdrAfter;

    /// <summary>How often, in a slot, LinBPQ is checked.</summary>
    public static readonly TimeSpan WindowPoll = TimeSpan.FromSeconds(5);

    /// <summary>How often, in a slot, the rig's window is renewed; also the longest wait between looks at the clock.</summary>
    public static readonly TimeSpan Check = TimeSpan.FromSeconds(60);

    /// <summary>How often a rig that has not been put back, or one that is transmitting, is looked at again.</summary>
    public static readonly TimeSpan RestoreCheck = TimeSpan.FromSeconds(10);

    /// <summary>How often a radio that is still transmitting after the drain is looked at again.</summary>
    public static readonly TimeSpan PttRetry = TimeSpan.FromSeconds(5);

    /// <summary>How often a problem that goes on is said again in the log.</summary>
    public static readonly TimeSpan Reminder = TimeSpan.FromMinutes(10);

    /// <summary>Who the rig's tuning window is for, in rig control's log lines.</summary>
    public const string Owner = "pdn-mailcast";

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly Func<SlotSchedule> _schedule;
    private readonly Func<bool> _onRadio;
    private readonly RigTuning _tuning;
    private readonly BpqNodeSettings? _bpq;
    private readonly SlotHooks? _hooks;
    private readonly string _file;
    private readonly string _rigRestoreFile;
    private readonly object _gate = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _owedOn;
    private bool _rigMoved;
    private bool _sysopHeld;
    private RigTuning? _restoreTarget;
    private int _owedPort;
    private DateTimeOffset? _finished;
    private string? _said;
    private DateTimeOffset _saidAt;
    private string? _portSaid;
    private string _state = "starting";
    private RetuneStage _stage = RetuneStage.Idle;
    private string? _lastProblem;
    private DateTimeOffset? _lastProblemAt;
    private DateTimeOffset? _nextSlot;

    /// <summary>
    /// A retuner for <paramref name="config"/>'s <c>rig</c> and <c>bpq</c>. Slots come from
    /// <paramref name="schedule"/>, read afresh each time; a slot is only retuned for when
    /// <paramref name="onRadio"/> says the audio comes from the radio. <paramref name="hooks"/>,
    /// if given, are run around each slot it retunes for.
    /// </summary>
    public Retuner(ReceiverConfig config, Func<SlotSchedule> schedule, Func<bool> onRadio, TimeProvider time, Action<string> log, SlotHooks? hooks = null)
        : this(config, schedule, onRadio, time, log, time, hooks)
    {
    }

    /// <summary>As the public one, with rig control on a clock of its own, so a test can step its polls by themselves.</summary>
    internal Retuner(ReceiverConfig config, Func<SlotSchedule> schedule, Func<bool> onRadio, TimeProvider time, Action<string> log, TimeProvider rigTime, SlotHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var rig = config.Rig ?? throw new ArgumentException("the config has no \"rig\"", nameof(config));
        _time = time;
        _log = log;
        _schedule = schedule;
        _onRadio = onRadio;
        _hooks = hooks;
        _bpq = config.Bpq;
        _owedPort = _bpq?.HfPort ?? 0;
        _tuning = new RigTuning((long)Math.Round(config.DialHz), "USB", 0);
        _file = InterlockFile.PathIn(config.StateDirectory);
        Endpoint = rig.Endpoint;
        _rigRestoreFile = Path.Combine(config.StateDirectory, RigRestoreFile.NameFor(Endpoint));
        Rig = new RigControl(new RigControlOptions
        {
            Endpoint = Endpoint,
            Time = rigTime,
            RestoreFile = _rigRestoreFile,
        });
        Rig.Journal += line =>
        {
            // A renewal a minute through each slot is not worth a line each.
            if (!line.Contains(" renewed its window ", StringComparison.Ordinal))
            {
                _log(Ascii.Clean(line));
            }
        };
        Rig.Problem += line =>
        {
            _log(Ascii.Clean(line));
            NoteProblem(Ascii.Clean(line.StartsWith("rig: WARNING - ", StringComparison.Ordinal) ? "rig: " + line["rig: WARNING - ".Length..] : line));
        };
        if (_bpq is not null)
        {
            Node = new BpqNode(_bpq);
            Node.SessionLost += why =>
            {
                _log($"retune: {why}");
                Wake();
            };
        }
    }

    /// <summary>pdn-soundmodem's rig control, on the config's rigctld.</summary>
    public RigControl Rig { get; }

    /// <summary>Where rigctld is.</summary>
    public RigctldEndpoint Endpoint { get; }

    /// <summary>LinBPQ's node, or null for a radio nothing else transmits on.</summary>
    public BpqNode? Node { get; }

    /// <summary>What it is doing now.</summary>
    public RetuneStage Stage
    {
        get
        {
            lock (_gate)
            {
                return _stage;
            }
        }
    }

    /// <summary>What it is doing now, in a sentence for the page and the log.</summary>
    public string State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The last thing that went wrong, and when, or null.</summary>
    public (string Text, DateTimeOffset At)? LastProblem
    {
        get
        {
            lock (_gate)
            {
                return _lastProblem is null ? null : (_lastProblem, _lastProblemAt!.Value);
            }
        }
    }

    /// <summary>The slot it is tuning for, or will tune for next.</summary>
    public DateTimeOffset? NextSlot
    {
        get
        {
            lock (_gate)
            {
                return _nextSlot;
            }
        }
    }

    /// <summary>Whether LinBPQ may be held off now, and owed its transmit back.</summary>
    public bool HoldingLinBpq
    {
        get
        {
            lock (_gate)
            {
                return _owedOn;
            }
        }
    }

    /// <summary>For tests: raised with the delay each time the loop has set a timer on the clock and is waiting for it.</summary>
    internal event Action<TimeSpan>? Waiting;

    /// <summary>
    /// Says so in the log when a receiver without <c>rig</c> in its config finds the note a
    /// retuning one left behind: LinBPQ's port may still be off, and only its sysop can turn it on.
    /// </summary>
    public static void SayIfLeftOver(string stateDirectory, Action<string> log)
    {
        string path = InterlockFile.PathIn(stateDirectory);
        if (InterlockFile.Read(path, 0) is not { } note)
        {
            return;
        }
        log($"retune: WARNING - found {path}: the receiver stopped during a slot while it held LinBPQ's port {(note.HfPort > 0 ? note.HfPort.ToString(CultureInfo.InvariantCulture) : "(unknown)")} transmit-off, "
            + "and there is no \"rig\" in the config now. Turn it back on by hand (as sysop at LinBPQ's node prompt: XMITOFF <port> 0, or restart LinBPQ), and check the radio is on your packet frequency");
        try
        {
            InterlockFile.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Runs until <paramref name="cancellation"/> is cancelled, then puts the rig back and turns
    /// LinBPQ back on if it safely can. Problems are logged and retried, never thrown.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            _log(_bpq is null
                ? $"retune: the radio is retuned through rigctld at {Endpoint} to USB dial {OnAir.Mhz(_tuning.DialHz)} MHz for each slot; \"dedicatedRadio\" says nothing else transmits on it"
                : $"retune: the radio is retuned through rigctld at {Endpoint} to USB dial {OnAir.Mhz(_tuning.DialHz)} MHz for each slot, with LinBPQ port {_bpq.HfPort}'s transmit held off meanwhile through its node at {_bpq.Host}:{_bpq.Port} as {_bpq.User}");
            Recover();
            if (_hooks is not null)
            {
                // A slot whose window the receiver stopped in last time is not run again: its
                // "after" runs instead, once the rig is back.
                _hooks.Recover();
                _finished = _hooks.RecoveredSlot ?? _finished;
            }
            await Rig.StartAsync(cancellation).ConfigureAwait(false);
            await SayPortAsync(cancellation).ConfigureAwait(false);
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await StepAsync(cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    // Nothing may end this loop but shutdown: LinBPQ may be owed its transmit back.
                    Warn($"retune: WARNING - unexpected {e.GetType().Name}: {Ascii.Clean(e.Message)}; carrying on");
                    await WaitAsync(Check, cancellation).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>At start: which LinBPQ port the number names, for the log, checked against <see cref="BpqNodeSettings.ExpectedPortId"/>.</summary>
    private async Task SayPortAsync(CancellationToken cancellation)
    {
        if (Node is null)
        {
            return;
        }
        try
        {
            _ = await PortProblemAsync(cancellation).ConfigureAwait(false);
        }
        catch (BpqNodeException e)
        {
            NoteProblem(e.Message);
            Warn($"retune: cannot ask LinBPQ about port {_bpq!.HfPort} yet ({e.Message}); trying again before the next slot");
        }
        finally
        {
            if (!HoldingLinBpq)
            {
                Node.Drop();
            }
        }
    }

    /// <summary>
    /// Reads PORTS and says what <see cref="BpqNodeSettings.HfPort"/> is called; returns why it
    /// is not the port to hold off, or null when it is.
    /// </summary>
    private async Task<string?> PortProblemAsync(CancellationToken cancellation)
    {
        int port = _bpq!.HfPort;
        string? id = await Node!.PortIdAsync(port, cancellation).ConfigureAwait(false);
        if (id is null)
        {
            return $"LinBPQ's PORTS lists no port {port}: set \"bpq\".\"hfPort\" to the number of the port on the shared radio";
        }
        if (id != _portSaid)
        {
            _portSaid = id;
            _log($"retune: LinBPQ port {port} is \"{id}\"");
        }
        return _bpq.ExpectedPortId is { } expected && !string.Equals(expected.Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase)
            ? $"LinBPQ port {port} is \"{id}\", not \"{expected.Trim()}\" as \"bpq\".\"expectedPortId\" says"
            : null;
    }

    /// <summary>A note left by a receiver that stopped part way: LinBPQ is owed its transmit back, once the rig is.</summary>
    private void Recover()
    {
        if (InterlockFile.Read(_file, _owedPort) is not { } note)
        {
            return;
        }
        if (_bpq is null)
        {
            SayIfLeftOver(Path.GetDirectoryName(_file)!, _log);
            return;
        }
        // Where rig control will put the rig back to, read before it starts and removes the file.
        var target = RigRestoreFile.Read(_rigRestoreFile, Endpoint, out _, out _);
        lock (_gate)
        {
            _owedOn = true;
            _rigMoved = note.Stage != "transmitOff";
            _sysopHeld = note.SysopHeld;
            _restoreTarget = target;
            _owedPort = note.HfPort;
        }
        string slot = note.Slot == default ? "a slot" : $"the {Hhmm(note.Slot)} UTC slot on {note.Slot.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        Warn($"retune: WARNING - the receiver stopped during {slot} last time, holding LinBPQ's port {note.HfPort} transmit-off (it left {_file}). "
            + "That is the safe way round: LinBPQ has not transmitted since, so not on the bulletin frequency either. "
            + (note.SysopHeld
                ? "The port was already off before that slot, so it is left off; putting the rig back"
                : "Putting the rig back first, then turning LinBPQ's transmit back on"));
        SetState(RetuneStage.Restoring, $"putting the rig back after the receiver stopped during a slot; LinBPQ port {note.HfPort} transmit off until it is");
    }

    private async Task StepAsync(CancellationToken cancellation)
    {
        if (HoldingLinBpq)
        {
            await GiveBackAsync(cancellation).ConfigureAwait(false);
            return;
        }
        if (_hooks is not null && _hooks.AfterOwedTo(this, unowned: _onRadio()))
        {
            // The rig is back and LinBPQ released (or never held off): the hook's turn.
            await _hooks.AfterAsync(this, unowned: _onRadio()).ConfigureAwait(false);
            return;
        }

        var now = _time.GetUtcNow();
        if (NextSlotFrom(now) is not { } slot)
        {
            SetState(RetuneStage.Idle, "idle: no slot runs in the year ahead");
            await WaitAsync(Check, cancellation).ConfigureAwait(false);
            return;
        }
        var opens = slot - Lead - HookLead;
        lock (_gate)
        {
            _nextSlot = slot;
        }
        if (!_onRadio())
        {
            SetState(RetuneStage.Idle, "not retuning: the audio does not come from the radio's sound card");
            await WaitAsync(Check, cancellation).ConfigureAwait(false);
            return;
        }
        if (now < opens)
        {
            SetState(RetuneStage.Idle, $"idle until {Hhmm(opens)} UTC, ready for the {Hhmm(slot)} UTC slot");
            await WaitAsync(Shorter(opens - now, Check), cancellation).ConfigureAwait(false);
            return;
        }
        await RunSlotAsync(slot, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The slot whose window is in progress at <paramref name="now"/>, or else the next one: only
    /// slots that run (in daylight), never one already done.
    /// </summary>
    private DateTimeOffset? NextSlotFrom(DateTimeOffset now)
    {
        var timetable = _schedule().Timetable;
        var utc = now.ToUniversalTime();
        if (timetable.ActiveAtOrBefore(utc) is { } latest && utc < latest + After && latest != _finished)
        {
            return latest;
        }
        var next = timetable.NextActiveAtOrAfter(utc);
        return next is { } n && n == _finished ? timetable.NextActiveAfter(n) : next;
    }

    /// <summary>One slot (and any that follow it too closely to give the radio back between): LinBPQ off, the rig tuned and kept, then released.</summary>
    private async Task RunSlotAsync(DateTimeOffset slot, CancellationToken cancellation)
    {
        var closes = slot + After;
        try
        {
            if (_hooks is { Configured: true } hooks)
            {
                SetState(RetuneStage.Idle, $"running the \"before\" command for the {Hhmm(slot)} UTC slot");
                var start = await hooks.BeforeAsync(slot, this, "; so the radio is not retuned and LinBPQ is not held off for that slot, as whatever it was to stop may still be transmitting", cancellation).ConfigureAwait(false);
                if (start != BeforeOutcome.Ok)
                {
                    if (start == BeforeOutcome.Busy)
                    {
                        // A web SDR's window from before the audio source changed: its "after"
                        // has not run, so neither does this slot's "before".
                        Warn($"retune: not retuning for the {Hhmm(slot)} UTC slot: the hooks are still running for a window from before the audio source changed");
                    }
                    NoteProblem(start == BeforeOutcome.Busy
                        ? $"the hooks were still running for another window, so the radio was not retuned for the {Hhmm(slot)} UTC slot"
                        : $"the \"before\" command failed, so the radio was not retuned for the {Hhmm(slot)} UTC slot");
                    SetState(RetuneStage.Idle, start == BeforeOutcome.Busy
                        ? $"not retuning for the {Hhmm(slot)} UTC slot: the hooks are still running for another window"
                        : $"not retuning for the {Hhmm(slot)} UTC slot: the \"before\" command failed; \"after\" runs at {Hhmm(closes)} UTC");
                    while (_time.GetUtcNow() < closes)
                    {
                        await WaitAsync(Shorter(closes - _time.GetUtcNow(), Check), cancellation).ConfigureAwait(false);
                    }
                    return;
                }
                var holdAt = slot - Lead;
                while (_time.GetUtcNow() < holdAt)
                {
                    SetState(RetuneStage.Idle, $"the \"before\" command is done; retuning at {Hhmm(holdAt)} UTC for the {Hhmm(slot)} UTC slot");
                    await WaitAsync(Shorter(holdAt - _time.GetUtcNow(), Check), cancellation).ConfigureAwait(false);
                }
            }

            if (_bpq is not null && Node is not null)
            {
                SetState(RetuneStage.HoldingTransmitOff, $"turning LinBPQ port {_bpq.HfPort}'s transmit off for the {Hhmm(slot)} UTC slot");
                while (!await HoldAsync(slot, cancellation).ConfigureAwait(false))
                {
                    if (_time.GetUtcNow() + Check >= closes)
                    {
                        Warn($"retune: not retuning for the {Hhmm(slot)} UTC slot");
                        return;
                    }
                    await WaitAsync(Check, cancellation).ConfigureAwait(false);
                }
                if (!await ReadyToTuneAsync(slot, closes, cancellation).ConfigureAwait(false))
                {
                    return;
                }
                WriteNote("tuned", slot);
            }

            var result = await Task.Run(() => Rig.Tune(_tuning, Length(closes), Owner), CancellationToken.None).ConfigureAwait(false);
            if (result.Granted || result.Outcome == RigTuneOutcome.Failed)
            {
                // Refused leaves the rig where it was; anything else may have moved it.
                lock (_gate)
                {
                    _rigMoved = true;
                    _restoreTarget = result.Window?.RestoreTo ?? Rig.Snapshot().RestoreOwed ?? _restoreTarget;
                }
            }
            if (!result.Granted)
            {
                NoteProblem($"could not tune the rig for the {Hhmm(slot)} UTC slot: {result.Why}");
                Warn($"retune: WARNING - could not tune the rig for the {Hhmm(slot)} UTC slot ({result.Why})");
                return;
            }
            string held = _bpq is null ? "" : $"; LinBPQ port {_bpq.HfPort} transmit off";
            SetState(RetuneStage.Tuned, $"tuned to {OnAir.Mhz(_tuning.DialHz)} MHz USB for the {Hhmm(slot)} UTC slot until {Hhmm(closes)} UTC{held}");
            _log($"retune: tuned to {OnAir.Mhz(_tuning.DialHz)} MHz USB for the {Hhmm(slot)} UTC slot until {Hhmm(closes)} UTC{held}");

            try
            {
                var renewed = _time.GetUtcNow();
                while (true)
                {
                    if (_time.GetUtcNow() >= closes)
                    {
                        // A following slot whose window would open before this one closes keeps the radio.
                        if (_schedule().Timetable.NextActiveAfter(slot) is { } next && next - Lead - HookLead <= closes)
                        {
                            _finished = slot;
                            slot = next;
                            closes = next + After;
                            _hooks?.Extend(slot);
                            _log($"retune: the {Hhmm(slot)} UTC slot follows straight on, so the rig stays tuned until {Hhmm(closes)} UTC");
                            SetState(RetuneStage.Tuned, $"tuned to {OnAir.Mhz(_tuning.DialHz)} MHz USB for the {Hhmm(slot)} UTC slot until {Hhmm(closes)} UTC{held}");
                        }
                        else
                        {
                            break;
                        }
                    }
                    await WaitAsync(Shorter(closes - _time.GetUtcNow(), WindowPoll), cancellation, wakeable: true).ConfigureAwait(false);
                    if (_time.GetUtcNow() >= closes)
                    {
                        continue;
                    }
                    if (Rig.Snapshot().Window is null)
                    {
                        Warn("retune: WARNING - the rig's tuning window ended before the slot did (rig control put it back); not listening on the bulletin frequency for the rest of this slot");
                        NoteProblem("the rig's tuning window ended before the slot did");
                        break;
                    }
                    if (_time.GetUtcNow() - renewed >= Check)
                    {
                        renewed = _time.GetUtcNow();
                        var again = await Task.Run(() => Rig.Tune(_tuning, Length(closes), Owner), CancellationToken.None).ConfigureAwait(false);
                        if (!again.Granted)
                        {
                            Warn($"retune: WARNING - could not renew the rig's tuning window ({again.Why})");
                        }
                    }
                    if (Node is not null && !await StillHeldAsync(cancellation).ConfigureAwait(false))
                    {
                        Warn($"retune: WARNING - cannot confirm that LinBPQ still holds port {_bpq!.HfPort}'s transmit off, so the rig goes back now, before the slot ends");
                        break;
                    }
                    if (Node is not null && await KeyedOnTheBulletinFrequencyAsync(cancellation).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            finally
            {
                await ReleaseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _finished = slot;
            if (!HoldingLinBpq)
            {
                Node?.Drop();
            }
        }
    }

    /// <summary>
    /// After XMITOFF: waits for frames already with the TNC to go out, then for rigctld to read
    /// PTT off. False, and no retune, if the radio is still transmitting or its PTT cannot be read.
    /// </summary>
    private async Task<bool> ReadyToTuneAsync(DateTimeOffset slot, DateTimeOffset closes, CancellationToken cancellation)
    {
        int drain = _bpq!.DrainSeconds;
        if (drain > 0)
        {
            SetState(RetuneStage.HoldingTransmitOff, $"LinBPQ port {_bpq.HfPort} transmit off; waiting {drain} s for frames already with the TNC to go out");
            await WaitAsync(TimeSpan.FromSeconds(drain), cancellation).ConfigureAwait(false);
        }
        while (true)
        {
            string problem;
            try
            {
                if (!await RigPtt.TransmittingAsync(Endpoint, cancellation).ConfigureAwait(false))
                {
                    return true;
                }
                problem = "the radio is still transmitting (rigctld reads PTT on) after LinBPQ's transmit was turned off; not tuning while it is";
            }
            catch (IOException e)
            {
                problem = $"cannot check that the radio is not transmitting ({Ascii.Clean(e.Message)}); not tuning without that";
            }
            NoteProblem(problem);
            Loud($"retune: WARNING - {problem}");
            if (_time.GetUtcNow() + PttRetry >= closes)
            {
                Warn($"retune: not retuning for the {Hhmm(slot)} UTC slot");
                return false;
            }
            await WaitAsync(PttRetry, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// In a window: true when rigctld reads PTT on, which with LinBPQ held off means the TNC sent
    /// something it had held back (for a busy channel, say), on the bulletin frequency. The rig
    /// then goes back at once. A PTT that cannot be read is noted and the slot carries on.
    /// </summary>
    private async Task<bool> KeyedOnTheBulletinFrequencyAsync(CancellationToken cancellation)
    {
        try
        {
            if (!await RigPtt.TransmittingAsync(Endpoint, cancellation).ConfigureAwait(false))
            {
                return false;
            }
        }
        catch (IOException e)
        {
            NoteProblem($"cannot read the radio's PTT during the slot ({Ascii.Clean(e.Message)})");
            return false;
        }
        NoteProblem("the radio transmitted while tuned to the bulletin frequency");
        Warn($"retune: WARNING - the radio is transmitting while tuned to the bulletin frequency (rigctld reads PTT on), though LinBPQ's port {_bpq!.HfPort} is held off: the TNC must have sent something it was holding. The rig goes back now");
        return true;
    }

    /// <summary>Ends the rig's window, which puts it back, and says how that went.</summary>
    private async Task ReleaseAsync()
    {
        if (Rig.Snapshot().Window is null)
        {
            return;
        }
        if (_bpq is not null)
        {
            SetState(RetuneStage.Restoring, $"putting the rig back; LinBPQ port {_bpq.HfPort} transmit off until it is");
        }
        await Task.Run(() => Rig.Release(Owner), CancellationToken.None).ConfigureAwait(false);
        if (_bpq is null)
        {
            SetState(RetuneStage.Idle, "the slot is over");
        }
    }

    /// <summary>
    /// With rigctld there to tune: the port checked, its XMITOFF read, the note written, then
    /// XMITOFF 1 sent. False when LinBPQ is not held off.
    /// </summary>
    private async Task<bool> HoldAsync(DateTimeOffset slot, CancellationToken cancellation)
    {
        int port = _bpq!.HfPort;
        if (!Rig.Connected)
        {
            // LinBPQ is not taken off the air for a slot the rig cannot be tuned for.
            string why = $"rigctld at {Endpoint} is not connected";
            NoteProblem(why);
            Warn($"retune: not retuning yet for the {Hhmm(slot)} UTC slot: {why}");
            return false;
        }
        bool owedBefore = HoldingLinBpq;
        bool already;
        try
        {
            if (await PortProblemAsync(cancellation).ConfigureAwait(false) is { } wrong)
            {
                NoteProblem(wrong);
                Warn($"retune: not retuning yet for the {Hhmm(slot)} UTC slot: {wrong}");
                return false;
            }
            already = await Node!.TransmitOffAsync(port, cancellation).ConfigureAwait(false);
        }
        catch (BpqNodeException e)
        {
            // Only questions so far: nothing has been changed.
            NoteProblem(e.Message);
            Warn($"retune: not retuning yet for the {Hhmm(slot)} UTC slot: {e.Message}");
            return false;
        }
        if (already && owedBefore)
        {
            // An earlier try of this one's got through after all.
            return true;
        }
        if (!TryWriteNote("transmitOff", slot, sysopHeld: already))
        {
            return false;
        }
        lock (_gate)
        {
            _owedOn = true;
            _owedPort = port;
            _sysopHeld = already;
        }
        if (already)
        {
            _log($"retune: LinBPQ port {port}'s transmit was already off before the {Hhmm(slot)} UTC slot (the sysop's doing?), so it is left off afterwards too");
            return true;
        }
        try
        {
            bool was = await Node.SetTransmitOffAsync(port, true, cancellation).ConfigureAwait(false);
            _log($"retune: LinBPQ port {port}'s transmit is off for the {Hhmm(slot)} UTC slot (XMITOFF {port} 1 confirmed{(was ? "; it was off already" : "")})");
            return true;
        }
        catch (BpqNodeException e)
        {
            NoteProblem($"LinBPQ did not confirm XMITOFF {port} 1: {e.Message}");
            Warn($"retune: not retuning yet for the {Hhmm(slot)} UTC slot: {e.Message}");
            if (!e.MaybeApplied && !owedBefore)
            {
                // A known refusal, or never sent, and no earlier try may have reached it: the
                // port is as it was.
                ClearNote();
            }
            return false;
        }
    }

    /// <summary>In a window: LinBPQ still has the port off, put right if it had been turned back on. False if that cannot be seen.</summary>
    private async Task<bool> StillHeldAsync(CancellationToken cancellation)
    {
        int port = _bpq!.HfPort;
        try
        {
            if (await Node!.TransmitOffAsync(port, cancellation).ConfigureAwait(false))
            {
                return true;
            }
            NoteProblem($"LinBPQ port {port}'s transmit was on again during a slot");
            Warn($"retune: WARNING - LinBPQ says port {port}'s transmit is on again (was LinBPQ restarted?); turning it off again");
            await Node.SetTransmitOffAsync(port, true, cancellation).ConfigureAwait(false);
            return true;
        }
        catch (BpqNodeException e)
        {
            NoteProblem(e.Message);
            _log($"retune: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// LinBPQ is owed its transmit back: given once the rig reads where it was before, and until
    /// then the port is kept off (put off again if LinBPQ has been restarted meanwhile).
    /// </summary>
    private async Task GiveBackAsync(CancellationToken cancellation)
    {
        int port;
        lock (_gate)
        {
            port = _owedPort;
        }
        if (!RigSafe(out string why))
        {
            SetState(RetuneStage.Restoring, $"putting the rig back; LinBPQ port {port} transmit off until it is ({why})");
            Loud($"retune: WARNING - LinBPQ port {port} stays transmit-off: {why}. Trying again every {RestoreCheck.TotalSeconds:F0} s");
            await KeepOffAsync(port, cancellation).ConfigureAwait(false);
            await WaitAsync(RestoreCheck, cancellation, wakeable: true).ConfigureAwait(false);
            return;
        }
        if (await TransmitOnAsync(port, cancellation).ConfigureAwait(false))
        {
            return;
        }
        await WaitAsync(Check, cancellation).ConfigureAwait(false);
    }

    /// <summary>While the rig cannot be put back: LinBPQ's port stays off, even across a LinBPQ restart.</summary>
    private async Task KeepOffAsync(int port, CancellationToken cancellation)
    {
        try
        {
            if (!await Node!.TransmitOffAsync(port, cancellation).ConfigureAwait(false))
            {
                Warn($"retune: WARNING - LinBPQ says port {port}'s transmit is on again (was LinBPQ restarted?) while the rig has not been put back; turning it off again");
                await Node.SetTransmitOffAsync(port, true, cancellation).ConfigureAwait(false);
            }
        }
        catch (BpqNodeException e)
        {
            NoteProblem(e.Message);
        }
    }

    /// <summary>Sends XMITOFF port 0, unless the sysop had it off. True once that is done and the note is gone.</summary>
    private async Task<bool> TransmitOnAsync(int port, CancellationToken cancellation)
    {
        bool sysop;
        lock (_gate)
        {
            sysop = _sysopHeld;
        }
        if (sysop)
        {
            _log($"retune: the rig is back; LinBPQ port {port}'s transmit was off before the slot, so it is left off (as sysop, XMITOFF {port} 0 turns it on)");
            ClearNote();
            Node?.Drop();
            _said = null;
            SetState(RetuneStage.Idle, "the slot is over");
            return true;
        }
        try
        {
            bool was = await Node!.SetTransmitOffAsync(port, false, cancellation).ConfigureAwait(false);
            _log(was
                ? $"retune: the rig is back; LinBPQ port {port}'s transmit is on again (XMITOFF {port} 0 confirmed)"
                : $"retune: the rig is back; LinBPQ port {port}'s transmit was on already (XMITOFF {port} 0 confirmed; LinBPQ restarted since, or never took the XMITOFF)");
            ClearNote();
            Node.Drop();
            _said = null;
            SetState(RetuneStage.Idle, "the slot is over");
            return true;
        }
        catch (BpqNodeException e)
        {
            NoteProblem($"cannot turn LinBPQ port {port}'s transmit back on yet: {e.Message}");
            Loud($"retune: WARNING - cannot turn LinBPQ port {port}'s transmit back on yet ({e.Message}); trying again every {Check.TotalSeconds:F0} s");
            SetState(RetuneStage.HoldingTransmitOff, $"the rig is back, but LinBPQ port {port}'s transmit is still off: LinBPQ cannot be reached to turn it on");
            return false;
        }
    }

    /// <summary>
    /// Whether the rig is known to be back: no window, no restore owed, and reading where it was
    /// before the slot (or, when that is not known, not the bulletin frequency).
    /// </summary>
    private bool RigSafe(out string why)
    {
        var state = Rig.Snapshot();
        bool moved;
        RigTuning? target;
        lock (_gate)
        {
            moved = _rigMoved;
            target = _restoreTarget;
        }
        // Not asked to move since LinBPQ was held off, and owed nothing: it is where it was.
        why = !moved && state.Window is null && state.RestoreOwed is null ? ""
            : state.Window is { } open ? $"the rig is still tuned to {open.Tuning}"
            : state.RestoreOwed is { } owed ? $"the rig has not been put back to {owed} yet"
            : !state.Connected ? $"rigctld at {Endpoint} is not connected, so where the rig is cannot be checked"
            : state.Tuning is not { } tuning ? "the rig has not been read yet"
            : target is not null && (tuning.DialHz != target.DialHz || !string.Equals(tuning.Mode, target.Mode, StringComparison.OrdinalIgnoreCase))
                ? $"the rig reads {tuning}, not {target} where it was before the slot"
            : target is null && tuning.DialHz == _tuning.DialHz
                ? $"the rig reads {tuning}, the bulletin frequency, and nothing says where it goes back to: set it to your packet frequency by hand"
            : "";
        return why.Length == 0;
    }

    /// <summary>
    /// Puts the rig back and turns LinBPQ on, if it safely can, as the receiver stops; then runs
    /// the "after" hook if it is owed.
    /// </summary>
    private async Task StopAsync()
    {
        try
        {
            await PutBackAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_hooks is not null)
            {
                await _hooks.AfterAsync(this, unowned: true, " as the receiver stops").ConfigureAwait(false);
            }
        }
    }

    private async Task PutBackAsync()
    {
        try
        {
            await ReleaseAsync().ConfigureAwait(false);
            if (HoldingLinBpq && Node is not null)
            {
                int port;
                lock (_gate)
                {
                    port = _owedPort;
                }
                if (RigSafe(out string why))
                {
                    using var limit = new CancellationTokenSource(BpqNode.ReplyTimeout * 3);
                    if (!await TransmitOnAsync(port, limit.Token).ConfigureAwait(false))
                    {
                        Warn($"retune: WARNING - stopping with LinBPQ port {port} transmit-off; the next start-up turns it back on");
                    }
                }
                else
                {
                    Warn($"retune: WARNING - stopping with LinBPQ port {port} transmit-off, because {why}; the next start-up puts the rig back and then turns it on");
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void WriteNote(string stage, DateTimeOffset slot)
    {
        bool sysop;
        lock (_gate)
        {
            sysop = _sysopHeld;
        }
        InterlockFile.Write(_file, new InterlockFile.Note(stage, $"{_bpq!.Host}:{_bpq.Port}", _bpq.HfPort, slot, _time.GetUtcNow(), sysop));
    }

    private bool TryWriteNote(string stage, DateTimeOffset slot, bool sysopHeld)
    {
        try
        {
            InterlockFile.Write(_file, new InterlockFile.Note(stage, $"{_bpq!.Host}:{_bpq.Port}", _bpq.HfPort, slot, _time.GetUtcNow(), sysopHeld));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the note a receiver killed in the slot would not know to turn LinBPQ back on.
            NoteProblem($"cannot write {_file}: {Ascii.Clean(e.Message)}");
            Warn($"retune: WARNING - cannot write {_file} ({Ascii.Clean(e.Message)}), so not retuning for the {Hhmm(slot)} UTC slot");
            return false;
        }
    }

    private void ClearNote()
    {
        lock (_gate)
        {
            _owedOn = false;
            _rigMoved = false;
            _sysopHeld = false;
            _restoreTarget = null;
            _owedPort = _bpq?.HfPort ?? 0;
        }
        try
        {
            InterlockFile.Delete(_file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Warn($"retune: WARNING - cannot remove {_file} ({Ascii.Clean(e.Message)}); the next start-up sends XMITOFF 0 again, which does no harm");
        }
    }

    /// <summary>How much earlier than <see cref="Lead"/> a slot's work starts: the "before" hook's timeout, so it is done by then.</summary>
    private TimeSpan HookLead => _hooks is { Configured: true } hooks ? hooks.BeforeLead : TimeSpan.Zero;

    /// <summary>How long to ask rig control for: past the next renewal, never past its cap.</summary>
    private TimeSpan Length(DateTimeOffset closes)
    {
        var wanted = closes - _time.GetUtcNow() + Check + TimeSpan.FromSeconds(30);
        return wanted < RigControl.MaxWindow ? wanted : RigControl.MaxWindow;
    }

    private void SetState(RetuneStage stage, string state)
    {
        lock (_gate)
        {
            _stage = stage;
            _state = state;
        }
    }

    private void NoteProblem(string text)
    {
        lock (_gate)
        {
            _lastProblem = Ascii.Clean(text);
            _lastProblemAt = _time.GetUtcNow();
        }
    }

    private void Warn(string line) => _log(Ascii.Clean(line));

    /// <summary>A line that may repeat: said when it changes, and again every <see cref="Reminder"/>.</summary>
    private void Loud(string line)
    {
        var now = _time.GetUtcNow();
        if (line == _said && now - _saidAt < Reminder)
        {
            return;
        }
        _said = line;
        _saidAt = now;
        Warn(line);
    }

    /// <summary>Ends a wakeable wait now: LinBPQ's session has ended.</summary>
    private void Wake()
    {
        lock (_gate)
        {
            _wake.TrySetResult();
        }
    }

    /// <summary>
    /// Waits <paramref name="delay"/> on the clock; a <paramref name="wakeable"/> wait also ends
    /// when LinBPQ's session does (or did since the last such wait).
    /// </summary>
    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellation, bool wakeable = false)
    {
        Task? wake = null;
        if (wakeable)
        {
            lock (_gate)
            {
                if (_wake.Task.IsCompleted)
                {
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    return;
                }
                wake = _wake.Task;
            }
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var tick = Task.Delay(delay, _time, stop.Token);
        Waiting?.Invoke(delay);
        if (wake is null)
        {
            await tick.ConfigureAwait(false);
            return;
        }
        await Task.WhenAny(tick, wake).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
    }

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? (a < TimeSpan.Zero ? TimeSpan.Zero : a) : b;

    private static string Hhmm(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Node is not null)
        {
            await Node.DisposeAsync().ConfigureAwait(false);
        }
        await Rig.DisposeAsync().ConfigureAwait(false);
    }
}
