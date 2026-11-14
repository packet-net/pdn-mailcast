using System.Globalization;
using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Station;
using Mailcast.HeadEnd.Status;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// M0LTE, 14 Nov 2026: the 10:00 slot (50 frames for 39 bulletins on ms110d-wn3-long) was held back
/// because the 10 MHz reference said NO PPS, and its retry at 10:05 said "skipped: nothing to send":
/// the retry plans the same slot afresh, filled to <c>slot.maxMinutes</c> less its lateness and the
/// margin (2 min 49 s at 10:05 against 7 min 49 s on time), which on a slow waveform beside a big
/// directory can leave no room for a bulletin piece. And every receiver had given the window back at
/// 10:03:30 with nothing heard. Now a held slot looks at the reference every 15 s and starts as soon
/// as it is back, up to 2 minutes in, while it can still be heard; later than that it rolls on.
/// </summary>
public class ReferenceHoldRetryTests
{
    private static readonly DateTimeOffset Ten = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly ReferenceFeed Ok = new(ReferenceFeed.Ok, null, null);
    private static readonly ReferenceFeed NoPps = new(ReferenceFeed.Unreliable, "NO PPS for over 60 s", Ten.AddMinutes(-2));

    // M0LTE's slot timing: hourly, catch-up and retry 5 minutes, the 10 s tone and 2 minute clear-channel wait left as they are.
    private const string M0lteLike = """
        {"callsign": "GB7RDG", "station": {"apiKey": "k"},
         "slot": {"minute": 0, "everyMinutes": 60, "catchUpMinutes": 5, "retryMinutes": 5},
         "schedule": {"modes": ["ms110d-wn3-long"]}}
        """;

    private sealed class Head : IDisposable
    {
        private readonly TempDirectory _dir = new();
        private readonly int _mark;

        public Head()
        {
            var start = Ten.AddHours(-3);
            Time = new VirtualTime(start);
            Config = HeadEndConfig.Parse(M0lteLike);
            var options = Config.ToScheduleOptions() with { Signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem()), Station = "M0LTE" };
            Store = new RotationStore(_dir.Path, Compression.Default, options, Journal, Time);
            for (int i = 0; i < 39; i++)
            {
                Store.Offer(Bulletins.Make(i, 2000 + (i * 397 % 9000)), DateOnly.FromDateTime(Ten.UtcDateTime));
            }
            Station = new FakeStation(Time, 4);
            var planner = new StoreSlotPlanner(Store, Compression.Default, options, Config.ToWaveforms(Waveforms.Measure), Config.ToSlotSettings(), Config.FillLimitAfter, Time);
            var runner = new SlotRunner(new SlotSettings { Callsign = "GB7RDG", SubChannel = 4 }, Station, Station, null, FakeAirtime.For(Station), Journal, Time, new FakeClockSync());
            Schedule = Config.ToSlotSchedule();
            Service = new HeadEndService(Schedule, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), planner, Store, [], runner, new StatusStore(null, Time), Journal, Time);

            // Three slots on the air first, so the bulletins are started and the directory lists them all, as at 10:00 live.
            for (int h = 0; h < 3; h++)
            {
                var slot = start.AddHours(h);
                Time.StepWallClock(slot - Time.GetUtcNow());
                var onTime = Run(slot);
                Assert.Equal(SlotOutcome.Completed, onTime.Outcome);
                OnTimeEstimate = onTime.EstimatedSeconds!.Value;
            }
            Time.StepWallClock(Ten - Time.GetUtcNow());
            _mark = Journal.Lines.Count;
        }

        public VirtualTime Time { get; }

        /// <summary>The airtime estimate of the 09:00 slot, which went on time.</summary>
        public double OnTimeEstimate { get; }

        public HeadEndConfig Config { get; }

        public FakeStation Station { get; }

        public RotationStore Store { get; }

        public SlotSchedule Schedule { get; }

        public HeadEndService Service { get; }

        public MemoryJournal Journal { get; } = new();

        /// <summary>The journal since the three slots before 10:00.</summary>
        public IReadOnlyList<string> Lines => [.. Journal.Lines.Skip(_mark)];

        public SlotReport Run(DateTimeOffset slot) => Time.Run(() => Service.RunSlotAsync(slot, CancellationToken.None));

        public uint[] NextEsis() => [.. Store.InRotation(Ten).Select(b => b.NextEsi)];

        public DateTimeOffset Next(SlotReport report) => HeadEndService.NextSlot(Time.GetUtcNow(), Schedule, TimeSpan.FromMinutes(5), report, TimeSpan.FromMinutes(5));

        public void Dispose() => _dir.Dispose();
    }

    [Fact]
    public void AHeldSlot_StartsAsSoonAsTheReferenceIsBack_InsideTheReceiversWindow_WithItsBulletins()
    {
        using var head = new Head();
        uint[] before = head.NextEsis();
        // NO PPS at the hour; back 40 s later.
        head.Station.ReferenceAt = t => t < Ten.AddSeconds(40) ? NoPps : Ok;

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal(Ten, report.Slot);
        // Looked at again every 15 s: started at the first look after it came back, well before receivers give up at 3 min 30 s.
        Assert.Equal(Ten.AddSeconds(45), report.Start, TimeSpan.FromSeconds(2));
        Assert.True(report.FramesSent > 0 && report.FramesSent == report.FramesPlanned, $"{report.FramesSent} of {report.FramesPlanned}");
        // Planned afresh for its lateness (the fill counts from the slot's time), so shorter than an on-time slot by about that much.
        Assert.InRange(head.OnTimeEstimate - report.EstimatedSeconds!.Value, 30, 60);
        // Bulletin pieces went, not just the directory.
        Assert.True(head.NextEsis().Zip(before).Any(p => p.First > p.Second), "no bulletin piece was sent");
        Assert.Contains("slot 2026-11-14 10:00Z: not sent: the 10 MHz reference is unreliable (NO PPS for over 60 s); looking at it again every 15 s until 10:02:00Z", head.Lines);
        Assert.Contains(head.Lines, l => l.StartsWith("slot 2026-11-14 10:00Z: the 10 MHz reference is ok; starting 45 s after the slot's time", StringComparison.Ordinal));
        Assert.DoesNotContain(head.Lines, l => l.Contains("nothing to send", StringComparison.Ordinal));
        Assert.Equal(Ten.AddHours(1), head.Next(report));
    }

    [Fact]
    public void AReferenceThatStaysBad_RollsOnToTheNextSlot_WithNothingCounted_AndNoUnheardRetryAtFivePast()
    {
        using var head = new Head();
        uint[] before = head.NextEsis();
        int leases = head.Station.LeaseRequests.Count;
        head.Station.Reference = NoPps;

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.False(report.Retryable);
        Assert.False(report.LeaseTaken);
        Assert.Equal(leases, head.Station.LeaseRequests.Count);
        Assert.Equal(before, head.NextEsis());
        // Given up at the last look that could still be heard; the next slot is 11:00, not a retry at 10:05.
        Assert.True(head.Time.GetUtcNow() <= Ten + HeadEndService.HoldLatestStart, $"gave up at {head.Time.GetUtcNow():HH:mm:ss}");
        Assert.Equal(Ten.AddHours(1), head.Next(report));
        Assert.Contains(head.Lines, l => l.StartsWith("slot 2026-11-14 10:00Z: not sent: the 10 MHz reference was still unreliable at 10:02:00Z, too late for receivers to hear the slot; everything rolls on to the next slot, 2026-11-14 11:00Z", StringComparison.Ordinal));
        Assert.DoesNotContain(head.Lines, l => l.Contains("trying again at", StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceThatGoesBadAgainAfterComingBack_IsHeldAgain_AndStillStartsInTime()
    {
        using var head = new Head();
        // Bad at the hour; ok for the look at 10:00:15 but bad again for the slot's own read just after; ok from 10:01.
        int reads = 0;
        head.Station.ReferenceAt = t => ++reads == 2 || t >= Ten.AddMinutes(1) ? Ok : NoPps;

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.InRange(report.Start, Ten.AddMinutes(1), Ten + HeadEndService.HoldLatestStart);
        Assert.Equal(2, head.Lines.Count(l => l.Contains("looking at it again every 15 s until 10:02:00Z", StringComparison.Ordinal)));
    }

    [Fact]
    public void AHoldSeenTooLateToBeHeard_IsNotRetried()
    {
        // The head end came up at 10:03 and caught the slot up: no time left to wait for the reference, and no retry at 10:05.
        using var head = new Head();
        head.Station.Reference = NoPps;
        head.Time.StepWallClock(TimeSpan.FromMinutes(3));

        var report = head.Run(Ten);

        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.False(report.Retryable);
        Assert.Equal(Ten.AddHours(1), head.Next(report));
        Assert.Contains("slot 2026-11-14 10:00Z: not sent: the 10 MHz reference is unreliable (NO PPS for over 60 s); the next slot is 2026-11-14 11:00Z", head.Lines);
    }

    [Fact]
    public void TheOldRetryAtFivePast_IsFilledToOnly2Min49_Against7Min49OnTime()
    {
        // The arithmetic behind "nothing to send" at 10:05: the 10 min hard stop, less 5 min late, less the 2 min channel wait and 11 s.
        var config = HeadEndConfig.Parse(M0lteLike);
        Assert.Equal(TimeSpan.Parse("00:07:49", CultureInfo.InvariantCulture), config.FillLimitAfter(TimeSpan.Zero));
        Assert.Equal(TimeSpan.Parse("00:02:49", CultureInfo.InvariantCulture), config.FillLimitAfter(TimeSpan.FromMinutes(5)));
        // A held slot that starts by 2 minutes in keeps most of it.
        Assert.Equal(TimeSpan.Parse("00:05:49", CultureInfo.InvariantCulture), config.FillLimitAfter(HeadEndService.HoldLatestStart));
    }
}
