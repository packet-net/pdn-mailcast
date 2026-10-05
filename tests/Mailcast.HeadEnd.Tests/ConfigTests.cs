using Packet.Mailcast;

namespace Mailcast.HeadEnd.Tests;

public class ConfigTests
{
    [Fact]
    public void Parse_TheSmallestConfigurationIsTheApiKey()
    {
        var config = HeadEndConfig.Parse("""{"station": {"apiKey": "k"}}""");
        Assert.Equal("GB7RDG", config.Callsign);
        Assert.Equal(new TimeOnly(12, 0), config.SlotTime);
        var slot = config.ToSlotSettings();
        Assert.Equal(TimeSpan.FromMinutes(2), slot.ChannelWait);
        Assert.Equal(TimeSpan.FromSeconds(30), slot.RenewEvery);
        Assert.Equal(32 * 1024, config.ToScheduleOptions().MaxBulletinSize);
    }

    [Fact]
    public void Parse_TheExampleFileIsValid()
    {
        string example = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "headend.example.json"));

        // As shipped it has no API key, so the service refuses to start until one is set.
        var e = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(example));
        Assert.Contains("apiKey", e.Message, StringComparison.Ordinal);

        var config = HeadEndConfig.Parse(example.Replace("\"apiKey\": \"\"", "\"apiKey\": \"k\"", StringComparison.Ordinal));
        Assert.NotNull(config.Intake.Fbb);
        Assert.Equal(FbbIntakeConfig.LinBpqFbbPort, config.Intake.Fbb.Login);
    }

    [Theory]
    [InlineData("""{"station": {}}""", "apiKey")]
    [InlineData("""{"station": {"apiKey": "k", "leaseSeconds": 60}}""", "leaseSeconds")]
    [InlineData("""{"station": {"apiKey": "k"}, "slot": {"timeUtc": "noon"}}""", "timeUtc")]
    [InlineData("""{"station": {"apiKey": "k"}, "colour": "blue"}""", "colour")]
    [InlineData("""{"station": {"apiKey": "k"}, "callsign": "NOT A CALL"}""", "callsign")]
    [InlineData("""{"station": {"apiKey": "k"}, "flex": {"enabled": true}}""", "flex")]
    [InlineData("""{"station": {"apiKey": "k"}, "schedule": {"symbolSize": 250}}""", "symbolSize")]
    [InlineData("""{"station": {"apiKey": "k"}, "schedule": {"symbolSize": 32}}""", "symbolSize")]
    [InlineData("""{"station": {"apiKey": "k"}, "schedule": {"symbolSize": 1000}}""", "symbolSize")]
    public void Parse_RefusesWhatCannotWork(string json, string mentions)
    {
        var e = Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(json));
        Assert.Contains(mentions, e.Message, StringComparison.Ordinal);
    }
}

public class SlotIntervalTests
{
    private static HeadEndConfig Parse(string slot, string schedule = "{}") =>
        HeadEndConfig.Parse($$"""{"station": {"apiKey": "k"}, "slot": {{slot}}, "schedule": {{schedule}}}""");

    private static void SameSchedule(ScheduleOptions expected, ScheduleOptions actual)
    {
        Assert.Equal(expected.SlotMinutes, actual.SlotMinutes);
        Assert.Equal(expected.SlotShares, actual.SlotShares);
        Assert.Equal(expected.SlotOffsets, actual.SlotOffsets);
        Assert.Equal(expected.CarryOverSlots, actual.CarryOverSlots);
        Assert.Equal(expected.ExtraSymbols, actual.ExtraSymbols);
        Assert.Equal(expected.DirectoryEvery, actual.DirectoryEvery);
        Assert.Equal(expected.RememberDays, actual.RememberDays);
    }

    [Fact]
    public void ADailyConfiguration_IsUnchanged()
    {
        // As every head end was configured before slots had an interval.
        var config = HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "slot": {"timeUtc": "12:00"}}""");
        Assert.Equal(1440, config.Slot.EveryMinutes);
        SameSchedule(new ScheduleOptions(), config.ToScheduleOptions());
        Assert.Equal([0, 1, 2], config.ToScheduleOptions().SlotOffsets);
        var slot = config.ToSlotSettings();
        Assert.Equal(TimeSpan.FromSeconds(30), slot.ToneLength);
        Assert.Equal(TimeSpan.FromMinutes(40), slot.MaxSlotLength);
        Assert.Equal(30, config.Slot.CatchUp);
        var schedule = config.ToSlotSchedule();
        Assert.Equal(TimeSpan.FromDays(1), schedule.Every);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), schedule.SlotAfter(new DateTimeOffset(2026, 10, 5, 11, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void AnHourlyConfiguration_GetsTheHourlyDefaults()
    {
        var config = Parse("""{"timeUtc": "00:00", "everyMinutes": 60}""");
        SameSchedule(ScheduleOptions.Hourly, config.ToScheduleOptions());
        var slot = config.ToSlotSettings();
        Assert.Equal(TimeSpan.FromSeconds(10), slot.ToneLength);
        Assert.Equal(TimeSpan.FromMinutes(10), slot.MaxSlotLength);
        Assert.Equal(5, config.Slot.CatchUp);
        Assert.Equal(TimeSpan.FromHours(1), config.ToSlotSchedule().Every);

        // Set explicitly, they win.
        var set = Parse("""{"everyMinutes": 60, "toneSeconds": 15, "maxMinutes": 20, "catchUpMinutes": 10}""");
        Assert.Equal(TimeSpan.FromSeconds(15), set.ToSlotSettings().ToneLength);
        Assert.Equal(TimeSpan.FromMinutes(20), set.ToSlotSettings().MaxSlotLength);
        Assert.Equal(10, set.Slot.CatchUp);
    }

    [Fact]
    public void OtherIntervals_KeepTheHourlyDefaultsInHours()
    {
        var half = Parse("""{"everyMinutes": 30}""").ToScheduleOptions();
        Assert.Equal([0, 10, 20, 34, 50], half.SlotOffsets);
        Assert.Equal(6, half.CarryOverSlots);
        var two = Parse("""{"everyMinutes": 120}""").ToScheduleOptions();
        Assert.Equal(two.SlotOffsets.Count, two.SlotShares.Count);
        Assert.True(two.SlotOffsets.Zip(two.SlotOffsets.Skip(1)).All(p => p.Second > p.First));
        BroadcastScheduler.Validate(two);
    }

    [Fact]
    public void ScheduleKeys_ReachTheScheduler()
    {
        var config = Parse("""{"everyMinutes": 60}""", """{"slotShares": [1.6, 0.8, 0.8], "slotOffsets": [0, 7, 14], "carryOverSlots": 1, "extraSymbols": 3}""");
        var options = config.ToScheduleOptions();
        Assert.Equal([1.6, 0.8, 0.8], options.SlotShares);
        Assert.Equal([0, 7, 14], options.SlotOffsets);
        Assert.Equal(1, options.CarryOverSlots);
        Assert.Equal(3, options.ExtraSymbols);

        // Shares alone are spread over the default's span.
        var spread = Parse("""{"everyMinutes": 60}""", """{"slotShares": [1.5, 1.0, 1.0]}""").ToScheduleOptions();
        Assert.Equal([0, 12, 25], spread.SlotOffsets);
    }

    [Fact]
    public void TheDailyStationsOldKeys_StillWork()
    {
        var old = Parse("""{"timeUtc": "12:00"}""", """{"daysCarried": 2, "totalOverhead": 3.0, "dayShares": [0.5, 0.5]}""").ToScheduleOptions();
        Assert.Equal([1.5, 1.5], old.SlotShares);
        Assert.Equal([0, 1], old.SlotOffsets);

        // On an hourly station, still a day apart.
        var hourly = Parse("""{"everyMinutes": 60}""", """{"daysCarried": 3}""").ToScheduleOptions();
        Assert.Equal([0, 24, 48], hourly.SlotOffsets);
        Assert.Equal(1.4, hourly.SlotShares[0], 9);
    }

    [Theory]
    [InlineData("""{"everyMinutes": 10}""", "{}", "everyMinutes")]
    [InlineData("""{"everyMinutes": 50}""", "{}", "everyMinutes")]
    [InlineData("""{"everyMinutes": 2880}""", "{}", "everyMinutes")]
    [InlineData("""{"everyMinutes": 60, "maxMinutes": 70}""", "{}", "maxMinutes")]
    [InlineData("""{"everyMinutes": 60, "catchUpMinutes": 60}""", "{}", "catchUpMinutes")]
    [InlineData("""{"everyMinutes": 60}""", """{"slotShares": []}""", "slotShares")]
    [InlineData("""{"everyMinutes": 60}""", """{"slotShares": [0, 1]}""", "slotShares")]
    [InlineData("""{"everyMinutes": 60}""", """{"slotShares": [1.5, 0.7], "slotOffsets": [0]}""", "slotOffsets")]
    [InlineData("""{"everyMinutes": 60}""", """{"slotOffsets": [0, 5, 5, 17, 25]}""", "slotOffsets")]
    [InlineData("""{"everyMinutes": 60}""", """{"slotOffsets": [1, 5, 10, 17, 25]}""", "slotOffsets")]
    [InlineData("""{"everyMinutes": 60}""", """{"daysCarried": 3, "slotShares": [1.5]}""", "not both")]
    [InlineData("""{"everyMinutes": 60}""", """{"daysCarried": 2, "dayShares": [0.7, 0.2]}""", "dayShares")]
    public void Parse_RefusesSlotSettingsThatCannotWork(string slot, string schedule, string mentions)
    {
        var e = Assert.Throws<ConfigException>(() => Parse(slot, schedule));
        Assert.Contains(mentions, e.Message, StringComparison.Ordinal);
    }
}

public class DaylightConfigTests
{
    private static HeadEndConfig Parse(string slot, string schedule = "{}") =>
        HeadEndConfig.Parse($$"""{"station": {"apiKey": "k"}, "slot": {{slot}}, "schedule": {{schedule}}}""");

    [Fact]
    public void Daylight_ReachesTheScheduleTheSchedulerAndTheDirectory_WithLighterCarrying()
    {
        var config = Parse("""{"timeUtc": "00:00", "everyMinutes": 60, "daylight": {"locator": "IO91lk", "afterSunriseMinutes": 120, "beforeSunsetMinutes": 30}}""");
        var expected = new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);
        Assert.Equal(expected, config.ToSlotTimetable());
        Assert.Equal(expected, config.ToSlotSchedule().Timetable);
        var options = config.ToScheduleOptions();
        Assert.Equal(expected, options.Timetable);
        Assert.Equal(ScheduleOptions.HourlyDaylight.SlotShares, options.SlotShares);
        Assert.Equal(ScheduleOptions.HourlyDaylight.SlotOffsets, options.SlotOffsets);

        // Shares given in the file still win.
        var set = Parse("""{"everyMinutes": 60, "daylight": {}}""", """{"slotShares": [1.5, 0.5, 0.5], "slotOffsets": [0, 5, 10]}""");
        Assert.Equal([1.5, 0.5, 0.5], set.ToScheduleOptions().SlotShares);
        Assert.Equal(DaylightRule.Gb7rdg, set.ToSlotSchedule().Daylight);
    }

    [Fact]
    public void WithoutDaylight_EverySlotRuns_AndTheDirectoryStillGivesTheSlots()
    {
        var config = Parse("""{"timeUtc": "00:00", "everyMinutes": 60}""");
        Assert.Null(config.ToSlotSchedule().Daylight);
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60), config.ToScheduleOptions().Timetable);
        Assert.Equal(ScheduleOptions.Hourly.SlotShares, config.ToScheduleOptions().SlotShares);
    }

    [Theory]
    [InlineData("""{"everyMinutes": 60, "daylight": {"locator": "XX99"}}""", "locator")]
    [InlineData("""{"everyMinutes": 60, "daylight": {"locator": "IO91lk", "afterSunriseMinutes": 800}}""", "afterSunriseMinutes")]
    [InlineData("""{"everyMinutes": 60, "daylight": {"afterSunriseMinutes": 600, "beforeSunsetMinutes": 600}}""", "no slot in a whole year")]
    [InlineData("""{"everyMinutes": 60, "daylight": {"sunrise": 1}}""", "sunrise")]
    public void Parse_RefusesDaylightSettingsThatCannotWork(string slot, string mentions)
    {
        var e = Assert.Throws<ConfigException>(() => Parse(slot));
        Assert.Contains(mentions, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExampleFile_IsGb7rdgsDaylightHours()
    {
        string example = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "headend.example.json"));
        var config = HeadEndConfig.Parse(example.Replace("\"apiKey\": \"\"", "\"apiKey\": \"k\"", StringComparison.Ordinal));
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg), config.ToSlotTimetable());
    }
}

public class SymbolSizeTests
{
    [Fact]
    public void A_Symbol_Size_Reaches_The_Scheduler_And_The_Default_Is_Unchanged()
    {
        var small = HeadEndConfig.Parse("""{"station": {"apiKey": "k"}, "schedule": {"symbolSize": 240}}""");
        Assert.Equal(240, small.ToScheduleOptions().SymbolSize);
        var standard = HeadEndConfig.Parse("""{"station": {"apiKey": "k"}}""");
        Assert.Equal(Packet.Mailcast.MailcastFrame.StandardSymbolSize, standard.ToScheduleOptions().SymbolSize);
    }
}

public class ClockSyncTests
{
    [Fact]
    public void KernelClockSync_AnswersWithoutThrowing()
    {
        // Whether this machine is synchronised is not the test's business; that the kernel can be asked is.
        var state = new Mailcast.HeadEnd.Slot.KernelClockSync().Check();
        Assert.False(string.IsNullOrWhiteSpace(state.Detail));
        if (OperatingSystem.IsLinux())
        {
            Assert.NotNull(state.Synchronised);
        }
    }
}
