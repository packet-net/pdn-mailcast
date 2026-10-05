using Mailcast.Core;
using Mailcast.HeadEnd.Slot;
using Xunit.Abstractions;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// Hourly slots in daylight only, at GB7RDG (IO91lk, from 2 hours after sunrise to 30 minutes
/// before sunset), on GB7RDG's volume as in <see cref="AirtimeBudgetTests"/>: about 25 bulletins
/// a day taken in round the clock, 240-octet symbols on WN4 in 18 s bursts. Each slot stops at
/// 10 minutes like a real one, and what it did not send is owed to the next.
/// </summary>
public class DaylightAirtimeTests(ITestOutputHelper output)
{
    // The hours (UTC) GB7RDG's bulletins of 27 September to 5 October were written in.
    private static readonly int[] Hours =
        [21, 5, 13, 21, 3, 9, 9, 10, 13, 19, 0, 1, 13, 18, 23, 0, 13, 0, 11, 13, 21, 10, 11, 13, 15, 15, 17, 17, 0, 13, 15, 16, 16, 9, 9, 10, 12, 13, 13, 15, 15, 16, 16, 0, 3, 5];

    /// <param name="WebSdrRebuilt">Of the bulletins first carried in the measured days, the share a web SDR receiver listening to 8 of each day's daylight slots hears at least K + 1 pieces of, with nothing lost.</param>
    public sealed record Result(int ActiveSlots, int Keyed, double MeanMinutes, double WorstMinutes, int CutShort, int Unfinished, double WebSdrRebuilt);

    /// <summary>
    /// Runs <paramref name="days"/> days of slots from <paramref name="start"/> and measures the
    /// last <paramref name="measured"/> of them.
    /// </summary>
    public static Result Simulate(ScheduleOptions options, DateTimeOffset start, int days = 6, int measured = 3)
    {
        var settings = new SlotSettings { MaxBurst = TimeSpan.FromSeconds(18), ToneLength = TimeSpan.FromSeconds(10) };
        var station = new FakeStation(new VirtualTime(DateTimeOffset.UnixEpoch), 4);
        var runner = new SlotRunner(settings, station, station, null, LinearAirtime.Measure("ms110d-wn4"), new MemoryJournal(), TimeProvider.System);
        var timetable = options.Timetable ?? new SlotTimetable(TimeOnly.MinValue, options.SlotMinutes);

        var rng = new Random(2026);
        var arrivals = new List<(DateTimeOffset At, Bulletin Bulletin)>();
        for (int day = 0; day < days; day++)
        {
            for (int i = 0; i < 25; i++)
            {
                int size = rng.Next(4) == 0 ? 8000 + rng.Next(12000) : 1500 + rng.Next(4000);
                arrivals.Add((start.AddDays(day).AddHours(Hours[rng.Next(Hours.Length)]).AddMinutes(rng.Next(60)), Bulletins.Make((day * 100) + i, size)));
            }
        }
        arrivals.Sort((a, b) => a.At.CompareTo(b.At));

        var held = new List<CarriedBulletin>();
        var directories = new Dictionary<ulong, uint>();
        var measureFrom = start.AddDays(days - measured);
        int active = 0;
        int cut = 0;
        var keyed = new List<double>();
        int next = 0;
        var slots = Enumerable.Range(0, days).SelectMany(d => timetable.ActiveSlotsOn(DateOnly.FromDateTime(start.AddDays(d).UtcDateTime)));
        // A web SDR receiver's slots, as the receiver picks them: 8 a day, spread over the day's daylight slots.
        var webSdr = new HashSet<DateTimeOffset>(Enumerable.Range(0, days).SelectMany(d =>
        {
            var day = timetable.ActiveSlotsOn(DateOnly.FromDateTime(start.AddDays(d).UtcDateTime));
            int n = Math.Min(8, day.Count);
            return Enumerable.Range(0, n).Select(i => day[i * day.Count / n]);
        }));
        var heard = new Dictionary<ulong, int>();
        foreach (var slot in slots)
        {
            while (next < arrivals.Count && arrivals[next].At <= slot)
            {
                var b = arrivals[next++].Bulletin;
                var transfer = TransferObject.ForBulletin(b, options.DictionaryId, Compression.Default, options.SymbolSize, options.Alignment);
                held.Add(new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, DateOnly.FromDateTime(slot.UtcDateTime), transfer, 0));
            }
            var plan = BroadcastScheduler.Plan(held.Where(c => BroadcastScheduler.InRotation(c, slot, options)), slot, 1, Compression.Default, options, id => directories.GetValueOrDefault(id));
            bool counted = slot >= measureFrom;
            active += counted ? 1 : 0;
            if (plan.BulletinFrames == 0)
            {
                continue;
            }

            // As a real slot: bursts until the next would run past the hard stop.
            var frames = plan.Frames.Select(f => new SlotFrame(f.ToBytes(), f.ObjectId, f.EncodingSymbolId)).ToList();
            var bursts = runner.BurstSizes(frames);
            TimeSpan onAir = settings.ToneLength + settings.PauseAfterTone;
            int sent = 0;
            foreach (int size in bursts)
            {
                var burst = runner.Airtime(frames.GetRange(sent, size), [size]);
                if (onAir + burst + settings.BurstGap > TimeSpan.FromMinutes(10))
                {
                    cut += counted ? 1 : 0;
                    break;
                }
                onAir += burst + settings.BurstGap;
                sent += size;
            }
            onAir += TimeSpan.FromSeconds(8); // the closing ident

            var nextEsi = new Dictionary<ulong, uint>();
            foreach (var f in plan.Frames.Take(sent))
            {
                if (webSdr.Contains(slot))
                {
                    heard[f.ObjectId] = heard.GetValueOrDefault(f.ObjectId) + 1;
                }
                nextEsi[f.ObjectId] = Math.Max(nextEsi.GetValueOrDefault(f.ObjectId), f.EncodingSymbolId + 1);
            }
            foreach (var o in plan.Objects)
            {
                if (!nextEsi.TryGetValue(o.Transfer.ObjectId, out uint after))
                {
                    continue;
                }
                if (o.Bid is null)
                {
                    directories[o.Transfer.ObjectId] = after;
                    continue;
                }
                int at = held.FindIndex(c => c.Bid == o.Bid);
                held[at] = held[at] with { NextEsi = Math.Max(after, held[at].NextEsi), FirstSlot = held[at].FirstSlot ?? slot };
            }
            if (counted)
            {
                keyed.Add(onAir.TotalMinutes);
            }
        }

        // Bulletins first carried in the measured days that did not get every carrying out.
        var end = start.AddDays(days);
        int unfinished = held.Count(c => c.FirstSlot is { } f && f >= measureFrom && f < end.AddDays(-2)
            && c.NextEsi < BroadcastScheduler.DueAt(c.Transfer.SourceSymbols, f, end, options));
        var measuredBulletins = held.Where(c => c.FirstSlot is { } f && f >= measureFrom && f < end.AddDays(-2)).ToList();
        double rebuilt = measuredBulletins.Count(c => heard.GetValueOrDefault(c.Transfer.ObjectId) >= c.Transfer.SourceSymbols + 1) / (double)Math.Max(1, measuredBulletins.Count);
        return new Result(active, keyed.Count, keyed.Sum() / Math.Max(1, active), keyed.DefaultIfEmpty(0).Max(), cut, unfinished, rebuilt);
    }

    private static ScheduleOptions Gb7rdg(ScheduleOptions options) => options with
    {
        SymbolSize = 240,
        Timetable = new SlotTimetable(TimeOnly.MinValue, 60, DaylightRule.Gb7rdg),
    };

    [Theory]
    [InlineData("2026-12-18", 4.2)]
    [InlineData("2026-10-02", 3.0)]
    [InlineData("2026-06-18", 2.0)]
    public void DaylightDefaults_AverageUnderFourMinutesAnActiveSlot_AndFinishEveryBulletin(string start, double most)
    {
        var options = Gb7rdg(ScheduleOptions.HourlyDaylight);
        var r = Simulate(options, DateTimeOffset.Parse(start + "T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        output.WriteLine($"from {start}: {r.ActiveSlots} daylight slots in 3 days, {r.Keyed} keyed, {r.MeanMinutes:0.00} min in an average one, {r.WorstMinutes:0.00} in the worst, {r.CutShort} cut short at 10 min, {r.Unfinished} bulletins not finished, {r.WebSdrRebuilt:P0} rebuilt by a web SDR on 8 slots a day");
        Assert.True(r.MeanMinutes < most, $"{r.MeanMinutes:0.00} min in an average daylight slot");
        Assert.Equal(0, r.Unfinished);
    }

    [Fact]
    public void HourlyShares_InWinterDaylight_WouldAverageFarOverFourMinutes()
    {
        // Why the daylight defaults carry less: all day's carrying in December's 5 daylight slots.
        var r = Simulate(Gb7rdg(ScheduleOptions.Hourly), new DateTimeOffset(2026, 12, 18, 0, 0, 0, TimeSpan.Zero));
        output.WriteLine($"hourly shares in December daylight: {r.MeanMinutes:0.00} min in an average daylight slot, {r.WorstMinutes:0.00} in the worst, {r.CutShort} cut short, {r.Unfinished} not finished");
        Assert.True(r.MeanMinutes > 5);
    }
}
