using Mailcast.Core;
using Mailcast.HeadEnd.Slot;
using Xunit.Abstractions;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// Hourly slots on GB7RDG's volume: about 25 bulletins a day, about 60 KB of them compressed,
/// taken in at the hours real ones were, sent as 240-octet symbols on WN4 in 18 s bursts.
/// </summary>
public class AirtimeBudgetTests(ITestOutputHelper output)
{
    // The hours (UTC) GB7RDG's bulletins of 27 September to 5 October were written in.
    private static readonly int[] Hours =
        [21, 5, 13, 21, 3, 9, 9, 10, 13, 19, 0, 1, 13, 18, 23, 0, 13, 0, 11, 13, 21, 10, 11, 13, 15, 15, 17, 17, 0, 13, 15, 16, 16, 9, 9, 10, 12, 13, 13, 15, 15, 16, 16, 0, 3, 5];

    [Fact]
    public void HourlyDefaults_AverageAboutThreeMinutesAnHour_AndTheWorstHourFitsTheHardStop()
    {
        var options = ScheduleOptions.Hourly with { SymbolSize = 240 };
        var settings = new SlotSettings { MaxBurst = TimeSpan.FromSeconds(18), ToneLength = TimeSpan.FromSeconds(10) };
        var station = new FakeStation(new VirtualTime(DateTimeOffset.UnixEpoch), 4);
        var runner = new SlotRunner(settings, station, station, null, LinearAirtime.Measure("ms110d-wn4"), new MemoryJournal(), TimeProvider.System);

        // Four days of bulletins, a quarter of them long, as GB7RDG's are.
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var rng = new Random(2026);
        var arrivals = new List<(DateTimeOffset At, Bulletin Bulletin)>();
        for (int day = 0; day < 4; day++)
        {
            for (int i = 0; i < 25; i++)
            {
                int size = rng.Next(4) == 0 ? 8000 + rng.Next(12000) : 1500 + rng.Next(4000);
                arrivals.Add((start.AddDays(day).AddHours(Hours[rng.Next(Hours.Length)]).AddMinutes(rng.Next(60)), Bulletins.Make((day * 100) + i, size)));
            }
        }
        arrivals.Sort((a, b) => a.At.CompareTo(b.At));

        // A head end in memory whose every slot goes out whole.
        var held = new List<CarriedBulletin>();
        var directories = new Dictionary<ulong, uint>();
        var keyed = new List<(DateTimeOffset Slot, TimeSpan OnAir)>();
        long compressed = 0;
        int next = 0;
        for (var slot = start; slot < start.AddDays(4); slot = slot.AddHours(1))
        {
            while (next < arrivals.Count && arrivals[next].At <= slot)
            {
                var b = arrivals[next++].Bulletin;
                var transfer = TransferObject.ForBulletin(b, options.DictionaryId, Compression.Default, options.SymbolSize, options.Alignment);
                held.Add(new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, DateOnly.FromDateTime(slot.UtcDateTime), transfer, 0));
                if (slot >= start.AddDays(2))
                {
                    compressed += transfer.Bytes.Length;
                }
            }
            var plan = BroadcastScheduler.Plan(held.Where(c => BroadcastScheduler.InRotation(c, slot, options)), slot, 1, Compression.Default, options, id => directories.GetValueOrDefault(id));
            foreach (var o in plan.Objects)
            {
                if (o.Bid is null)
                {
                    directories[o.Transfer.ObjectId] = o.NextEsi;
                    continue;
                }
                int at = held.FindIndex(c => c.Bid == o.Bid);
                held[at] = held[at] with { NextEsi = o.NextEsi, FirstSlot = o.Count > 0 ? held[at].FirstSlot ?? slot : held[at].FirstSlot };
            }
            if (plan.BulletinFrames == 0 || slot < start.AddDays(2))
            {
                continue;
            }
            var frames = plan.Frames.Select(f => new SlotFrame(f.ToBytes(), f.ObjectId, f.EncodingSymbolId)).ToList();
            var bursts = runner.BurstSizes(frames);
            // The bursts, the tone, the pause for the opening ident, a second between bursts and the closing ident.
            TimeSpan onAir = runner.Airtime(frames, bursts) + settings.ToneLength + settings.PauseAfterTone + (settings.BurstGap * (bursts.Count - 1)) + TimeSpan.FromSeconds(8);
            keyed.Add((slot, onAir));
        }

        // The sample is GB7RDG's volume: about 60 KB a day once compressed.
        double perDay = compressed / 2.0;
        output.WriteLine($"{perDay / 1000:0} KB a day compressed, {keyed.Count} of 48 slots keyed");
        Assert.InRange(perDay, 55_000, 80_000);

        double mean = keyed.Sum(k => k.OnAir.TotalMinutes) / 48;
        double worst = keyed.Max(k => k.OnAir.TotalMinutes);
        output.WriteLine($"on the air: {mean:0.00} min in an average hour, {worst:0.00} min in the worst");
        Assert.InRange(mean, 1.5, 3.3);
        Assert.True(worst < 10, $"the worst hour is {worst:0.0} min, which the 10 minute hard stop would cut");
    }
}
