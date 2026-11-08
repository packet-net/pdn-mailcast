using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Slot;
using Mailcast.HeadEnd.Status;

namespace Mailcast.HeadEnd.Tests;

/// <summary>The head end's evening slots (<c>slot.evening</c>): the setting, the waveforms, and a slot run end to end.</summary>
public class EveningSlotHeadEndTests
{
    private const string EveningSlot = """{"minute": 0, "everyMinutes": 60, "daylight": {"locator": "IO91lk"}, "evening": {"untilUtc": "22:00", "modes": ["ms110d-wn3"]}}""";

    private static readonly string[] DaylightModes = ["ms110d-wn4", "ms110d-wn4-long"];

    private const string TwoModes = """{"modes": ["ms110d-wn4", "ms110d-wn4-long"]}""";

    private static HeadEndConfig Parse(string slot, string schedule = TwoModes) =>
        HeadEndConfig.Parse($$"""{"callsign": "GB7RDG", "station": {"apiKey": "k"}, "slot": {{slot}}, "schedule": {{schedule}}}""");

    private static readonly SlotTimetable Expected = new(TimeOnly.MinValue, 60, StationFixture.Daylight, new EveningRule(new TimeOnly(22, 0)));

    private static readonly DateTimeOffset SixPm = new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Noon = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSetting_ReachesTheTimetableTheScheduleAndTheDirectory_AndLeftOutChangesNothing()
    {
        var config = Parse(EveningSlot);
        Assert.Equal(Expected, config.ToSlotTimetable());
        Assert.Equal(Expected, config.ToSlotSchedule().Timetable);
        Assert.Equal(Expected, config.ToScheduleOptions().Timetable);
        // Midwinter at Reading: sunset 15:57 UTC.
        Assert.EndsWith("; 5 slots, 11:00, 12:00, 13:00, 14:00 and 15:00 UTC; 6 evening slots, 16:00, 17:00, 18:00, 19:00, 20:00 and 21:00 UTC", config.ToSlotSchedule().DescribeDay(new DateOnly(2026, 12, 21)), StringComparison.Ordinal);
        Assert.Equal("evening: extra slots from sunset until 22:00 UTC at IO91lk, on ms110d-wn3; they never count against the daylight slots",
            Program.DescribeEvening(config, config.ToWaveforms(_ => new LinearAirtime(TimeSpan.Zero, TimeSpan.Zero, 0.01))));

        var without = Parse("""{"minute": 0, "everyMinutes": 60, "daylight": {"locator": "IO91lk"}}""");
        Assert.Equal(Expected.WithoutEvening(), without.ToSlotTimetable());
        Assert.Null(without.ToSlotSchedule().Timetable.Evening);
        Assert.DoesNotContain("evening", without.ToSlotSchedule().DescribeDay(new DateOnly(2026, 11, 8)), StringComparison.Ordinal);
        Assert.Null(Program.DescribeEvening(without, without.ToWaveforms(_ => new LinearAirtime(TimeSpan.Zero, TimeSpan.Zero, 0.01))));
    }

    [Fact]
    public void EveningSlots_TakeTheirOwnModes_AndTheModemIsSwitchedEverySlot()
    {
        var airtime = new LinearAirtime(TimeSpan.Zero, TimeSpan.Zero, 0.01);
        var waveforms = Parse(EveningSlot).ToWaveforms(_ => airtime);
        Assert.True(waveforms.SetOnModem);
        Assert.All(Enumerable.Range(17, 5), h => Assert.Equal("ms110d-wn3", waveforms.ModeFor(SixPm.Date.AddHours(h))));
        Assert.Contains(waveforms.ModeFor(Noon), DaylightModes);
        Assert.Equal([3, 0], waveforms.For(SixPm).SetHardware);

        // Without schedule.modes the daylight slots are on the modem's own mode, switched back to it each slot.
        var plain = Parse(EveningSlot, "{}").ToWaveforms(_ => airtime);
        Assert.True(plain.SetOnModem);
        Assert.Equal("ms110d-wn4", plain.ModeFor(Noon));
        Assert.NotNull(plain.For(Noon).SetHardware);
        Assert.Equal("ms110d-wn3", plain.ModeFor(SixPm));

        // Without evening modes the evening slots take the daylight turns.
        var same = Parse("""{"minute": 0, "everyMinutes": 60, "daylight": {"locator": "IO91lk"}, "evening": {"untilUtc": "22:00"}}""").ToWaveforms(_ => airtime);
        Assert.Null(same.EveningModes);
        Assert.Contains(same.ModeFor(SixPm), DaylightModes);
    }

    [Theory]
    [InlineData("""{"everyMinutes": 60, "evening": {"untilUtc": "22:00"}}""", TwoModes, "needs \"slot\".\"daylight\"")]
    [InlineData("""{"everyMinutes": 60, "daylight": {"locator": "IO91lk"}, "evening": {"untilUtc": "22"}}""", TwoModes, "\"untilUtc\" '22' is not HH:mm")]
    [InlineData("""{"everyMinutes": 60, "daylight": {"locator": "IO91lk"}, "evening": {}}""", TwoModes, "\"untilUtc\"")]
    [InlineData("""{"everyMinutes": 60, "daylight": {"locator": "IO91lk"}, "evening": {"untilUtc": "22:00"}}""", """{"rule": "shares"}""", "needs the budget rule")]
    [InlineData("""{"everyMinutes": 60, "daylight": {"locator": "IO91lk"}, "evening": {"untilUtc": "22:00", "modes": []}}""", TwoModes, "at least one mode")]
    [InlineData("""{"everyMinutes": 60, "daylight": {"locator": "IO91lk"}, "evening": {"untilUtc": "22:00", "modes": ["afsk1200"]}}""", TwoModes, "'afsk1200' is not an MS110D waveform")]
    [InlineData("""{"everyMinutes": 60, "daylight": {"locator": "IO91lk"}, "evening": {"untilUtc": "22:00", "fromUtc": "18:00"}}""", TwoModes, "fromUtc")]
    public void Parse_RefusesEveningSettingsThatCannotWork(string slot, string schedule, string mentions)
    {
        var e = Assert.Throws<ConfigException>(() => Parse(slot, schedule));
        Assert.Contains(mentions, e.Message, StringComparison.Ordinal);
    }

    private sealed class Head
    {
        public Head(TempDirectory dir, DateTimeOffset start)
        {
            Time = new VirtualTime(start);
            Station = new FakeStation(Time, 4);
            var config = Parse(EveningSlot);
            var options = config.ToScheduleOptions();
            Store = new RotationStore(dir.Path, Compression.Default, options, Journal, Time);
            Status = new StatusStore(null, Time);
            var planner = new StoreSlotPlanner(Store, Compression.Default, options, config.ToWaveforms(_ => FakeAirtime.For(Station)), config.ToSlotSettings(), config.FillLimitAfter, Time);
            var runner = new SlotRunner(new SlotSettings { Callsign = "GB7RDG", SubChannel = 4 }, Station, Station, null, FakeAirtime.For(Station), Journal, Time, new FakeClockSync());
            Service = new HeadEndService(config.ToSlotSchedule(), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), planner, Store, [], runner, Status, Journal, Time);
        }

        public VirtualTime Time { get; }

        public FakeStation Station { get; }

        public RotationStore Store { get; }

        public StatusStore Status { get; }

        public HeadEndService Service { get; }

        public MemoryJournal Journal { get; } = new();

        public SlotReport Run(DateTimeOffset slot, string? requestedBy = null) => Time.Run(() => Service.RunSlotAsync(slot, CancellationToken.None, requestedBy));
    }

    [Fact]
    public void AnEveningSlot_GoesOut_MarkedAsEvening_AndLeavesTheDaylightCountsAlone()
    {
        using var dir = new TempDirectory();
        var head = new Head(dir, SixPm);
        foreach (int seed in Enumerable.Range(1, 4))
        {
            head.Store.Offer(Bulletins.Make(seed, 3000), new DateOnly(2026, 11, 8));
        }

        var report = head.Run(SixPm);
        Assert.Equal(SlotOutcome.Completed, report.Outcome);
        Assert.True(report.Evening);
        Assert.Equal("ms110d-wn3", report.Mode);
        Assert.True(report.FramesSent > 0);
        Assert.Contains(head.Journal.Lines, l => l.StartsWith("evening slot 2026-11-08 18:00Z: done, ", StringComparison.Ordinal));
        Assert.DoesNotContain(head.Journal.Lines, l => l.StartsWith("slot 2026-11-08 18:00Z", StringComparison.Ordinal));
        Assert.All(head.Journal.Lines, l => Assert.True(l.All(char.IsAscii), l));
        Assert.Contains("\"evening\":true", head.Status.Render().Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);

        // Not one daylight count or first slot moved; the evening counts did.
        var held = head.Store.InRotation(SixPm.AddHours(1));
        Assert.Equal(4, held.Count);
        Assert.All(held, c => Assert.Equal((0u, (DateTimeOffset?)null), (c.NextEsi, c.FirstSlot)));
        Assert.All(held, c => Assert.True(c.EveningNextEsi > 0));
    }

    [Fact]
    public void ADaylightSlot_IsNotMarked_AndAOneOffSlotInTheEvening_IsANormalOne()
    {
        using var dir = new TempDirectory();
        var head = new Head(dir, Noon);
        head.Store.Offer(Bulletins.Make(1, 3000), new DateOnly(2026, 11, 8));
        var report = head.Run(Noon);
        Assert.False(report.Evening);
        Assert.Contains(head.Journal.Lines, l => l.StartsWith("slot 2026-11-08 12:00Z: done, ", StringComparison.Ordinal));
        Assert.Contains("\"evening\":false", head.Status.Render().Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(Noon, head.Store.InRotation(Noon.AddHours(1)).Single().FirstSlot);

        using var other = new TempDirectory();
        var asked = new Head(other, SixPm);
        asked.Store.Offer(Bulletins.Make(1, 3000), new DateOnly(2026, 11, 8));
        var oneOff = asked.Run(SixPm, "a test");
        Assert.False(oneOff.Evening);
        Assert.Equal(SixPm, asked.Store.InRotation(SixPm.AddHours(1)).Single().FirstSlot);
    }
}
