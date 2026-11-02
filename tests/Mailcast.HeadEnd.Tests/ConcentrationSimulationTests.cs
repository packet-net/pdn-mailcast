using System.Globalization;
using Packet.Mailcast;
using Xunit.Abstractions;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// Issue #234 measured: GB7RDG's slots (hourly daylight at IO91lk, WN4, about 870 frames a day) through the head end's own planner,
/// fed the intake the head end really held (<c>Data/gb7rdg-held-2026-11-02.csv</c>: the 204 bulletins held on 2 November, first seen
/// 20 October to 1 November, with their real K), replayed twice in a row so the second pass starts with a backlog. Listeners are
/// replayed over the one run of the head end.
/// </summary>
/// <remarks>
/// Two listener models. Independent: every frame is heard with the same chance. Slot-correlated: each slot a listener either hears it,
/// with chance <c>pSlot</c>, and then hears the share <c>f</c> of its frames, or misses it entirely; <c>pSlot * f</c> is the share of
/// frames heard. A listener rebuilds a bulletin with K + 1 pieces and gives it up when the head end stops sending it. The model that
/// reproduces the real daily reports best (<see cref="Calibration_AgainstTheRealReports"/>) is <see cref="MainP"/>.
/// </remarks>
public class ConcentrationSimulationTests(ITestOutputHelper output)
{
    // The sweep: the rule as it is left at the schedule keys, "" for the defaults. The OLD-STRIP lines are the ones removed to run
    // this same test on the commit before the change.
    private static readonly (string Name, string Schedule)[] Configs = // OLD-STRIP
    [
        ("slotCap 0.6, target 3", ", \"bulk\": [\"LU9DCE\"], \"slotCap\": 0.6, \"target\": 3"),
        ("slotCap 0.6, target 4", ", \"bulk\": [\"LU9DCE\"], \"slotCap\": 0.6, \"target\": 4"),
        ("slotCap 0.6, target 6", ", \"bulk\": [\"LU9DCE\"], \"slotCap\": 0.6, \"target\": 6"),
        ("slotCap 1, target 3", ", \"bulk\": [\"LU9DCE\"], \"slotCap\": 1, \"target\": 3"),
        ("slotCap 1, target 4", ", \"bulk\": [\"LU9DCE\"], \"slotCap\": 1, \"target\": 4"),
        ("slotCap 1, target 6", ", \"bulk\": [\"LU9DCE\"], \"slotCap\": 1, \"target\": 6"),
        ("slotCap 2, target 3", ", \"bulk\": [\"LU9DCE\"], \"slotCap\": 2, \"target\": 3"),
        ("slotCap 2, target 4 (the new defaults)", ", \"bulk\": [\"LU9DCE\"]"),
        ("slotCap 2, target 6", ", \"bulk\": [\"LU9DCE\"], \"slotCap\": 2, \"target\": 6"),
    ];
    // OLD-STRIP-CONFIGS

    /// <summary>The chance a listener hears a slot at all, in the model that fits the real reports best.</summary>
    private const double MainP = 0.3;

    /// <summary>The frames the head end sent in a day in the reports (about 890), which a listener's frames heard are a share of.</summary>
    private const double RealFramesPerDay = 890;

    private static readonly double[] Shares = [0.15, 0.25, 0.50, 0.75];

    /// <summary>The real listeners: frames heard, bulletins completed and given up, per day, averaged over the days of the 25 October to 1 November reports that had the fields.</summary>
    private static readonly (string Call, double Frames, double Completed, double GivenUp)[] Real =
    [
        ("GM4CXF", 633, 16.9, 0.6),
        ("M9TWM", 234, 8.3, 12.2),
        ("G7TAJ", 218, 8.4, 4.1),
        ("G8BPQ", 220, 6.6, 11.7),
        ("GM8BPQ", 102, 2.1, 11.9),
    ];

    private abstract record Event(DateTimeOffset At);

    private sealed record Retired(DateTimeOffset At, string Bid) : Event(At);

    private sealed record Sent(DateTimeOffset At, IReadOnlyDictionary<string, int> Pieces) : Event(At);

    private sealed record Trace(List<Event> Events, Dictionary<string, int> K, Dictionary<string, bool> Bulk, DateTimeOffset WindowStart, int MeasuredDays, double FramesPerDay);

    private sealed record Row(string Bid, string Suffix, int K, int Day);

    private static List<Row> ReadIntake()
    {
        var rows = new List<Row>();
        DateOnly? first = null;
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "gb7rdg-held-2026-11-02.csv")).Skip(1))
        {
            var f = line.Split(',');
            var day = DateOnly.ParseExact(f[0], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            first ??= day;
            rows.Add(new Row(f[1], f[2], int.Parse(f[3], CultureInfo.InvariantCulture), day.DayNumber - first.Value.DayNumber));
        }
        return rows;
    }

    /// <summary>A made-up bulletin of exactly <paramref name="k"/> pieces from <paramref name="from"/>.</summary>
    private static Bulletin MakeWithK(int seed, int k, string from)
    {
        var options = ScheduleOptions.HourlyBudget;
        int K(int size) => TransferObject.ForBulletin(Bulletins.Make(seed, size, 'B', from), options.DictionaryId, Compression.Default, MailcastFrame.FixedSymbolSize, MailcastFrame.StandardAlignment).SourceSymbols;
        int size = Math.Max(200, k * 190);
        int best = size;
        for (int i = 0; i < 40; i++)
        {
            int got = K(size);
            if (got == k)
            {
                best = size;
                break;
            }
            size = Math.Max(100, (int)(size * (k / (double)got) * (got < k ? 1.01 : 0.99)));
            best = size;
        }
        return Bulletins.Make(seed, best, 'B', from);
    }

    private static readonly Lazy<(List<(DateTimeOffset At, Bulletin Bulletin, bool Bulk)> Arrivals, int Span, int Passes)> Replay = new(() =>
    {
        var rows = ReadIntake();
        int span = rows.Max(r => r.Day) + 1;
        int passes = 2;
        var start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var rng = new Random(2026);
        var arrivals = new List<(DateTimeOffset At, Bulletin Bulletin, bool Bulk)>();
        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                bool bulk = row.Suffix == "LU9DCE";
                var bulletin = MakeWithK((pass * 1000) + i + 1, row.K, bulk ? "LU9DCE" : "G4ABC");
                arrivals.Add((start.AddDays((pass * span) + row.Day).AddMinutes(rng.Next(1440)), bulletin, bulk));
            }
        }
        arrivals.Sort((a, b) => a.At.CompareTo(b.At));
        return (arrivals, span, passes);
    });

    private static Trace Record(string schedule)
    {
        var (arrivals, span, passes) = Replay.Value;
        int days = span * passes;
        var start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

        using var headEnd = new BudgetTests.HeadEnd("\"modes\": [\"ms110d-wn4\"]" + schedule);
        var timetable = headEnd.Config.ToSlotTimetable();
        var trace = new Trace([], [], arrivals.ToDictionary(a => a.Bulletin.Bid, a => a.Bulk, StringComparer.Ordinal), start.AddDays(span), span, 0);
        var active = new HashSet<string>(StringComparer.Ordinal);
        long frames = 0;
        int next = 0;
        foreach (var slot in Enumerable.Range(0, days).SelectMany(d => timetable.ActiveSlotsOn(DateOnly.FromDateTime(start.AddDays(d).UtcDateTime))))
        {
            while (next < arrivals.Count && arrivals[next].At <= slot)
            {
                var a = arrivals[next++];
                headEnd.Offer(a.Bulletin, DateOnly.FromDateTime(a.At.UtcDateTime), a.At);
            }
            var inRotation = headEnd.InRotation(slot).Select(c => c.Bid).ToHashSet(StringComparer.Ordinal);
            foreach (string gone in active.Where(b => !inRotation.Contains(b)))
            {
                trace.Events.Add(new Retired(slot, gone));
            }
            active = inRotation;
            var (plan, _) = headEnd.Run(slot);
            if (plan.BulletinFrames == 0)
            {
                continue;
            }
            if (slot >= trace.WindowStart)
            {
                frames += plan.Frames.Count;
            }
            var pieces = plan.Objects.Where(o => o.Bid is not null && o.Count > 0).ToDictionary(o => o.Bid!, o => o.Count, StringComparer.Ordinal);
            foreach (var o in plan.Objects.Where(o => o.Bid is not null))
            {
                trace.K[o.Bid!] = o.Transfer.SourceSymbols;
            }
            trace.Events.Add(new Sent(slot, pieces));
        }
        return trace with { FramesPerDay = frames / (double)span };
    }

    /// <summary>Completed and given up per day over the measured days: [normal, bulk] each.</summary>
    private static (double[] Completed, double[] GivenUp) Evaluate(Trace trace, double share, double pSlot, int listeners, int seed)
    {
        pSlot = Math.Max(pSlot, share);
        double f = share / pSlot;
        var rng = new Random(seed);
        var heard = new Dictionary<(int, string), int>();
        var done = new HashSet<(int, string)>();
        var completed = new double[2];
        var givenUp = new double[2];
        foreach (var e in trace.Events)
        {
            bool measured = e.At >= trace.WindowStart;
            if (e is Retired r)
            {
                for (int l = 0; l < listeners; l++)
                {
                    if (measured && !done.Contains((l, r.Bid)))
                    {
                        givenUp[trace.Bulk[r.Bid] ? 1 : 0] += 1.0 / listeners;
                    }
                }
                continue;
            }
            var sent = (Sent)e;
            for (int l = 0; l < listeners; l++)
            {
                if (rng.NextDouble() >= pSlot)
                {
                    continue;
                }
                foreach (var (bid, count) in sent.Pieces)
                {
                    if (done.Contains((l, bid)))
                    {
                        continue;
                    }
                    int got = 0;
                    for (int i = 0; i < count; i++)
                    {
                        got += rng.NextDouble() < f ? 1 : 0;
                    }
                    int total = heard[(l, bid)] = heard.GetValueOrDefault((l, bid)) + got;
                    if (total >= trace.K[bid] + 1)
                    {
                        done.Add((l, bid));
                        if (measured)
                        {
                            completed[trace.Bulk[bid] ? 1 : 0] += 1.0 / listeners;
                        }
                    }
                }
            }
        }
        for (int c = 0; c < 2; c++)
        {
            completed[c] /= trace.MeasuredDays;
            givenUp[c] /= trace.MeasuredDays;
        }
        return (completed, givenUp);
    }

    /// <summary>How well the models reproduce the real reports, with whatever rule is in the tree (the old one on the commit before the change).</summary>
    [Fact]
    public void Calibration_AgainstTheRealReports()
    {
        var trace = Record("");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"calibration on the rule in this tree: {trace.FramesPerDay:0} frames a day, {trace.K.Count} bulletins, {trace.Bulk.Values.Count(b => b)} bulk"));
        double[] ps = [1.0, 0.6, 0.4, 0.3, 0.2];
        var error = ps.ToDictionary(p => p, _ => new List<double>());
        foreach (var (call, frames, completed, givenUp) in Real)
        {
            double share = Math.Min(1.0, frames / RealFramesPerDay);
            var line = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{call,-7} hears {share:P0}: real {completed:0.0} done {givenUp:0.0} given up;") };
            foreach (double p in ps)
            {
                var (c, g) = Evaluate(trace, share, p, 12, 5);
                double simDone = c.Sum();
                double simGiven = g.Sum();
                error[p].Add(Math.Abs(simDone - completed) / Math.Max(completed, 1) + (Math.Abs(simGiven - givenUp) / Math.Max(givenUp, 1)));
                line.Add(string.Create(CultureInfo.InvariantCulture, $"pSlot {p:0.0}: {simDone:0.0} done {simGiven:0.0} given up;"));
            }
            output.WriteLine(string.Join(" ", line));
        }
        foreach (double p in ps)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"model pSlot {p:0.0}: mean relative error (completed + given up) {error[p].Average():0.00}"));
        }
        Assert.NotEmpty(trace.Events);
    }

    /// <summary>The sweep: completed and given up per day, normal and bulk, for listeners hearing 15, 25, 50 and 75% of the frames.</summary>
    [Fact]
    public void Sweep_CompletedAndGivenUpPerDay()
    {
        foreach (var (name, schedule) in Configs)
        {
            var trace = Record(schedule);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: {trace.FramesPerDay:0} frames a day (listeners hear each slot with chance {Math.Max(MainP, 0):0.0})"));
            output.WriteLine("| Hears | Completed normal | Completed bulk | Given up normal | Given up bulk |");
            foreach (double share in Shares)
            {
                var (c, g) = Evaluate(trace, share, MainP, 12, 7);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {share:P0} | {c[0]:0.0} | {c[1]:0.0} | {g[0]:0.0} | {g[1]:0.0} |"));
            }
        }
        Assert.NotEmpty(Configs);
    }
}
