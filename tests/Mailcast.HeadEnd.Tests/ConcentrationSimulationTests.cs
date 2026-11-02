using System.Globalization;
using Packet.Mailcast;
using Xunit.Abstractions;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// Issue #234 measured: GB7RDG's slots (hourly daylight at IO91lk, WN4 and WN3 in turn, filled to 7.8 min) with an intake like its
/// November one, and listeners who each hear a fixed share of the frames, through the head end's own planner.
/// </summary>
/// <remarks>
/// The intake: 16 bulletins a day, 65% from LU9DCE (the bulk digests: 8 to 32 pieces, 20 on average) and the rest from other senders
/// (2 to 12 pieces, 7 on average), so a day is about 250 pieces, as the 42 MCR1 daily reports since 25 October put it. There is no copy
/// of the head end's store here, so the sizes are drawn, not read. A listener hears each frame with the same chance, rebuilds a bulletin
/// with K + 1 pieces, and gives it up when the head end stops sending it (it leaves the rotation) before that. The same test, run on
/// the commit before the change (the least covered first, 0.6 K a slot, 6 K), gave the "old" figures in the pull request.
/// </remarks>
public class ConcentrationSimulationTests(ITestOutputHelper output)
{
    private const string Bulk = ", \"bulk\": [\"LU9DCE\"]"; // OLD-STRIP-BULK

    private static readonly int[] Hours = [21, 5, 13, 21, 3, 9, 9, 10, 13, 19, 0, 1, 13, 18, 23, 0, 13, 0, 11, 13, 21, 10, 11, 13, 15, 15, 17, 17, 0, 13, 15, 16, 16, 9, 9, 10, 12, 13, 13, 15, 15, 16, 16, 0, 3, 5];

    private sealed record Arrival(DateTimeOffset At, Packet.Mailcast.Bulletin Bulletin, bool Bulk);

    private static List<Arrival> Intake(DateTimeOffset start, int days, int seed)
    {
        // Pieces per byte of these made-up bulletins, so that a size can be chosen for a number of pieces.
        var probe = Bulletins.Make(0, 6000);
        double perByte = TransferObject.ForBulletin(probe, ScheduleOptions.HourlyBudget.DictionaryId, Compression.Default, MailcastFrame.FixedSymbolSize, MailcastFrame.StandardAlignment).SourceSymbols / 6000.0;
        var rng = new Random(seed);
        var arrivals = new List<Arrival>();
        for (int day = 0; day < days; day++)
        {
            for (int i = 0; i < 16; i++)
            {
                bool bulk = rng.NextDouble() < 0.65;
                int pieces = bulk ? 8 + rng.Next(25) : 2 + rng.Next(11);
                string from = bulk ? "LU9DCE" : new[] { "G4ABC", "GM4CXF", "M0XYZ", "DL1ABC" }[rng.Next(4)];
                var at = start.AddDays(day).AddHours(Hours[rng.Next(Hours.Length)]).AddMinutes(rng.Next(60));
                arrivals.Add(new Arrival(at, Bulletins.Make((day * 100) + i + 1, (int)(pieces / perByte), 'B', from), bulk));
            }
        }
        arrivals.Sort((a, b) => a.At.CompareTo(b.At));
        return arrivals;
    }

    private sealed class Tally
    {
        public double[] Completed = new double[2];
        public double[] GivenUp = new double[2];
    }

    /// <summary>Runs the head end for <paramref name="days"/> days and returns, for each heard share, the average per day of the last <paramref name="measured"/> days.</summary>
    private static Dictionary<double, Tally> Run(double[] shares, int listeners, int days, int measured, out double framesPerDay, string extra = "")
    {
        using var headEnd = new BudgetTests.HeadEnd("\"modes\": [\"ms110d-wn4\"]" + Bulk + extra);
        var start = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        var arrivals = Intake(start, days, 2026);
        var timetable = headEnd.Config.ToSlotTimetable();
        var bulkOf = arrivals.ToDictionary(a => a.Bulletin.Bid, a => a.Bulk, StringComparer.Ordinal);
        var rng = new Random(11);
        var tallies = shares.ToDictionary(s => s, _ => new Tally());
        var heard = new Dictionary<(double, int, string), int>();
        var complete = new HashSet<(double, int, string)>();
        var active = new HashSet<string>(StringComparer.Ordinal);
        var windowStart = start.AddDays(days - measured);
        long frames = 0;
        int next = 0;
        void Retire(string bid, DateTimeOffset when)
        {
            foreach (double share in shares)
            {
                for (int l = 0; l < listeners; l++)
                {
                    if (!complete.Contains((share, l, bid)) && when >= windowStart)
                    {
                        tallies[share].GivenUp[bulkOf[bid] ? 1 : 0] += 1.0 / listeners;
                    }
                }
            }
        }
        foreach (var slot in Enumerable.Range(0, days).SelectMany(d => timetable.ActiveSlotsOn(DateOnly.FromDateTime(start.AddDays(d).UtcDateTime))))
        {
            while (next < arrivals.Count && arrivals[next].At <= slot)
            {
                headEnd.Offer(arrivals[next].Bulletin, DateOnly.FromDateTime(arrivals[next].At.UtcDateTime), arrivals[next].At);
                next++;
            }
            var inRotation = headEnd.InRotation(slot).Select(c => c.Bid).ToHashSet(StringComparer.Ordinal);
            foreach (string gone in active.Where(b => !inRotation.Contains(b)).ToList())
            {
                Retire(gone, slot);
            }
            active = inRotation;
            var (plan, _) = headEnd.Run(slot);
            if (plan.BulletinFrames == 0)
            {
                continue;
            }
            if (slot >= windowStart)
            {
                frames += plan.Frames.Count;
            }
            var byObject = plan.Objects.Where(o => o.Bid is not null).ToDictionary(o => o.Transfer.ObjectId, o => (Bid: o.Bid!, K: o.Transfer.SourceSymbols));
            foreach (var frame in plan.Frames.Where(f => byObject.ContainsKey(f.ObjectId)))
            {
                var (bid, k) = byObject[frame.ObjectId];
                foreach (double share in shares)
                {
                    for (int l = 0; l < listeners; l++)
                    {
                        if (rng.NextDouble() >= share || complete.Contains((share, l, bid)))
                        {
                            continue;
                        }
                        var key = (share, l, bid);
                        heard[key] = heard.GetValueOrDefault(key) + 1;
                        if (heard[key] >= k + 1)
                        {
                            complete.Add(key);
                            if (slot >= windowStart)
                            {
                                tallies[share].Completed[bulkOf[bid] ? 1 : 0] += 1.0 / listeners;
                            }
                        }
                    }
                }
            }
        }
        foreach (var t in tallies.Values)
        {
            for (int c = 0; c < 2; c++)
            {
                t.Completed[c] /= measured;
                t.GivenUp[c] /= measured;
            }
        }
        framesPerDay = frames / (double)measured;
        return tallies;
    }

    [Fact]
    public void ListenersHearingAShareOfTheFrames_FinishMoreNormalMail()
    {
        double[] shares = [0.15, 0.25, 0.50, 0.75];
        var result = Run(shares, 6, 28, 14, out double frames);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{frames:0} frames a day on average; per day, completed normal/bulk and given up normal/bulk"));
        foreach (double share in shares)
        {
            var t = result[share];
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"hears {share:P0}: completed {t.Completed[0]:0.0} normal {t.Completed[1]:0.0} bulk, given up {t.GivenUp[0]:0.0} normal {t.GivenUp[1]:0.0} bulk"));
        }
        // About 5.6 normal bulletins a day arrive. Someone hearing half of the frames finishes nearly all of them and gives up almost none;
        // someone hearing a quarter finishes about 2 more a day than the least-covered-first rule did (1.1).
        Assert.True(result[0.50].Completed[0] > 5.0 && result[0.50].GivenUp[0] < 0.5);
        Assert.True(result[0.25].Completed[0] > 1.8, $"{result[0.25].Completed[0]:0.0} normal a day at 25%");
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(4.0)]
    public void Sensitivity_TheTarget(double target)
    {
        double[] shares = [0.15, 0.25, 0.50, 0.75];
        var result = Run(shares, 6, 28, 14, out double frames, string.Create(CultureInfo.InvariantCulture, $", \"target\": {target}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"target {target} K, {frames:0} frames a day"));
        foreach (double share in shares)
        {
            var t = result[share];
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"hears {share:P0}: completed {t.Completed[0]:0.0} normal {t.Completed[1]:0.0} bulk, given up {t.GivenUp[0]:0.0} normal {t.GivenUp[1]:0.0} bulk"));
        }
        Assert.True(result[0.75].Completed[0] > 5.0);
    }
}
