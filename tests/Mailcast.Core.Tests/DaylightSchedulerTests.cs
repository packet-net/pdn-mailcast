using System.Globalization;

namespace Mailcast.Core.Tests;

/// <summary>Carrying bulletins when only the daylight slots run.</summary>
public class DaylightSchedulerTests
{
    private static readonly SlotTimetable Gb7rdg = new(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg);

    private static readonly ScheduleOptions FiveCarryings = ScheduleOptions.Hourly with { SymbolSize = 240, Timetable = Gb7rdg };

    private static DateTimeOffset T(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    [Fact]
    public void Repeats_InTheDark_MoveToTheNextDaylightSlot_EachToItsOwn()
    {
        // First carried at 15:00 on 5 October: the hourly repeats are due 20:00, 01:00, 08:00 and
        // 16:00 the next day. The first three are dark (the 6th's window opens about 08:13), so they
        // take the 6th's first three daylight slots in turn, and 16:00 is daylight.
        var slots = BroadcastScheduler.CarryingSlots(T("2026-10-05T15:00:00Z"), FiveCarryings);
        Assert.Equal(
            [T("2026-10-05T15:00:00Z"), T("2026-10-06T09:00:00Z"), T("2026-10-06T10:00:00Z"), T("2026-10-06T11:00:00Z"), T("2026-10-06T16:00:00Z")],
            slots);
        // Three carry-over slots in daylight after 16:00: 17:00, then 09:00 and 10:00 on the 7th.
        Assert.Equal(T("2026-10-07T11:00:00Z"), BroadcastScheduler.RotationEnd(T("2026-10-05T15:00:00Z"), FiveCarryings));

        var daylight = ScheduleOptions.HourlyDaylight with { Timetable = Gb7rdg };
        Assert.Equal([T("2026-10-05T15:00:00Z"), T("2026-10-06T09:00:00Z")], BroadcastScheduler.CarryingSlots(T("2026-10-05T15:00:00Z"), daylight));
    }

    [Fact]
    public void WithoutARule_CarryingSlotsAndRotation_AreTheHourlyOnes()
    {
        var plain = ScheduleOptions.Hourly with { Timetable = new SlotTimetable(TimeOnly.MinValue, 60) };
        var first = T("2026-10-05T15:00:00Z");
        Assert.Equal([0, 5, 10, 17, 25], BroadcastScheduler.CarryingSlots(first, plain).Select(s => (int)(s - first).TotalHours));
        Assert.Equal(first.AddHours(29), BroadcastScheduler.RotationEnd(first, plain));
        var carried = new CarriedBulletin("1_X", "t", 10, new DateOnly(2026, 10, 5), TransferObject.ForBulletin(TestBulletins.Day(1, 1)[0], ZstdDictionary.Gb7rdg1Id, Compression.Default), 0, first);
        for (int h = -2; h < 32; h++)
        {
            var slot = first.AddHours(h);
            Assert.Equal(BroadcastScheduler.InRotation(carried, slot, ScheduleOptions.Hourly), BroadcastScheduler.InRotation(carried, slot, plain));
        }
    }

    /// <summary>
    /// Runs every daylight slot from <paramref name="start"/> for <paramref name="days"/> days,
    /// with bulletins taken in at the given times, each slot going out whole except those in
    /// <paramref name="skipped"/>. Returns every (object, ESI) sent, by slot.
    /// </summary>
    private static List<(DateTimeOffset Slot, ulong ObjectId, uint Esi)> Run(ScheduleOptions options, List<(DateTimeOffset At, Bulletin Bulletin)> arrivals, DateTimeOffset start, int days, HashSet<DateTimeOffset>? skipped, out List<CarriedBulletin> held)
    {
        held = [];
        var sent = new List<(DateTimeOffset, ulong, uint)>();
        var directories = new Dictionary<ulong, uint>();
        int next = 0;
        foreach (var slot in Enumerable.Range(0, days).SelectMany(d => options.Timetable!.ActiveSlotsOn(DateOnly.FromDateTime(start.AddDays(d).UtcDateTime))))
        {
            while (next < arrivals.Count && arrivals[next].At <= slot)
            {
                var b = arrivals[next++].Bulletin;
                held.Add(new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, DateOnly.FromDateTime(slot.UtcDateTime),
                    TransferObject.ForBulletin(b, options.DictionaryId, Compression.Default, options.SymbolSize, options.Alignment), 0));
            }
            if (skipped?.Contains(slot) == true)
            {
                continue;
            }
            var snapshot = held;
            var plan = BroadcastScheduler.Plan(snapshot.Where(c => BroadcastScheduler.InRotation(c, slot, options)), slot, 1, Compression.Default, options, id => directories.GetValueOrDefault(id));
            Assert.Equal(options.Timetable, plan.Directory.Schedule);
            foreach (var f in plan.Frames)
            {
                sent.Add((slot, f.ObjectId, f.EncodingSymbolId));
            }
            foreach (var o in plan.Objects)
            {
                if (o.Bid is null)
                {
                    directories[o.Transfer.ObjectId] = o.NextEsi;
                    continue;
                }
                int at = held.FindIndex(c => c.Bid == o.Bid);
                held[at] = held[at] with { NextEsi = o.NextEsi, FirstSlot = held[at].FirstSlot ?? slot };
            }
        }
        return sent;
    }

    private static List<(DateTimeOffset At, Bulletin Bulletin)> Arrivals()
    {
        // Ten bulletins through a day and a night, several of them after dark.
        var bulletins = TestBulletins.Day(7, 10);
        int[] hours = [1, 4, 8, 10, 13, 15, 16, 18, 21, 23];
        return [.. bulletins.Select((b, i) => (T("2026-10-05T00:30:00Z").AddHours(hours[i] - 0.5), b))];
    }

    [Fact]
    public void EveryCarrying_GoesOutInItsOwnDaylightSlot_NoEsiTwice_NoneLost()
    {
        var sent = Run(FiveCarryings, Arrivals(), T("2026-10-05T00:00:00Z"), 5, null, out var held);
        Assert.All(sent, s => Assert.True(Gb7rdg.IsActive(s.Slot), $"{s.Slot:u} is dark"));
        Assert.Equal(sent.Count, sent.Select(s => (s.ObjectId, s.Esi)).Distinct().Count());
        foreach (var c in held)
        {
            var counts = BroadcastScheduler.SymbolsPerCarrying(c.Transfer.SourceSymbols, FiveCarryings);
            var carryings = BroadcastScheduler.CarryingSlots(c.FirstSlot!.Value, FiveCarryings);
            Assert.Equal(carryings.Count, carryings.Distinct().Count());
            for (int i = 0; i < counts.Length; i++)
            {
                Assert.Equal(counts[i], sent.Count(s => s.ObjectId == c.Transfer.ObjectId && s.Slot == carryings[i]));
            }
            Assert.Equal(counts.Sum(), sent.Count(s => s.ObjectId == c.Transfer.ObjectId));
            Assert.Equal((uint)counts.Sum(), c.NextEsi);
        }
    }

    [Fact]
    public void ASkippedDaylightSlot_IsMadeUpInTheNext_WithFreshEsis()
    {
        // The morning's first slot does not run; the 10:00 one sends what it owed on top of its own.
        var skipped = new HashSet<DateTimeOffset> { T("2026-10-06T09:00:00Z") };
        var sent = Run(FiveCarryings, Arrivals(), T("2026-10-05T00:00:00Z"), 5, skipped, out var held);
        Assert.DoesNotContain(sent, s => s.Slot == T("2026-10-06T09:00:00Z"));
        Assert.Equal(sent.Count, sent.Select(s => (s.ObjectId, s.Esi)).Distinct().Count());
        foreach (var c in held)
        {
            int total = BroadcastScheduler.SymbolsPerCarrying(c.Transfer.SourceSymbols, FiveCarryings).Sum();
            Assert.Equal(total, sent.Count(s => s.ObjectId == c.Transfer.ObjectId));
        }
    }

    [Fact]
    public void Plan_FromBulletinsAlone_StartsWhereEarlierDaylightSlotsLeftOff()
    {
        var bulletin = TestBulletins.Day(3, 1)[0];
        var first = T("2026-10-05T15:00:00Z");
        var plan = BroadcastScheduler.Plan([new BroadcastBulletin(bulletin, new DateOnly(2026, 10, 5), first)], T("2026-10-06T10:00:00Z"), 1, Compression.Default, FiveCarryings);
        var o = Assert.Single(plan.Objects, x => x.Bid is not null);
        var counts = BroadcastScheduler.SymbolsPerCarrying(o.Transfer.SourceSymbols, FiveCarryings);
        // 15:00 and 09:00 have gone; 10:00 is the third carrying.
        Assert.Equal((uint)(counts[0] + counts[1]), o.FirstEsi);
        Assert.Equal(counts[2], o.Count);
        // And out of rotation once the last carry-over slot has gone.
        Assert.DoesNotContain(BroadcastScheduler.Plan([new BroadcastBulletin(bulletin, new DateOnly(2026, 10, 5), first)], T("2026-10-07T11:00:00Z"), 1, Compression.Default, FiveCarryings).Objects, x => x.Bid is not null);
    }

    [Fact]
    public void ASlotOnDemand_InTheDark_CountsAsTheFirstCarrying_AndRepeatsFollowInDaylight()
    {
        var options = ScheduleOptions.HourlyDaylight with { Timetable = Gb7rdg };
        var slots = BroadcastScheduler.CarryingSlots(T("2026-10-05T21:27:00Z"), options);
        Assert.Equal([T("2026-10-05T21:27:00Z"), T("2026-10-06T09:00:00Z")], slots);
    }

    [Fact]
    public void Validate_RefusesATimetableOfAnotherInterval()
    {
        Assert.Throws<ArgumentException>(() => BroadcastScheduler.Validate(ScheduleOptions.Hourly with { Timetable = new SlotTimetable(TimeOnly.MinValue, 30) }));
    }
}
