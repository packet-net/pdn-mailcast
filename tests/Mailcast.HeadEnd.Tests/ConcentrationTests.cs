using Mailcast.HeadEnd.Intake;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Tests;

/// <summary>Issue #234: the head end finishes a few bulletins at a time, and the bulk senders' bulletins last.</summary>
public class ConcentrationTests
{
    private static string Json(string schedule) => $$"""{"callsign": "M0LTE", "station": {"apiKey": "k"}, "slot": {"everyMinutes": 60}, "schedule": { {{schedule}} } }""";

    [Fact]
    public void Config_DefaultsAreUnchanged_AndNothingIsBulk()
    {
        var rule = HeadEndConfig.Parse(Json("")).ToScheduleOptions().Budget!;
        Assert.Equal(0.6, rule.SlotCap);
        Assert.Equal(6.0, rule.RetireCoverage);
        Assert.Equal(3, rule.SpreadSlots);
        Assert.Null(rule.Bulk);
        Assert.False(rule.IsBulk("LU9DCE"));
    }

    [Fact]
    public void Config_BulkIsAListOfCallsigns_AnySsidAndCaseIgnored()
    {
        var config = HeadEndConfig.Parse(Json("\"bulk\": [\"lu9dce\", \"G4XYZ-3\"], \"retireCoverage\": 5"));
        var rule = config.ToScheduleOptions().Budget!;
        Assert.Equal(5.0, rule.RetireCoverage);
        Assert.True(rule.IsBulk("LU9DCE"));
        Assert.True(rule.IsBulk("lu9dce-5"));
        Assert.True(rule.IsBulk("G4XYZ"));
        Assert.False(rule.IsBulk("G4ABC"));
        Assert.Equal(["G4XYZ", "LU9DCE"], rule.Bulk!.Order());
        Assert.Equal(["lu9dce", "G4XYZ-3"], config.Schedule.Bulk);
    }

    [Fact]
    public void Config_BadBulk_IsRefused()
    {
        Assert.Contains("bulk", Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(Json("\"bulk\": [\"not a callsign\"]"))).Message);
        Assert.Throws<ConfigException>(() => HeadEndConfig.Parse(Json("\"rule\": \"shares\", \"bulk\": [\"LU9DCE\"]")));
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

    [Fact]
    public void AHeldBulletinWithoutFromOrTakenIn_GetsItsSenderFromItsObject_AndRetiresNoEarlierThan48HoursAfterItsDayEnds()
    {
        using var dir = new TempDirectory();
        var options = ScheduleOptions.HourlyBudget with { Budget = new BudgetRule { Bulk = new HashSet<string> { "LU9DCE" } } };
        var day = new DateOnly(2026, 11, 2);
        var original = new HeadEndStore(dir.Path, Compression.Default, options).Offer(Bulletins.Make(1, 3000, from: "LU9DCE"), day, new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero))!;
        // As a store from before this change left it: no From, no TakenIn.
        foreach (string state in Directory.EnumerateFiles(Path.Combine(dir.Path, "bulletins"), "state.txt", SearchOption.AllDirectories))
        {
            File.WriteAllLines(state, File.ReadAllLines(state).Where(l => !l.StartsWith("From:", StringComparison.Ordinal) && !l.StartsWith("TakenIn:", StringComparison.Ordinal)));
        }
        var lines = new List<string>();
        var held = new HeadEndStore(dir.Path, Compression.Default, options, lines.Add);
        var c = Assert.Single(held.InRotation(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("LU9DCE", c.From);
        Assert.True(options.Budget!.IsBulk(c.From));
        Assert.Null(c.TakenIn);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), c.TakenAt);

        // 48 hours after the end of its first-seen day, not after the start of it.
        var end = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        Assert.Single(held.InRotation(end.AddHours(47)));
        Assert.Empty(lines);
        Assert.Empty(held.InRotation(end.AddHours(48)));
        Assert.Empty(held.InRotation(end.AddHours(60)));
        Assert.Single(lines);
        Assert.Empty(held.InRotation(end.AddHours(72))); // too late to be news again
        Assert.Single(lines);
    }

    [Fact]
    public void ABulletinThatIsCarried_SendsItsEnvelopeSeveralTimesASlot_WithFreshPieces()
    {
        using var dir = new TempDirectory();
        var options = ScheduleOptions.HourlyBudget;
        var store = new HeadEndStore(dir.Path, Compression.Default, options);
        var c = store.Offer(Bulletins.Make(1, 6000), new DateOnly(2026, 11, 2), new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.Zero))!;
        Assert.NotNull(c.Envelope);
        var slot = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var budget = new SlotBudget(TimeSpan.FromSeconds(1000), lengths => TimeSpan.FromSeconds(lengths.Count));
        var first = BroadcastScheduler.Plan(store.InRotation(slot), slot, 1, Compression.Default, options, store.DirectoryNextEsi, budget);
        var envelope = Assert.Single(first.Objects, o => o.EnvelopeFor is not null);
        Assert.Equal(c.Envelope!.SourceSymbols * BroadcastScheduler.EnvelopeCopiesPerSlot, envelope.Count);
        store.Commit(first);
        var second = BroadcastScheduler.Plan(store.InRotation(slot.AddHours(1)), slot.AddHours(1), 1, Compression.Default, options, store.DirectoryNextEsi, budget);
        Assert.Equal(envelope.NextEsi, Assert.Single(second.Objects, o => o.EnvelopeFor is not null).FirstEsi);
    }

    private static CarriedBulletin Held(int seed, string from, double coverage, DateTimeOffset taken, ScheduleOptions options)
    {
        var bulletin = Bulletins.Make(seed, 6000, 'B', from);
        var transfer = TransferObject.ForBulletin(bulletin, options.DictionaryId, Compression.Default, MailcastFrame.FixedSymbolSize, MailcastFrame.StandardAlignment);
        return new CarriedBulletin(bulletin.Bid, bulletin.Title, 6000, DateOnly.FromDateTime(taken.UtcDateTime), transfer, (uint)(coverage * transfer.SourceSymbols), coverage > 0 ? taken : null, From: from, TakenIn: taken);
    }

    [Fact]
    public void Bulk_GoesAfterEveryNormalBulletin_ButLeastCoveredFirstWithinEachClass()
    {
        var options = ScheduleOptions.HourlyBudget with { Budget = new BudgetRule { Bulk = new HashSet<string> { "LU9DCE" } } };
        var budget = (int frames) => new SlotBudget(TimeSpan.FromSeconds(frames), lengths => TimeSpan.FromSeconds(lengths.Count));
        var slot = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var freshBulk = Held(1, "LU9DCE", 0, slot.AddHours(-1), options);
        var coveredNormal = Held(2, "G4ABC", 2.0, slot.AddHours(-5), options);
        var freshNormal = Held(3, "G4ABC", 0, slot.AddHours(-2), options);
        var plan = BroadcastScheduler.Plan([freshBulk, coveredNormal, freshNormal], slot, 1, Compression.Default, options, null, budget(200));
        Assert.Equal(2, plan.NormalCarried);
        Assert.Equal(1, plan.BulkCarried);

        // Tight: the normal ones take it all, the less covered first, though the bulk one is the least covered of all.
        int directory = plan.Objects[0].Count;
        var tight = BroadcastScheduler.Plan([freshBulk, coveredNormal, freshNormal], slot, 1, Compression.Default, options, null, budget(directory + 6));
        int Count(SlotBroadcast p, CarriedBulletin c) => p.Objects.Single(o => o.Bid == c.Bid).Count;
        Assert.Equal(0, Count(tight, freshBulk));
        Assert.True(Count(tight, freshNormal) >= Count(tight, coveredNormal));
        Assert.True(Count(tight, freshNormal) > 0);
        Assert.Equal(0, tight.BulkCarried);

        // Bulk bulletins are ordered least covered first among themselves.
        var covered = Held(4, "LU9DCE", 1.0, slot.AddHours(-3), options);
        var bulkOnly = BroadcastScheduler.Plan([covered, freshBulk], slot, 1, Compression.Default, options, null, budget(directory + 6));
        Assert.True(Count(bulkOnly, freshBulk) > Count(bulkOnly, covered));
    }

    [Fact]
    public void LeftoverPieces_AreNotCountedAsCarried()
    {
        var options = ScheduleOptions.HourlyBudget;
        var budget = new SlotBudget(TimeSpan.FromSeconds(1000), lengths => TimeSpan.FromSeconds(lengths.Count));
        var slot = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var spare = Held(1, "G4ABC", 6.5, slot.AddHours(-5), options);
        var fresh = Held(2, "G4ABC", 0, slot.AddHours(-1), options);
        var plan = BroadcastScheduler.Plan([spare, fresh], slot, 1, Compression.Default, options, null, budget);
        Assert.True(plan.Objects.Single(o => o.Bid == spare.Bid).Count > 0);
        Assert.Equal(1, plan.NormalCarried);
    }
}
