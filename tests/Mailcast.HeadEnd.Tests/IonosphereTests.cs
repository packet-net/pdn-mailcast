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
/// The ionosonde reading at the head end: sent inside the airtime budget in slots that key anyway,
/// never deciding whether a slot keys, logged once a slot, and in the report, /status and the config.
/// </summary>
public class IonosphereTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 6, 14, 0, 0, TimeSpan.Zero);

    private static readonly HeadEndConfig Config = HeadEndConfig.Parse("""
        {
          "station": { "apiKey": "k", "mode": "ms110d-wn4", "maxBurstSeconds": 18 },
          "slot": { "timeUtc": "00:00", "everyMinutes": 60, "maxMinutes": 10, "toneSeconds": 10, "channelWaitSeconds": 120 },
          "schedule": { "symbolSize": 240 }
        }
        """);

    private static readonly IonoReading Marginal = IonoEvaluator.Evaluate(
        [new IonoSounding("RL052", Slot.AddMinutes(-12), IonoSource.PropQuest, 6.05, M3000: 3.3)], new IonoSettings(), Slot);

    private static (StoreSlotPlanner Planner, SlotSettings Settings) Planner(TempDirectory state, int bulletins, Func<IonoReading>? reading)
    {
        var options = Config.ToScheduleOptions();
        var store = new RotationStore(state.Path, Compression.Default, options, new MemoryJournal());
        for (int i = 0; i < bulletins; i++)
        {
            store.Offer(Bulletins.Make(700 + i, 6000), new DateOnly(2026, 10, 6));
        }
        var settings = Config.ToSlotSettings();
        var waveforms = Config.ToWaveforms(LinearAirtime.Measure);
        return (new StoreSlotPlanner(store, Compression.Default, options, waveforms, settings, Config.FillLimitAfter, new FakeTimeProvider(Slot.AddSeconds(30)), reading), settings);
    }

    [Fact]
    public void ABusySlot_CarriesTheReadingInTwoFrames_InsideTheSameBudget()
    {
        using var state = new TempDirectory();
        var (planner, settings) = Planner(state, 40, () => Marginal);
        var plan = planner.Plan(Slot);
        Assert.Equal(2, plan.IonosphereFrames);
        Assert.Same(Marginal, plan.Ionosphere);
        var reading = plan.Frames.Where(f => MailcastFrame.TryParse(f.Payload, out var m) && m!.Oti.TransferLength == IonoRecord.ObjectLength).ToList();
        Assert.Equal([0u, 1u], reading.Select(f => f.Esi));
        Assert.All(reading, f => Assert.Equal(55, f.Payload.Length));
        Assert.DoesNotContain(plan.Broadcast!.Directory.Entries, e => e.ObjectId == reading[0].ObjectId);

        TimeSpan onAir = new SlotAirtime(settings, plan.Waveform!.Airtime).ForPayloads([.. plan.Frames.Select(f => f.Payload.Length)]);
        Assert.True(onAir <= Config.FillLimitAfter(TimeSpan.FromSeconds(30)), $"{onAir} on the air");

        // The same slot without the reading: the same budget, a frame or so more of bulletins.
        using var other = new TempDirectory();
        var without = Planner(other, 40, null).Planner.Plan(Slot);
        Assert.Null(without.Ionosphere);
        Assert.InRange(without.Broadcast!.BulletinFrames - plan.Broadcast.BulletinFrames, 0, 2);
    }

    [Fact]
    public void ASlotWithNothingToSend_StillSendsNothing_AndNoReadingSendsNothing()
    {
        using var state = new TempDirectory();
        var idle = Planner(state, 0, () => Marginal).Planner.Plan(Slot);
        Assert.Empty(idle.Frames);
        Assert.Equal(0, idle.IonosphereFrames);
        Assert.EndsWith("; not sent: this slot sends nothing", HeadEndService.IonosphereLine(idle, Slot), StringComparison.Ordinal);

        using var busy = new TempDirectory();
        var none = Planner(busy, 5, () => IonoReading.None).Planner.Plan(Slot);
        Assert.NotEmpty(none.Frames);
        Assert.Equal(0, none.IonosphereFrames);
        Assert.Equal("ionosonde: UNKNOWN, no reading; nothing to send", HeadEndService.IonosphereLine(none, Slot));

        // A reading that cannot be had is no reading: the slot is planned as ever.
        using var broken = new TempDirectory();
        var failing = Planner(broken, 5, () => throw new InvalidOperationException("boom")).Planner.Plan(Slot);
        Assert.Equal(none.Frames.Count, failing.Frames.Count);
        Assert.Equal(0, failing.IonosphereFrames);
    }

    [Fact]
    public void AStaleReading_IsStillSent_AsUnknownWithItsLastValues()
    {
        using var state = new TempDirectory();
        var stale = Marginal.AsOf(Slot.AddHours(2), TimeSpan.FromMinutes(45));
        var plan = Planner(state, 5, () => stale).Planner.Plan(Slot);
        Assert.Equal(2, plan.IonosphereFrames);
        var frame = plan.Frames.Select(f => MailcastFrame.TryParse(f.Payload, out var m) ? m! : null).First(m => m!.Oti.TransferLength == IonoRecord.ObjectLength)!;
        var decoder = new Mailcast.RaptorQ.ObjectDecoder(frame.Oti);
        decoder.Add(new Mailcast.RaptorQ.PayloadId(0, frame.EncodingSymbolId), frame.Symbol.Span);
        Assert.True(IonoRecord.TryDecode(decoder.TryDecode(), out var sent));
        Assert.Equal((IonoState.Unknown, 6.05, 132), (sent.State, sent.FoF2, sent.AgeMinutes));
    }

    [Fact]
    public void TheJournalLine_IsPlainAscii_WithTheNumbers()
    {
        using var state = new TempDirectory();
        var plan = Planner(state, 5, () => Marginal).Planner.Plan(Slot);
        string line = HeadEndService.IonosphereLine(plan, Slot)!;
        Assert.Equal(
            "ionosonde: MARGINAL, Chilton RL052 at 2026-10-06 13:48Z (12 min old, PROPquest): foF2 6.05 MHz, MUF 6.05/6.83/8.76 MHz at 100/500/1000 km (MUFs estimated from foF2 and M(3000)F2); 100 km closed, 500 km closed, 1000 km good; skip zone about 570 km; sent in 2 frames",
            line);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void Status_HasIonoNow_AndTheLastSlotsReading()
    {
        var time = new FakeTimeProvider(Slot.AddMinutes(10));
        using var state = new TempDirectory();
        var status = new StatusStore(state.Path, time) { Ionosphere = () => Marginal.AsOf(time.GetUtcNow(), TimeSpan.FromMinutes(45)) };
        status.RecordSlot(new SlotReport { Slot = Slot, Day = DateOnly.FromDateTime(Slot.UtcDateTime), Outcome = SlotOutcome.Completed, Ionosphere = Marginal, IonosphereFrames = 2 });
        using var json = JsonDocument.Parse(status.Render());
        var iono = json.RootElement.GetProperty("iono");
        Assert.Equal("MARGINAL", iono.GetProperty("state").GetString());
        Assert.Equal(6.05, iono.GetProperty("foF2").GetDouble());
        Assert.Equal(570, iono.GetProperty("skipZoneKm").GetInt32());
        Assert.Equal("RL052", iono.GetProperty("station").GetString());
        Assert.Equal(22, iono.GetProperty("ageMinutes").GetInt32());
        Assert.Equal("propquest", iono.GetProperty("source").GetString());
        Assert.Equal("estimated", iono.GetProperty("method").GetString());
        foreach (string key in new[] { "mufd100", "mufd500", "mufd1000", "soundingTimeUtc" })
        {
            Assert.True(iono.TryGetProperty(key, out _), key);
        }
        var last = json.RootElement.GetProperty("lastSlot");
        Assert.Equal("MARGINAL", last.GetProperty("ionosphere").GetProperty("state").GetString());
        Assert.Equal(2, last.GetProperty("ionosphereFrames").GetInt32());

        // The report survives a restart, reading and all.
        var again = new StatusStore(state.Path, time);
        Assert.Equal(Marginal, again.LastSlot!.Ionosphere);
        Assert.False(JsonDocument.Parse(again.Render()).RootElement.TryGetProperty("iono", out var off) && off.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void Config_HasTheDefaults_AndRefusesNonsense()
    {
        var settings = Config.Ionosphere.ToSettings();
        Assert.Equal((7.1, 0.85, TimeSpan.FromMinutes(45)), (settings.OpenMhz, settings.ReliableFactor, settings.StaleAfter));
        Assert.Equal(["RL052", "FF051", "DB049"], settings.Stations);
        var custom = HeadEndConfig.Parse("""{ "station": { "apiKey": "k" }, "ionosphere": { "openMhz": 7.2, "stations": ["ff051"] } }""");
        Assert.Equal(["FF051"], custom.Ionosphere.ToSettings().Stations);
        Assert.Empty(HeadEndConfig.Parse("""{ "station": { "apiKey": "k" }, "ionosphere": { "stations": [] } }""").Ionosphere.Stations);
        var e = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""{ "station": { "apiKey": "k" }, "ionosphere": { "openMhz": 0, "reliableFactor": 1.5, "staleMinutes": -1, "stations": ["Chilton"] } }"""));
        Assert.Contains("\"openMhz\"", e.Message, StringComparison.Ordinal);
        Assert.Contains("\"reliableFactor\"", e.Message, StringComparison.Ordinal);
        Assert.Contains("\"staleMinutes\"", e.Message, StringComparison.Ordinal);
        Assert.Contains("\"stations\"", e.Message, StringComparison.Ordinal);
        Assert.Throws<ConfigException>(() => HeadEndConfig.Parse("""{ "station": { "apiKey": "k" }, "ionosphere": { "pollMinutes": 5 } }"""));

        string example = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "headend.example.json"));
        var fromExample = HeadEndConfig.Parse(example.Replace("\"apiKey\": \"\"", "\"apiKey\": \"k\"", StringComparison.Ordinal));
        Assert.Equal(new IonosphereConfig().Stations, fromExample.Ionosphere.Stations);
    }

    [Fact]
    public void TheReadingIsKeptFresh_OnlyNearASlot()
    {
        var schedule = Config.ToSlotSchedule();
        // In daylight with hourly slots there is always one within the hour.
        Assert.True(Program.WantReading(schedule, new DateTimeOffset(2026, 10, 6, 13, 30, 0, TimeSpan.Zero)));
        // A daily slot at noon: from 11:00 to 12:15 only.
        var daily = SlotSchedule.Daily(new TimeOnly(12, 0));
        Assert.False(Program.WantReading(daily, new DateTimeOffset(2026, 10, 6, 10, 59, 0, TimeSpan.Zero)));
        Assert.True(Program.WantReading(daily, new DateTimeOffset(2026, 10, 6, 11, 0, 0, TimeSpan.Zero)));
        Assert.True(Program.WantReading(daily, new DateTimeOffset(2026, 10, 6, 12, 15, 0, TimeSpan.Zero)));
        Assert.False(Program.WantReading(daily, new DateTimeOffset(2026, 10, 6, 12, 16, 0, TimeSpan.Zero)));
    }
}
