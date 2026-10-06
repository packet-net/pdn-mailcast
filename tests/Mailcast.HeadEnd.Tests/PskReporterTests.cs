using System.Text.Json;
using Packet.Mailcast;
using Packet.Mailcast.Propagation;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Status;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// The PSK Reporter reading at the head end: beside the ionosonde's in slots that key anyway,
/// inside the airtime budget, never deciding whether a slot keys, logged once a slot, and in the
/// report, /status and the config.
/// </summary>
public class PskReporterTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 6, 14, 0, 0, TimeSpan.Zero);

    private static readonly HeadEndConfig Config = HeadEndConfig.Parse("""
        {
          "station": { "apiKey": "k", "mode": "ms110d-wn4", "maxBurstSeconds": 18 },
          "slot": { "timeUtc": "00:00", "everyMinutes": 60, "maxMinutes": 10, "toneSeconds": 10, "channelWaitSeconds": 120 },
          "schedule": { "symbolSize": 240 }
        }
        """);

    private static readonly IonoReading Iono = IonoEvaluator.Evaluate(
        [new IonoSounding("RL052", Slot.AddMinutes(-12), IonoSource.PropQuest, 6.05, M3000: 3.3)], new IonoSettings(), Slot);

    /// <summary>Like 2026-10-06: nothing under 250 km on 40 m, plenty from 300 to 800, and 80 m busy close in.</summary>
    private static readonly PskReading Spots = PskEvaluator.Evaluate(Like20261006(), Slot, PskEvaluator.Window);

    private static IEnumerable<PskSpot> Like20261006()
    {
        long sq = 1;
        IEnumerable<PskSpot> Many(int count, int km, int stations, PskBand band, int snr)
        {
            for (int i = 0; i < count; i++)
            {
                yield return new PskSpot(sq++, Slot.AddMinutes(-3 - (i % 25)), band, km + (i % 9), snr + (i % 3), $"S{i % stations}", $"S{(i + 1) % stations}");
            }
        }
        return [.. Many(30, 320, 12, PskBand.Forty, -14), .. Many(60, 540, 18, PskBand.Forty, -10), .. Many(30, 780, 10, PskBand.Forty, -13), .. Many(20, 140, 9, PskBand.Eighty, -4)];
    }

    private static StoreSlotPlanner Planner(TempDirectory state, int bulletins, Func<IonoReading>? iono, Func<PskReading>? spots)
    {
        var options = Config.ToScheduleOptions();
        var store = new RotationStore(state.Path, Compression.Default, options, new MemoryJournal());
        for (int i = 0; i < bulletins; i++)
        {
            store.Offer(Bulletins.Make(700 + i, 6000), new DateOnly(2026, 10, 6));
        }
        return new StoreSlotPlanner(store, Compression.Default, options, Config.ToWaveforms(LinearAirtime.Measure), Config.ToSlotSettings(), Config.FillLimitAfter, new FakeTimeProvider(Slot.AddSeconds(30)), iono, spots);
    }

    private static List<SlotFrame> Of(SlotPlan plan, int objectLength) =>
        [.. plan.Frames.Where(f => MailcastFrame.TryParse(f.Payload, out var m) && m!.Oti.TransferLength == objectLength)];

    [Fact]
    public void ABusySlot_CarriesBothReadings_TwoFramesEach_InsideTheSameBudget()
    {
        using var state = new TempDirectory();
        var plan = Planner(state, 40, () => Iono, () => Spots).Plan(Slot);
        Assert.Equal((2, 2), (plan.IonosphereFrames, plan.PskReporterFrames));
        Assert.Same(Spots, plan.PskReporter);
        var spots = Of(plan, PskRecord.ObjectLength);
        Assert.Equal([0u, 1u], spots.Select(f => f.Esi));
        Assert.All(spots, f => Assert.Equal(59, f.Payload.Length));
        Assert.Equal(2, Of(plan, IonoRecord.ObjectLength).Count);
        Assert.DoesNotContain(plan.Broadcast!.Directory.Entries, e => e.ObjectId == spots[0].ObjectId);

        TimeSpan onAir = new SlotAirtime(Config.ToSlotSettings(), plan.Waveform!.Airtime).ForPayloads([.. plan.Frames.Select(f => f.Payload.Length)]);
        Assert.True(onAir <= Config.FillLimitAfter(TimeSpan.FromSeconds(30)), $"{onAir} on the air");

        // What a receiver rebuilds from either frame is the reading, 40 m only.
        Assert.True(MailcastFrame.TryParse(spots[1].Payload, out var frame));
        var decoder = new Mailcast.RaptorQ.ObjectDecoder(frame!.Oti);
        decoder.Add(new Mailcast.RaptorQ.PayloadId(0, frame.EncodingSymbolId), frame.Symbol.Span);
        Assert.True(PskRecord.TryDecode(decoder.TryDecode(), out var sent));
        Assert.Equal(Spots.Forty!.Bins, sent.Forty!.Bins);
        Assert.Equal((IonoState.Marginal, PathVerdict.Closed, PathVerdict.Open, PathVerdict.Open), (sent.State, sent.At100, sent.At500, sent.At1000));
        Assert.Equal(PskEvidence.OtherDistances, sent.Forty.At(100)!.ClosedBy);
        Assert.Null(sent.Eighty);

        // The same slot without it: a frame or so more of bulletins, nothing else changes.
        using var other = new TempDirectory();
        var without = Planner(other, 40, () => Iono, null).Plan(Slot);
        Assert.Null(without.PskReporter);
        Assert.Equal(0, without.PskReporterFrames);
        Assert.InRange(without.Broadcast!.BulletinFrames - plan.Broadcast.BulletinFrames, 0, 2);
    }

    [Fact]
    public void ItNeverMakesASlotKey_AndAFeedThatFailsIsNoReading()
    {
        using var state = new TempDirectory();
        var idle = Planner(state, 0, null, () => Spots).Plan(Slot);
        Assert.Empty(idle.Frames);
        Assert.Equal(0, idle.PskReporterFrames);
        Assert.EndsWith("; not sent: this slot sends nothing", HeadEndService.PskReporterLine(idle, Slot), StringComparison.Ordinal);

        using var busy = new TempDirectory();
        var none = Planner(busy, 5, null, () => PskReading.None).Plan(Slot);
        Assert.NotEmpty(none.Frames);
        Assert.Equal(0, none.PskReporterFrames);
        Assert.Equal("pskreporter: UNKNOWN, no reading; nothing to send", HeadEndService.PskReporterLine(none, Slot));

        using var broken = new TempDirectory();
        var failing = Planner(broken, 5, null, () => throw new InvalidOperationException("boom")).Plan(Slot);
        Assert.Equal(none.Frames.Count, failing.Frames.Count);
        Assert.Equal(0, failing.PskReporterFrames);

        // With the feed down it says UNKNOWN on the air, with the counts it had.
        using var down = new TempDirectory();
        var unknown = PskEvaluator.Evaluate(Like20261006(), Slot, TimeSpan.FromMinutes(5));
        var plan = Planner(down, 5, null, () => unknown).Plan(Slot);
        Assert.Equal(2, plan.PskReporterFrames);
        Assert.True(MailcastFrame.TryParse(Of(plan, PskRecord.ObjectLength)[0].Payload, out var frame));
        var decoder = new Mailcast.RaptorQ.ObjectDecoder(frame!.Oti);
        decoder.Add(new Mailcast.RaptorQ.PayloadId(0, frame.EncodingSymbolId), frame.Symbol.Span);
        Assert.True(PskRecord.TryDecode(decoder.TryDecode(), out var sent));
        Assert.True(sent.FeedDown);
        Assert.Equal((IonoState.Unknown, PathVerdict.NoData), (sent.State, sent.At500));
        Assert.Equal(90, sent.Forty!.At(500)!.Spots);
    }

    [Fact]
    public void TheJournalLine_IsPlainAscii_WithBothBands()
    {
        using var state = new TempDirectory();
        var plan = Planner(state, 5, null, () => Spots).Plan(Slot);
        string line = HeadEndService.PskReporterLine(plan, Slot)!;
        Assert.Equal(
            "pskreporter: MARGINAL, last 30 min to 14:00Z (0 min old), UK and Ireland FT8/FT4/WSPR: 40 m 100 km closed (0 spots), 500 km open (90 spots, 18 stations, median -10 dB), 1000 km open (30 spots, 10 stations, median -12 dB); skip zone about 320 km; 80 m (not sent) 100 km open (20 spots, 9 stations, median -3 dB), 500 km too few spots (0 spots), 1000 km too few spots (0 spots); no skip zone, open close in; sent in 2 frames",
            line);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void Status_HasTheReadingNow_TheFeed_AndTheLastSlotsReading_AcrossARestart()
    {
        var time = new FakeTimeProvider(Slot.AddMinutes(10));
        using var state = new TempDirectory();
        var feed = new PskFeedStatus(true, Slot.AddHours(-1), Slot.AddMinutes(9), 140, 151, 0, null);
        var status = new StatusStore(state.Path, time) { PskReporter = () => Spots, PskReporterFeed = () => feed };
        status.RecordSlot(new SlotReport { Slot = Slot, Day = DateOnly.FromDateTime(Slot.UtcDateTime), Outcome = SlotOutcome.Completed, PskReporter = Spots, PskReporterFrames = 2 });
        using var json = JsonDocument.Parse(status.Render());
        var now = json.RootElement.GetProperty("pskReporter");
        Assert.Equal("MARGINAL", now.GetProperty("state").GetString());
        Assert.Equal(320, now.GetProperty("skipZoneKm").GetInt32());
        Assert.Equal("closed", now.GetProperty("at100").GetString());
        var forty = now.GetProperty("forty");
        Assert.Equal("40m", forty.GetProperty("band").GetString());
        var near = forty.GetProperty("bins")[0];
        Assert.Equal((100, 30, 250, "closed", 0, "otherDistances"), (near.GetProperty("km").GetInt32(), near.GetProperty("fromKm").GetInt32(), near.GetProperty("toKm").GetInt32(), near.GetProperty("verdict").GetString(), near.GetProperty("spots").GetInt32(), near.GetProperty("closedBy").GetString()));
        Assert.Equal(-10, forty.GetProperty("bins")[1].GetProperty("snrMedianDb").GetInt32());
        // 80 m is in the status, though not on the air.
        Assert.Equal("open", now.GetProperty("eighty").GetProperty("bins")[0].GetProperty("verdict").GetString());
        Assert.True(json.RootElement.GetProperty("pskReporterFeed").GetProperty("connected").GetBoolean());
        Assert.Equal(140, json.RootElement.GetProperty("pskReporterFeed").GetProperty("spotsHeld").GetInt32());
        var last = json.RootElement.GetProperty("lastSlot");
        Assert.Equal("MARGINAL", last.GetProperty("pskReporter").GetProperty("state").GetString());
        Assert.Equal(2, last.GetProperty("pskReporterFrames").GetInt32());

        var again = new StatusStore(state.Path, time);
        var kept = again.LastSlot!.PskReporter!;
        Assert.Equal(Spots.ObservedUtc, kept.ObservedUtc);
        Assert.Equal(Spots.Forty!.Bins, kept.Forty!.Bins);
        Assert.Equal(Spots.Eighty!.Bins, kept.Eighty!.Bins);
        Assert.Equal(Spots.JournalLine(Slot), kept.JournalLine(Slot));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(again.Render()).RootElement.GetProperty("pskReporter").ValueKind);
    }

    [Fact]
    public void TheConfig_HasItOnByDefault_AndChecksIt()
    {
        Assert.True(Config.PskReporter.Enabled);
        Assert.Equal(("mqtt.pskreporter.info", 1883), (Config.PskReporter.Host, Config.PskReporter.Port));
        var off = HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "pskReporter": {"enabled": false}}""");
        Assert.False(off.PskReporter.Enabled);
        var e = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "pskReporter": {"port": 0, "host": ""}}"""));
        Assert.Contains("\"pskReporter\".\"port\"", e.Message, StringComparison.Ordinal);
        Assert.Contains("\"pskReporter\".\"host\"", e.Message, StringComparison.Ordinal);

        string example = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "headend.example.json"));
        var fromExample = HeadEndConfig.Parse(example.Replace("\"apiKey\": \"\"", "\"apiKey\": \"k\"", StringComparison.Ordinal));
        Assert.Equal(new PskReporterConfig(), fromExample.PskReporter);
    }
}
