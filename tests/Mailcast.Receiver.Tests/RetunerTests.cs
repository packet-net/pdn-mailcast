using System.Collections.Concurrent;
using System.Threading.Channels;
using Mailcast.Receiver.Hooks;
using Mailcast.Receiver.Retune;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.SoundModem.Rig;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The shared radio: LinBPQ held off with XMITOFF, the rig tuned to the bulletin frequency for a
/// slot and put back, and every way that goes wrong, against a fake LinBPQ node and a fake
/// rigctld on loopback. The retuner's waits are on a fake clock that a test moves by exactly what
/// the retuner asked for; rig control's own polls are on a second fake clock, moved only when a
/// test wants a poll, and each poll is waited for by seeing rig control set its next timer.
/// Nothing here is timed.
/// </summary>
public sealed partial class RetunerTests
{
    /// <summary>Where the station keeps the radio for packet: 7.048 MHz USB.</summary>
    private const long PacketDialHz = 7_048_000;

    private const long BulletinDialHz = 7_052_000;

    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A fake clock that says each time a timer is set on it: rig control's loop setting its next nap.</summary>
    private sealed class RigClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public Channel<bool> Timers { get; } = Channel.CreateUnbounded<bool>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Timers.Writer.TryWrite(true);
            return timer;
        }
    }

    /// <summary>A retuner with its fakes, run on fake clocks.</summary>
    private sealed class Station : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly TempDirectory? _ownDir;
        private readonly bool _ownFakes;
        private Task? _run;
        private TimeSpan? _pending;

        public Station(
            DateTimeOffset start,
            bool dedicated = false,
            FakeRigctld? rig = null,
            FakeLinBpqNode? node = null,
            string? dir = null,
            Func<bool>? onRadio = null,
            BpqNodeSettings? bpq = null,
            Func<BpqNodeSettings, BpqNodeSettings>? adjust = null,
            SlotSchedule? schedule = null,
            HooksSettings? hooks = null)
        {
            _ownFakes = rig is null;
            Rig = rig ?? new FakeRigctld(PacketDialHz, "USB", 2400);
            Node = dedicated ? null : node ?? new FakeLinBpqNode(1, 2);
            Rig.Recorded = Events.Enqueue;
            if (Node is not null)
            {
                Node.Recorded = Events.Enqueue;
            }
            if (dir is null)
            {
                _ownDir = new TempDirectory();
                dir = _ownDir.Path;
            }
            Dir = dir;
            Clock = new FakeTimeProvider(start);
            RigTime = new RigClock(start);
            var settings = bpq ?? Node?.Settings();
            Config = new ReceiverConfig
            {
                Audio = "plughw:CARD=Device,DEV=0",
                StateDirectory = Dir,
                Daylight = null,
                Rig = new RigSettings { Rigctld = Rig.Endpoint.ToString(), DedicatedRadio = dedicated },
                Bpq = settings is null ? null : adjust?.Invoke(settings) ?? settings,
                Hooks = hooks,
            };
            Config.Validate();
            var slots = schedule ?? Config.Schedule;
            // The hooks' lines go in with the rig's and LinBPQ's events too, to see the order.
            Hooks = hooks is null ? null : new SlotHooks(() => Config, Clock, line =>
            {
                Log.Enqueue(line);
                Events.Enqueue(line);
            });
            Retuner = new Retuner(Config, () => slots, onRadio ?? (() => true), Clock, Log.Enqueue, RigTime, Hooks);
            Retuner.Waiting += d => Waits.Writer.TryWrite(d);
            Retuner.Rig.Changed += RigChanges.Enqueue;
        }

        public FakeRigctld Rig { get; }

        public FakeLinBpqNode? Node { get; }

        public string Dir { get; }

        public FakeTimeProvider Clock { get; }

        public RigClock RigTime { get; }

        public ReceiverConfig Config { get; }

        public Retuner Retuner { get; }

        public SlotHooks? Hooks { get; }

        public ConcurrentQueue<string> Log { get; } = new();

        /// <summary>Every snapshot rig control has raised <see cref="RigControl.Changed"/> with.</summary>
        public ConcurrentQueue<RigState> RigChanges { get; } = new();

        /// <summary>The set commands to the rig and every node command, in the order they arrived.</summary>
        public ConcurrentQueue<string> Events { get; } = new();

        public Channel<TimeSpan> Waits { get; } = Channel.CreateUnbounded<TimeSpan>();

        public string NotePath => InterlockFile.PathIn(Dir);

        public void Start() => _run = Retuner.RunAsync(_stop.Token);

        /// <summary>
        /// Waits until the retuner is waiting on the clock, and returns for how long; again without
        /// moving the clock, the same wait. The bound is only so a broken build fails rather than hangs.
        /// </summary>
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
                throw new TimeoutException($"the retuner set no timer; at {Clock.GetUtcNow():HH:mm:ss}, it said: {string.Join(" | ", Log)}; events: {string.Join(" | ", Events)}");
            }
        }

        /// <summary>After something that ends the retuner's wait early (LinBPQ's session ending): its next wait.</summary>
        public async Task<TimeSpan> Resync()
        {
            _pending = null;
            return await NextWait();
        }

        /// <summary>Lets the retuner's next wait run out, and waits for the one after it to be set.</summary>
        public async Task Step()
        {
            var wait = await NextWait();
            _pending = null;
            Clock.Advance(wait);
        }

        /// <summary>
        /// Steps until <paramref name="done"/>, looked at only while the retuner is waiting, or
        /// fails after <paramref name="most"/> steps.
        /// </summary>
        public async Task StepUntil(Func<bool> done, string what, int most = 60)
        {
            for (int i = 0; i < most; i++)
            {
                await NextWait();
                if (done())
                {
                    return;
                }
                await Step();
            }
            Assert.True(done(), $"{what}; at {Clock.GetUtcNow():HH:mm:ss}: {Retuner.State}; rig {Retuner.Rig.Snapshot()}; said {string.Join(" | ", Log.TakeLast(8))}");
        }

        /// <summary>Steps until the clock reads <paramref name="at"/> or later, and the retuner is waiting again.</summary>
        public async Task RunTo(DateTimeOffset at)
        {
            while (Clock.GetUtcNow() < at)
            {
                await Step();
            }
            await NextWait();
        }

        /// <summary>
        /// Lets rig control's loop poll (or reconnect) until a <see cref="RigControl.Changed"/>
        /// snapshot raised since, or the state after a poll, satisfies <paramref name="done"/>:
        /// each round moves its clock past any nap and waits for it to set the next one, so the
        /// poll has run.
        /// </summary>
        public async Task RigUntil(Func<RigState, bool> done, string what, int most = 20)
        {
            int seen = RigChanges.Count;
            bool Done() => RigChanges.Skip(seen).Any(done) || done(Retuner.Rig.Snapshot());
            for (int i = 0; i < most; i++)
            {
                if (Done())
                {
                    return;
                }
                while (RigTime.Timers.Reader.TryRead(out _))
                {
                }
                RigTime.Advance(TimeSpan.FromSeconds(30));
                await RigTime.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            }
            Assert.True(Done(), what);
        }

        public int Index(string evt) => Events.ToList().IndexOf(evt);

        /// <summary>Where the first event starting with <paramref name="start"/> is, or -1.</summary>
        public int IndexStarting(string start) => Events.ToList().FindIndex(e => e.StartsWith(start, StringComparison.Ordinal));

        public int LastIndex(string evt) => Events.ToList().LastIndexOf(evt);

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            if (_run is not null)
            {
                await _run;
                _run = null;
            }
            await Retuner.DisposeAsync();
            Hooks?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            // Nothing logged ever carries the sysop password.
            Assert.DoesNotContain(Log, l => l.Contains(FakeLinBpqNode.Password, StringComparison.Ordinal));
            if (_ownFakes)
            {
                await Rig.DisposeAsync();
            }
            _ownDir?.Dispose();
        }
    }

    /// <summary>Starts at 11:58:30 and steps through 11:59 (LinBPQ held off) and the 15 s drain, to the rig tuned for 12:00.</summary>
    private static async Task<Station> TunedForNoon(Station station)
    {
        station.Start();
        await station.Step();
        await station.Step();
        await station.NextWait();
        Assert.Equal(RetuneStage.Tuned, station.Retuner.Stage);
        return station;
    }

    private static DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 10, 5, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public async Task NormalSlot_HoldsLinBpqOffDrainsTunesListensRestoresThenLetsLinBpqGo()
    {
        await using var s = new Station(At(11, 58, 30));
        s.Start();
        await s.NextWait();
        Assert.Equal(RetuneStage.Idle, s.Retuner.Stage);
        Assert.Contains("idle until 11:59 UTC, ready for the 12:00 UTC slot", s.Retuner.State, StringComparison.Ordinal);
        Assert.Contains(s.Log, l => l == "retune: LinBPQ port 2 is \"HF through QtSoundModem 2\"");
        Assert.Equal(0, s.Node!.XmitOff(2));

        // 11:59: LinBPQ's port 2 is off and the note says so, but the rig waits out the drain.
        Assert.Equal(TimeSpan.FromSeconds(30), await s.NextWait());
        await s.Step();
        Assert.Equal(TimeSpan.FromSeconds(15), await s.NextWait());
        Assert.Equal(RetuneStage.HoldingTransmitOff, s.Retuner.Stage);
        Assert.Equal(1, s.Node.XmitOff(2));
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Equal("transmitOff", InterlockFile.Read(s.NotePath, 2)!.Stage);

        // 11:59:15: PTT reads off, so the rig goes to the bulletin frequency.
        await s.Step();
        Assert.Equal(TimeSpan.FromSeconds(5), await s.NextWait());
        Assert.Equal(RetuneStage.Tuned, s.Retuner.Stage);
        Assert.Equal(BulletinDialHz, s.Rig.DialHz);
        Assert.Equal("USB", s.Rig.Mode);
        Assert.Equal("tuned", InterlockFile.Read(s.NotePath, 2)!.Stage);
        Assert.Contains("tuned to 7.052 MHz USB for the 12:00 UTC slot until 12:12 UTC; LinBPQ port 2 transmit off", s.Retuner.State, StringComparison.Ordinal);

        // Through the slot it stays there, LinBPQ checked every 5 s.
        await s.RunTo(At(12, 11));
        Assert.Equal(BulletinDialHz, s.Rig.DialHz);
        Assert.Equal(1, s.Node.XmitOff(2));
        Assert.True(s.Node.Commands.Count(c => c == "XMITOFF 2") > 100);

        // 12:12: the rig goes back, and only then LinBPQ transmits again.
        await s.RunTo(At(12, 12));
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Equal(0, s.Node.XmitOff(2));
        Assert.Equal(RetuneStage.Idle, s.Retuner.Stage);
        Assert.False(File.Exists(s.NotePath));
        Assert.False(s.Retuner.HoldingLinBpq);
        Assert.False(s.Retuner.Node!.Connected, "the session is let go between slots");

        int off = s.Index("bpq: XMITOFF 2 1");
        int tuned = s.Index($"rig: F {BulletinDialHz}");
        int restored = s.Index($"rig: F {PacketDialHz}");
        int on = s.Index("bpq: XMITOFF 2 0");
        Assert.True(off >= 0 && off < tuned && tuned < restored && restored < on, string.Join(" | ", s.Events));
        Assert.Single(s.Events, e => e == "bpq: XMITOFF 2 1");
        Assert.Single(s.Events, e => e == "bpq: XMITOFF 2 0");
        Assert.Null(s.Retuner.LastProblem);
        Assert.Contains("ready for the 13:00 UTC slot", s.Retuner.State, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no port")]
    [InlineData("not sysop")]
    [InlineData("wrong password")]
    [InlineData("unreachable")]
    [InlineData("other port id")]
    public async Task XmitOffNotConfirmed_TheRigIsNeverTuned(string why)
    {
        await using var node = why == "no port" ? new FakeLinBpqNode(1) : new FakeLinBpqNode(1, 2);
        node.Sysop = why != "not sysop";
        node.Accepting = why != "unreachable";
        await using var station = new Station(At(11, 58, 30), node: node,
            bpq: why == "wrong password" ? node.Settings(password: "not-it") : null,
            adjust: why == "other port id" ? b => b with { ExpectedPortId = "HF through Direwolf" } : null);
        station.Start();

        await station.RunTo(At(12, 13));

        Assert.Empty(station.Rig.Sets);
        Assert.Equal(PacketDialHz, station.Rig.DialHz);
        Assert.Equal(RetuneStage.Idle, station.Retuner.Stage);
        Assert.False(File.Exists(station.NotePath));
        Assert.DoesNotContain("XMITOFF 2 1", node.Commands);
        Assert.Contains(station.Log, l => l.Contains("not retuning", StringComparison.Ordinal));
        string expected = why switch
        {
            "no port" => "lists no port 2",
            "not sysop" => "sysop status",
            "wrong password" => "refused the password",
            "other port id" => "LinBPQ port 2 is \"HF through QtSoundModem 2\", not \"HF through Direwolf\"",
            _ => "closed the connection",
        };
        Assert.Contains(expected, station.Retuner.LastProblem!.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XmitOffAnsweredWithNonsense_CountsAsTakenAndIsUndoneAfterwards()
    {
        await using var s = new Station(At(11, 58, 30));
        s.Node!.GarbleAfter = "XMITOFF 2 1";
        s.Start();
        await s.Step();
        await s.NextWait();

        // LinBPQ did take it, but said something else: not tuned, and owed back.
        Assert.Equal(1, s.Node.XmitOff(2));
        Assert.True(s.Retuner.HoldingLinBpq);
        Assert.True(File.Exists(s.NotePath));
        Assert.Empty(s.Rig.Sets);
        Assert.Contains("neither yes nor no", s.Retuner.LastProblem!.Value.Text, StringComparison.Ordinal);

        // The next try finds it off, as its own, and goes ahead; afterwards it is turned on.
        await s.StepUntil(() => s.Retuner.Stage == RetuneStage.Tuned, "tuned once LinBPQ is seen to be off");
        await s.RunTo(At(12, 12, 30));
        Assert.Equal(0, s.Node.XmitOff(2));
        Assert.Contains("XMITOFF 2 0", s.Node.Commands);
        Assert.False(File.Exists(s.NotePath));
    }

    [Fact]
    public async Task PortAlreadyOffBeforeTheSlot_IsLeftOffAfterwards()
    {
        await using var s = new Station(At(11, 58, 30));
        s.Node!.SetXmitOff(2, 1);
        await TunedForNoon(s);

        Assert.True(InterlockFile.Read(s.NotePath, 2)!.SysopHeld);
        Assert.DoesNotContain("XMITOFF 2 1", s.Node.Commands);
        Assert.Contains(s.Log, l => l.Contains("was already off before the 12:00 UTC slot", StringComparison.Ordinal));

        await s.RunTo(At(12, 12, 30));
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Equal(1, s.Node.XmitOff(2));
        Assert.DoesNotContain("XMITOFF 2 0", s.Node.Commands);
        Assert.Contains(s.Log, l => l.Contains("so it is left off", StringComparison.Ordinal));
        Assert.False(File.Exists(s.NotePath));
        Assert.Equal(RetuneStage.Idle, s.Retuner.Stage);
    }

    [Fact]
    public async Task RigctldDownAtTheSlot_LinBpqIsNeverHeldOff()
    {
        await using var s = new Station(At(11, 58, 30));
        s.Rig.Accepting = false;
        s.Start();
        await s.RunTo(At(12, 13));

        Assert.DoesNotContain(s.Node!.Commands, c => c.StartsWith("XMITOFF 2 ", StringComparison.Ordinal));
        Assert.Equal(0, s.Node.XmitOff(2));
        Assert.False(s.Retuner.HoldingLinBpq);
        Assert.False(File.Exists(s.NotePath));
        Assert.Contains("is not connected", s.Retuner.LastProblem!.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RigctldGoneBeforeTheTune_LinBpqGoesBackOnWithoutIt()
    {
        await using var s = new Station(At(11, 58, 30));
        s.Start();
        await s.Step();
        await s.NextWait();
        Assert.Equal(1, s.Node!.XmitOff(2));

        // rigctld goes away during the drain: PTT cannot be read, so the rig is never tuned...
        s.Rig.Accepting = false;
        s.Rig.Kill();
        await s.RunTo(At(12, 13));
        Assert.Empty(s.Rig.Sets);
        Assert.Contains("cannot check that the radio is not transmitting", s.Retuner.LastProblem!.Value.Text, StringComparison.Ordinal);

        // ...and as it was never moved, LinBPQ is let go without waiting for rigctld.
        Assert.Equal(0, s.Node.XmitOff(2));
        Assert.False(File.Exists(s.NotePath));
    }

    [Fact]
    public async Task StillTransmittingAfterTheDrain_TunesOnlyOncePttIsOff()
    {
        await using var s = new Station(At(11, 58, 30));
        s.Start();
        await s.Step();
        s.Rig.Ptt = true;
        await s.Step();
        await s.NextWait();

        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Equal(RetuneStage.HoldingTransmitOff, s.Retuner.Stage);
        Assert.Contains("still transmitting", s.Retuner.LastProblem!.Value.Text, StringComparison.Ordinal);
        for (int i = 0; i < 3; i++)
        {
            await s.Step();
        }
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Single(s.Log, l => l.Contains("still transmitting", StringComparison.Ordinal));

        s.Rig.Ptt = false;
        await s.StepUntil(() => s.Retuner.Stage == RetuneStage.Tuned, "tuned once PTT is off");
        Assert.Equal(BulletinDialHz, s.Rig.DialHz);
    }

    [Fact]
    public async Task KeyedDuringTheSlot_TheRigGoesBackAtOnceAndLinBpqIsLetGo()
    {
        await using var s = await TunedForNoon(new Station(At(11, 58, 30)));
        await s.RunTo(At(12, 4));

        // The TNC sends a frame it had held back for a busy channel.
        s.Rig.Ptt = true;
        await s.Step();
        await s.NextWait();

        Assert.Equal(At(12, 4, 5), s.Clock.GetUtcNow());
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Single(s.Log, l => l.Contains("transmitting while tuned to the bulletin frequency", StringComparison.Ordinal));
        Assert.True(s.Index($"rig: F {PacketDialHz}") < s.Index("bpq: XMITOFF 2 0"), string.Join(" | ", s.Events));
        Assert.Equal(0, s.Node!.XmitOff(2));

        // Not taken again for the rest of the slot.
        s.Rig.Ptt = false;
        await s.RunTo(At(12, 20));
        Assert.Single(s.Events, e => e == $"rig: F {BulletinDialHz}");
        Assert.Single(s.Log, l => l.Contains("transmitting while tuned to the bulletin frequency", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongestDrain_StillTunesBeforeTheSlotStarts()
    {
        await using var s = new Station(At(11, 58, 30), adjust: b => b with { DrainSeconds = BpqNodeSettings.MostDrainSeconds });
        s.Start();
        await s.StepUntil(() => s.Retuner.Stage == RetuneStage.Tuned, "tuned");

        Assert.True(s.Clock.GetUtcNow() < Noon, $"tuned at {s.Clock.GetUtcNow():HH:mm:ss}");
        Assert.Equal(At(11, 59, 40), s.Clock.GetUtcNow());
    }

    [Fact]
    public async Task RigCannotBePutBack_LinBpqStaysOffAndTheRestoreIsRetried()
    {
        await using var s = await TunedForNoon(new Station(At(11, 58, 30)));
        await s.RunTo(At(12, 11));

        // The rig stops taking a new dial just before the slot ends.
        s.Rig.RefusesFrequency = true;
        await s.RunTo(At(12, 12));
        for (int i = 0; i < 3; i++)
        {
            await s.Step();
        }

        Assert.Equal(BulletinDialHz, s.Rig.DialHz);
        Assert.Equal(1, s.Node!.XmitOff(2));
        Assert.DoesNotContain("XMITOFF 2 0", s.Node.Commands);
        Assert.Equal(RetuneStage.Restoring, s.Retuner.Stage);
        Assert.True(s.Retuner.HoldingLinBpq);
        Assert.True(File.Exists(s.NotePath));
        Assert.Contains(s.Log, l => l.Contains("retune: WARNING - LinBPQ port 2 stays transmit-off: the rig has not been put back to 7.048000 MHz USB", StringComparison.Ordinal));
        Assert.Single(s.Log, l => l.Contains("stays transmit-off", StringComparison.Ordinal));

        // Rig control tries the restore again at each of its polls.
        int tries = s.Rig.Commands.Count(c => c.Command == $"F {PacketDialHz}");
        await s.RigUntil(_ => s.Rig.Commands.Count(c => c.Command == $"F {PacketDialHz}") >= tries + 2, "two more tries at the restore", most: 10);
        Assert.Equal(1, s.Node.XmitOff(2));

        // Once the rig takes it, the rig goes back, and then LinBPQ transmits again.
        s.Rig.RefusesFrequency = false;
        await s.RigUntil(state => state.RestoreOwed is null && state.Window is null && state.Tuning?.DialHz == PacketDialHz, "the rig put back");
        await s.StepUntil(() => s.Node.XmitOff(2) == 0, "LinBPQ's transmit back on once the rig is back");
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.True(s.LastIndex($"rig: F {PacketDialHz}") < s.Index("bpq: XMITOFF 2 0"));
        Assert.False(File.Exists(s.NotePath));
    }

    [Fact]
    public async Task RigNotBackWhereItWas_LinBpqStaysOffUntilItIs()
    {
        await using var s = await TunedForNoon(new Station(At(11, 58, 30)));
        await s.RunTo(At(12, 11));

        // The rig takes the restore's dial but lands somewhere else.
        s.Rig.StuckDialHz = 7_050_000;
        await s.RunTo(At(12, 12));
        for (int i = 0; i < 3; i++)
        {
            await s.Step();
        }

        Assert.Equal(7_050_000, s.Rig.DialHz);
        Assert.Null(s.Retuner.Rig.Snapshot().RestoreOwed);
        Assert.Equal(1, s.Node!.XmitOff(2));
        Assert.Equal(RetuneStage.Restoring, s.Retuner.Stage);
        Assert.Contains("the rig reads 7.050000 MHz USB", s.Retuner.State, StringComparison.Ordinal);
        Assert.Contains("not 7.048000 MHz USB (2400 Hz passband) where it was before the slot", s.Retuner.State, StringComparison.Ordinal);

        // Put right by hand: once rig control reads it there, LinBPQ is let go.
        s.Rig.StuckDialHz = null;
        s.Rig.DialHz = PacketDialHz;
        await s.RigUntil(state => state.Tuning?.DialHz == PacketDialHz, "the rig read on the packet frequency");
        await s.StepUntil(() => s.Node.XmitOff(2) == 0, "LinBPQ's transmit back on");
    }

    [Fact]
    public async Task NodeIdlesOutMidSlot_LogsInAgainAtOnceAndCarriesOn()
    {
        await using var s = await TunedForNoon(new Station(At(11, 58, 30)));
        await s.RunTo(At(12, 3));
        int logins = s.Node!.Connections;

        s.Node.IdleOut();
        await s.Resync();

        Assert.Equal(At(12, 3), s.Clock.GetUtcNow());
        Assert.Contains(s.Log, l => l.Contains("LinBPQ ended the node session", StringComparison.Ordinal));
        Assert.Equal(logins + 1, s.Node.Connections);
        Assert.Equal(RetuneStage.Tuned, s.Retuner.Stage);
        Assert.Equal(1, s.Node.XmitOff(2));

        await s.RunTo(At(12, 12));
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Equal(0, s.Node.XmitOff(2));
    }

    [Fact]
    public async Task NodeSessionDropsMidSlot_LogsInAgainAtOnceAndCarriesOn()
    {
        await using var s = await TunedForNoon(new Station(At(11, 58, 30)));
        await s.RunTo(At(12, 3));
        int logins = s.Node!.Connections;

        s.Node.Kill();
        await s.Resync();

        Assert.Equal(At(12, 3), s.Clock.GetUtcNow());
        Assert.Equal(RetuneStage.Tuned, s.Retuner.Stage);
        Assert.Equal(BulletinDialHz, s.Rig.DialHz);
        Assert.Equal(1, s.Node.XmitOff(2));
        Assert.Equal(logins + 1, s.Node.Connections);

        await s.RunTo(At(12, 12));
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Equal(0, s.Node.XmitOff(2));
    }

    [Fact]
    public async Task LinBpqRestartedMidSlot_ItsPortIsTurnedOffAgainAtOnce()
    {
        await using var s = await TunedForNoon(new Station(At(11, 58, 30)));
        await s.RunTo(At(12, 3));

        // A LinBPQ restart closes the session and clears XMITOFF.
        s.Node!.Restart();
        await s.Resync();

        Assert.Equal(At(12, 3), s.Clock.GetUtcNow());
        Assert.Equal(1, s.Node.XmitOff(2));
        Assert.Equal(2, s.Node.Commands.Count(c => c == "XMITOFF 2 1"));
        Assert.Equal(RetuneStage.Tuned, s.Retuner.Stage);
        Assert.Contains("on again", s.Retuner.LastProblem!.Value.Text, StringComparison.Ordinal);
        Assert.Contains(s.Log, l => l.Contains("was LinBPQ restarted?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NodeGoneMidSlot_TheRigGoesBackAtOnceAndLinBpqIsTurnedOnWhenItAnswers()
    {
        await using var s = await TunedForNoon(new Station(At(11, 58, 30)));
        await s.RunTo(At(12, 3));

        s.Node!.Accepting = false;
        s.Node.Kill();
        await s.Resync();

        // It cannot see LinBPQ holding off, so the rig is put back there and then...
        Assert.Equal(At(12, 3), s.Clock.GetUtcNow());
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Contains(s.Log, l => l.Contains("cannot confirm that LinBPQ still holds port 2's transmit off, so the rig goes back now", StringComparison.Ordinal));
        // ...and LinBPQ, unreachable, is still owed its transmit back.
        Assert.Equal(RetuneStage.HoldingTransmitOff, s.Retuner.Stage);
        Assert.True(File.Exists(s.NotePath));

        s.Node.Accepting = true;
        await s.StepUntil(() => s.Node.XmitOff(2) == 0, "LinBPQ's transmit back on once it answers");
        Assert.False(File.Exists(s.NotePath));

        // Not retuned again for the rest of this slot.
        await s.RunTo(At(12, 30));
        Assert.Single(s.Events, e => e == $"rig: F {BulletinDialHz}");
        Assert.Contains("ready for the 13:00 UTC slot", s.Retuner.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XmitOffAnswerLost_LinBpqIsTurnedBackOnAfterwardsEvenWithRigctldAway()
    {
        await using var s = new Station(At(11, 58, 30));
        // LinBPQ takes the XMITOFF, the answer never comes, and then it is gone for the slot.
        s.Node!.DropAfter = "XMITOFF 2 1";
        s.Node.RefuseAfterDrop = true;
        s.Start();
        await s.Step();
        await s.NextWait();
        Assert.Equal(1, s.Node.XmitOff(2));
        Assert.True(s.Retuner.HoldingLinBpq, "it may have been taken, so it is owed back");
        await s.RunTo(At(12, 13));
        Assert.Empty(s.Rig.Sets);

        // The rig was never moved, so rigctld being away is no reason to keep LinBPQ off.
        s.Rig.Accepting = false;
        s.Rig.Kill();
        s.Node.Accepting = true;
        await s.StepUntil(() => s.Node.XmitOff(2) == 0, "LinBPQ's transmit back on");
        Assert.False(File.Exists(s.NotePath));
        Assert.Empty(s.Rig.Sets);
    }

    [Fact]
    public async Task ReceiverRestartedMidSlot_PutsTheRigBackBeforeLinBpqTransmits()
    {
        await using var rig = new FakeRigctld(PacketDialHz, "USB", 2400);
        await using var node = new FakeLinBpqNode(1, 2);
        using var dir = new TempDirectory();

        // The first receiver gets as far as tuning for 12:00. What it left on disk then is what a
        // receiver killed at that moment leaves.
        string[] left;
        await using (var first = await TunedForNoon(new Station(At(11, 58, 30), rig: rig, node: node, dir: dir.Path)))
        {
            Assert.True(File.Exists(first.NotePath));
            left = [first.NotePath, Path.Combine(dir.Path, RigRestoreFile.NameFor(rig.Endpoint))];
            foreach (string file in left)
            {
                File.Copy(file, file + ".saved");
            }
        }
        foreach (string file in left)
        {
            File.Move(file + ".saved", file, overwrite: true);
        }
        rig.DialHz = BulletinDialHz;
        node.SetXmitOff(2, 1);

        // The next start, a minute later, in the same slot.
        await using var s = new Station(At(12, 0, 30), rig: rig, node: node, dir: dir.Path);
        s.Start();
        await s.StepUntil(() => s.Events.Contains("bpq: XMITOFF 2 0"), "LinBPQ's transmit back on after the restart");

        Assert.Contains(s.Log, l => l.Contains("retune: WARNING - the receiver stopped during the 12:00 UTC slot on 2026-10-05 last time, holding LinBPQ's port 2 transmit-off", StringComparison.Ordinal)
            && l.Contains("the safe way round", StringComparison.Ordinal));
        int restored = s.Index($"rig: F {PacketDialHz}");
        int on = s.Index("bpq: XMITOFF 2 0");
        Assert.True(restored >= 0 && restored < on, string.Join(" | ", s.Events));

        // Then, still inside the slot, it takes the radio again for the rest of it.
        await s.StepUntil(() => s.Retuner.Stage == RetuneStage.Tuned, "retuned for the rest of the slot");
        Assert.True(on < s.Index("bpq: XMITOFF 2 1") && s.Index("bpq: XMITOFF 2 1") < s.Index($"rig: F {BulletinDialHz}"));
    }

    [Fact]
    public async Task ReceiverRestarted_WithRigctldAway_KeepsLinBpqOffUntilTheRigIsBack()
    {
        await using var rig = new FakeRigctld(BulletinDialHz, "USB", 2400);
        await using var node = new FakeLinBpqNode(1, 2);
        using var dir = new TempDirectory();
        InterlockFile.Write(InterlockFile.PathIn(dir.Path), new InterlockFile.Note("tuned", $"127.0.0.1:{node.Port}", 2, Noon, Noon));
        RigRestoreFile.Write(Path.Combine(dir.Path, RigRestoreFile.NameFor(rig.Endpoint)), rig.Endpoint, new RigTuning(PacketDialHz, "USB", 2400));
        node.SetXmitOff(2, 1);
        rig.Accepting = false;

        await using var s = new Station(At(12, 30), rig: rig, node: node, dir: dir.Path);
        s.Start();
        for (int i = 0; i < 10; i++)
        {
            await s.Step();
        }
        Assert.Equal(1, node.XmitOff(2));
        Assert.DoesNotContain("XMITOFF 2 0", node.Commands);
        Assert.Equal(RetuneStage.Restoring, s.Retuner.Stage);

        rig.Accepting = true;
        await s.RigUntil(state => state.Connected && state.RestoreOwed is null, "rigctld back and the rig restored");
        await s.StepUntil(() => node.XmitOff(2) == 0, "LinBPQ's transmit back on once the rig is back");
        Assert.Equal(PacketDialHz, rig.DialHz);
        Assert.False(File.Exists(s.NotePath));
    }

    [Fact]
    public async Task StoppedMidSlot_PutsTheRigBackAndLinBpqOn()
    {
        var s = await TunedForNoon(new Station(At(11, 58, 30)));
        await s.RunTo(At(12, 5));

        await s.StopAsync();

        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Equal(0, s.Node!.XmitOff(2));
        Assert.False(File.Exists(s.NotePath));
        Assert.True(s.Index($"rig: F {PacketDialHz}") < s.Index("bpq: XMITOFF 2 0"));
        await s.DisposeAsync();
    }

    [Fact]
    public async Task DedicatedRadio_TunesAndRestoresWithoutLinBpq()
    {
        await using var s = new Station(At(11, 58, 30), dedicated: true);
        s.Start();
        await s.Step();
        await s.NextWait();

        Assert.Equal(RetuneStage.Tuned, s.Retuner.Stage);
        Assert.Equal(BulletinDialHz, s.Rig.DialHz);
        Assert.False(File.Exists(s.NotePath));

        await s.RunTo(At(12, 12));
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Equal(RetuneStage.Idle, s.Retuner.Stage);
    }

    [Fact]
    public async Task SlotsCloserThanAWindow_KeepTheRadioBetweenThem()
    {
        await using var s = new Station(At(11, 58, 30), schedule: new SlotSchedule(new TimeOnly(0, 0), 10));
        await TunedForNoon(s);

        await s.RunTo(At(12, 15));

        Assert.Equal(BulletinDialHz, s.Rig.DialHz);
        Assert.Equal(1, s.Node!.XmitOff(2));
        Assert.Single(s.Events, e => e == "bpq: XMITOFF 2 1");
        Assert.Single(s.Events, e => e == $"rig: F {BulletinDialHz}");
        Assert.Contains(s.Log, l => l.Contains("the 12:10 UTC slot follows straight on, so the rig stays tuned until 12:22 UTC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoSlotRuns_TheRadioIsNeverBorrowed()
    {
        // Daylight hours no day of the year has.
        var never = new SlotSchedule(new TimeOnly(0, 0), 60, new DaylightRule("IO91lk", 600, 600));
        await using var s = new Station(At(11, 58, 30), schedule: never);
        s.Start();
        await s.RunTo(At(12, 15));

        Assert.Empty(s.Rig.Sets);
        Assert.DoesNotContain(s.Node!.Commands, c => c.StartsWith("XMITOFF 2 ", StringComparison.Ordinal));
        Assert.Contains("no slot runs", s.Retuner.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudioNotFromTheRadio_NeverRetunes()
    {
        await using var s = new Station(At(11, 58, 30), onRadio: () => false);
        s.Start();
        await s.RunTo(At(12, 15));

        Assert.Empty(s.Rig.Sets);
        Assert.DoesNotContain(s.Node!.Commands, c => c.StartsWith("XMITOFF 2 ", StringComparison.Ordinal));
        Assert.Contains("not retuning", s.Retuner.State, StringComparison.Ordinal);
    }

    [Fact]
    public void LeftOverNote_WithoutRigInTheConfig_SaysToTurnLinBpqOnByHand()
    {
        using var dir = new TempDirectory();
        InterlockFile.Write(InterlockFile.PathIn(dir.Path), new InterlockFile.Note("tuned", "127.0.0.1:8010", 2, Noon, Noon));
        var log = new List<string>();

        Retuner.SayIfLeftOver(dir.Path, log.Add);

        Assert.Contains(log, l => l.Contains("port 2 transmit-off", StringComparison.Ordinal) && l.Contains("XMITOFF <port> 0", StringComparison.Ordinal));
        Assert.False(File.Exists(InterlockFile.PathIn(dir.Path)));
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task NormalSlot_AgainstARealLinBpq()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bpq = await LinBpqContainer.StartNodeAsync("bpq32-node.cfg", limit.Token);
        var settings = new BpqNodeSettings { Host = "127.0.0.1", Port = bpq.TelnetPort, User = "sysop", Password = "s3cret-sysop", HfPort = 2, ExpectedPortId = "HF through QtSoundModem" };
        await using var look = new BpqNode(settings);

        await using var s = await TunedForNoon(new Station(At(11, 58, 30), bpq: settings));
        Assert.Equal(BulletinDialHz, s.Rig.DialHz);
        Assert.True(await look.TransmitOffAsync(2, limit.Token));
        look.Drop();

        await s.RunTo(At(12, 12));
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.False(await look.TransmitOffAsync(2, limit.Token));
        Assert.Equal(RetuneStage.Idle, s.Retuner.Stage);
        Assert.False(File.Exists(s.NotePath));
        Assert.Contains(s.Log, l => l == "retune: LinBPQ port 2 is \"HF through QtSoundModem\"");
    }
}
