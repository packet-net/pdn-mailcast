using Mailcast.Receiver.Retune;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// Issue #49, for a shared rig: ending a slot's window early gives the rig back and releases
/// LinBPQ through the normal end-of-window path, so the "after" hook still runs, in the same
/// order as a window that closes in the usual way.
/// </summary>
public sealed partial class RetunerTests
{
    [Fact]
    public async Task EndsEarly_GivesBackRigAndReleasesLinBpq_ThenRunsAfterHook_InOrder()
    {
        using var scripts = new TempDirectory();
        string record = Path.Combine(scripts.Path, "runs");
        bool ready = false;
        await using var s = new Station(At(11, 57, 30), hooks: Scripts(scripts.Path, record), earlyEnd: _ => ready ? "the slot is done with" : null);
        s.Start();
        await s.StepUntil(() => s.Retuner.Stage == RetuneStage.Tuned, "tuned for 12:00");
        Assert.Single(HookScript.Runs(record)); // "before" has run; "after" has not yet

        ready = true;
        await s.Step();
        await s.NextWait();

        Assert.Equal(["before 2026-10-05T12:00:00Z 7052.0 7053.8 none", "after 2026-10-05T12:00:00Z 7052.0 7053.8 1"], HookScript.Runs(record));
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Equal(0, s.Node!.XmitOff(2));
        int restored = s.Index($"rig: F {PacketDialHz}");
        int on = s.Index("bpq: XMITOFF 2 0");
        int after = s.IndexStarting("hooks: running \"after\"");
        Assert.True(restored >= 0 && restored < on && on < after, string.Join(" | ", s.Events));
        Assert.Contains(s.Log, l => l.Contains("ending the 12:00 UTC slot early", StringComparison.Ordinal) && l.Contains("the slot is done with", StringComparison.Ordinal));
        Assert.False(File.Exists(s.Hooks!.NotePath));
        Assert.Null(s.Retuner.LastProblem);
    }

    [Fact]
    public async Task NothingHeard_GivesBackRigAndReleasesLinBpq_AtThreeAndAHalfMinutes_ThenRunsAfterHook()
    {
        // The receiver's own check, on the retuner's clock: its audio listening since well before the slot.
        using var scripts = new TempDirectory();
        using var stateDir = new TempDirectory();
        string record = Path.Combine(scripts.Path, "runs");
        ReceiverHost? host = null;
        await using var s = new Station(At(11, 57, 30), hooks: Scripts(scripts.Path, record), earlyEnd: slot => host?.EarlyEndReason(slot));
        host = new ReceiverHost(new ReceiverConfig { Audio = Samples.SoundCard, StateDirectory = stateDir.Path, Stations = StationFixture.EverySlot }, s.Clock, _ => { })
        {
            ListeningSince = At(11, 0),
        };
        // Counted from when this retuner put the rig on the bulletin frequency, as the host does with its own.
        host.RadioTunedSince = () => s.Retuner.TunedSince;
        try
        {
            s.Start();
            await s.StepUntil(() => s.Retuner.Stage == RetuneStage.Tuned, "tuned for 12:00");

            await s.RunTo(At(12, 3, 25));
            Assert.Equal(RetuneStage.Tuned, s.Retuner.Stage);
            Assert.True(s.Retuner.TunedSince <= At(12, 0));

            await s.RunTo(At(12, 4, 30));

            Assert.Equal(["before 2026-10-05T12:00:00Z 7052.0 7053.8 none", "after 2026-10-05T12:00:00Z 7052.0 7053.8 1"], HookScript.Runs(record));
            Assert.Equal(PacketDialHz, s.Rig.DialHz);
            Assert.Equal(0, s.Node!.XmitOff(2));
            Assert.Contains("retune: ending the 12:00 UTC slot early: nothing heard from GB7RDG by 12:03:30 (no tone, probe or frame)", s.Log);
            Assert.Null(s.Retuner.TunedSince);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task NeverReady_RunsToTheUsualClose_Unaffected()
    {
        // earlyEnd never fires: the slot closes exactly as it would without issue #49.
        using var scripts = new TempDirectory();
        string record = Path.Combine(scripts.Path, "runs");
        await using var s = new Station(At(11, 57, 30), hooks: Scripts(scripts.Path, record), earlyEnd: _ => null);
        s.Start();
        await s.StepUntil(() => s.Retuner.Stage == RetuneStage.Tuned, "tuned for 12:00");

        await s.RunTo(At(12, 13));

        Assert.Equal(["before 2026-10-05T12:00:00Z 7052.0 7053.8 none", "after 2026-10-05T12:00:00Z 7052.0 7053.8 1"], HookScript.Runs(record));
        Assert.DoesNotContain(s.Log, l => l.Contains("ending the 12:00 UTC slot early", StringComparison.Ordinal));
    }
}
