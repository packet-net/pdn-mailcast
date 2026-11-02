using Mailcast.HeadEnd.Intake;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Tests;

/// <summary>Issue #234: the head end finishes a few bulletins at a time, and the bulk senders' bulletins last.</summary>
public class ConcentrationTests
{
    private static string Json(string schedule) => $$"""{"callsign": "M0LTE", "station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": { {{schedule}} } }""";

    [Fact]
    public void Config_DefaultsAre2KASlot_4KTarget_AndNothingBulk()
    {
        var rule = HeadEndConfig.Parse(Json("")).ToScheduleOptions().Budget!;
        Assert.Equal(2.0, rule.SlotCap);
        Assert.Equal(4.0, rule.Target);
        Assert.Null(rule.Bulk);
        Assert.False(rule.IsBulk("LU9DCE"));
    }

    [Fact]
    public void Config_BulkIsAListOfCallsigns_AnySsidAndCaseIgnored()
    {
        var config = HeadEndConfig.Parse(Json("\"bulk\": [\"lu9dce\", \"G4XYZ-3\"], \"target\": 3, \"slotCap\": 1.5"));
        var rule = config.ToScheduleOptions().Budget!;
        Assert.Equal(3.0, rule.Target);
        Assert.Equal(1.5, rule.SlotCap);
        Assert.True(rule.IsBulk("LU9DCE"));
        Assert.True(rule.IsBulk("lu9dce-5"));
        Assert.True(rule.IsBulk("G4XYZ"));
        Assert.False(rule.IsBulk("G4ABC"));
        Assert.Equal(["G4XYZ", "LU9DCE"], rule.Bulk!.Order());
        Assert.Equal(["lu9dce", "G4XYZ-3"], config.Schedule.Bulk);
    }

    [Fact]
    public void Config_BadBulkOrTarget_IsRefused()
    {
        Assert.Contains("bulk", Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(Json("\"bulk\": [\"not a callsign\"]"))).Message);
        Assert.Contains("target", Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(Json("\"target\": 0"))).Message);
        Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(Json("\"rule\": \"shares\", \"bulk\": [\"LU9DCE\"]")));
    }

    [Fact]
    public void Config_OldRetireCoverageAndSpreadSlots_AreStillReadButIgnored()
    {
        var config = HeadEndConfig.Parse(Json("\"retireCoverage\": 6, \"spreadSlots\": 3, \"slotCap\": 0.6"));
        Assert.Equal(6, config.Schedule.RetireCoverage);
        Assert.Equal(3, config.Schedule.SpreadSlots);
        var rule = config.ToScheduleOptions().Budget!;
        Assert.Equal(4.0, rule.Target);
        Assert.Equal(0.6, rule.SlotCap);
    }

    [Fact]
    public void Store_KeepsWhoSentItAndWhenItWasTakenIn_AcrossARestart_InTheOrderTakenIn()
    {
        using var dir = new TempDirectory();
        var options = ScheduleOptions.HourlyBudget;
        var day = new DateOnly(2026, 11, 2);
        var at = new DateTimeOffset(2026, 11, 2, 7, 30, 15, 250, TimeSpan.Zero);
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var a = store.Offer(Bulletins.Make(1, 3000, from: "LU9DCE"), day, at)!;
        var b = store.Offer(Bulletins.Make(2, 3000, from: "G4ABC"), day, at)!; // the same moment: nudged on, never equal
        Assert.Equal("LU9DCE", a.From);
        Assert.Equal(at, a.TakenIn);
        Assert.True(b.TakenIn > a.TakenIn);

        var again = new HeadEndStore(dir.Path, Compression.Default, options).InRotation(at.AddHours(1)).OrderBy(c => c.TakenAt).ToList();
        Assert.Equal([a.Bid, b.Bid], again.Select(c => c.Bid));
        Assert.Equal(["LU9DCE", "G4ABC"], again.Select(c => c.From));
        Assert.Equal(a.TakenIn, again[0].TakenIn);
    }

    [Fact]
    public void Store_RetiresABulletinNotStartedIn48Hours_WithOneJournalLine()
    {
        using var dir = new TempDirectory();
        var lines = new List<string>();
        var options = ScheduleOptions.HourlyBudget;
        var at = new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);
        var store = new HeadEndStore(dir.Path, Compression.Default, options, lines.Add);
        var c = store.Offer(Bulletins.Make(1, 3000, from: "LU9DCE"), new DateOnly(2026, 11, 2), at)!;
        Assert.Single(store.InRotation(at.AddHours(47)));
        Assert.Empty(lines);
        Assert.Empty(store.InRotation(at.AddHours(48)));
        Assert.Empty(store.InRotation(at.AddHours(49)));
        var line = Assert.Single(lines);
        Assert.Contains(c.Bid, line);
        Assert.Contains("LU9DCE", line);
        Assert.All(line, ch => Assert.True(ch < 128));
    }

    [Fact]
    public void RotationStore_TakesABulletinInAtTheClockOrAtMidnightOfADifferentDay()
    {
        using var dir = new TempDirectory();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 41, 0, TimeSpan.Zero));
        var store = new RotationStore(dir.Path, Compression.Default, ScheduleOptions.HourlyBudget, new MemoryJournal(), clock);
        Assert.Equal(OfferOutcome.Added, store.Offer(Bulletins.Make(1, 3000), new DateOnly(2026, 11, 2)));
        Assert.Equal(OfferOutcome.Added, store.Offer(Bulletins.Make(2, 3000), new DateOnly(2026, 11, 1)));
        var held = store.InRotation(clock.GetUtcNow()).OrderBy(c => c.TakenAt).ToList();
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), held[0].TakenIn);
        Assert.Equal(clock.GetUtcNow(), held[1].TakenIn);
    }
}
