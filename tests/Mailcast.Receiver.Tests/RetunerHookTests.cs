using System.Runtime.Versioning;
using Mailcast.Receiver.Hooks;
using Mailcast.Receiver.Retune;
using Packet.SoundModem.Rig;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The hooks with a retuned radio: "before" ahead of LinBPQ being held off and the rig tuned, and
/// "after" once the rig is back and LinBPQ released, on the same fake clocks as the rest.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed partial class RetunerTests
{
    /// <summary>Both hooks as scripts noting each run in <paramref name="record"/>; "before" has a 60 s timeout.</summary>
    private static HooksSettings Scripts(string dir, string record, int beforeExit = 0) => new()
    {
        Before = HookScript.Write(dir, "before.sh", record, beforeExit),
        After = HookScript.Write(dir, "after.sh", record),
    };

    [Fact]
    public async Task Hooks_BeforeRunsAheadOfTheInterlock_AndAfterOnceTheRigIsBackAndLinBpqReleased()
    {
        using var scripts = new TempDirectory();
        string record = Path.Combine(scripts.Path, "runs");
        await using var s = new Station(At(11, 57, 30), hooks: Scripts(scripts.Path, record));
        s.Start();

        // A 60 s "before" starts a minute ahead of the usual 11:59, so it is done by then.
        Assert.Equal(TimeSpan.FromSeconds(30), await s.NextWait());
        Assert.Contains("idle until 11:58 UTC, ready for the 12:00 UTC slot", s.Retuner.State, StringComparison.Ordinal);
        await s.Step();
        Assert.Equal(TimeSpan.FromSeconds(60), await s.NextWait());
        Assert.Equal(["before 2026-10-05T12:00:00Z 7052.0 7053.8 none"], HookScript.Runs(record));
        Assert.Equal(0, s.Node!.XmitOff(2));
        Assert.Empty(s.Rig.Sets);
        Assert.Contains("retuning at 11:59 UTC for the 12:00 UTC slot", s.Retuner.State, StringComparison.Ordinal);

        await s.StepUntil(() => s.Retuner.Stage == RetuneStage.Tuned, "tuned for 12:00");
        Assert.Single(HookScript.Runs(record));
        await s.RunTo(At(12, 13));

        Assert.Equal(["before 2026-10-05T12:00:00Z 7052.0 7053.8 none", "after 2026-10-05T12:00:00Z 7052.0 7053.8 1"], HookScript.Runs(record));
        int before = s.IndexStarting("hooks: running \"before\"");
        int off = s.Index("bpq: XMITOFF 2 1");
        int tuned = s.Index($"rig: F {BulletinDialHz}");
        int restored = s.Index($"rig: F {PacketDialHz}");
        int on = s.Index("bpq: XMITOFF 2 0");
        int after = s.IndexStarting("hooks: running \"after\"");
        Assert.True(before >= 0 && before < off && off < tuned && tuned < restored && restored < on && on < after, string.Join(" | ", s.Events));
        Assert.False(File.Exists(s.Hooks!.NotePath));
        Assert.Null(s.Retuner.LastProblem);
    }

    [Fact]
    public async Task Hooks_BeforeFails_LinBpqIsNotHeldOffNorTheRigTuned_AndAfterRunsAtTheClose()
    {
        using var scripts = new TempDirectory();
        string record = Path.Combine(scripts.Path, "runs");
        await using var s = new Station(At(11, 57, 30), hooks: Scripts(scripts.Path, record, beforeExit: 1));
        s.Start();

        await s.RunTo(At(12, 11));
        Assert.DoesNotContain("XMITOFF 2 1", s.Node!.Commands);
        Assert.Empty(s.Rig.Sets);
        Assert.Single(HookScript.Runs(record));
        Assert.Contains("not retuning for the 12:00 UTC slot: the \"before\" command failed", s.Retuner.State, StringComparison.Ordinal);
        Assert.Single(s.Log, l => l.Contains("WARNING", StringComparison.Ordinal));
        Assert.Contains(s.Log, l => l.StartsWith("hooks: WARNING - \"before\" for the 12:00 UTC slot exited with 1", StringComparison.Ordinal)
            && l.Contains("the radio is not retuned and LinBPQ is not held off", StringComparison.Ordinal));

        await s.RunTo(At(12, 13));
        Assert.Equal("after 2026-10-05T12:00:00Z 7052.0 7053.8 0", HookScript.Runs(record)[^1]);
        Assert.Equal(PacketDialHz, s.Rig.DialHz);
        Assert.Equal(0, s.Node.XmitOff(2));
        Assert.False(File.Exists(s.Hooks!.NotePath));
    }

    [Fact]
    public async Task Hooks_StoppedMidSlot_AfterRunsOnceTheRigIsBackAndLinBpqOn()
    {
        using var scripts = new TempDirectory();
        string record = Path.Combine(scripts.Path, "runs");
        var s = new Station(At(11, 57, 30), hooks: Scripts(scripts.Path, record));
        s.Start();
        await s.StepUntil(() => s.Retuner.Stage == RetuneStage.Tuned, "tuned for 12:00");
        await s.RunTo(At(12, 5));

        await s.StopAsync();

        Assert.Equal("after 2026-10-05T12:00:00Z 7052.0 7053.8 1", HookScript.Runs(record)[^1]);
        int restored = s.Index($"rig: F {PacketDialHz}");
        int on = s.Index("bpq: XMITOFF 2 0");
        int after = s.IndexStarting("hooks: running \"after\" for the 12:00 UTC slot as the receiver stops");
        Assert.True(restored >= 0 && restored < on && on < after, string.Join(" | ", s.Events));
        Assert.False(File.Exists(s.Hooks!.NotePath));
        await s.DisposeAsync();
    }

    [Fact]
    public async Task Hooks_RestartedMidSlot_AfterRunsOnceTheRigIsBack_AndThatSlotIsLeftAlone()
    {
        await using var rig = new FakeRigctld(PacketDialHz, "USB", 2400);
        await using var node = new FakeLinBpqNode(1, 2);
        using var dir = new TempDirectory();
        using var scripts = new TempDirectory();
        string record = Path.Combine(scripts.Path, "runs");

        // The first receiver gets as far as tuning for 12:00; what it left on disk then is what a
        // receiver killed at that moment leaves.
        string[] left;
        await using (var first = new Station(At(11, 57, 30), rig: rig, node: node, dir: dir.Path, hooks: Scripts(scripts.Path, record)))
        {
            first.Start();
            await first.StepUntil(() => first.Retuner.Stage == RetuneStage.Tuned, "tuned for 12:00");
            left = [first.NotePath, Path.Combine(dir.Path, RigRestoreFile.NameFor(rig.Endpoint)), first.Hooks!.NotePath];
            foreach (string file in left)
            {
                File.Copy(file, file + ".saved");
            }
        }
        foreach (string file in left)
        {
            File.Move(file + ".saved", file, overwrite: true);
        }
        File.Delete(record);
        rig.DialHz = BulletinDialHz;
        node.SetXmitOff(2, 1);

        // The next start, in the same slot.
        await using var s = new Station(At(12, 0, 30), rig: rig, node: node, dir: dir.Path, hooks: Scripts(scripts.Path, record));
        s.Start();
        await s.StepUntil(() => HookScript.Runs(record).Length > 0, "\"after\" run after the restart");

        Assert.Equal(["after 2026-10-05T12:00:00Z 7052.0 7053.8 1"], HookScript.Runs(record));
        int restored = s.Index($"rig: F {PacketDialHz}");
        int on = s.Index("bpq: XMITOFF 2 0");
        int after = s.IndexStarting("hooks: running \"after\" for the 12:00 UTC slot");
        Assert.True(restored >= 0 && restored < on && on < after, string.Join(" | ", s.Events));

        // The rest of the 12:00 slot is left alone: no "before", no retune, until 13:00's.
        await s.RunTo(At(12, 57));
        Assert.Single(HookScript.Runs(record));
        Assert.DoesNotContain("bpq: XMITOFF 2 1", s.Events);
        Assert.Contains("ready for the 13:00 UTC slot", s.Retuner.State, StringComparison.Ordinal);
        Assert.False(File.Exists(s.Hooks!.NotePath));
    }
}
