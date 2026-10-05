using Xunit.Abstractions;
using Mailcast.HeadEnd.Flex;
using Mailcast.HeadEnd.Slot;

namespace Mailcast.HeadEnd.Tests;

public class SlotRunnerTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static readonly ReferenceReading Gps = new("GPSDO locked", true);

    private sealed class Rig
    {
        public Rig(SlotSettings? settings = null, Func<TimeSpan, double?>? pa = null, bool flexReachable = true, bool withFlex = true)
        {
            Time = new VirtualTime(Noon);
            Station = new FakeStation(Time, 4);
            Settings = settings ?? new SlotSettings { SubChannel = 4 };
            Flex = withFlex ? new FakeFlex(Time, pa ?? (_ => 40.0), Gps, flexReachable) : null;
            Runner = new SlotRunner(Settings, Station, Station, Flex, FakeAirtime.For(Station), Journal, Time);
        }

        public VirtualTime Time { get; }

        public FakeStation Station { get; }

        public SlotSettings Settings { get; }

        public FakeFlex? Flex { get; }

        public MemoryJournal Journal { get; } = new();

        public SlotRunner Runner { get; }

        public List<int> Progress { get; } = [];

        public SlotReport Run(int frames) =>
            Time.Run(() => Runner.RunAsync(Day, [.. Enumerable.Range(0, frames).Select(i => FakeAirtime.Frame(i))], 10, CancellationToken.None, Progress.Add));
    }

    private static void AssertNothingKeyedOutsideTheLease(FakeStation station)
    {
        Assert.All(station.Keyups, k => Assert.True(k.InsideLease, $"{k.What} from {k.Start:HH:mm:ss} to {k.End:HH:mm:ss} was not inside the lease"));
        Assert.All(station.Frames, f => Assert.True(f.LeaseHeld, $"a frame was queued at {f.At:HH:mm:ss} without the lease"));
    }

    [Fact]
    public void Run_TakesTheLeaseFirst_RenewsIt_SendsEverything_AndReleasesIt()
    {
        var rig = new Rig();
        var report = rig.Run(60);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal(60, report.FramesSent);
        Assert.Equal(60, report.FramesQueued);
        Assert.True(report.ToneSent);
        Assert.Equal("GPS locked (GPSDO locked)", report.Reference);

        var firstLease = rig.Station.LeaseRequests[0];
        Assert.True(firstLease.Granted);
        Assert.All(rig.Station.Keyups, k => Assert.True(k.Start >= firstLease.At));
        Assert.Equal("tone 1800 Hz", rig.Station.Keyups[0].What);
        Assert.Equal(TimeSpan.FromSeconds(30), rig.Station.Keyups[0].End - rig.Station.Keyups[0].Start);

        // Renewed every 30 s for the length of the slot.
        TimeSpan length = report.End - report.Start;
        Assert.True(report.LeaseRenewals >= (int)(length.TotalSeconds / 30) - 1, $"{report.LeaseRenewals} renewals in {length}");
        Assert.All(rig.Station.LeaseRequests, r => Assert.True(r.Granted));

        // Released once, after the last keyup.
        Assert.Single(rig.Station.Releases);
        Assert.True(rig.Station.Releases[0] >= rig.Station.Keyups[^1].End);
        Assert.False(rig.Station.LeaseHeld);
        AssertNothingKeyedOutsideTheLease(rig.Station);
    }

    [Fact]
    public void Run_QueuesOneBurstAtATime_SizedToTheBurstLimit()
    {
        var rig = new Rig();
        var report = rig.Run(30);

        // 7.25 s a frame and 1.2 s a burst: seven frames is 52 s, eight would pass 58 s.
        var bursts = rig.Station.Keyups.Skip(1).ToList();
        Assert.Equal(["burst of 7", "burst of 7", "burst of 7", "burst of 7", "burst of 2"], bursts.Select(b => b.What));
        Assert.Equal(5, report.Bursts);

        // Every frame of a burst is queued together, and only once the previous burst is acknowledged.
        var queuedAt = rig.Station.Frames.Select(f => f.At).Distinct().ToList();
        Assert.Equal(5, queuedAt.Count);
        for (int i = 1; i < bursts.Count; i++)
        {
            Assert.True(queuedAt[i] >= bursts[i - 1].End, $"burst {i + 1} was queued before burst {i} had gone");
        }
        Assert.Equal([7, 14, 21, 28, 30], rig.Progress);
    }

    [Fact]
    public void Run_FramesPerBurstOverridesTheModel()
    {
        var rig = new Rig(new SlotSettings { SubChannel = 4, FramesPerBurst = 4 });
        rig.Run(10);
        Assert.Equal(["burst of 4", "burst of 4", "burst of 2"], rig.Station.Keyups.Skip(1).Select(b => b.What));
    }

    [Fact]
    public void Run_AbortsCleanlyWhenARenewalIsRefused()
    {
        var rig = new Rig();
        rig.Station.RefuseAfterRequests = 4;
        var report = rig.Run(100);

        Assert.Equal(SlotOutcome.Aborted, report.Outcome);
        Assert.StartsWith("lease renewal failed", report.Reason, StringComparison.Ordinal);
        Assert.True(report.FramesQueued < 100);
        Assert.Equal(report.FramesQueued, report.FramesSent);

        // Nothing queued after the refusal, and what was on the air finished inside the lease.
        DateTimeOffset refused = rig.Station.LeaseRequests.First(r => !r.Granted).At;
        Assert.All(rig.Station.Frames, f => Assert.True(f.At < refused));
        AssertNothingKeyedOutsideTheLease(rig.Station);
        Assert.Single(rig.Station.Releases);
        Assert.Contains(rig.Journal.Lines, l => l.Contains("ABORTED", StringComparison.Ordinal) && l.Contains("roll to tomorrow", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_StopsWhenThePaGetsTooHot()
    {
        // 40 C, rising to 75 C four minutes in.
        var rig = new Rig(pa: t => t < TimeSpan.FromMinutes(4) ? 40 : 75);
        var report = rig.Run(100);

        Assert.Equal(SlotOutcome.Aborted, report.Outcome);
        Assert.Contains("PA temperature 75.0 C passed the 70.0 C limit", report.Reason, StringComparison.Ordinal);
        Assert.Equal(75, report.PaTemperatureMaxC);
        DateTimeOffset hot = Noon + TimeSpan.FromMinutes(4);
        // The burst on the air when it got hot finishes; nothing is queued after it.
        Assert.All(rig.Station.Frames, f => Assert.True(f.At < hot + TimeSpan.FromSeconds(65)));
        Assert.True(report.FramesSent < 100);
        AssertNothingKeyedOutsideTheLease(rig.Station);
        Assert.Single(rig.Station.Releases);
    }

    [Fact]
    public void Run_WillNotStartWithAHotPa()
    {
        var rig = new Rig(pa: _ => 72);
        var report = rig.Run(10);
        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.Empty(rig.Station.LeaseRequests);
        Assert.Empty(rig.Station.Keyups);
    }

    [Fact]
    public void Run_HasAHardStopAtTheMaximumLength()
    {
        var rig = new Rig(new SlotSettings { SubChannel = 4, MaxSlotLength = TimeSpan.FromMinutes(5) });
        var report = rig.Run(200);

        Assert.Equal(SlotOutcome.Aborted, report.Outcome);
        Assert.Contains("maximum slot length", report.Reason, StringComparison.Ordinal);
        Assert.All(rig.Station.Keyups, k => Assert.True(k.End <= Noon + TimeSpan.FromMinutes(5), $"{k.What} ran to {k.End:HH:mm:ss}"));
        Assert.True(report.FramesSent > 0);
        AssertNothingKeyedOutsideTheLease(rig.Station);
    }

    [Fact]
    public void Run_RetriesTheToneWhileTheChannelIsBusy()
    {
        var rig = new Rig();
        rig.Station.BusyTones = 1;
        var report = rig.Run(5);
        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.True(report.ToneSent);
        Assert.Contains(rig.Journal.Lines, l => l.Contains("channel busy, no tone yet", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_GoesAheadWithoutTheToneWhenTheChannelStaysBusy()
    {
        var rig = new Rig();
        rig.Station.BusyTones = -1;
        var report = rig.Run(5);
        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.False(report.ToneSent);
        Assert.All(rig.Station.Keyups, k => Assert.StartsWith("burst", k.What, StringComparison.Ordinal));
        Assert.True(rig.Station.Frames[0].At >= Noon + TimeSpan.FromMinutes(2));
        AssertNothingKeyedOutsideTheLease(rig.Station);
    }

    [Fact]
    public void Run_SkipsWhenTheChannelStaysBusyAndTheConfigurationSaysSo()
    {
        var rig = new Rig(new SlotSettings { SubChannel = 4, WhenStillBusy = BusyPolicy.Skip });
        rig.Station.BusyTones = -1;
        var report = rig.Run(5);
        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.Empty(rig.Station.Keyups);
        Assert.Empty(rig.Station.Frames);
        Assert.Single(rig.Station.Releases);
        Assert.Equal(0, report.FramesQueued);
    }

    [Fact]
    public void Run_CarriesOnWithoutAToneTheStationWillNotSend()
    {
        var rig = new Rig();
        rig.Station.ToneAvailable = false;
        var report = rig.Run(5);
        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.False(report.ToneSent);
    }

    [Fact]
    public void Run_SkipsWhenTheLeaseIsRefused_WithoutKeying()
    {
        var rig = new Rig();
        rig.Station.RefuseFirstLease = true;
        var report = rig.Run(5);
        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.Contains("refused the transmit lease", report.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Station.Keyups);
        Assert.Empty(rig.Station.Frames);
        Assert.Empty(rig.Station.Releases);
    }

    [Fact]
    public void Run_SkipsOrCarriesOnWhenTheFlexIsUnreachable_AsConfigured()
    {
        var skip = new Rig(new SlotSettings { SubChannel = 4, WhenFlexUnreachable = FlexUnreachablePolicy.Skip }, flexReachable: false);
        Assert.Equal(SlotOutcome.Skipped, skip.Run(5).Outcome);
        Assert.Empty(skip.Station.LeaseRequests);

        var carryOn = new Rig(flexReachable: false);
        var report = carryOn.Run(5);
        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal("Flex unreachable", report.Reference);
        Assert.Null(report.PaTemperatureMaxC);
    }

    [Fact]
    public void Run_AbortsWhenTheModemStopsAcknowledging()
    {
        var rig = new Rig();
        rig.Station.AckOnlyFirst = 10;
        var report = rig.Run(30);
        Assert.Equal(SlotOutcome.Aborted, report.Outcome);
        Assert.Contains("acknowledged 3 of the 7 frames of burst 2", report.Reason, StringComparison.Ordinal);
        Assert.Equal(10, report.FramesSent);
        Assert.Equal(14, report.FramesQueued);
    }

    [Fact]
    public void Run_WithNothingToSend_KeysNothing()
    {
        var rig = new Rig();
        var report = rig.Run(0);
        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.Empty(rig.Station.LeaseRequests);
    }

    [Fact]
    public void Run_WithoutAFlex_SaysSo()
    {
        var rig = new Rig(withFlex: false);
        var report = rig.Run(3);
        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal("not read (no Flex configured)", report.Reference);
    }

    [Fact]
    public void Run_JournalIsPlainAscii()
    {
        var rig = new Rig();
        rig.Station.BusyTones = 1;
        rig.Run(10);
        foreach (string line in rig.Journal.Lines)
        {
            output.WriteLine(line);
        }
        Assert.All(rig.Journal.Lines, l => Assert.All(l, c => Assert.InRange(c, ' ', '~')));
        Assert.Contains(rig.Journal.Lines, l => l.Contains("done, 12:00:00 to", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_NeverQueuesABurstThatCouldOutliveTheLease()
    {
        // A lease only just long enough: the runner renews before each burst rather than risk it.
        var rig = new Rig(new SlotSettings { SubChannel = 4, LeaseLength = TimeSpan.FromSeconds(75), RenewEvery = TimeSpan.FromSeconds(30) });
        var report = rig.Run(30);
        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        AssertNothingKeyedOutsideTheLease(rig.Station);
    }
}
