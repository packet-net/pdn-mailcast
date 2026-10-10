using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.Mailcast.Feedback;
using Packet.Mailcast.Propagation;
using Mailcast.Receiver.Delivery;
using Mailcast.Receiver.Feedback;
using Mailcast.Receiver.Web;

namespace Mailcast.Receiver.Tests;

/// <summary>The daily report: when it goes, what it says, what the BBS said, and that it is off unless asked for.</summary>
public class FeedbackTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);
    private static readonly SlotSchedule Schedule = new ReceiverConfig().Schedule;
    private static readonly IReadOnlyList<DateTimeOffset> Slots = Schedule.ActiveOn(Day);
    private static readonly DateTimeOffset ReportAt = Slots[^1] + FeedbackService.AfterLastSlot;

    /// <summary>A BBS session that keeps what it is given and answers as told (accepted unless told otherwise).</summary>
    private sealed class FakeSession : IBbsSession
    {
        public List<Bulletin> Sent { get; } = [];

        public Queue<DeliveryVerdict> Answers { get; } = new();

        public Task<SessionReport> DeliverAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation)
        {
            Sent.AddRange(bulletins);
            var verdict = Answers.Count > 0 ? Answers.Dequeue() : DeliveryVerdict.Accepted;
            return Task.FromResult(new SessionReport(verdict != DeliveryVerdict.NotOffered, verdict == DeliveryVerdict.NotOffered ? "no answer from 127.0.0.1:8011 within 30 s" : null,
                [.. bulletins.Select(b => new DeliveryOutcome(b.Bid, verdict, verdict == DeliveryVerdict.Refused ? "the BBS answered Reject" : null))], 0));
        }
    }

    private sealed class Rig(string dir, FeedbackSettings? settings = null)
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        public FakeSession Bbs { get; } = new();

        public List<string> Log { get; } = [];

        public SlotSummary? Last { get; set; }

        public IonoReading? Iono { get; set; }

        public PskReading? Psk { get; set; }

        public Dictionary<DateTimeOffset, ReportChannel> Channels { get; } = [];

        public FeedbackSettings? Settings { get; set; } = settings ?? new FeedbackSettings { Enabled = true, Callsign = "g4abc" };

        public FeedbackService Start() => new(new FeedbackSources
        {
            Settings = () => Settings,
            Schedule = () => Schedule,
            Listened = Schedule.ActiveOn,
            LastSlot = () => Last,
            Ionosphere = () => Iono,
            PskReporter = () => Psk,
            Channel = slot => Channels.GetValueOrDefault(slot),
            Locator = () => "io91LK",
            Audio = () => "sc",
            Version = "0.6.0",
        }, Bbs, dir, Time, Log.Add);

        public async Task AtAsync(FeedbackService service, DateTimeOffset at)
        {
            Time.SetUtcNow(at);
            await service.TickAsync(CancellationToken.None);
        }

        /// <summary>Listens through every slot of <paramref name="day"/>, hearing each, ticking at the start and end of each window.</summary>
        public async Task ListenAsync(FeedbackService service, DateOnly day, int? only = null)
        {
            var slots = Schedule.ActiveOn(day);
            for (int i = 0; i < slots.Count; i++)
            {
                if (only is { } n && i >= n)
                {
                    break;
                }
                var slot = slots[i];
                Last = null;
                await AtAsync(service, slot.AddMinutes(1));
                Last = new SlotSummary(slot, new ToneReport(1801.2, 1.2, 15.6 + i, TimeSpan.FromSeconds(10)), 150 + i, slot.AddMinutes(9), slot)
                {
                    FrameCounts = ImmutableSortedDictionary<string, int>.Empty.Add(Waveform.Name(i % 2 == 0 ? 4 : 3), 150 + i),
                };
                await AtAsync(service, slot.AddMinutes(12));
            }
        }
    }

    [Fact]
    public async Task Report_GoesHalfAnHourAfterTheLastSlot_OnceOnly()
    {
        using var dir = new TempDirectory();
        var rig = new Rig(dir.Path);
        var service = rig.Start();

        await rig.ListenAsync(service, Day);
        await rig.AtAsync(service, ReportAt.AddMinutes(-1));
        Assert.Empty(rig.Bbs.Sent);
        Assert.Equal(ReportAt, service.Next(rig.Time.GetUtcNow()));

        await rig.AtAsync(service, ReportAt);
        var mail = Assert.Single(rig.Bbs.Sent);
        Assert.Equal(('B', "G4ABC", "MCAST", "GB7RDG.#42.GBR.EURO"), (mail.Type, mail.From, mail.To, mail.At));
        Assert.Matches("^6279[A-Z0-9]{2}G4ABC$", mail.Bid);
        Assert.Equal("MCR G4ABC 2026-10-06", mail.Title);
        Assert.Empty(mail.RoutingLines);
        var report = DailyReport.Parse(mail.Title, mail.Body);
        Assert.Equal(Slots.Count, report.Slots.Count);
        Assert.Equal("IO91lk", report.Header.Locator);
        Assert.Equal(("0.6.0", "sc"), (report.Header.ReceiverVersion, report.Header.Audio));
        Assert.Equal((TimeOnly.FromDateTime(Slots[0].UtcDateTime), "W4", 150, 16.0, 1.2), (report.Slots[0].Start, report.Slots[0].Waveform, report.Slots[0].Frames, report.Slots[0].SnrDb, report.Slots[0].OffsetHz));
        Assert.Equal("W3", report.Slots[1].Waveform);
        Assert.InRange(mail.Body.Length, 100, 600);
        Assert.Contains(rig.Log, l => l.StartsWith("feedback: the report for 2026-10-06 (MCR G4ABC 2026-10-06,", StringComparison.Ordinal) && l.EndsWith("was accepted by the BBS", StringComparison.Ordinal));
        Assert.All(rig.Log, l => Assert.All(l, c => Assert.InRange(c, ' ', '~')));

        // Not again: not on the next look, nor after a restart, nor later that evening.
        await rig.AtAsync(service, ReportAt.AddMinutes(1));
        var restarted = rig.Start();
        await rig.AtAsync(restarted, ReportAt.AddMinutes(20));
        await rig.AtAsync(restarted, ReportAt.AddHours(5));
        Assert.Single(rig.Bbs.Sent);
        Assert.Equal(FeedbackService.ReportTime(Schedule, Day.AddDays(1)), restarted.Next(rig.Time.GetUtcNow()));
    }

    [Fact]
    public async Task Restart_AfterTheReportWasMissed_SendsItOnce()
    {
        using var dir = new TempDirectory();
        var rig = new Rig(dir.Path);
        var service = rig.Start();
        await rig.ListenAsync(service, Day, only: 3);
        // Stopped before the day's report went; started again the next morning, before any slot.
        var next = Schedule.ActiveOn(Day.AddDays(1));

        var restarted = rig.Start();
        await rig.AtAsync(restarted, next[0].AddHours(-2));
        await rig.AtAsync(restarted, next[0].AddHours(-1));
        await rig.ListenAsync(restarted, Day.AddDays(1), only: 2);

        var mail = Assert.Single(rig.Bbs.Sent);
        var report = DailyReport.Parse(mail.Title, mail.Body);
        Assert.Equal(Day, report.Day);
        Assert.Equal(3, report.Slots.Count);
        Assert.Equal(next[0].AddHours(-2), restarted.Last!.SentAt);

        // And that day's own report goes as usual.
        await rig.AtAsync(restarted, FeedbackService.ReportTime(Schedule, Day.AddDays(1))!.Value);
        Assert.Equal(2, rig.Bbs.Sent.Count);
        Assert.Equal(2, DailyReport.Parse(rig.Bbs.Sent[1].Title, rig.Bbs.Sent[1].Body).Slots.Count);
    }

    [Fact]
    public async Task Slots_NotListenedTo_AreLeftOut_AndOnesHeardNothingIn_AreKept()
    {
        using var dir = new TempDirectory();
        var rig = new Rig(dir.Path);
        var service = rig.Start();
        // Started late: the first slot's window had closed. The second was listened to but
        // nothing was heard; the third had the ionosonde's verdict and a channel measurement.
        await rig.AtAsync(service, Slots[0].AddMinutes(30));
        await rig.AtAsync(service, Slots[1].AddMinutes(3));
        rig.Iono = IonoEvaluator.Evaluate([new IonoSounding("RL052", Slots[2], IonoSource.Giro, 6.05, M3000: 3.3)], new IonoSettings(), Slots[2].AddMinutes(5));
        rig.Psk = new PskReading { State = IonoState.Good, ObservedUtc = Slots[2].AddMinutes(2) };
        rig.Channels[Slots[2]] = new ReportChannel(2, 1.94, -16.6, 0.347, 0.214, 287, 'b');
        await rig.AtAsync(service, Slots[2].AddMinutes(5));
        rig.Last = new SlotSummary(Slots[2], null, 7, Slots[2].AddMinutes(6), Slots[2]);
        await rig.AtAsync(service, Slots[2].AddMinutes(12));
        service.NoteError(ReportErrors.Bbs);
        service.NoteError(ReportErrors.Bbs);
        service.NoteError(ReportErrors.Audio);
        service.NoteRebuilt();
        service.NoteDelivered(1);

        await rig.AtAsync(service, ReportAt);

        var mail = Assert.Single(rig.Bbs.Sent);
        var lines = mail.Body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("MCR1 0.6.0 IO91lk sc 1/1 3:AUD1,BBS2 0", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Equal($"{Slots[1]:HH} - 0 - - -", lines[1]);
        Assert.Equal($"{Slots[2]:HH} - 7 - - IM+PG 2 1.9/-17 0.35 0.21 287 b", lines[2]);
    }

    /// <summary>
    /// Issue #86: accepted and already-had are counted apart, so a day like G7TAJ's (three
    /// already had, nothing newly accepted) reads as such in the header's fifth and new seventh
    /// fields, rather than already-had being folded into delivered.
    /// </summary>
    [Fact]
    public async Task AcceptedAndAlreadyHad_AreCountedApart_InTheReportsHeader()
    {
        using var dir = new TempDirectory();
        var rig = new Rig(dir.Path);
        var service = rig.Start();
        await rig.ListenAsync(service, Day, only: 1);
        service.NoteRebuilt();
        service.NoteRebuilt();
        service.NoteRebuilt();
        service.NoteDelivered(0);
        service.NoteAlreadyHad(3);

        await rig.AtAsync(service, ReportAt);

        var mail = Assert.Single(rig.Bbs.Sent);
        var report = DailyReport.Parse(mail.Title, mail.Body);
        Assert.Equal((3, 0, 3), (report.Header.Rebuilt, report.Header.Delivered, report.Header.AlreadyHad));
        var lines = mail.Body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("MCR1 0.6.0 IO91lk sc 3/0 0 3", lines[0]);
    }

    /// <summary>
    /// Issue #86: a feedback.json a pre-#86 receiver left, with no "alreadyHad" at all on a
    /// day's record, loads without failing and starts that count at zero rather than refusing to
    /// read the file - so upgrading mid-day does not break the day's report.
    /// </summary>
    [Fact]
    public async Task Upgrade_DayRecordWithoutAlreadyHad_LoadsAsZero_AndCountsFromThen()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, FeedbackService.FileName), """
            {
              "days": [
                {
                  "day": "2026-10-06",
                  "rebuilt": 2,
                  "delivered": 1,
                  "errors": {}
                }
              ],
              "last": null
            }
            """);
        var rig = new Rig(dir.Path);
        var service = rig.Start();

        await rig.ListenAsync(service, Day, only: 1);
        service.NoteAlreadyHad(2);
        await rig.AtAsync(service, ReportAt);

        var mail = Assert.Single(rig.Bbs.Sent);
        var report = DailyReport.Parse(mail.Title, mail.Body);
        Assert.Equal((2, 1, 2), (report.Header.Rebuilt, report.Header.Delivered, report.Header.AlreadyHad));
    }

    [Fact]
    public async Task Refused_IsNotSentAgain_ButShownOnThePage()
    {
        using var dir = new TempDirectory();
        var rig = new Rig(dir.Path);
        var service = rig.Start();
        rig.Bbs.Answers.Enqueue(DeliveryVerdict.Refused);
        await rig.ListenAsync(service, Day, only: 1);

        await rig.AtAsync(service, ReportAt);
        await rig.AtAsync(service, ReportAt.AddMinutes(30));
        await rig.AtAsync(rig.Start(), ReportAt.AddHours(1));

        Assert.Single(rig.Bbs.Sent);
        Assert.Equal(FeedbackAnswer.Refused, service.Last!.Answer);
        var view = JsonSerializer.SerializeToElement(service.View(), ReceiverConfig.JsonLine);
        Assert.True(view.GetProperty("enabled").GetBoolean());
        Assert.Equal("refused", view.GetProperty("answer").GetString());
        Assert.Equal("refused by the BBS: the BBS answered Reject; it is not sent again", view.GetProperty("lastAnswer").GetString());
        Assert.Equal(ReportAt, view.GetProperty("lastSent").GetDateTimeOffset());
        Assert.StartsWith("MCR G4ABC 2026-10-06\nMCR1 0.6.0 IO91lk sc 0/0 0 0\n", view.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(FeedbackService.ReportTime(Schedule, Day.AddDays(1)), view.GetProperty("next").GetDateTimeOffset());
        Assert.Contains(rig.Log, l => l.Contains("was refused by the BBS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeferredOrFailed_IsOfferedAgainLater_WithTheSameBid()
    {
        using var dir = new TempDirectory();
        var rig = new Rig(dir.Path);
        var service = rig.Start();
        rig.Bbs.Answers.Enqueue(DeliveryVerdict.Deferred);
        rig.Bbs.Answers.Enqueue(DeliveryVerdict.NotOffered);
        await rig.ListenAsync(service, Day, only: 1);

        await rig.AtAsync(service, ReportAt);
        Assert.Equal(FeedbackAnswer.Deferred, service.Last!.Answer);
        Assert.Equal(ReportAt.AddHours(1), service.Next(rig.Time.GetUtcNow()));
        await rig.AtAsync(service, ReportAt.AddMinutes(59));
        Assert.Single(rig.Bbs.Sent);

        await rig.AtAsync(service, ReportAt.AddHours(1));
        Assert.Equal(FeedbackAnswer.Failed, service.Last!.Answer);
        Assert.Contains("no answer from", service.Last.Detail, StringComparison.Ordinal);
        Assert.Equal(ReportAt.AddHours(3), service.Last.RetryAt);

        // A restart in between changes nothing: it is still owed, and goes when due.
        var restarted = rig.Start();
        await rig.AtAsync(restarted, ReportAt.AddHours(2));
        Assert.Equal(2, rig.Bbs.Sent.Count);
        await rig.AtAsync(restarted, ReportAt.AddHours(3));
        Assert.Equal(FeedbackAnswer.Accepted, restarted.Last!.Answer);
        Assert.Equal(3, rig.Bbs.Sent.Count);
        Assert.Single(rig.Bbs.Sent.Select(b => b.Bid).Distinct());
        await rig.AtAsync(restarted, ReportAt.AddHours(5));
        Assert.Equal(3, rig.Bbs.Sent.Count);
    }

    [Fact]
    public async Task Retry_AfterTheCallsignChanged_GoesFromTheCallsignItWasMadeWith()
    {
        using var dir = new TempDirectory();
        var rig = new Rig(dir.Path);
        var service = rig.Start();
        rig.Bbs.Answers.Enqueue(DeliveryVerdict.Deferred);
        await rig.ListenAsync(service, Day, only: 1);
        await rig.AtAsync(service, ReportAt);
        Assert.Equal(FeedbackAnswer.Deferred, service.Last!.Answer);

        // Changed on the status page while it waits; across a restart too.
        rig.Settings = new FeedbackSettings { Enabled = true, Callsign = "M0XYZ" };
        var restarted = rig.Start();
        await rig.AtAsync(restarted, ReportAt.AddHours(1));

        Assert.Equal(2, rig.Bbs.Sent.Count);
        var again = rig.Bbs.Sent[1];
        Assert.Equal(("G4ABC", rig.Bbs.Sent[0].Bid, "MCR G4ABC 2026-10-06"), (again.From, again.Bid, again.Title));
        Assert.Equal(FeedbackAnswer.Accepted, restarted.Last!.Answer);
    }

    [Fact]
    public async Task Upgrade_AReportWaitingAsAPersonalMail_GoesAsTheBulletin()
    {
        // feedback.json as v0.8.1 left it: the report for 6 October deferred by the BBS when it
        // was a personal mail to M0LTE, to be offered again at 18:30. Nothing in it says P or
        // M0LTE: the message is made afresh at each offer.
        using var dir = new TempDirectory();
        const string Body = "MCR1 0.8.1 IO91lk sc 0/0 0\r\n09 W4 150 16 +1.2 IG\r\n";
        File.WriteAllText(Path.Combine(dir.Path, FeedbackService.FileName), $$"""
            {
              "days": [],
              "last": {
                "day": "2026-10-06",
                "title": "MCR G4ABC 2026-10-06",
                "body": "{{Body.Replace("\r\n", "\\r\\n", StringComparison.Ordinal)}}",
                "bid": "6279K7G4ABC",
                "from": "G4ABC",
                "sentAt": "2026-10-06T17:30:00+00:00",
                "answer": "deferred",
                "retryAt": "2026-10-06T18:30:00+00:00",
                "offers": ["2026-10-06T17:30:00+00:00"]
              },
              "receiverId": "K7"
            }
            """);
        var rig = new Rig(dir.Path);
        var service = rig.Start();
        Assert.Equal(FeedbackAnswer.Deferred, service.Last!.Answer);

        await rig.AtAsync(service, new DateTimeOffset(2026, 10, 6, 18, 30, 0, TimeSpan.Zero));

        var mail = Assert.Single(rig.Bbs.Sent);
        Assert.Equal(('B', "G4ABC", "MCAST", "GB7RDG.#42.GBR.EURO"), (mail.Type, mail.From, mail.To, mail.At));
        Assert.Equal(("6279K7G4ABC", "MCR G4ABC 2026-10-06", Body), (mail.Bid, mail.Title, mail.Body));
        Assert.Equal(FeedbackAnswer.Accepted, service.Last!.Answer);
        Assert.Equal(2, service.Last.Offers.Count);
    }

    [Fact]
    public void Retries_WaitLongerEachTime_AndAtMostSixADay()
    {
        var t = new DateTimeOffset(2026, 10, 6, 0, 30, 0, TimeSpan.Zero);
        Assert.Equal(t.AddHours(1), FeedbackService.RetryTime([t]));
        Assert.Equal(t.AddHours(3), FeedbackService.RetryTime([t, t.AddHours(1)]));
        Assert.Equal(t.AddHours(7), FeedbackService.RetryTime([t, t.AddHours(1), t.AddHours(3)]));
        Assert.Equal(t.AddHours(11), FeedbackService.RetryTime([t, t.AddHours(1), t.AddHours(3), t.AddHours(7)]));

        // Six offers in a day (a restart can bring the next one forward): no more until tomorrow.
        var six = Enumerable.Range(0, 6).Select(i => t.AddHours(i * 3)).ToList();
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero), FeedbackService.RetryTime(six));
        var late = Enumerable.Range(0, 6).Select(i => t.AddHours(20).AddMinutes(i * 30)).ToList();
        Assert.Equal(late[^1].AddHours(4), FeedbackService.RetryTime(late));
    }

    [Fact]
    public async Task ClockPutBack_ADayRecordedInTheFuture_IsLetGo()
    {
        using var dir = new TempDirectory();
        var ahead = new Rig(dir.Path);
        var service = ahead.Start();
        await ahead.ListenAsync(service, Day, only: 1);
        await ahead.AtAsync(service, ReportAt);
        Assert.Single(ahead.Bbs.Sent);

        // The clock was a day fast; put right, the day before's report still goes.
        var right = new Rig(dir.Path);
        var restarted = right.Start();
        await right.ListenAsync(restarted, Day.AddDays(-1), only: 2);
        await right.AtAsync(restarted, FeedbackService.ReportTime(Schedule, Day.AddDays(-1))!.Value);

        var mail = Assert.Single(right.Bbs.Sent);
        Assert.Equal(Day.AddDays(-1), DailyReport.Parse(mail.Title, mail.Body).Day);
        Assert.Equal(2, DailyReport.Parse(mail.Title, mail.Body).Slots.Count);
        Assert.Contains(right.Log, l => l.Contains("the clock must have been wrong", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ubersdr:wessex.zapto.org", "wessex.zapto.org")]
    [InlineData("ubersdr:https://Wessex.Zapto.org:443", "wessex.zapto.org")]
    [InlineData("ubersdr:192.168.1.20:8073", "sdr")]
    [InlineData("ubersdr:localhost:8073", "sdr")]
    [InlineData("ubersdr:shack-sdr:8073", "sdr")]
    [InlineData("ubersdr:sdr.local", "sdr")]
    [InlineData("ubersdr:sdr.lan:8073", "sdr")]
    [InlineData("plughw:CARD=Device,DEV=0", "sc")]
    public void Audio_WebSdrIsNamedOnlyWhenItIsAPublicName(string audio, string expected)
    {
        Assert.Equal(expected, ReportAudio.For(AudioSource.Parse(audio)));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("[::1]")]
    [InlineData("fe80::1")]
    [InlineData("box.home.arpa")]
    [InlineData("rx.internal")]
    public void Audio_PrivateHosts_AreJustSdr(string host)
    {
        Assert.Equal(ReportAudio.PrivateSdr, ReportAudio.WebSdr(host));
    }

    [Fact]
    public async Task Off_ByDefault_SendsNothingAndKeepsNothing()
    {
        Assert.Null(new ReceiverConfig().Feedback);
        Assert.False(new FeedbackSettings().Enabled);
        using var dir = new TempDirectory();
        string configPath = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(configPath, "{}");
        Assert.Null(ReceiverConfig.Load(configPath).Feedback);
        Assert.Null(ReceiverConfig.Load(Path.Combine(AppContext.BaseDirectory, "receiver.example.json")).Feedback);

        foreach (var settings in new FeedbackSettings?[] { null, new() { Enabled = false, Callsign = "G4ABC" } })
        {
            var rig = new Rig(dir.Path) { Settings = settings };
            var service = rig.Start();
            await rig.ListenAsync(service, Day);
            service.NoteError(ReportErrors.Bbs);
            await rig.AtAsync(service, ReportAt.AddMinutes(5));

            Assert.Empty(rig.Bbs.Sent);
            Assert.Empty(rig.Log);
            Assert.False(File.Exists(Path.Combine(dir.Path, FeedbackService.FileName)));
            Assert.Null(service.Next(rig.Time.GetUtcNow()));
            var view = JsonSerializer.SerializeToElement(service.View(), ReceiverConfig.JsonLine);
            Assert.False(view.GetProperty("enabled").GetBoolean());
        }

        // The status page says so.
        var config = new ReceiverConfig { Audio = "wav:/nonexistent.wav", StateDirectory = dir.Path };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        await using var page = new StatusPage(host, null, _ => { });
        var status = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine);
        Assert.False(status.GetProperty("feedback").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Report_StartsAndStopsWithTheSetting()
    {
        using var dir = new TempDirectory();
        var rig = new Rig(dir.Path) { Settings = new FeedbackSettings { Enabled = false, Callsign = "G4ABC" } };
        var service = rig.Start();

        // Off: nothing noted.
        await rig.ListenAsync(service, Day);
        Assert.Empty(rig.Log);

        // Turned on after the day's last slot but before its report, as from the status page:
        // there is nothing to report for today, so the next is tomorrow's.
        var tomorrow = Day.AddDays(1);
        rig.Settings = new FeedbackSettings { Enabled = true, Callsign = "G4ABC" };
        await rig.AtAsync(service, Slots[^1].AddMinutes(20));
        Assert.Equal(FeedbackService.ReportTime(Schedule, tomorrow), service.Next(rig.Time.GetUtcNow()));
        Assert.Contains(rig.Log, l => l.StartsWith("feedback: on.", StringComparison.Ordinal));
        Assert.True(JsonSerializer.SerializeToElement(service.View(), ReceiverConfig.JsonLine).GetProperty("enabled").GetBoolean());
        await rig.AtAsync(service, ReportAt);
        Assert.Empty(rig.Bbs.Sent);

        await rig.ListenAsync(service, tomorrow);
        await rig.AtAsync(service, FeedbackService.ReportTime(Schedule, tomorrow)!.Value);
        var mail = Assert.Single(rig.Bbs.Sent);
        Assert.Equal(tomorrow, DailyReport.Parse(mail.Title, mail.Body).Day);

        // Turned off again: said once, and the next day's does not go.
        var after = tomorrow.AddDays(1);
        rig.Settings = new FeedbackSettings { Enabled = false, Callsign = "G4ABC" };
        await rig.ListenAsync(service, after);
        await rig.AtAsync(service, FeedbackService.ReportTime(Schedule, after)!.Value.AddMinutes(5));
        Assert.Single(rig.Bbs.Sent);
        Assert.Single(rig.Log, l => l.StartsWith("feedback: off", StringComparison.Ordinal));
        Assert.Null(service.Next(rig.Time.GetUtcNow()));
        Assert.False(JsonSerializer.SerializeToElement(service.View(), ReceiverConfig.JsonLine).GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("G4ABC")]
    [InlineData("m0lte")]
    [InlineData("2E0ABC")]
    [InlineData("GB7RDG")]
    [InlineData("K1A")]
    [InlineData("9A1AA")]
    [InlineData(" VE3XYZ ")]
    public void Callsign_Plausible_IsTaken(string callsign)
    {
        Assert.True(FeedbackSettings.IsPlausibleCallsign(callsign));
        var config = new ReceiverConfig { Feedback = new FeedbackSettings { Enabled = true, Callsign = callsign } };
        config.Validate();
        Assert.Equal(callsign.Trim().ToUpperInvariant(), config.Feedback!.From);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("G4ABC-1")]
    [InlineData("NOCALL")]
    [InlineData("N0CALL")]
    [InlineData("1234")]
    [InlineData("G4")]
    [InlineData("G4ABCDE")]
    [InlineData("G4 ABC")]
    [InlineData("ABC123")]
    public void Callsign_NotPlausible_IsRefusedWhenEnabled(string? callsign)
    {
        Assert.False(FeedbackSettings.IsPlausibleCallsign(callsign));
        var e = Assert.Throws<ConfigException>(() => new ReceiverConfig { Feedback = new FeedbackSettings { Enabled = true, Callsign = callsign } }.Validate());
        Assert.Contains("\"feedback\"", e.Message, StringComparison.Ordinal);
        Assert.Contains("callsign", e.Message, StringComparison.Ordinal);

        // Not enabled, it is not looked at.
        new ReceiverConfig { Feedback = new FeedbackSettings { Enabled = false, Callsign = callsign } }.Validate();
    }

    [Theory]
    [InlineData("""{ "feedback": { "enabled": true, "callsign": "G4ABC", "to": "G0XYZ" } }""", "\"to\" is not a setting of \"feedback\"")]
    [InlineData("""{ "feedback": { "enabled": true } }""", "has no \"callsign\"")]
    [InlineData("""{ "feedback": { "enabled": "yes", "callsign": "G4ABC" } }""", "\"feedback\" setting that cannot be read")]
    public void Config_FeedbackThatCannotWork_IsRefusedWithAReason(string json, string expected)
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, json);

        var e = Assert.Throws<ConfigException>(() => ReceiverConfig.Load(path));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_Feedback_LoadsAndSavesBack()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "receiver.json");
        File.WriteAllText(path, """{ "feedback": { "enabled": true, "callsign": "G4ABC" } }""");

        var config = ReceiverConfig.Load(path);
        Assert.Equal(new FeedbackSettings { Enabled = true, Callsign = "G4ABC" }, config.Feedback);
        config.Save(path);
        Assert.Equal(config.Feedback, ReceiverConfig.Load(path).Feedback);
    }

    [Fact]
    public void Bid_IsUniqueToTheListenerTheReceiverAndTheDay_AndFitsFbb()
    {
        Assert.Equal("6279K7G4ABC", FeedbackService.Bid("G4ABC", "K7", Day));
        Assert.Equal("6365X02E0ABC", FeedbackService.Bid("2E0ABC", "X0", new DateOnly(2026, 12, 31)));
        Assert.True(FeedbackService.Bid("2E0ABC", "X0", new DateOnly(2026, 12, 31)).Length <= BbsClient.MaxBidLength);
    }

    [Fact]
    public async Task Bid_ReceiversOwnPart_IsKeptAcrossRestarts()
    {
        using var dir = new TempDirectory();
        var rig = new Rig(dir.Path);
        var service = rig.Start();
        await rig.ListenAsync(service, Day, only: 1);
        await rig.AtAsync(service, ReportAt);
        var restarted = rig.Start();
        await rig.ListenAsync(restarted, Day.AddDays(1), only: 1);
        await rig.AtAsync(restarted, FeedbackService.ReportTime(Schedule, Day.AddDays(1))!.Value);

        Assert.Equal(2, rig.Bbs.Sent.Count);
        Assert.Equal(rig.Bbs.Sent[0].Bid[4..6], rig.Bbs.Sent[1].Bid[4..6]);
        Assert.Equal("6280", rig.Bbs.Sent[1].Bid[..4]);
    }
}
