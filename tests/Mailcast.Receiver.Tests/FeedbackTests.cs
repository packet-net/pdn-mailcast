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

        public Dictionary<DateTimeOffset, ReportChannel> Channels { get; } = [];

        public FeedbackSettings? Settings { get; set; } = settings ?? new FeedbackSettings { Enabled = true, Callsign = "g4abc" };

        public FeedbackService Start() => new(new FeedbackSources
        {
            Settings = () => Settings,
            Schedule = () => Schedule,
            Listened = Schedule.ActiveOn,
            LastSlot = () => Last,
            Ionosphere = () => Iono,
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
        Assert.Equal(('P', "G4ABC", "M0LTE", "GB7RDG.#42.GBR.EURO", "G4ABC_61006"), (mail.Type, mail.From, mail.To, mail.At, mail.Bid));
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
        Assert.Equal("MCR1 0.6.0 IO91lk sc 1/1 3:AUD1,BBS2", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Equal($"{Slots[1]:HH} - 0 - - -", lines[1]);
        Assert.Equal($"{Slots[2]:HH} - 7 - - IM 2 1.9/-17 0.35 0.21 287 b", lines[2]);
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
        Assert.StartsWith("MCR G4ABC 2026-10-06\nMCR1 0.6.0 IO91lk sc 0/0 0\n", view.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(FeedbackService.ReportTime(Schedule, Day.AddDays(1)), view.GetProperty("next").GetDateTimeOffset());
        Assert.Contains(rig.Log, l => l.Contains("was refused by the BBS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeferredOrFailed_IsOfferedAgainAfterTenMinutes_WithTheSameBid()
    {
        using var dir = new TempDirectory();
        var rig = new Rig(dir.Path);
        var service = rig.Start();
        rig.Bbs.Answers.Enqueue(DeliveryVerdict.Deferred);
        rig.Bbs.Answers.Enqueue(DeliveryVerdict.NotOffered);
        await rig.ListenAsync(service, Day, only: 1);

        await rig.AtAsync(service, ReportAt);
        Assert.Equal(FeedbackAnswer.Deferred, service.Last!.Answer);
        Assert.Equal(ReportAt + FeedbackService.Retry, service.Next(rig.Time.GetUtcNow()));
        await rig.AtAsync(service, ReportAt.AddMinutes(9));
        Assert.Single(rig.Bbs.Sent);

        await rig.AtAsync(service, ReportAt.AddMinutes(10));
        Assert.Equal(FeedbackAnswer.Failed, service.Last!.Answer);
        Assert.Contains("no answer from", service.Last.Detail, StringComparison.Ordinal);

        // A restart in between changes nothing: it is still owed, and goes when due.
        var restarted = rig.Start();
        await rig.AtAsync(restarted, ReportAt.AddMinutes(20));
        Assert.Equal(FeedbackAnswer.Accepted, restarted.Last!.Answer);
        Assert.Equal(3, rig.Bbs.Sent.Count);
        Assert.Single(rig.Bbs.Sent.Select(b => b.Bid).Distinct());
        await rig.AtAsync(restarted, ReportAt.AddMinutes(40));
        Assert.Equal(3, rig.Bbs.Sent.Count);
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
    public void Bid_IsUniqueToTheListenerAndTheDay_AndFitsFbb()
    {
        Assert.Equal("G4ABC_61006", FeedbackService.Bid("G4ABC", Day));
        Assert.Equal("2E0ABC_61231", FeedbackService.Bid("2E0ABC", new DateOnly(2026, 12, 31)));
        Assert.True(FeedbackService.Bid("2E0ABC", Day).Length <= BbsClient.MaxBidLength);
    }
}
