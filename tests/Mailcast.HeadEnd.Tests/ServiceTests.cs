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
    private static readonly SlotSchedule Daily = SlotSchedule.Daily(Noon);
    private static readonly DateOnly Day1 = new(2026, 10, 5);

    private static DateTimeOffset NoonOn(DateOnly day) => new(day.ToDateTime(Noon, DateTimeKind.Utc));

    [Fact]
    public void NextSlot_IsTodayUntilItHasRunOrIsTooLate()
    {
        var catchUp = TimeSpan.FromMinutes(30);
        var morning = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(morning, Daily, catchUp, null));

        var late = new DateTimeOffset(2026, 10, 5, 12, 20, 0, TimeSpan.Zero);
        Assert.Equal(late, HeadEndService.NextSlot(late, Daily, catchUp, null));

        var tooLate = new DateTimeOffset(2026, 10, 5, 12, 31, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(tooLate, Daily, catchUp, null));

        var ran = new SlotReport { Day = Day1, Outcome = SlotOutcome.Completed };
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(late, Daily, catchUp, ran));

        var stopped = ran with { Outcome = SlotOutcome.Aborted, Reason = "the head end is stopping" };
        Assert.Equal(late, HeadEndService.NextSlot(late, Daily, catchUp, stopped));

        // A clock that has gone back behind a day already run never runs an earlier day.
        var ahead = ran with { Day = Day1.AddDays(3) };
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(late, Daily, catchUp, ahead));

        var yesterday = ran with { Day = Day1.AddDays(-1) };
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(morning, Daily, catchUp, yesterday));
    }

    private sealed class Head
    {
        public Head(TempDirectory dir, DateOnly day, SlotSettings settings, IReadOnlyList<ScheduledIntake>? intakes = null)
            : this(dir, NoonOn(day), Daily, new ScheduleOptions(), settings, intakes)
        {
        }

        public Head(TempDirectory dir, DateTimeOffset start, SlotSchedule schedule, ScheduleOptions options, SlotSettings settings, IReadOnlyList<ScheduledIntake>? intakes = null, string? statusDirectory = null, TimeSpan? catchUp = null)
        {
            Time = new VirtualTime(start);
            Station = new FakeStation(Time, 4);
            Options = options;
            Status = new StatusStore(statusDirectory, Time);
            Store = new RotationStore(dir.Path, Compression.Default, options, Journal);
            Planner = new StoreSlotPlanner(Store, Compression.Default, options);
            var runner = new SlotRunner(settings, Station, Station, null, FakeAirtime.For(Station), Journal, Time, Clock);
            Service = new HeadEndService(schedule, catchUp ?? TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), Planner, Store, intakes ?? [], runner, Status, Journal, Time);
        }

        public ScheduleOptions Options { get; }

        public FakeClockSync Clock { get; } = new();

        public StatusStore Status { get; }

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

        public SlotReport Run(DateOnly day, TimeSpan? stopAfter = null) => Run(NoonOn(day), stopAfter);

        public SlotReport Run(DateTimeOffset slot, TimeSpan? stopAfter = null) => Time.Run(async () =>
        {
            using var stop = stopAfter is TimeSpan t ? new CancellationTokenSource(t, Time) : new CancellationTokenSource();
            return await Service.RunSlotAsync(slot, stop.Token);
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
        int planned = monday.Planner.Plan(NoonOn(Day1)).Frames.Count;
        var first = monday.Run(Day1);
        Assert.Equal(SlotOutcome.Aborted, first.Outcome);
        Assert.InRange(first.FramesQueued, 1, planned - 1);
        var sentMonday = monday.Sent().ToList();

        // Tuesday, after a restart: the rest of Monday's share is owed, on top of Tuesday's own.
        var tuesday = new Head(dir, Day1.AddDays(1), settings with { MaxSlotLength = TimeSpan.FromMinutes(40) });
        var plan = tuesday.Planner.Plan(NoonOn(Day1.AddDays(1)));
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
            int[] perDay = BroadcastScheduler.SymbolsPerCarrying(o.Transfer.SourceSymbols, options);
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
        var whole = first.Planner.Plan(NoonOn(Day1));
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
        var rest = again.Planner.Plan(NoonOn(Day1));
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
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 6, 0, TimeSpan.Zero), HeadEndService.NextSlot(at, Daily, catchUp, skipped, retry));

        var late = skipped with { End = new DateTimeOffset(2026, 10, 5, 12, 27, 0, TimeSpan.Zero) };
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(late.End, Daily, catchUp, late, retry));

        var notRetryable = skipped with { Retryable = false };
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), HeadEndService.NextSlot(at, Daily, catchUp, notRetryable, retry));
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

    // Hourly: slots on the hour, counted from 00:00.
    private static readonly SlotSchedule Hourly = new(new TimeOnly(0, 0), TimeSpan.FromHours(1));

    private static DateTimeOffset At(int hour, int minute = 0, int second = 0) => new(2026, 10, 5, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void SlotSchedule_FindsTheSlotRunningAtAnyTime()
    {
        Assert.Equal(At(12), Hourly.SlotAtOrBefore(At(12)));
        Assert.Equal(At(12), Hourly.SlotAtOrBefore(At(12, 59, 59)));
        Assert.Equal(At(13), Hourly.SlotAfter(At(12, 0, 1)));
        var halfPast = new SlotSchedule(new TimeOnly(12, 30), TimeSpan.FromHours(1));
        Assert.Equal(At(0, 30), halfPast.SlotAtOrBefore(At(1, 10)));
        Assert.Equal(At(0, 30).AddDays(-1).AddHours(23), halfPast.SlotAtOrBefore(At(0, 10)));
        Assert.Equal(At(12), Daily.SlotAtOrBefore(At(23, 0)));
        Assert.Equal(At(12).AddDays(-1), Daily.SlotAtOrBefore(At(11, 59)));
        Assert.Throws<ArgumentException>(() => new SlotSchedule(new TimeOnly(0, 0), TimeSpan.FromMinutes(50)));
    }

    [Fact]
    public void NextSlot_Hourly_CatchesUpRetriesAndNeverRunsAnEarlierSlot()
    {
        var catchUp = TimeSpan.FromMinutes(5);
        var retry = TimeSpan.FromMinutes(5);
        Assert.Equal(At(12), HeadEndService.NextSlot(At(11, 40), Hourly, catchUp, null));
        Assert.Equal(At(12, 3), HeadEndService.NextSlot(At(12, 3), Hourly, catchUp, null));
        Assert.Equal(At(13), HeadEndService.NextSlot(At(12, 6), Hourly, catchUp, null));

        var ran = new SlotReport { Slot = At(12), Day = Day1, Outcome = SlotOutcome.Completed, End = At(12, 4) };
        Assert.Equal(At(13), HeadEndService.NextSlot(At(12, 4), Hourly, catchUp, ran));
        Assert.Equal(At(13), HeadEndService.NextSlot(At(13), Hourly, catchUp, ran));
        Assert.Equal(At(14, 2), HeadEndService.NextSlot(At(14, 2), Hourly, catchUp, ran));

        // Cut short by a restart: carried on within the catch-up window, not after.
        var stopped = ran with { Outcome = SlotOutcome.Aborted, Reason = "the head end is stopping", End = At(12, 1) };
        Assert.Equal(At(12, 2), HeadEndService.NextSlot(At(12, 2), Hourly, catchUp, stopped));
        Assert.Equal(At(13), HeadEndService.NextSlot(At(12, 6), Hourly, catchUp, stopped));

        // Skipped at the station: tried again five minutes on, while inside the window.
        var skipped = ran with { Outcome = SlotOutcome.Skipped, Retryable = true, End = At(12, 0, 10) };
        Assert.Equal(At(12, 5, 10), HeadEndService.NextSlot(At(12, 0, 10), Hourly, TimeSpan.FromMinutes(10), skipped, retry));
        Assert.Equal(At(13), HeadEndService.NextSlot(At(12, 0, 10), Hourly, catchUp, skipped, retry));

        // The clock has gone back behind the last slot run: the slot after that one, never an earlier.
        var later = ran with { Slot = At(15), End = At(15, 3) };
        Assert.Equal(At(16), HeadEndService.NextSlot(At(12, 30), Hourly, catchUp, later));
        Assert.Equal(At(16), HeadEndService.NextSlot(At(15, 2), Hourly, catchUp, later));

        // A report from before slots had times names only its day, and was that day's slot at the anchor.
        var old = new SlotReport { Day = Day1, Outcome = SlotOutcome.Completed };
        Assert.Equal(At(0), Hourly.SlotOf(old));
        Assert.Equal(At(12), Daily.SlotOf(old));
        Assert.Equal(At(1), HeadEndService.NextSlot(At(0, 30), Hourly, catchUp, old));
    }

    private static Head HourlyHead(TempDirectory dir, DateTimeOffset start, string? statusDirectory = null) =>
        new(dir, start, Hourly, ScheduleOptions.Hourly, new SlotSettings { SubChannel = 4, MaxSlotLength = TimeSpan.FromMinutes(10), ToneLength = TimeSpan.FromSeconds(10) },
            statusDirectory: statusDirectory, catchUp: TimeSpan.FromMinutes(5));

    /// <summary>When each slot took its lease: the first request after a release, not the renewals.</summary>
    private static List<DateTimeOffset> LeaseTimes(Head head)
    {
        var taken = new List<DateTimeOffset>();
        DateTimeOffset? released = null;
        foreach (var (at, granted) in head.Station.LeaseRequests.Where(r => r.Granted))
        {
            if (taken.Count == 0 || (released is DateTimeOffset r && at >= r && taken[^1] < r))
            {
                taken.Add(at);
            }
            released = head.Station.Releases.Where(t => t >= at).Cast<DateTimeOffset?>().FirstOrDefault();
        }
        return taken;
    }

    [Fact]
    public void Hourly_RunsEachSlotWithSomethingDue_AndKeysNothingInTheRest()
    {
        using var dir = new TempDirectory();
        var head = HourlyHead(dir, At(11, 59, 30));
        head.Store.Offer(Bulletins.Make(60, 3000), Day1);
        head.Time.Run(async () =>
        {
            using var stop = new CancellationTokenSource();
            Task service = head.Service.RunAsync(stop.Token);
            await Task.Delay(TimeSpan.FromMinutes(90), head.Time);
            head.Store.Offer(Bulletins.Make(61, 3000), Day1); // taken in at 13:29:30
            await Task.Delay(TimeSpan.FromMinutes(120), head.Time);
            await stop.CancelAsync();
            await service;
            return 0;
        }, TimeSpan.FromHours(5));

        // 12:00 for the first, 14:00 for the second; 13:00 and 15:00 had nothing due.
        var leases = LeaseTimes(head);
        Assert.Contains(leases, t => t >= At(12) && t < At(12, 2));
        Assert.Contains(leases, t => t >= At(14) && t < At(14, 2));
        Assert.DoesNotContain(leases, t => t >= At(13) && t < At(14));
        Assert.DoesNotContain(leases, t => t >= At(15));
        Assert.Contains(head.Journal.Lines, l => l.StartsWith("slot 2026-10-05 13:00Z: skipped: nothing to send", StringComparison.Ordinal));
        Assert.Equal(new SlotsToday(Day1, 4, 2, 0, 2), head.Status.SlotsToday);
        Assert.Contains(head.Journal.Lines, l => l == "slots today (2026-10-05): 4, 2 completed, 0 cut short, 2 skipped");
        Assert.Equal(head.Sent().Count(), head.Sent().Distinct().Count());
    }

    [Fact]
    public void Hourly_AClockSteppedBackAfterASlot_NeverRunsItOrAnEarlierOneAgain()
    {
        using var dir = new TempDirectory();
        var head = HourlyHead(dir, At(11, 59, 30));
        head.Store.Offer(Bulletins.Make(62, 3000), Day1);
        head.Time.Run(async () =>
        {
            using var stop = new CancellationTokenSource();
            Task service = head.Service.RunAsync(stop.Token);
            await Task.Delay(TimeSpan.FromMinutes(20), head.Time);
            Assert.Single(LeaseTimes(head));
            head.Time.StepWallClock(TimeSpan.FromHours(-2)); // 12:19:30 becomes 10:19:30
            head.Store.Offer(Bulletins.Make(63, 3000), Day1);
            await Task.Delay(TimeSpan.FromMinutes(150), head.Time); // to 12:49:30 by the stepped clock
            Assert.Single(LeaseTimes(head));
            await Task.Delay(TimeSpan.FromMinutes(15), head.Time); // past 13:00
            await stop.CancelAsync();
            await service;
            return 0;
        }, TimeSpan.FromHours(5));
        var leases = LeaseTimes(head);
        Assert.Equal(2, leases.Count);
        Assert.InRange(leases[1], At(13), At(13, 2));
        Assert.Contains(head.Journal.Lines, l => l.Contains("next slot 2026-10-05 13:00Z (the clock moved)", StringComparison.Ordinal) || l.Contains("next slot 2026-10-05 13:00Z;", StringComparison.Ordinal));
    }

    [Fact]
    public void Hourly_ARestartMidSlot_CarriesOnInsideTheCatchUpWindow_AndRepeatsNothing()
    {
        using var dir = new TempDirectory();
        using var status = new TempDirectory();
        var first = HourlyHead(dir, At(12), status.Path);
        foreach (int seed in Enumerable.Range(70, 6))
        {
            first.Store.Offer(Bulletins.Make(seed, 5000), Day1);
        }
        var whole = first.Planner.Plan(At(12));
        var stopped = first.Run(At(12), stopAfter: TimeSpan.FromMinutes(1));
        Assert.Equal(SlotOutcome.Aborted, stopped.Outcome);
        Assert.Equal(At(12), stopped.Slot);
        Assert.InRange(first.Sent().Count(), 1, whole.Frames.Count - 1);

        // Back two minutes later: the same slot carries on, then nothing more is due for an hour.
        var again = HourlyHead(dir, At(12, 2), status.Path);
        again.Time.Run(async () =>
        {
            using var stop = new CancellationTokenSource();
            Task service = again.Service.RunAsync(stop.Token);
            await Task.Delay(TimeSpan.FromMinutes(70), again.Time);
            await stop.CancelAsync();
            await service;
            return 0;
        });
        var leases = LeaseTimes(again);
        Assert.Single(leases);
        Assert.InRange(leases[0], At(12, 2), At(12, 3));
        var bulletinIds = whole.Broadcast!.Objects.Skip(1).Select(o => o.Transfer.ObjectId).ToHashSet();
        var all = first.Sent().Concat(again.Sent()).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(whole.Broadcast.BulletinFrames, all.Count(f => bulletinIds.Contains(f.Item1)));
        Assert.Equal(At(13), again.Status.LastSlot!.Slot);
        Assert.Equal(SlotOutcome.Skipped, again.Status.LastSlot.Outcome);
    }

    [Fact]
    public void RunNow_StartsAOneOffSlot_RefusesASecondWhileItRuns_AndTheScheduleCarriesOn()
    {
        using var dir = new TempDirectory();
        var head = HourlyHead(dir, At(11, 59, 30));
        head.Store.Offer(Bulletins.Make(80, 4000), Day1);
        RunNowAnswer? accepted = null;
        RunNowAnswer? busy = null;
        head.Time.Run(async () =>
        {
            using var stop = new CancellationTokenSource();
            Task service = head.Service.RunAsync(stop.Token);
            await Task.Delay(TimeSpan.FromMinutes(25), head.Time); // 12:24:30, the 12:00 slot long done
            head.Store.Offer(Bulletins.Make(81, 4000), Day1);
            accepted = head.Service.RequestRunNow("tester");
            await Task.Delay(TimeSpan.FromSeconds(20), head.Time);
            busy = head.Service.RequestRunNow("someone else");
            await Task.Delay(TimeSpan.FromMinutes(30), head.Time); // 12:55
            head.Store.Offer(Bulletins.Make(82, 4000), Day1);
            await Task.Delay(TimeSpan.FromMinutes(10), head.Time); // past 13:00
            await stop.CancelAsync();
            await service;
            return 0;
        });

        Assert.True(accepted!.Accepted);
        Assert.Equal(At(12, 24), accepted.Slot);
        Assert.False(busy!.Accepted);
        Assert.Contains("a slot is running", busy.Problem, StringComparison.Ordinal);
        var leases = LeaseTimes(head);
        Assert.Equal(3, leases.Count);
        Assert.InRange(leases[1], At(12, 24, 30), At(12, 26));
        Assert.InRange(leases[2], At(13), At(13, 2));
        Assert.Contains(head.Journal.Lines, l => l == "run now: asked for by tester; slot 2026-10-05 12:24Z starts now");
        Assert.Contains(head.Journal.Lines, l => l.StartsWith("run now: asked for by someone else, refused: a slot is running", StringComparison.Ordinal));
        Assert.Contains(head.Journal.Lines, l => l == "slot 2026-10-05 12:24Z: a one-off slot, asked for by tester");
        Assert.Equal(head.Sent().Count(), head.Sent().Distinct().Count());
        Assert.Equal(new SlotsToday(Day1, 3, 3, 0, 0), head.Status.SlotsToday);
        Assert.Null(head.Status.LastSlot!.RequestedBy);
    }

    [Fact]
    public void RunNow_WithNothingDue_SendsTheDirectory_AndAClockThatIsNotSynchronisedStillStopsIt()
    {
        using var dir = new TempDirectory();
        var head = HourlyHead(dir, At(12));
        head.Store.Offer(Bulletins.Make(83, 3000), Day1);
        Assert.Equal(SlotOutcome.Completed, head.Run(At(12)).Outcome);
        int before = head.Station.Frames.Count;

        head.Time.Advance(TimeSpan.FromMinutes(20));
        var oneOff = head.Time.Run(() => head.Service.RunSlotAsync(At(12, 20), CancellationToken.None, "tester"));
        Assert.Equal(SlotOutcome.Completed, oneOff.Outcome);
        Assert.Equal("tester", oneOff.RequestedBy);
        var directoryOnly = head.Station.Frames.Skip(before).ToList();
        Assert.NotEmpty(directoryOnly);
        Assert.Single(head.Sent().Skip(before).Select(f => f.Item1).Distinct());

        head.Clock.Synchronised = false;
        var refused = head.Time.Run(() => head.Service.RunSlotAsync(At(12, 30), CancellationToken.None, "tester"));
        Assert.Equal(SlotOutcome.Skipped, refused.Outcome);
        Assert.Contains("clock", refused.Reason, StringComparison.Ordinal);
        Assert.Equal(before + directoryOnly.Count, head.Station.Frames.Count);
    }

    [Fact]
    public void RunNow_RefusesWhenTheClockIsBehindTheLastSlotRun()
    {
        using var dir = new TempDirectory();
        var head = HourlyHead(dir, At(12));
        head.Status.RecordSlot(new SlotReport { Slot = At(15), Day = Day1, Outcome = SlotOutcome.Completed, End = At(15, 3) });
        var answer = head.Service.RequestRunNow("tester");
        Assert.False(answer.Accepted);
        Assert.Contains("behind the last slot run", answer.Problem, StringComparison.Ordinal);
    }

    // Hourly in daylight at GB7RDG: on 5 October 09:00 to 17:00 UTC, on the 6th likewise.
    private static readonly SlotSchedule Daylight = new(new TimeOnly(0, 0), TimeSpan.FromHours(1), DaylightRule.Gb7rdg);

    private static Head DaylightHead(TempDirectory dir, DateTimeOffset start) =>
        new(dir, start, Daylight, ScheduleOptions.HourlyDaylight with { Timetable = Daylight.Timetable },
            new SlotSettings { SubChannel = 4, MaxSlotLength = TimeSpan.FromMinutes(10), ToneLength = TimeSpan.FromSeconds(10) },
            catchUp: TimeSpan.FromMinutes(5));

    [Fact]
    public void NextSlot_Daylight_PassesOverTheDarkSlots()
    {
        var catchUp = TimeSpan.FromMinutes(5);
        Assert.Equal(At(9), HeadEndService.NextSlot(At(3), Daylight, catchUp, null));
        Assert.Equal(At(9), HeadEndService.NextSlot(At(8, 2), Daylight, catchUp, null));
        Assert.Equal(At(9, 3), HeadEndService.NextSlot(At(9, 3), Daylight, catchUp, null));
        var ran = new SlotReport { Slot = At(17), Day = Day1, Outcome = SlotOutcome.Completed, End = At(17, 3) };
        Assert.Equal(At(9).AddDays(1), HeadEndService.NextSlot(At(17, 4), Daylight, catchUp, ran));
        Assert.Equal(At(9).AddDays(1), HeadEndService.NextSlot(At(23), Daylight, catchUp, ran));
        // A slot on demand at night counts as the last run; the next is still the morning's first.
        var oneOff = ran with { Slot = At(21, 27), End = At(21, 30) };
        Assert.Equal(At(9).AddDays(1), HeadEndService.NextSlot(At(21, 31), Daylight, catchUp, oneOff));
        // Late, but not by more than the catch-up window: the slot still runs.
        var stopped = ran with { Slot = At(16), Outcome = SlotOutcome.Aborted, Reason = "the head end is stopping", End = At(16, 1) };
        Assert.Equal(At(16, 3), HeadEndService.NextSlot(At(16, 3), Daylight, catchUp, stopped));
    }

    [Fact]
    public void Daylight_RunsOnlyTheDaylightSlots_SaysWhichOnceADay_AndARunNowAtNightStillRuns()
    {
        using var dir = new TempDirectory();
        var head = DaylightHead(dir, At(15, 59, 30));
        head.Store.Offer(Bulletins.Make(90, 3000), Day1);
        RunNowAnswer? night = null;
        head.Time.Run(async () =>
        {
            using var stop = new CancellationTokenSource();
            Task service = head.Service.RunAsync(stop.Token);
            await Task.Delay(TimeSpan.FromMinutes(30), head.Time); // 16:29:30
            head.Store.Offer(Bulletins.Make(91, 3000), Day1);
            await Task.Delay(TimeSpan.FromMinutes(240), head.Time); // 20:29:30
            head.Store.Offer(Bulletins.Make(92, 3000), Day1);
            night = head.Service.RequestRunNow("tester");
            await Task.Delay(TimeSpan.FromHours(14), head.Time); // 10:29:30 on the 6th
            await stop.CancelAsync();
            await service;
            return 0;
        }, TimeSpan.FromHours(20));

        // 16:00 for the first, 17:00 for the second, 20:29 on demand for the third, then nothing in
        // the dark: all three repeats, due 5 hours on (21:00, 22:00, 01:29), wait for 09:00.
        var leases = LeaseTimes(head);
        Assert.True(night!.Accepted);
        Assert.Equal(4, leases.Count);
        Assert.InRange(leases[0], At(16), At(16, 2));
        Assert.InRange(leases[1], At(17), At(17, 2));
        Assert.InRange(leases[2], At(20, 29, 30), At(20, 31));
        Assert.InRange(leases[3], At(9).AddDays(1), At(9, 2).AddDays(1));
        Assert.Contains(head.Journal.Lines, l => l.StartsWith("slot 2026-10-06 10:00Z: skipped: nothing to send", StringComparison.Ordinal));
        Assert.Equal(head.Sent().Count(), head.Sent().Distinct().Count());
        Assert.Contains(head.Journal.Lines, l => l == "daylight 2026-10-05 at IO91lk: sunrise 06:11Z, sunset 17:33Z; 9 slots, 09:00, 10:00, 11:00, 12:00, 13:00, 14:00, 15:00, 16:00 and 17:00 UTC");
        Assert.Single(head.Journal.Lines, l => l.StartsWith("daylight 2026-10-06 ", StringComparison.Ordinal));
        Assert.DoesNotContain(head.Journal.Lines, l => l.StartsWith("slot 2026-10-05 18:00Z", StringComparison.Ordinal) || l.StartsWith("slot 2026-10-06 08:00Z", StringComparison.Ordinal));
        Assert.Contains(head.Journal.Lines, l => l == "next slot 2026-10-06 09:00Z; 3 bulletins held");
    }

    [Fact]
    public void Daylight_TheDirectoryCarriesTheTimetable()
    {
        using var dir = new TempDirectory();
        var head = DaylightHead(dir, At(12));
        head.Store.Offer(Bulletins.Make(93, 3000), Day1);
        var plan = head.Planner.Plan(At(12));
        Assert.Equal(Daylight.Timetable, plan.Broadcast!.Directory.Schedule);
        Assert.Equal(Daylight.Timetable, BroadcastDirectory.Parse(plan.Broadcast.Directory.Serialize()).Schedule);
    }
}
