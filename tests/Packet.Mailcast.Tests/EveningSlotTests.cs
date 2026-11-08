using System.Globalization;

namespace Packet.Mailcast.Tests;

/// <summary>
/// Evening slots: which slots they are through the year, how the directory carries them (and that receivers from before
/// ignore them), and that their pieces never count against the daylight strategy.
/// </summary>
public class EveningSlotTests
{
    private static readonly EveningRule Until22 = new(new TimeOnly(22, 0));

    private static readonly SlotTimetable Gb7rdg = new(TimeOnly.MinValue, 60, StationFixture.Daylight, Until22);

    private static string[] Hhmm(IEnumerable<DateTimeOffset> slots) => [.. slots.Select(s => s.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture))];

    private static DateOnly D(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Theory]
    // Sunset 17:33 UTC: daylight to 17:00, evening from 18:00.
    [InlineData("2026-10-05", "18:00,19:00,20:00,21:00")]
    // Sunset 15:57: the evening has six slots.
    [InlineData("2026-12-21", "16:00,17:00,18:00,19:00,20:00,21:00")]
    // Sunset 20:25: one.
    [InlineData("2026-06-21", "21:00")]
    public void Gb7rdg_EveningSlots_RunFromSunset_ToBefore2200(string day, string expected)
    {
        var date = D(day);
        Assert.Equal(expected.Split(','), Hhmm(Gb7rdg.EveningSlotsOn(date)));
        // The daylight slots are unchanged, and none is also an evening slot.
        var daylight = new SlotTimetable(TimeOnly.MinValue, 60, StationFixture.Daylight).ActiveSlotsOn(date);
        Assert.Empty(daylight.Intersect(Gb7rdg.EveningSlotsOn(date)));
        Assert.Equal(daylight.Concat(Gb7rdg.EveningSlotsOn(date)).Order(), Gb7rdg.ActiveSlotsOn(date));
        Assert.All(Gb7rdg.EveningSlotsOn(date), s => Assert.True(Gb7rdg.IsActive(s) && Gb7rdg.IsEvening(s) && !StationFixture.Daylight.Allows(s)));
        Assert.All(daylight, s => Assert.False(Gb7rdg.IsEvening(s)));
    }

    [Fact]
    public void ADay_WhoseSunsetIsAtOrAfterTheEnd_HasNone()
    {
        // At Reading in June the sun sets at 20:25 UTC, after an end of 20:00; and 21:00 itself is not before 21:00.
        Assert.Empty(new SlotTimetable(TimeOnly.MinValue, 60, StationFixture.Daylight, new EveningRule(new TimeOnly(20, 0))).EveningSlotsOn(D("2026-06-21")));
        Assert.Empty(new SlotTimetable(TimeOnly.MinValue, 60, StationFixture.Daylight, new EveningRule(new TimeOnly(21, 0))).EveningSlotsOn(D("2026-06-21")));

        // Reykjavik at midsummer: the sun sets after midnight UTC, so after 22:00, and there is no evening at all.
        var reykjavik = new SlotTimetable(TimeOnly.MinValue, 60, new DaylightRule("HP94ad", 120, 30), Until22);
        var sun = reykjavik.Daylight!.WindowOn(D("2026-06-21")).Sun;
        Assert.True(sun.Sunset!.Value >= new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.Zero));
        Assert.Empty(reykjavik.EveningSlotsOn(D("2026-06-21")));
        Assert.Empty(reykjavik.EveningSlotsOn(D("2026-06-22")));
        // In winter it has some, from its own sunset.
        Assert.NotEmpty(reykjavik.EveningSlotsOn(D("2026-12-21")));
    }

    [Fact]
    public void AListenersSunset_EndsTheEveningForThatListener()
    {
        // SA6DAZ in Sweden: it is dark there before Reading's sunset all year, so with the opt-out it hears no evening slot.
        var sweden = Gb7rdg with { Evening = Until22.EndingAtSunsetAt("JO67fm") };
        foreach (var day in new[] { "2026-10-05", "2026-12-21", "2026-06-21" })
        {
            Assert.Empty(sweden.EveningSlotsOn(D(day)));
            Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60, StationFixture.Daylight).ActiveSlotsOn(D(day)), sweden.ActiveSlotsOn(D(day)));
        }

        // A listener in the far west of Ireland keeps the evening slots that start before sunset there.
        var west = Gb7rdg with { Evening = Until22.EndingAtSunsetAt("IO41") };
        Assert.True(Maidenhead.TryParse("IO41", out double lat, out double lon));
        foreach (var day in new[] { "2026-10-05", "2026-12-21", "2026-06-21" })
        {
            var sunset = Solar.SunriseSunset(D(day), lat, lon).Sunset!.Value;
            Assert.Equal(Gb7rdg.EveningSlotsOn(D(day)).Where(s => s < sunset), west.EveningSlotsOn(D(day)));
        }
        Assert.Equal(["16:00"], Hhmm(west.EveningSlotsOn(D("2026-12-21"))));

        // The listener's own sunset is never sent.
        Assert.Equal(Gb7rdg.ToFields(), west.ToFields());
        Assert.Equal(Gb7rdg, SlotTimetable.FromFields(west.ToFields()));
    }

    [Fact]
    public void Fields_RoundTrip_AndAnUnreadableEveningLeavesTheDaylightStanding()
    {
        Assert.Equal(["slots=00:00/60", "daylight=IO91lk/120/30", "evening=22:00"], Gb7rdg.ToFields());
        Assert.Equal(Gb7rdg, SlotTimetable.FromFields(Gb7rdg.ToFields()));

        var daylightOnly = Gb7rdg.WithoutEvening;
        Assert.Equal(daylightOnly, SlotTimetable.FromFields(["slots=00:00/60", "daylight=IO91lk/120/30", "evening=sunset+30/22:00"]));
        Assert.Equal(daylightOnly, SlotTimetable.FromFields(["slots=00:00/60", "daylight=IO91lk/120/30", "evening=25:00"]));
        // Without a daylight rule every slot runs, so an evening means nothing and is not written or read.
        Assert.Equal(new SlotTimetable(TimeOnly.MinValue, 60), SlotTimetable.FromFields(["slots=00:00/60", "evening=22:00"]));
        Assert.Equal(["slots=00:00/60"], new SlotTimetable(TimeOnly.MinValue, 60, null, Until22).ToFields());
    }

    // How a receiver before the evening slots read a timetable: SlotTimetable.FromFields as released, copied here.
    private static SlotTimetable? BeforeEveningFromFields(IEnumerable<string> fields)
    {
        string? slots = null;
        string? daylight = null;
        foreach (var field in fields)
        {
            if (field.StartsWith("slots=", StringComparison.Ordinal))
            {
                slots ??= field["slots=".Length..];
            }
            else if (field.StartsWith("daylight=", StringComparison.Ordinal))
            {
                daylight ??= field["daylight=".Length..];
            }
        }
        if (slots is null)
        {
            return null;
        }
        var s = slots.Split('/');
        if (s.Length != 2
            || !TimeOnly.TryParseExact(s[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor)
            || !int.TryParse(s[1], NumberStyles.None, CultureInfo.InvariantCulture, out int every)
            || every < 1 || 1440 % every != 0)
        {
            return null;
        }
        DaylightRule? rule = null;
        if (daylight is not null)
        {
            var d = daylight.Split('/');
            if (d.Length != 3
                || !int.TryParse(d[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int after)
                || !int.TryParse(d[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int before))
            {
                return null;
            }
            try
            {
                rule = new DaylightRule(d[0], after, before);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        return new SlotTimetable(anchor, every, rule);
    }

    [Fact]
    public void Directory_CarriesTheEvening_AndReceiversFromBefore_ReadTheDaylightSlotsAlone()
    {
        var signer = DirectorySigner.FromPem(DirectorySigner.NewKeyPem());
        var objects = TestBulletins.Day(7, 3).Select(b => (Bulletin: b, Object: TransferObject.ForBulletin(b, ZstdDictionary.Dictionary1Id, Compression.Default))).ToList();
        var entries = objects.Select(o => new DirectoryEntry(o.Object.ObjectId, o.Object.DictionaryId, o.Bulletin.Serialize().Length, o.Bulletin.Bid, o.Bulletin.Title,
            Hash: ObjectId.LongHash(o.Object.DictionaryId, o.Object.Bytes))).ToList();
        var directory = new BroadcastDirectory(new DateOnly(2026, 11, 8), entries, Gb7rdg, "ms110d-wn3", signer);
        byte[] bytes = directory.Serialize();
        string first = Bulletin.TextEncoding.GetString(bytes).Split('\n')[2];
        Assert.Contains("\tslots=00:00/60\tdaylight=IO91lk/120/30\tevening=22:00\t", first, StringComparison.Ordinal);

        var parsed = BroadcastDirectory.Parse(bytes);
        Assert.Equal(Gb7rdg, parsed.Schedule);
        var key = TrustedKey.Parse("TEST", signer.PublicKeyBase64);
        Assert.Equal(SignatureState.Trusted, parsed.Check([key]).State);

        // A receiver from before: the same entries, a signature that checks, and the daylight slots alone, so it simply
        // does not listen in the evening.
        var old = V0181Directory.Parse(bytes);
        Assert.Equal(parsed.Entries, old.Entries);
        Assert.Equal(SignatureState.Trusted, old.Check([key]).State);
        Assert.Equal(Gb7rdg.WithoutEvening, BeforeEveningFromFields(first.Split('\t')[5..]));
        Assert.Equal(StationFixture.Daylight, BeforeEveningFromFields(first.Split('\t')[5..])!.Daylight);
    }

    // The planning side: an airtime of one second a frame, so the budget is a frame count.
    private static readonly DateTimeOffset Afternoon = new(2026, 11, 8, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Evening = new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextMorning = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static readonly ScheduleOptions Options = ScheduleOptions.HourlyBudget with { Timetable = Gb7rdg, Station = "GB7RDG" };

    private static SlotBudget Frames(int frames) => new(TimeSpan.FromSeconds(frames), lengths => TimeSpan.FromSeconds(lengths.Count));

    private static HeadEndStore Store(string path, IEnumerable<Bulletin> bulletins)
    {
        var store = new HeadEndStore(path, Compression.Default, Options);
        int i = 0;
        foreach (var b in bulletins)
        {
            store.Offer(b, DateOnly.FromDateTime(Afternoon.UtcDateTime), Afternoon.AddMinutes(-30).AddSeconds(i++));
        }
        return store;
    }

    private static (string Bid, uint FirstEsi, int Count)[] Shares(SlotBroadcast plan) =>
        [.. plan.Objects.Where(o => o.Bid is not null).Select(o => (o.Bid!, o.FirstEsi, o.Count))];

    private static SlotBroadcast Daylight(HeadEndStore store, DateTimeOffset slot) =>
        BroadcastScheduler.Plan(store.InRotation(slot), slot, 1, Compression.Default, Options, store.DirectoryNextEsi, Frames(60));

    private static SlotBroadcast PlanEvening(HeadEndStore store, DateTimeOffset slot, int frames = 60) =>
        BroadcastScheduler.PlanEvening(store.InRotation(slot), slot, 1, Compression.Default, Options, store.DirectoryNextEsi, Frames(frames));

    [Fact]
    public void EveningPieces_LeaveEverythingTheDaylightSlotsDecide_AsItWas()
    {
        using var withEvening = new TempDirectory();
        using var without = new TempDirectory();
        var bulletins = TestBulletins.Day(11, 5);
        var a = Store(withEvening.Path, bulletins);
        var b = Store(without.Path, bulletins);
        a.Commit(Daylight(a, Afternoon));
        b.Commit(Daylight(b, Afternoon));
        var before = a.InRotation(Evening).ToDictionary(c => c.Bid, c => (c.NextEsi, c.FirstSlot));

        // Only one of the two head ends sends the evening slots.
        var evening = PlanEvening(a, Evening);
        Assert.True(evening.Evening);
        Assert.True(evening.BulletinFrames > 0);
        a.Commit(evening);
        a.Commit(PlanEvening(a, Evening.AddHours(1)));

        // Its daylight counts, first slots, coverage and tiers are untouched, and its evening counts moved.
        var after = a.InRotation(Evening);
        Assert.All(after, c => Assert.Equal(before[c.Bid], (c.NextEsi, c.FirstSlot)));
        Assert.All(after, c => Assert.True(c.EveningNextEsi > 0));
        var rule = Options.Budget!;
        Assert.All(after, c => Assert.Equal(
            BroadcastScheduler.Leftover(b.InRotation(Evening).Single(x => x.Bid == c.Bid), NextMorning, rule),
            BroadcastScheduler.Leftover(c, NextMorning, rule)));

        // So the next daylight slot gives every bulletin exactly what it would have without the evening.
        var nextA = Daylight(a, NextMorning);
        var nextB = Daylight(b, NextMorning);
        Assert.Equal(Shares(nextB), Shares(nextA));
        Assert.Equal(nextB.LeftoverBulletins, nextA.LeftoverBulletins);
        Assert.Equal(nextB.Directory.Entries, nextA.Directory.Entries);
    }

    [Fact]
    public void EveningPieces_AreFresh_NeverADaylightOne_AndARestartNeverRepeatsOne()
    {
        using var dir = new TempDirectory();
        var bulletins = TestBulletins.Day(12, 4);
        var store = Store(dir.Path, bulletins);
        var daylight = Daylight(store, Afternoon);
        store.Commit(daylight);
        var ids = daylight.Objects.Where(o => o.Bid is not null).Select(o => o.Transfer.ObjectId).ToHashSet();
        HashSet<(ulong, uint)> Pieces(SlotBroadcast plan, int count) =>
            [.. plan.Frames.Take(count).Where(f => ids.Contains(f.ObjectId)).Select(f => (f.ObjectId, f.EncodingSymbolId))];
        var sent = Pieces(daylight, daylight.Frames.Count);

        // Half an evening slot goes out, then the head end restarts.
        var first = PlanEvening(store, Evening);
        int half = first.Frames.Count / 2;
        store.Commit(first, half);
        var firstPieces = Pieces(first, half);
        Assert.NotEmpty(firstPieces);
        Assert.Empty(firstPieces.Intersect(sent));
        sent.UnionWith(firstPieces);

        var reopened = new HeadEndStore(dir.Path, Compression.Default, Options);
        Assert.All(reopened.InRotation(Evening), c => Assert.Equal(store.InRotation(Evening).Single(x => x.Bid == c.Bid).EveningNextEsi, c.EveningNextEsi));
        var again = PlanEvening(reopened, Evening);
        var againPieces = Pieces(again, again.Frames.Count);
        Assert.NotEmpty(againPieces);
        Assert.Empty(againPieces.Intersect(sent));
        reopened.Commit(again);
        sent.UnionWith(againPieces);

        // And the next daylight slot carries on from its own count, meeting none of them either.
        var morning = Daylight(reopened, NextMorning);
        Assert.Empty(Pieces(morning, morning.Frames.Count).Intersect(sent));
    }

    [Fact]
    public void AnEveningSlot_CarriesTheLeastEveningCoveredFirst_UpToTheCap_AndNothingRetired()
    {
        var rule = Options.Budget!;
        var bulletins = TestBulletins.Day(13, 3).Select(b => TransferObject.ForBulletin(b, Options.DictionaryId, Compression.Default, MailcastFrame.FixedSymbolSize, MailcastFrame.StandardAlignment) is var t
            ? new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, new DateOnly(2026, 11, 8), t, 0, Afternoon) : null!).ToList();
        // One well covered in the daylight slots (the leftover tier), one already heard a lot in the evening, one retired.
        var leftover = bulletins[0] with { NextEsi = (uint)(7 * bulletins[0].Transfer.SourceSymbols) };
        var eveningCovered = bulletins[1] with { EveningNextEsi = (uint)(2 * bulletins[1].Transfer.SourceSymbols) };
        var retired = bulletins[2] with { FirstSlot = Evening.AddHours(-49) };
        Assert.True(BroadcastScheduler.Leftover(leftover, Evening, rule));
        Assert.True(BroadcastScheduler.Retired(retired, Evening, rule));

        var plenty = BroadcastScheduler.PlanEvening([leftover, eveningCovered, retired], Evening, 1, Compression.Default, Options, null, Frames(1000));
        var shares = plenty.Objects.Where(o => o.Bid is not null).ToDictionary(o => o.Bid!);
        Assert.DoesNotContain(retired.Bid, shares.Keys);
        Assert.DoesNotContain(plenty.Directory.Entries, e => e.Bid == retired.Bid);
        Assert.Equal(BroadcastScheduler.CapSymbols(leftover.Transfer.SourceSymbols, rule), shares[leftover.Bid].Count);
        Assert.Equal(BroadcastScheduler.CapSymbols(eveningCovered.Transfer.SourceSymbols, rule), shares[eveningCovered.Bid].Count);
        Assert.Equal(BroadcastScheduler.EveningFirstCount, shares[leftover.Bid].FirstEsi);
        Assert.Equal(BroadcastScheduler.EveningFirstCount + eveningCovered.EveningNextEsi, shares[eveningCovered.Bid].FirstEsi);

        // Short of room, the leftover-tier one comes first: it has had nothing in the evening yet.
        var tight = Enumerable.Range(1, 100).Select(n => BroadcastScheduler.PlanEvening([leftover, eveningCovered], Evening, 1, Compression.Default, Options, null, Frames(n)))
            .First(p => p.BulletinFrames > 0);
        var tightShares = tight.Objects.Where(o => o.Bid is not null).ToDictionary(o => o.Bid!, o => o.Count);
        Assert.Equal(tight.BulletinFrames, tightShares[leftover.Bid]);
        Assert.Equal(0, tightShares[eveningCovered.Bid]);

        // Nothing in rotation: no bulletin frames, so a planner keys nothing.
        Assert.Equal(0, BroadcastScheduler.PlanEvening([retired], Evening, 1, Compression.Default, Options, null, Frames(1000)).BulletinFrames);
    }
}
