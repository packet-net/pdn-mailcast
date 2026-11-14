using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Station;
using Mailcast.HeadEnd.Status;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// The station's 10 MHz reference feed (<c>reference</c> in <c>GET /api/txlease</c>): unreliable holds
/// the slot with nothing counted, unknown sends the CW E marker instead of the tone, ok or absent
/// changes nothing.
/// </summary>
public class ReferenceFeedTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly ReferenceFeed Ok = new(ReferenceFeed.Ok, null, null);
    private static readonly ReferenceFeed Lost = new(ReferenceFeed.Unreliable, "GPS LOST", Noon.AddMinutes(-20));
    private static readonly ReferenceFeed Offline = new(ReferenceFeed.Unknown, "display offline", null);
    /// <summary>What pdn-soundmodem says for the report that cost the 14:00 slot on 2026-11-14 (packet-net/pdn-soundmodem#807).</summary>
    private static readonly ReferenceFeed Relocking = new(ReferenceFeed.Holdover, "UNLOCKED, relocking 1124/1500 s, TIE -33 ns", Noon.AddMinutes(-1));

    private sealed class Rig
    {
        public Rig()
        {
            Time = new VirtualTime(Noon);
            // Room for the 30 s tone and the probe after it.
            Station = new FakeStation(Time, 4) { MaxSeconds = 60 };
            Runner = new SlotRunner(new SlotSettings { Callsign = "M0LTE", SubChannel = 4 }, Station, Station, null, FakeAirtime.For(Station), Journal, Time, new FakeClockSync());
        }

        public VirtualTime Time { get; }

        public FakeStation Station { get; }

        public MemoryJournal Journal { get; } = new();

        public SlotRunner Runner { get; }

        public List<int> Progress { get; } = [];

        public SlotReport Run(int frames, bool evening = false) =>
            Time.Run(() => Runner.RunAsync(Noon, [.. Enumerable.Range(0, frames).Select(i => FakeAirtime.Frame(i))], 10, CancellationToken.None, Progress.Add, evening: evening));
    }

    [Fact]
    public void Unreliable_HoldsTheSlot_BeforeAnythingAtTheStationIsTouched()
    {
        var rig = new Rig();
        rig.Station.Reference = Lost;
        var report = rig.Run(20);

        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.True(report.Retryable);
        Assert.Equal("the 10 MHz reference is unreliable (GPS LOST)", report.Reason);
        Assert.Equal("unreliable", report.ReferenceFeed);
        Assert.Equal("GPS LOST", report.ReferenceFeedDetail);
        Assert.False(report.LeaseTaken);
        Assert.Equal(0, report.FramesQueued);
        Assert.Empty(rig.Progress);
        Assert.Empty(rig.Station.LeaseRequests);
        Assert.Empty(rig.Station.ToneRequests);
        Assert.Empty(rig.Station.Keyups);
        Assert.Empty(rig.Station.SetHardware);
        // The head end writes the one line for it, with the retry time: the runner's own skip line is left out.
        Assert.DoesNotContain(rig.Journal.Lines, l => l.Contains("skipped:", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_SendsTheCwEMarkerInsteadOfTheTone_WithTheProbeAfterIt()
    {
        var rig = new Rig();
        rig.Station.Reference = Offline;
        var report = rig.Run(10);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal(10, report.FramesSent);
        Assert.True(report.MarkerSent);
        Assert.False(report.ToneSent);
        Assert.True(report.ProbeSent);
        Assert.Equal("unknown", report.ReferenceFeed);
        Assert.Equal("display offline", report.ReferenceFeedDetail);
        Assert.Equal([ToneMarker.CwE], rig.Station.MarkersAsked);
        Assert.StartsWith("cw-e marker 1800 Hz and probe", rig.Station.Keyups[0].What, StringComparison.Ordinal);
        Assert.Single(rig.Journal.Lines, l => l.Contains("the 10 MHz reference state is unknown (display offline), so the CW E marker went instead of the calibration tone, 30 s at 1800 Hz, then the channel probe", StringComparison.Ordinal));
        Assert.Contains(rig.Journal.Lines, l => l.Contains(": done, ", StringComparison.Ordinal) && l.Contains("tone replaced by the CW E marker", StringComparison.Ordinal) && l.EndsWith(", reference feed unknown (display offline)", StringComparison.Ordinal));
        Assert.All(rig.Journal.Lines, l => Assert.True(l.All(char.IsAscii), l));
    }

    [Fact]
    public void Unknown_OnAStationThatRefusesTheMarker_FallsBackToTheTone_AndSaysSoOncePerRun()
    {
        var rig = new Rig();
        rig.Station.Reference = Offline;
        rig.Station.RejectsMarker = true;
        var first = rig.Run(5);
        // The pair, then the marker alone (the 400 might have been the probe's), then the plain tone with the probe.

        Assert.Equal(SlotOutcome.Completed, first.Outcome);
        Assert.True(first.ToneSent);
        Assert.False(first.MarkerSent);
        Assert.True(first.ProbeSent);
        Assert.Equal("unknown", first.ReferenceFeed);
        Assert.Equal([ToneMarker.CwE, ToneMarker.CwE, null], rig.Station.MarkersAsked);
        Assert.Equal([true, false, true], rig.Station.ToneRequests.Select(r => r.Probe is not null));
        Assert.StartsWith("tone 1800 Hz and probe", rig.Station.Keyups.Single(k => !k.What.StartsWith("burst", StringComparison.Ordinal)).What, StringComparison.Ordinal);
        Assert.Contains(rig.Journal.Lines, l => l.Contains("calibration tone sent, 30 s at 1800 Hz, then the channel probe", StringComparison.Ordinal) && l.Contains("the 10 MHz reference state is unknown (display offline) but the station does not know the CW E marker", StringComparison.Ordinal));

        // The next slot asks again (the station may have been upgraded), but says nothing more of the refusal.
        rig.Run(5);
        Assert.Equal([ToneMarker.CwE, ToneMarker.CwE, null, ToneMarker.CwE, ToneMarker.CwE, null], rig.Station.MarkersAsked);
        Assert.Single(rig.Journal.Lines, l => l.Contains("the station would not send the CW E marker (HTTP 400", StringComparison.Ordinal) && l.Contains("the marker is asked for again each slot", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Journal.Lines, l => l.Contains("restarts", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_WhenThe400WasTheProbes_SendsTheMarkerAlone()
    {
        var rig = new Rig();
        rig.Station.Reference = Offline;
        rig.Station.RejectsProbe = true;
        var report = rig.Run(5);

        Assert.True(report.MarkerSent);
        Assert.False(report.ToneSent);
        Assert.False(report.ProbeSent);
        Assert.Equal([ToneMarker.CwE, ToneMarker.CwE], rig.Station.MarkersAsked);
        Assert.Equal([true, false], rig.Station.ToneRequests.Select(r => r.Probe is not null));
        Assert.Equal("cw-e marker 1800 Hz", rig.Station.Keyups[0].What);
        Assert.Contains(rig.Journal.Lines, l => l.Contains("the station would not send the CW E marker with the channel probe (HTTP 400", StringComparison.Ordinal) && l.EndsWith("asked for the marker alone", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Journal.Lines, l => l.Contains("plain calibration tone instead", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_OnAStationThatIgnoresTheMarker_CountsThePlainTone_AndSaysSoOncePerRun()
    {
        // Today's pdn-soundmodem ignores the field: 200, the plain tone, and no echo.
        var rig = new Rig();
        rig.Station.Reference = Offline;
        rig.Station.KnowsMarker = false;
        var first = rig.Run(5);

        Assert.Equal(SlotOutcome.Completed, first.Outcome);
        Assert.True(first.ToneSent);
        Assert.False(first.MarkerSent);
        Assert.True(first.ProbeSent);
        Assert.Equal([ToneMarker.CwE], rig.Station.MarkersAsked);
        Assert.StartsWith("tone 1800 Hz and probe", rig.Station.Keyups[0].What, StringComparison.Ordinal);
        Assert.Contains(rig.Journal.Lines, l => l.Contains("calibration tone sent, 30 s at 1800 Hz, then the channel probe", StringComparison.Ordinal) && l.Contains("the 10 MHz reference state is unknown (display offline) but the station does not know the CW E marker", StringComparison.Ordinal));
        Assert.Contains(rig.Journal.Lines, l => l.Contains(": done, ", StringComparison.Ordinal) && l.Contains("tone sent", StringComparison.Ordinal));

        rig.Run(5);
        Assert.Equal([ToneMarker.CwE, ToneMarker.CwE], rig.Station.MarkersAsked);
        Assert.Single(rig.Journal.Lines, l => l.EndsWith(": the station sent the plain tone; it does not know the CW marker", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Journal.Lines, l => l.Contains("would not send", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OkOrNoFeed_RunsAsAlways(bool withFeed)
    {
        var rig = new Rig();
        rig.Station.Reference = withFeed ? Ok : null;
        var report = rig.Run(10);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.True(report.ToneSent);
        Assert.False(report.MarkerSent);
        Assert.Equal(withFeed ? "ok" : null, report.ReferenceFeed);
        Assert.Null(report.ReferenceFeedDetail);
        Assert.Equal([null], rig.Station.MarkersAsked);
        Assert.StartsWith("tone 1800 Hz", rig.Station.Keyups[0].What, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Journal.Lines, l => l.Contains("10 MHz", StringComparison.Ordinal));
        // A good feed is said only in the done line, beside the Flex's own view; no feed, nothing.
        Assert.Equal(withFeed ? 1 : 0, rig.Journal.Lines.Count(l => l.Contains("reference feed", StringComparison.Ordinal)));
        Assert.Contains(rig.Journal.Lines, l => l.Contains(": done, ", StringComparison.Ordinal) && l.Contains("tone sent", StringComparison.Ordinal)
            && (withFeed ? l.EndsWith(", reference feed ok", StringComparison.Ordinal) : !l.Contains("reference feed", StringComparison.Ordinal)));
    }

    [Fact]
    public void Holdover_SendsTheSlotWithTheSteadyTone_MarkedAsSentOnHoldover()
    {
        var rig = new Rig();
        rig.Station.Reference = Relocking;
        var report = rig.Run(10);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal(10, report.FramesSent);
        // The tone is still worth measuring: the OCXO is tens of nanoseconds off, so no CW E marker.
        Assert.True(report.ToneSent);
        Assert.False(report.MarkerSent);
        Assert.True(report.ProbeSent);
        Assert.Equal([null], rig.Station.MarkersAsked);
        Assert.True(report.SentOnHoldover);
        Assert.Equal("holdover", report.ReferenceFeed);
        Assert.Equal("UNLOCKED, relocking 1124/1500 s, TIE -33 ns", report.ReferenceFeedDetail);
        Assert.Single(rig.Journal.Lines, l => l.EndsWith(": the 10 MHz reference is in holdover (UNLOCKED, relocking 1124/1500 s, TIE -33 ns); fine to send on, so the slot goes on, marked as sent on holdover", StringComparison.Ordinal));
        Assert.Contains(rig.Journal.Lines, l => l.Contains(": done, ", StringComparison.Ordinal) && l.Contains("tone sent", StringComparison.Ordinal)
            && l.EndsWith(", reference feed in holdover (UNLOCKED, relocking 1124/1500 s, TIE -33 ns)", StringComparison.Ordinal));
        Assert.All(rig.Journal.Lines, l => Assert.True(l.All(char.IsAscii), l));
    }

    [Fact]
    public void TurningHoldoverMidSlot_NeverStopsIt()
    {
        // The 14:00 slot on 2026-11-14: the reference went from ok to "NO PPS for over 60 s" 75 s in,
        // and the slot was aborted after 8 of 70 frames. Holdover now: it goes to the end.
        var rig = new Rig();
        DateTimeOffset turns = Noon + TimeSpan.FromMinutes(1.25);
        rig.Station.ReferenceAt = t => t < turns ? Ok : Relocking;
        var report = rig.Run(40);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Null(report.Reason);
        Assert.Equal(40, report.FramesSent);
        Assert.True(report.ToneSent);
        Assert.True(report.SentOnHoldover);
        Assert.Equal("holdover", report.ReferenceFeed);
        Assert.Single(rig.Journal.Lines, l => l.Contains("the 10 MHz reference is in holdover", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Journal.Lines, l => l.Contains("ABORTED", StringComparison.Ordinal) || l.Contains("stopping", StringComparison.Ordinal));
    }

    [Fact]
    public void FromHoldoverToARealFaultMidSlot_StillStopsIt()
    {
        var rig = new Rig();
        DateTimeOffset turns = Noon + TimeSpan.FromMinutes(3);
        var past = new ReferenceFeed(ReferenceFeed.Unreliable, "UNLOCKED, in holdover for over 6h00m", turns);
        rig.Station.ReferenceAt = t => t < turns ? Relocking : past;
        var report = rig.Run(40);

        Assert.Equal(SlotOutcome.Aborted, report.Outcome);
        Assert.Equal("the 10 MHz reference turned unreliable (UNLOCKED, in holdover for over 6h00m)", report.Reason);
        Assert.Equal("unreliable", report.ReferenceFeed);
        Assert.True(report.SentOnHoldover, "it was sent on holdover until it stopped");
        Assert.InRange(report.FramesSent, 1, 39);
        Assert.All(rig.Station.Keyups, k => Assert.True(k.End <= turns + TimeSpan.FromMinutes(1.1), $"{k.What} at {k.Start:HH:mm:ss} keyed after the reference went"));
    }

    [Fact]
    public void TheWorstFeed_RanksHoldoverBetweenOkAndUnknown()
    {
        var rig = new Rig();
        int reads = 0;
        rig.Station.ReferenceAt = _ => ++reads switch { 1 => Ok, 2 => Relocking, 3 => Offline, _ => Relocking };
        var report = rig.Run(30);

        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal("unknown", report.ReferenceFeed);
        Assert.True(report.SentOnHoldover, "a later unknown does not hide that it was sent on holdover");
    }

    [Fact]
    public void NoFeed_ReadsTheLeaseOnlyAsBefore_ExceptOnceBeforeTheSlot()
    {
        // A station with no feed is read once more than before (the look before anything is
        // touched), and not before every burst.
        var rig = new Rig();
        rig.Run(30);
        int reads = rig.Station.ReferenceReads;
        var fed = new Rig();
        fed.Station.Reference = Ok;
        var report = fed.Run(30);
        Assert.Equal(5, report.Bursts);
        Assert.Equal(reads + 5, fed.Station.ReferenceReads);
    }

    [Fact]
    public void TurningUnreliableMidSlot_StopsCleanly_AndKeysNothingMore()
    {
        var rig = new Rig();
        // Good for the first few minutes, then lost.
        DateTimeOffset turns = Noon + TimeSpan.FromMinutes(3);
        rig.Station.ReferenceAt = t => t < turns ? Ok : Lost;
        var report = rig.Run(40);

        Assert.Equal(SlotOutcome.Aborted, report.Outcome);
        Assert.Equal("the 10 MHz reference turned unreliable (GPS LOST)", report.Reason);
        Assert.Equal("unreliable", report.ReferenceFeed);
        Assert.False(report.Retryable);
        Assert.InRange(report.FramesSent, 1, 39);
        Assert.Equal(report.FramesSent, report.FramesQueued);
        Assert.All(rig.Station.Keyups, k => Assert.True(k.End <= turns + TimeSpan.FromMinutes(1.1), $"{k.What} at {k.Start:HH:mm:ss} keyed after the reference went"));
        Assert.Single(rig.Station.Releases);
        Assert.False(rig.Station.LeaseHeld);
        Assert.Contains(rig.Journal.Lines, l => l.Contains("the 10 MHz reference turned unreliable (GPS LOST); stopping, nothing more is queued", StringComparison.Ordinal));
        Assert.Contains(rig.Journal.Lines, l => l.Contains("ABORTED", StringComparison.Ordinal) && l.EndsWith(", reference feed unreliable (GPS LOST)", StringComparison.Ordinal));
    }

    [Fact]
    public void TurningUnreliableDuringTheChannelCheck_SkipsWithNothingCounted()
    {
        var rig = new Rig();
        // The channel is busy for a minute; the reference goes while the head end waits for it.
        rig.Station.ChannelBusyUntil = Noon + TimeSpan.FromSeconds(60);
        rig.Station.ReferenceAt = t => t < Noon + TimeSpan.FromSeconds(20) ? Ok : Lost;
        var report = rig.Run(10);

        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.True(report.Retryable);
        Assert.Equal("the 10 MHz reference is unreliable (GPS LOST)", report.Reason);
        Assert.True(report.LeaseTaken);
        Assert.Equal(0, report.FramesQueued);
        Assert.Empty(rig.Progress);
        Assert.Empty(rig.Station.ToneRequests);
        Assert.Empty(rig.Station.Frames);
        Assert.Single(rig.Station.Releases);
        Assert.Contains(rig.Journal.Lines, l => l.Contains("before any frame was queued; stopping, nothing counted", StringComparison.Ordinal));
    }

    [Fact]
    public void TurningUnreliableAfterTheTone_IsCaughtBeforeTheFirstBurst()
    {
        var rig = new Rig();
        // Ok through the tone and the probe, lost by the first burst after the 8 s pause.
        DateTimeOffset turns = Noon + TimeSpan.FromSeconds(30) + ChannelProbe.Airtime + TimeSpan.FromSeconds(2);
        rig.Station.ReferenceAt = t => t < turns ? Ok : Lost;
        var report = rig.Run(10);

        Assert.Equal(SlotOutcome.Skipped, report.Outcome);
        Assert.True(report.Retryable);
        Assert.True(report.ToneSent);
        Assert.Equal("the 10 MHz reference turned unreliable (GPS LOST) after the calibration tone went, before any bulletin", report.Reason);
        Assert.Equal(0, report.FramesQueued);
        Assert.Empty(rig.Station.Frames);
    }

    [Fact]
    public void AFailedRead_KeepsTheChecksBeforeEveryBurst_AndOnAStationWithAFeedCountsAsUnknown()
    {
        var rig = new Rig();
        rig.Station.Reference = Ok;
        // The first read (before the slot) shows the feed; the next, just before the tone, fails.
        rig.Station.FailedReads.Add(2);
        var report = rig.Run(30);
        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.Equal("unknown", report.ReferenceFeed);
        Assert.Equal("the station's lease could not be read", report.ReferenceFeedDetail);
        Assert.True(report.MarkerSent);
        Assert.Equal(5, report.Bursts);
        Assert.Equal(2 + 5, rig.Station.ReferenceReads);

        // Remembered across slots: a later slot whose every read fails is unknown and marked, and still checked before each burst.
        rig.Station.FailAllReads = true;
        int before = rig.Station.ReferenceReads;
        var blind = rig.Run(30);
        Assert.Equal(SlotOutcome.Completed, blind.Outcome);
        Assert.Equal("unknown", blind.ReferenceFeed);
        Assert.True(blind.MarkerSent);
        Assert.True(rig.Station.ReferenceReads - before >= 2 + 5, $"{rig.Station.ReferenceReads - before} reads");

        // A station that never showed a feed: a failed read is just a failed read, but the bursts are still checked.
        var plain = new Rig();
        plain.Station.FailedReads.Add(2);
        var unfed = plain.Run(30);
        Assert.Null(unfed.ReferenceFeed);
        Assert.False(unfed.MarkerSent);
        Assert.Equal(2 + 5, plain.Station.ReferenceReads);
    }

    [Fact]
    public void AFeedTheStationDrops_NoLongerMarksTheSlot()
    {
        var rig = new Rig();
        // Unknown before the slot, then the station stops sending a reference at all.
        rig.Station.ReferenceAt = t => rig.Station.ReferenceReads <= 1 ? Offline : null;
        var report = rig.Run(5);
        Assert.Equal([null], rig.Station.MarkersAsked);
        Assert.True(report.ToneSent);
        Assert.False(report.MarkerSent);
    }

    [Fact]
    public void AnAbortAlreadyUnderWay_KeepsItsReason()
    {
        var rig = new Rig();
        // The tone waits out the station's minute behind other traffic; the renewal at 30 s is
        // refused meanwhile, and the reference goes at 45 s, before the read after the refused tone.
        rig.Station.FirstBusyReadMisses = true;
        rig.Station.RefuseAfterRequests = 1;
        bool sawLost = false;
        rig.Station.ReferenceAt = t =>
        {
            sawLost |= t >= Noon + TimeSpan.FromSeconds(45);
            return t < Noon + TimeSpan.FromSeconds(45) ? Ok : Lost;
        };
        var report = rig.Time.Run(() =>
        {
            rig.Station.KeyOtherTraffic(TimeSpan.FromSeconds(90));
            return rig.Runner.RunAsync(Noon, [.. Enumerable.Range(0, 5).Select(i => FakeAirtime.Frame(i))], 10, CancellationToken.None);
        });
        Assert.True(sawLost, "the reference was never read after it went");
        Assert.Equal(SlotOutcome.Aborted, report.Outcome);
        Assert.StartsWith("lease renewal failed", report.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Journal.Lines, l => l.Contains("turned unreliable", StringComparison.Ordinal));
    }

    private const string Hourly = """{"minute": 0, "everyMinutes": 60, "catchUpMinutes": 30, "retryMinutes": 5}""";

    private sealed class Head
    {
        public Head(TempDirectory dir, TimeSpan? catchUpMinutes = null)
        {
            TimeSpan catchUp = catchUpMinutes ?? TimeSpan.FromMinutes(30);
            Time = new VirtualTime(Noon);
            Station = new FakeStation(Time, 4);
            var config = HeadEndConfig.Parse($$"""{"callsign": "GB7RDG", "station": {"apiKey": "k"}, "slot": {{Hourly}}}""");
            var options = config.ToScheduleOptions();
            Store = new RotationStore(dir.Path, Compression.Default, options, Journal, Time);
            Status = new StatusStore(null, Time);
            var planner = new StoreSlotPlanner(Store, Compression.Default, options, config.ToWaveforms(_ => FakeAirtime.For(Station)), config.ToSlotSettings(), config.FillLimitAfter, Time);
            var runner = new SlotRunner(new SlotSettings { Callsign = "GB7RDG", SubChannel = 4 }, Station, Station, null, FakeAirtime.For(Station), Journal, Time, new FakeClockSync());
            Schedule = config.ToSlotSchedule();
            Service = new HeadEndService(Schedule, catchUp, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), planner, Store, [], runner, Status, Journal, Time);
        }

        public VirtualTime Time { get; }

        public FakeStation Station { get; }

        public RotationStore Store { get; }

        public StatusStore Status { get; }

        public SlotSchedule Schedule { get; }

        public HeadEndService Service { get; }

        public MemoryJournal Journal { get; } = new();

        public SlotReport Run(DateTimeOffset slot) => Time.Run(() => Service.RunSlotAsync(slot, CancellationToken.None));
    }

    [Fact]
    public void TheHeadEnd_HoldsTheSlot_CountsNothing_LooksAgainUntilTwoMinutesIn_AndThenSendsTheNextSlot()
    {
        using var dir = new TempDirectory();
        var head = new Head(dir);
        head.Store.Offer(Bulletins.Make(1, 3000), DateOnly.FromDateTime(Noon.UtcDateTime));
        head.Station.Reference = Lost;

        var held = head.Run(Noon);
        Assert.Equal(SlotOutcome.Skipped, held.Outcome);
        // Looked at every 15 s while the slot could still be heard, then given up: no retry at 10:05, which no receiver would hear.
        Assert.False(held.Retryable);
        Assert.Equal(
            [
                "slot 2026-11-09 10:00Z: not sent: the 10 MHz reference is unreliable (GPS LOST); looking at it again every 15 s until 10:02:00Z",
                "slot 2026-11-09 10:00Z: not sent: the 10 MHz reference was still unreliable at 10:02:00Z, too late for receivers to hear the slot; everything rolls on to the next slot, 2026-11-09 11:00Z",
            ],
            head.Journal.Lines.Where(l => l.Contains("10 MHz", StringComparison.Ordinal)));
        string status = head.Status.Render().Replace(" ", "", StringComparison.Ordinal);
        Assert.Contains("\"referenceFeed\":\"unreliable\"", status, StringComparison.Ordinal);
        Assert.Contains("\"referenceFeedDetail\":\"GPSLOST\"", status, StringComparison.Ordinal);
        Assert.Contains("\"markerSent\":false", status, StringComparison.Ordinal);

        // Nothing counted: no pieces spent, no first slot.
        var bulletin = head.Store.InRotation(Noon).Single();
        Assert.Equal((0u, 0u, (DateTimeOffset?)null), (bulletin.NextEsi, bulletin.EveningNextEsi, bulletin.FirstSlot));

        // The next slot goes once the reference is back, with everything the held one would have sent.
        DateTimeOffset next = HeadEndService.NextSlot(head.Time.GetUtcNow(), head.Schedule, TimeSpan.FromMinutes(30), held, TimeSpan.FromMinutes(5));
        Assert.Equal(Noon.AddHours(1), next);
        head.Station.Reference = Ok;
        head.Time.Advance(next - head.Time.GetUtcNow());
        var sent = head.Run(next);
        Assert.Equal(SlotOutcome.Completed, sent.Outcome);
        Assert.Equal("ok", sent.ReferenceFeed);
        Assert.Equal(next, head.Store.InRotation(Noon).Single().FirstSlot);
        Assert.Contains("\"referenceFeed\":\"ok\"", head.Status.Render().Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AtGb7rdgsSettings_ASkipAtTheStation_IsTriedAgainAtFivePast()
    {
        // catchUpMinutes 5 and retryMinutes 5, and an attempt that ends a little after the hour. A reference hold is
        // looked at again in place instead (ReferenceHoldRetryTests); this is any other skip that may clear: the lease refused.
        using var dir = new TempDirectory();
        var head = new Head(dir, TimeSpan.FromMinutes(5));
        head.Store.Offer(Bulletins.Make(1, 3000), DateOnly.FromDateTime(Noon.UtcDateTime));
        head.Station.ReadDelay = TimeSpan.FromMilliseconds(300);
        head.Station.RefuseFirstLease = true;
        var held = head.Run(Noon);
        Assert.Equal(SlotOutcome.Skipped, held.Outcome);
        Assert.True(held.Retryable);
        Assert.True(held.End > Noon);
        Assert.Equal(Noon.AddMinutes(5), HeadEndService.NextSlot(head.Time.GetUtcNow(), head.Schedule, TimeSpan.FromMinutes(5), held, TimeSpan.FromMinutes(5)));

        // The retry at 10:05 is the last inside the window: one that fails too waits for the next slot.
        head.Time.Advance(Noon.AddMinutes(5) - head.Time.GetUtcNow());
        var again = head.Run(Noon);
        Assert.Equal(Noon.AddHours(1), HeadEndService.NextSlot(head.Time.GetUtcNow(), head.Schedule, TimeSpan.FromMinutes(5), again, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void TheHeadEnd_SaysStopped_WhenTheToneHadGone()
    {
        using var dir = new TempDirectory();
        var head = new Head(dir);
        var stopped = new SlotReport { Slot = Noon, Day = DateOnly.FromDateTime(Noon.UtcDateTime), Start = Noon, End = Noon.AddMinutes(1), Outcome = SlotOutcome.Skipped, Retryable = true, ToneSent = true, Reason = "the 10 MHz reference turned unreliable (GPS LOST) after the calibration tone went, before any bulletin", ReferenceFeed = "unreliable" };
        Assert.Equal("slot 2026-11-09 10:00Z: stopped before any bulletin went: the 10 MHz reference turned unreliable (GPS LOST) after the calibration tone went, before any bulletin; trying again at 10:05Z", head.Service.ReferenceHoldLine(stopped, Noon.AddMinutes(1)));
    }

    [Fact]
    public void TheHeadEnd_NamesTheNextSlot_WhenTheRetryWouldBePastTheCatchUpWindow()
    {
        using var dir = new TempDirectory();
        var head = new Head(dir);
        head.Station.Reference = Lost;
        var late = new SlotReport { Slot = Noon, Day = DateOnly.FromDateTime(Noon.UtcDateTime), Start = Noon.AddSeconds(1801), End = Noon.AddSeconds(1801), Outcome = SlotOutcome.Skipped, Retryable = true, Reason = "the 10 MHz reference is unreliable (GPS LOST)", ReferenceFeed = "unreliable", Evening = true };
        Assert.Equal("evening slot 2026-11-09 10:00Z: not sent: the 10 MHz reference is unreliable (GPS LOST); the next slot is 2026-11-09 11:00Z", head.Service.ReferenceHoldLine(late, Noon.AddSeconds(1801)));

        // Anything else gets no such line.
        Assert.Null(head.Service.ReferenceHoldLine(late with { ReferenceFeed = "ok", Reason = "the channel stayed busy for 2 min" }, Noon.AddSeconds(1801)));
        Assert.Null(head.Service.ReferenceHoldLine(late with { Outcome = SlotOutcome.Aborted }, Noon.AddSeconds(1801)));
    }

    private sealed class Handler(string answer) : HttpMessageHandler
    {
        public List<JsonNode?> Bodies { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    [Theory]
    [InlineData("""{"held": false, "channelBusy": false}""", null, null, null)]
    [InlineData("""{"held": false, "channelBusy": false, "reference": {"state": "ok", "detail": "GPS locked", "since": "2026-11-09T09:40:00Z"}}""", "ok", "GPS locked", "2026-11-09T09:40:00Z")]
    [InlineData("""{"held": false, "reference": {"state": "unreliable", "detail": "GPS LOST", "since": "2026-11-09T09:40:00Z"}}""", "unreliable", "GPS LOST", "2026-11-09T09:40:00Z")]
    [InlineData("""{"held": false, "reference": {"state": "UNKNOWN", "detail": "display offline"}}""", "unknown", "display offline", null)]
    [InlineData("""{"held": false, "reference": {"state": "holdover", "detail": "UNLOCKED, relocking 1124/1500 s, TIE -33 ns", "since": "2026-11-14T14:01:15Z"}}""", "holdover", "UNLOCKED, relocking 1124/1500 s, TIE -33 ns", "2026-11-14T14:01:15Z")]
    [InlineData("""{"held": false, "reference": {"state": "warming", "detail": "  OCXO° warm\nup  "}}""", "unknown", "OCXO  warm up", null)]
    [InlineData("""{"held": false, "reference": {}}""", "unknown", null, null)]
    [InlineData("""{"held": false, "reference": null}""", null, null, null)]
    [InlineData("""{"held": false, "reference": "ok"}""", "unknown", "the station's reference field could not be read", null)]
    [InlineData("""{"held": false, "reference": 5}""", "unknown", "the station's reference field could not be read", null)]
    [InlineData("""{"held": false, "reference": ["ok"]}""", "unknown", "the station's reference field could not be read", null)]
    public async Task TheClient_ReadsTheReferenceFeed(string answer, string? state, string? detail, string? since)
    {
        using var handler = new Handler(answer);
        using var api = new StationApiClient(new Uri("http://127.0.0.1:8107/"), "secret", TimeSpan.FromSeconds(30), handler);
        var lease = await api.ReadLeaseAsync(CancellationToken.None);
        Assert.Equal(state, lease.Reference?.State);
        Assert.Equal(detail, lease.Reference?.Detail);
        Assert.Equal(since is null ? null : DateTimeOffset.Parse(since, System.Globalization.CultureInfo.InvariantCulture), lease.Reference?.Since);
    }

    [Fact]
    public async Task TheClient_AsksForTheMarker_OnlyWhenGivenOne()
    {
        using var handler = new Handler("""{"transmitted": true, "sent": "cw-e marker", "probe": "zc255-2400-rrc015-v1", "probeComplete": true}""");
        using var api = new StationApiClient(new Uri("http://127.0.0.1:8107/"), "secret", TimeSpan.FromSeconds(30), handler);
        var marked = await api.SendToneAsync(4, 1800, 30, new ProbeRequest("zc255", 1.5, 1800), ToneMarker.CwE, CancellationToken.None);
        Assert.Equal(ToneOutcome.Sent, marked.Outcome);
        Assert.Null(marked.Marker);
        await api.SendToneAsync(4, 1800, 30, null, null, CancellationToken.None);
        Assert.Equal("cw-e", handler.Bodies[0]!["marker"]!.GetValue<string>());
        Assert.False(handler.Bodies[1]!.AsObject().ContainsKey("marker"));

        // A station that sent the marker echoes it.
        using var echo = new Handler("""{"transmitted": true, "sent": "cw-e marker", "marker": "cw-e"}""");
        using var echoApi = new StationApiClient(new Uri("http://127.0.0.1:8107/"), "secret", TimeSpan.FromSeconds(30), echo);
        Assert.Equal(ToneMarker.CwE, (await echoApi.SendToneAsync(4, 1800, 30, null, ToneMarker.CwE, CancellationToken.None)).Marker);

        // A station that refuses it: 400, which the runner answers with the plain tone.
        using var old = new Handler("""{"error": "unknown field marker"}""") { Status = HttpStatusCode.BadRequest };
        using var oldApi = new StationApiClient(new Uri("http://127.0.0.1:8107/"), "secret", TimeSpan.FromSeconds(30), old);
        var refused = await oldApi.SendToneAsync(4, 1800, 30, null, ToneMarker.CwE, CancellationToken.None);
        Assert.Equal(400, refused.Status);
        Assert.NotEqual(ToneOutcome.Sent, refused.Outcome);
    }
}
