using Mailcast.Core;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Status;

namespace Mailcast.HeadEnd.Tests;

public class ServiceTests
{
    private static readonly TimeOnly Noon = new(12, 0);
    private static readonly DateOnly Day1 = new(2026, 10, 5);

    [Fact]
    public void NextSlot_IsTodayUntilItHasRunOrIsTooLate()
    {
        var catchUp = TimeSpan.FromMinutes(30);
        var morning = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(morning, Noon, catchUp, null));

        var late = new DateTimeOffset(2026, 10, 5, 12, 20, 0, TimeSpan.Zero);
        Assert.Equal(late, HeadEndService.NextSlot(late, Noon, catchUp, null));

        var tooLate = new DateTimeOffset(2026, 10, 5, 12, 31, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(tooLate, Noon, catchUp, null));

        var ran = new SlotReport { Day = Day1, Outcome = SlotOutcome.Completed };
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(late, Noon, catchUp, ran));

        var stopped = ran with { Outcome = SlotOutcome.Aborted, Reason = "the head end is stopping" };
        Assert.Equal(late, HeadEndService.NextSlot(late, Noon, catchUp, stopped));

        // A clock that has gone back behind a day already run never runs an earlier day.
        var ahead = ran with { Day = Day1.AddDays(3) };
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(late, Noon, catchUp, ahead));

        var yesterday = ran with { Day = Day1.AddDays(-1) };
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(morning, Noon, catchUp, yesterday));
    }

    private sealed class Head
    {
        public Head(TempDirectory dir, DateOnly day, SlotSettings settings, IReadOnlyList<ScheduledIntake>? intakes = null)
        {
            Time = new VirtualTime(new DateTimeOffset(day.ToDateTime(Noon, DateTimeKind.Utc)));
            Station = new FakeStation(Time, 4);
            var options = new ScheduleOptions();
            Store = new RotationStore(dir.Path, Compression.Default, options, Journal);
            Planner = new StoreSlotPlanner(Store, Compression.Default, options);
            var runner = new SlotRunner(settings, Station, Station, null, FakeAirtime.For(Station), Journal, Time, new FakeClockSync());
            Service = new HeadEndService(Noon, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), Planner, Store, intakes ?? [], runner, new StatusStore(null, Time), Journal, Time);
        }

        public VirtualTime Time { get; }

        public FakeStation Station { get; }

        public RotationStore Store { get; }

        public StoreSlotPlanner Planner { get; }

        public HeadEndService Service { get; }

        public MemoryJournal Journal { get; } = new();

        public DateTimeOffset? WaitForTheSlotAcrossAClockStep(TimeSpan step) => Time.Run(async () =>
        {
            using var stop = new CancellationTokenSource();
            Task service = Service.RunAsync(stop.Token);
            await Task.Delay(TimeSpan.FromSeconds(90), Time);
            Time.StepWallClock(step);
            for (int i = 0; i < 30 && Station.LeaseRequests.Count == 0; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), Time);
            }
            await stop.CancelAsync();
            await service;
            return Station.LeaseRequests.Count > 0 ? Station.LeaseRequests[0].At : (DateTimeOffset?)null;
        });

        public SlotReport Run(DateOnly day, TimeSpan? stopAfter = null) => Time.Run(async () =>
        {
            using var stop = stopAfter is TimeSpan t ? new CancellationTokenSource(t, Time) : new CancellationTokenSource();
            return await Service.RunSlotAsync(day, stop.Token);
        });

        public IEnumerable<(ulong, uint)> Sent() => Station.Frames.Select(f =>
        {
            Assert.True(MailcastFrame.TryParse(f.Frame.AsSpan(16), out var frame));
            return (frame!.ObjectId, frame.EncodingSymbolId);
        });
    }

    [Fact]
    public void ASlotCutShort_RollsItsUnsentFramesToTomorrow_AndNoEsiIsEverSentTwice()
    {
        using var dir = new TempDirectory();
        var settings = new SlotSettings { SubChannel = 4, MaxSlotLength = TimeSpan.FromMinutes(4) };

        var monday = new Head(dir, Day1, settings);
        foreach (int seed in Enumerable.Range(1, 8))
        {
            monday.Store.Offer(Bulletins.Make(seed, 4000), Day1);
        }
        int planned = monday.Planner.Plan(Day1).Frames.Count;
        var first = monday.Run(Day1);
        Assert.Equal(SlotOutcome.Aborted, first.Outcome);
        Assert.InRange(first.FramesQueued, 1, planned - 1);
        var sentMonday = monday.Sent().ToList();

        // Tuesday, after a restart: the rest of Monday's share is owed, on top of Tuesday's own.
        var tuesday = new Head(dir, Day1.AddDays(1), settings with { MaxSlotLength = TimeSpan.FromMinutes(40) });
        var plan = tuesday.Planner.Plan(Day1.AddDays(1));
        var second = tuesday.Run(Day1.AddDays(1));
        Assert.Equal(SlotOutcome.Completed, second.Outcome);
        var sentTuesday = tuesday.Sent().ToList();

        var all = sentMonday.Concat(sentTuesday).Where(f => plan.Broadcast!.Objects.Skip(1).Any(o => o.Transfer.ObjectId == f.Item1)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());

        // Each bulletin's ESIs run on unbroken from 0: nothing was skipped, nothing repeated.
        foreach (var group in all.GroupBy(f => f.Item1))
        {
            Assert.Equal(Enumerable.Range(0, group.Count()).Select(i => (uint)i), group.Select(f => f.Item2).Order());
        }
        var options = new ScheduleOptions();
        foreach (var o in plan.Broadcast!.Objects.Skip(1))
        {
            int[] perDay = BroadcastScheduler.SymbolsPerDay(o.Transfer.SourceSymbols, options);
            Assert.Equal(perDay[0] + perDay[1], (int)o.NextEsi);
        }
    }

    [Fact]
    public void ARestartMidSlot_CarriesOnTheSameDayWithoutRepeatingAnyPiece()
    {
        using var dir = new TempDirectory();
        var settings = new SlotSettings { SubChannel = 4 };
        var first = new Head(dir, Day1, settings);
        foreach (int seed in Enumerable.Range(20, 6))
        {
            first.Store.Offer(Bulletins.Make(seed, 5000), Day1);
        }
        var whole = first.Planner.Plan(Day1);
        var bulletinIds = whole.Broadcast!.Objects.Skip(1).Select(o => o.Transfer.ObjectId).ToHashSet();
        int plannedBulletinFrames = whole.Frames.Count(f => bulletinIds.Contains(f.ObjectId));
        var stopped = first.Run(Day1, stopAfter: TimeSpan.FromMinutes(2));
        Assert.Equal(SlotOutcome.Aborted, stopped.Outcome);
        Assert.Equal("the head end is stopping", stopped.Reason);
        Assert.Single(first.Station.Releases);
        int sentBulletinFrames = first.Sent().Count(f => bulletinIds.Contains(f.Item1));
        Assert.InRange(sentBulletinFrames, 1, plannedBulletinFrames - 1);

        // After the restart the same day's plan holds only the bulletin frames not yet queued, and
        // the directory carries on from where it stopped too.
        var again = new Head(dir, Day1, settings);
        var rest = again.Planner.Plan(Day1);
        Assert.Equal(plannedBulletinFrames - sentBulletinFrames, rest.Frames.Count(f => bulletinIds.Contains(f.ObjectId)));
        Assert.True(rest.Broadcast!.Objects[0].FirstEsi > 0);
        Assert.Equal(SlotOutcome.Completed, again.Run(Day1).Outcome);
        var all = first.Sent().Concat(again.Sent()).ToList();
        Assert.Equal(plannedBulletinFrames, all.Count(f => bulletinIds.Contains(f.Item1)));
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void NextSlot_RetriesASlotSkippedAtTheStation_WithinTheCatchUpWindow()
    {
        var catchUp = TimeSpan.FromMinutes(30);
        var retry = TimeSpan.FromMinutes(5);
        var skipped = new SlotReport { Day = Day1, Outcome = SlotOutcome.Skipped, Retryable = true, End = new DateTimeOffset(2026, 10, 5, 12, 1, 0, TimeSpan.Zero) };
        var at = new DateTimeOffset(2026, 10, 5, 12, 1, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 6, 0, TimeSpan.Zero), HeadEndService.NextSlot(at, Noon, catchUp, skipped, retry));

        var late = skipped with { End = new DateTimeOffset(2026, 10, 5, 12, 27, 0, TimeSpan.Zero) };
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(late.End, Noon, catchUp, late, retry));

        var notRetryable = skipped with { Retryable = false };
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(at, Noon, catchUp, notRetryable, retry));
    }

    private sealed class HangingIntake : IBulletinIntake
    {
        public string Name => "hanging";

        public async Task<IntakeResult> CollectAsync(DateOnly today, CancellationToken cancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return IntakeResult.Nothing;
        }
    }

    [Fact]
    public void ABbsThatDoesNotAnswer_DelaysTheSlotOnlyByThePreSlotLimit()
    {
        using var dir = new TempDirectory();
        var head = new Head(dir, Day1, new SlotSettings { SubChannel = 4 }, [new ScheduledIntake(new HangingIntake(), TimeSpan.FromMinutes(30))]);
        head.Store.Offer(Bulletins.Make(40, 3000), Day1);
        var report = head.Run(Day1);
        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 30, TimeSpan.Zero), head.Station.LeaseRequests[0].At);
        Assert.Contains(head.Journal.Lines, l => l.Contains("not finished within 30 s before the slot", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWaitForTheSlot_FollowsAClockCorrection()
    {
        // Booted at 00:30 by a clock eleven and a half hours slow, corrected 90 s later to 12:02.
        using var dir = new TempDirectory();
        var head = new Head(dir, Day1, new SlotSettings { SubChannel = 4 });
        head.Store.Offer(Bulletins.Make(41, 3000), Day1);
        head.Time.StepWallClock(TimeSpan.FromHours(-11.5));
        DateTimeOffset? started = head.WaitForTheSlotAcrossAClockStep(TimeSpan.FromHours(11.5));
        Assert.NotNull(started);
        Assert.InRange(started.Value, new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 5, 12, 3, 0, TimeSpan.Zero));
    }
}
