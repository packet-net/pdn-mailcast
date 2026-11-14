using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Flex;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Status;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// A slot skipped for a reason at the station that may clear used to be tried again at
/// <c>slot.retryMinutes</c> (10:05 for the 10:00 slot), after every receiver had given the window back
/// (3 min 30 s with nothing heard), and filled to only 2 min 49 s at M0LTE. Now, as for the 10 MHz
/// reference (<see cref="ReferenceHoldRetryTests"/>), what often clears within seconds (the clock, the
/// Flex API, the KISS port, the lease, the rig's tuning) is tried again in place every 15 s up to 2
/// minutes in, while it can still be heard; what does not (a hot PA, a modem that will not take the
/// waveform) rolls straight on to the next slot. Nothing is ever tried at 10:05.
/// </summary>
public class SkipRetryInPlaceTests
{
    private static readonly DateTimeOffset Ten = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    // M0LTE's slot timing: hourly, catch-up and retry 5 minutes, the 10 s tone and 2 minute clear-channel wait left as they are.
    private const string M0lteLike = """
        {"callsign": "GB7RDG", "station": {"apiKey": "k"},
         "slot": {"minute": 0, "everyMinutes": 60, "catchUpMinutes": 5, "retryMinutes": 5}}
        """;

    private sealed class Head : IDisposable
    {
        private readonly TempDirectory _dir = new();

        public Head(SlotSettings? settings = null, Func<TimeSpan, double?>? pa = null, string? config = null)
        {
            Time = new VirtualTime(Ten);
            Config = HeadEndConfig.Parse(config ?? M0lteLike);
            var options = Config.ToScheduleOptions();
            Store = new RotationStore(_dir.Path, Compression.Default, options, Journal, Time);
            for (int i = 0; i < 5; i++)
            {
                Store.Offer(Bulletins.Make(i, 3000), DateOnly.FromDateTime(Ten.UtcDateTime));
            }
            Station = new FakeStation(Time, 4);
            Settings = settings ?? new SlotSettings { Callsign = "GB7RDG", SubChannel = 4 };
            Flex = pa is null && settings?.WhenFlexUnreachable != FlexUnreachablePolicy.Skip ? null : new FakeFlex(Time, pa ?? (_ => 40.0), new ReferenceReading("external 10 MHz, locked", true));
            var planner = new StoreSlotPlanner(Store, Compression.Default, options, Config.ToWaveforms(_ => FakeAirtime.For(Station)), Config.ToSlotSettings(), Config.FillLimitAfter, Time);
            var runner = new SlotRunner(Settings, Station, Station, Flex, FakeAirtime.For(Station), Journal, Time, Clock);
            Schedule = Config.ToSlotSchedule();
            Service = new HeadEndService(Schedule, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), planner, Store, [], runner, new StatusStore(null, Time), Journal, Time);
        }

        public VirtualTime Time { get; }

        public HeadEndConfig Config { get; }

        public SlotSettings Settings { get; }

        public FakeStation Station { get; }

        public FakeFlex? Flex { get; }

        public FakeClockSync Clock { get; } = new();

        public RotationStore Store { get; }

        public SlotSchedule Schedule { get; }

        public HeadEndService Service { get; }

        public MemoryJournal Journal { get; } = new();

        public IEnumerable<string> Lines(string containing) => Journal.Lines.Where(l => l.Contains(containing, StringComparison.Ordinal));

        public SlotReport Run(DateTimeOffset slot) => Time.Run(() => Service.RunSlotAsync(slot, CancellationToken.None));

        public uint[] NextEsis() => [.. Store.InRotation(Ten).Select(b => b.NextEsi)];

        public DateTimeOffset Next(SlotReport report) => HeadEndService.NextSlot(Time.GetUtcNow(), Schedule, TimeSpan.FromMinutes(5), report, TimeSpan.FromMinutes(5));

        public void Dispose() => _dir.Dispose();
    }

    [Theory]
    [InlineData(SkipCause.Clock, true)]
    [InlineData(SkipCause.Flex, true)]
    [InlineData(SkipCause.Reference, true)]
    [InlineData(SkipCause.KissPort, true)]
    [InlineData(SkipCause.Lease, true)]
    [InlineData(SkipCause.Tune, true)]
    [InlineData(SkipCause.HotPa, false)]
    [InlineData(SkipCause.Waveform, false)]
    [InlineData(SkipCause.None, false)]
    public void EachReason_IsTriedAgainInPlace_OrRolledOn(SkipCause cause, bool inPlace) =>
        Assert.Equal(inPlace, HeadEndService.RetriedInPlace(cause));

    [Fact]
    public void AClockNotYetSynchronised_StartsTheSlotOnceItIs_InsideTheReceiversWindow()
    {
        using var head = new Head();
        uint[] before = head.NextEsis();
        // chrony steps the clock 40 s after the hour.
        head.Clock.SynchronisedIf = () => head.Time.GetUtcNow() >= Ten.AddSeconds(40);

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal(Ten.AddSeconds(45), report.Start, TimeSpan.FromSeconds(2));
        Assert.True(head.NextEsis().Zip(before).Any(p => p.First > p.Second), "no bulletin piece was sent");
        // Said once, not once for each try that found the same.
        Assert.Single(head.Lines("trying again every 15 s until 10:02:00Z"));
        Assert.Contains(head.Journal.Lines, l => l.StartsWith("slot 2026-11-14 10:00Z: not sent: the system clock is not known to be synchronised", StringComparison.Ordinal));
        Assert.Empty(head.Lines("trying again at"));
        Assert.Equal(Ten.AddHours(1), head.Next(report));
    }

    [Fact]
    public void ALeaseRefusedOnce_IsTakenAt15Seconds_AndTheSlotGoes()
    {
        using var head = new Head();
        head.Station.RefuseFirstLease = true;

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal(Ten.AddSeconds(15), report.Start, TimeSpan.FromSeconds(2));
        Assert.Equal([false, true], head.Station.LeaseRequests.Select(r => r.Granted).Take(2));
        Assert.Contains("slot 2026-11-14 10:00Z: not sent: the station refused the transmit lease: sub-channel 2 holds the transmit lease until later; trying again every 15 s until 10:02:00Z", head.Journal.Lines);
    }

    [Fact]
    public void ARigThatCannotBeTuned_IsAskedAgainInPlace()
    {
        using var head = new Head();
        // rigctld refuses the first try and both of its 2 s retries, then is back.
        head.Station.TuneRefusal = "HTTP 500: rigctld: connection refused";
        head.Station.TuneRefusalsLeft = 3;

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.InRange(report.Start, Ten.AddSeconds(15), Ten.AddSeconds(25));
    }

    [Fact]
    public void AFlexThatComesBack_IsUsed_WhenTheConfigurationRequiresIt()
    {
        using var head = new Head(new SlotSettings { Callsign = "GB7RDG", SubChannel = 4, WhenFlexUnreachable = FlexUnreachablePolicy.Skip });
        // The Flex is back 70 s after the hour.
        head.Flex!.ReachableIf = () => head.Time.GetUtcNow() >= Ten.AddSeconds(70);

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal(Ten.AddSeconds(75), report.Start, TimeSpan.FromSeconds(2));
        Assert.Equal(6, head.Flex.Connects);
    }

    [Fact]
    public void AKissPortThatStaysDown_RollsOnAtTwoMinutes_WithNothingCounted_AndNoRetryAtFivePast()
    {
        using var head = new Head();
        uint[] before = head.NextEsis();
        head.Station.KissUnreachableUntil = Ten.AddHours(1);

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.False(report.Retryable);
        Assert.Equal(before, head.NextEsis());
        Assert.Empty(head.Station.LeaseRequests);
        // The slot's own try and one every 15 s to 10:02.
        Assert.Equal(9, head.Station.KissConnects);
        Assert.True(head.Time.GetUtcNow() <= Ten + HeadEndService.HoldLatestStart, $"gave up at {head.Time.GetUtcNow():HH:mm:ss}");
        Assert.Equal(Ten.AddHours(1), head.Next(report));
        Assert.Equal(
            [
                "slot 2026-11-14 10:00Z: not sent: cannot reach the station's KISS port: connection refused; trying again every 15 s until 10:02:00Z",
                "slot 2026-11-14 10:00Z: not sent: cannot reach the station's KISS port: connection refused, still at 10:02:00Z, too late for receivers to hear the slot; everything rolls on to the next slot, 2026-11-14 11:00Z",
            ],
            head.Lines("KISS port"));
        Assert.Empty(head.Lines("skipped:"));
    }

    [Fact]
    public void AHeldSlotHeldNextForSomethingElse_SaysSo_AndKeepsTrying()
    {
        using var head = new Head();
        // The KISS port is back at 10:00:20, but the lease is refused then; taken at the try after.
        head.Station.KissUnreachableUntil = Ten.AddSeconds(20);
        head.Station.RefuseFirstLease = true;

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal(Ten.AddSeconds(45), report.Start, TimeSpan.FromSeconds(2));
        Assert.Single(head.Lines("KISS port: connection refused; trying again every 15 s"));
        Assert.Single(head.Lines("refused the transmit lease: sub-channel 2 holds the transmit lease until later; trying again every 15 s"));
    }

    [Fact]
    public void AHotPa_RollsStraightOnToTheNextSlot()
    {
        using var head = new Head(pa: _ => 75.0);

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.False(report.Retryable);
        Assert.Equal(1, head.Flex!.Connects);
        Assert.Equal(Ten, head.Time.GetUtcNow(), TimeSpan.FromSeconds(1));
        Assert.Equal(Ten.AddHours(1), head.Next(report));
        Assert.Equal(["slot 2026-11-14 10:00Z: not sent: the PA is already at 75.0 C, over the 70.0 C limit; the next slot is 2026-11-14 11:00Z"], head.Lines("the PA is already"));
    }

    [Fact]
    public void AModemThatWillNotTakeTheWaveform_RollsStraightOnToTheNextSlot()
    {
        // The broadcast modem is configured on WN4, and the slot goes on WN3 long: a SETHW the modem never confirms.
        using var head = new Head(config: """
            {"callsign": "GB7RDG", "station": {"apiKey": "k"},
             "slot": {"minute": 0, "everyMinutes": 60, "catchUpMinutes": 5, "retryMinutes": 5},
             "schedule": {"modes": ["ms110d-wn3-long"]}}
            """);
        head.Station.AppliesSetHardware = false;

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.Equal(SkipCause.Waveform, report.Cause);
        Assert.False(report.Retryable);
        // Tried once (and put back on a fresh link), not again every 15 s.
        Assert.Equal(2, head.Station.KissConnects);
        Assert.Empty(head.Station.LeaseRequests);
        Assert.True(head.Time.GetUtcNow() < Ten.AddSeconds(15 + 60), $"done at {head.Time.GetUtcNow():HH:mm:ss}");
        Assert.Equal(Ten.AddHours(1), head.Next(report));
        Assert.Single(head.Lines("; the next slot is 2026-11-14 11:00Z"));
    }

    [Fact]
    public void AHoldSeenTooLateToBeHeard_IsNotTriedAgain()
    {
        // The head end came up at 10:03 and caught the slot up, to find the lease refused: no time left, and no retry at 10:05.
        using var head = new Head();
        head.Station.RefuseAfterRequests = 0;
        head.Time.StepWallClock(TimeSpan.FromMinutes(3));

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.False(report.Retryable);
        Assert.Single(head.Station.LeaseRequests);
        Assert.Equal(Ten.AddHours(1), head.Next(report));
        Assert.Contains("slot 2026-11-14 10:00Z: not sent: the station refused the transmit lease: sub-channel 2 holds the transmit lease until later; the next slot is 2026-11-14 11:00Z", head.Journal.Lines);
    }
}
