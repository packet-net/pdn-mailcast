using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Mailcast.HeadEnd.Slot;
using Packet.Mailcast;
using Xunit.Abstractions;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// Issue #241 measured: how many near-misses a longer leftover tier rescues, and what it costs. GB7RDG's schedule (hourly daylight at
/// IO91lk, WN4 and WN3 short interleaver in turn, 18 s bursts, bulk LU9DCE last) through the head end's own planner, fed the real intake
/// (<c>Data/gb7rdg-held-2026-11-02.csv</c>) three times in a row; the middle pass is the cohort measured, so its bulletins meet a backlog
/// and have the whole third pass to retire in, however long the tier.
/// </summary>
/// <remarks>
/// Listeners lose whole slots and whole bursts together. Each simulated day a listener takes one real listener's day of per-slot reception
/// (<c>Data/gb7rdg-slot-reception-2026-10.csv</c>: frames heard over frames sent, per slot, 20 October to 1 November, before the GPSDO fault),
/// scaled to the share wanted. In a slot with share x, each burst is heard with chance x / 0.95 and each frame of a heard burst with 0.95.
/// A listener rebuilds a bulletin with K + 1 pieces. A give-up is counted as receivers now count it (issue #240): the bulletin retired
/// while the listener held at least ceil(K/2) pieces and had not finished it. Run with SIM241_OUT set to a file to get the tables; skipped
/// otherwise, it takes minutes.
/// </remarks>
public class LeftoverTierSimulationTests(ITestOutputHelper output)
{
    private const double InBurst = 0.95;

    /// <summary>
    /// A rule to try: retirement at <paramref name="Coverage"/> K or <paramref name="Hours"/> h, and for the bulletins it singles out
    /// (the normal ones with <paramref name="NormalOnly"/>, or those a reporting listener holds <paramref name="NearShare"/> of K of,
    /// as of yesterday's reports) at <paramref name="NearCoverage"/> K or <paramref name="NearHours"/> h.
    /// </summary>
    private sealed record Config(string Name, double Coverage, double Hours, double NearCoverage = 0, double NearHours = 0, double NearShare = 0, bool NormalOnly = false, bool StrongReportersOnly = false)
    {
        public bool UsesNear => NearShare > 0 || NormalOnly;

        public bool Reports => NearShare > 0;
    }

    private sealed record Info(string Bid, bool Bulk, DateTimeOffset TakenIn, int Pass);

    private sealed record SlotRec(DateTimeOffset At, int Day, int IndexInDay, int[] Frames, int[] Bursts, double OnAirMinutes, int[] RetiredBefore)
    {
        public int InRotation { get; init; }

        public int LeftoverFrames { get; init; }

        public int BulkFrames { get; init; }
    }

    private sealed class Trace
    {
        public List<SlotRec> Slots { get; } = [];

        public Dictionary<int, int> K { get; } = [];

        /// <summary>When the bulletin would have retired under today's 10 K or 48 h: from here on its pieces are the longer tier's.</summary>
        public Dictionary<int, DateTimeOffset> BaseRetire { get; } = [];

        public int NearFlagged { get; set; }

        public HashSet<int> Started { get; } = [];
    }

    private static readonly ConcurrentDictionary<int, Lazy<(List<(TimeSpan At, Bulletin Bulletin, bool Bulk, int Pass)> Arrivals, int Span)>> Intakes = new();

    /// <summary>The real intake three times over, each bulletin at a random minute of its day (<paramref name="seed"/>).</summary>
    private static (List<(TimeSpan At, Bulletin Bulletin, bool Bulk, int Pass)> Arrivals, int Span) Intake(int seed) =>
        Intakes.GetOrAdd(seed, s => new(() => MakeIntake(s))).Value;

    private static (List<(TimeSpan At, Bulletin Bulletin, bool Bulk, int Pass)> Arrivals, int Span) MakeIntake(int seed)
    {
        var rows = new List<(string Suffix, int K, int Day)>();
        DateOnly? first = null;
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "gb7rdg-held-2026-11-02.csv")).Skip(1))
        {
            var f = line.Split(',');
            var day = DateOnly.ParseExact(f[0], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            first ??= day;
            rows.Add((f[2], int.Parse(f[3], CultureInfo.InvariantCulture), day.DayNumber - first.Value.DayNumber));
        }
        int span = rows.Max(r => r.Day) + 1;
        var rng = new Random(seed);
        var arrivals = new List<(TimeSpan, Bulletin, bool, int)>();
        for (int pass = 0; pass < 3; pass++)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                bool bulk = rows[i].Suffix == "LU9DCE";
                arrivals.Add((TimeSpan.FromDays((pass * span) + rows[i].Day).Add(TimeSpan.FromMinutes(rng.Next(1440))),
                    MakeWithK((pass * 1000) + i + 1, rows[i].K, bulk ? "LU9DCE" : "G4ABC"), bulk, pass));
            }
        }
        arrivals.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return (arrivals, span);
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

    /// <summary>Real per-slot reception, one array of shares per listener-day, in slot order.</summary>
    private static readonly Lazy<Dictionary<string, List<double[]>>> Reception = new(() =>
    {
        var days = new Dictionary<(string, string), List<(string Slot, double Share)>>();
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "gb7rdg-slot-reception-2026-10.csv")).Skip(1))
        {
            var f = line.Split(',');
            double share = Math.Min(1.0, int.Parse(f[3], CultureInfo.InvariantCulture) / (double)int.Parse(f[4], CultureInfo.InvariantCulture));
            (days.TryGetValue((f[0], f[1]), out var list) ? list : days[(f[0], f[1])] = []).Add((f[2], share));
        }
        return days.GroupBy(d => d.Key.Item1).ToDictionary(g => g.Key, g => g.Select(d => d.Value.OrderBy(s => s.Slot, StringComparer.Ordinal).Select(s => s.Share).ToArray()).Where(a => a.Length >= 3).ToList());
    });

    private static readonly string[] Weak = ["GM8BPQ", "G7TAJ", "G8BPQ", "M9TWM", "M0NVK"];

    private static readonly string[] Strong = ["M7TAW", "GM4CXF"];

    /// <summary>A pool of listener-days and a scale that brings its mean share of frames to the share wanted.</summary>
    private sealed record Pool(List<double[]> Days, double Scale)
    {
        public double Share(int day, int index) => Math.Min(1.0, Days[day][index % Days[day].Length] * Scale);

        public static Pool For(IEnumerable<string> calls, double? share)
        {
            var days = calls.SelectMany(c => Reception.Value[c]).ToList();
            var all = days.SelectMany(d => d).ToArray();
            if (share is not double target)
            {
                return new Pool(days, 1.0);
            }
            double lo = 0, hi = 50;
            for (int i = 0; i < 60; i++)
            {
                double mid = (lo + hi) / 2;
                if (all.Average(x => Math.Min(1.0, x * mid)) < target)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            return new Pool(days, (lo + hi) / 2);
        }
    }

    /// <summary>One listener: the pieces held of each bulletin, and when it finished each.</summary>
    private sealed class Listener(Pool pool, int seed)
    {
        private readonly Random _days = new(seed);
        private readonly Random _frames = new(seed ^ 0x5bd1e995);
        private readonly List<int> _dayPick = [];

        public Dictionary<int, int> Held { get; } = [];

        public Dictionary<int, DateTimeOffset> Done { get; } = [];

        public void Hear(SlotRec slot, Dictionary<int, int> k)
        {
            while (_dayPick.Count <= slot.Day)
            {
                _dayPick.Add(_days.Next(pool.Days.Count));
            }
            double x = pool.Share(_dayPick[slot.Day], slot.IndexInDay);
            if (x <= 0)
            {
                return;
            }
            double pBurst = Math.Min(1.0, x / InBurst);
            double pFrame = x >= InBurst ? x : InBurst;
            int at = 0;
            foreach (int size in slot.Bursts)
            {
                bool heard = _frames.NextDouble() < pBurst;
                for (int i = at; i < at + size && i < slot.Frames.Length; i++)
                {
                    int b = slot.Frames[i];
                    if (!heard || b < 0 || _frames.NextDouble() >= pFrame || Done.ContainsKey(b))
                    {
                        continue;
                    }
                    int held = Held[b] = Held.GetValueOrDefault(b) + 1;
                    if (held >= k[b] + 1)
                    {
                        Done[b] = slot.At;
                    }
                }
                at += size;
            }
        }
    }

    private static string Schedule => "\"modes\": [\"ms110d-wn4\", \"ms110d-wn3\"], \"bulk\": [\"LU9DCE\"]";

    private static Trace Record(Config config, DateTimeOffset start, List<Info> infos, string schedule, int seed)
    {
        var (arrivals, span) = Intake(seed);
        int days = span * 3;
        var near = new HashSet<string>(StringComparer.Ordinal);
        BroadcastScheduler.SimRetire.Value = new SimRetireOverride(config.Coverage, config.Hours, config.UsesNear ? near.Contains : null, config.NearCoverage, config.NearHours);
        try
        {
            using var headEnd = new BudgetTests.HeadEnd(schedule, realStore: true);
            var timetable = headEnd.Config.ToSlotTimetable();
            var trace = new Trace();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var sentBefore = new Dictionary<int, int>();
            var firstSlot = new Dictionary<int, DateTimeOffset>();
            var reporters = config.Reports ? (config.StrongReportersOnly ? Strong : Weak.Concat(Strong)).Select((c, i) => new Listener(Pool.For([c], null), 900 + i)).ToList() : [];
            var active = new HashSet<int>();
            int next = 0;
            int lastDay = -1;
            int inDay = 0;
            for (int d = 0; d < days; d++)
            {
                foreach (var slot in timetable.ActiveSlotsOn(DateOnly.FromDateTime(start.AddDays(d).UtcDateTime)))
                {
                    while (next < arrivals.Count && start.Add(arrivals[next].At) <= slot)
                    {
                        var a = arrivals[next++];
                        var takenIn = start.Add(a.At);
                        index[a.Bulletin.Bid] = infos.Count;
                        if (config.NormalOnly && !a.Bulk)
                        {
                            near.Add(a.Bulletin.Bid);
                        }
                        infos.Add(new Info(a.Bulletin.Bid, a.Bulk, takenIn, a.Pass));
                        headEnd.Offer(a.Bulletin, DateOnly.FromDateTime(takenIn.UtcDateTime), takenIn);
                    }
                    if (d != lastDay)
                    {
                        // The listeners' reports of yesterday reach the head end before today's first slot.
                        foreach (var r in reporters)
                        {
                            foreach (var (b, held) in r.Held)
                            {
                                if (!r.Done.ContainsKey(b) && held >= Math.Ceiling(config.NearShare * trace.K[b]) && near.Add(infos[b].Bid))
                                {
                                    trace.NearFlagged++;
                                }
                            }
                        }
                        lastDay = d;
                        inDay = 0;
                    }
                    var rotation = headEnd.InRotation(slot);
                    var inRotation = rotation.Select(c => index[c.Bid]).ToHashSet();
                    var leftover = rotation.Where(c => BroadcastScheduler.Leftover(c, slot, headEnd.Options.Budget!)).Select(c => index[c.Bid]).ToHashSet();
                    int[] retired = [.. active.Where(b => !inRotation.Contains(b))];
                    active = inRotation;
                    foreach (int b in inRotation.Where(b => firstSlot.ContainsKey(b) && !trace.BaseRetire.ContainsKey(b)))
                    {
                        int k = trace.K[b];
                        if (sentBefore[b] >= Math.Ceiling(10.0 * k) || slot - firstSlot[b] >= TimeSpan.FromHours(48))
                        {
                            trace.BaseRetire[b] = slot;
                        }
                    }
                    var (plan, waveform) = headEnd.Run(slot);
                    if (plan.BulletinFrames == 0)
                    {
                        trace.Slots.Add(new SlotRec(slot, d, inDay++, [], [], 0, retired) { InRotation = inRotation.Count });
                        continue;
                    }
                    var byObject = new Dictionary<ulong, int>();
                    foreach (var o in plan.Objects.Where(o => o.Bid is not null))
                    {
                        int b = index[o.Bid!];
                        byObject[o.Transfer.ObjectId] = b;
                        trace.K[b] = o.Transfer.SourceSymbols;
                        if (o.Count > 0)
                        {
                            sentBefore[b] = sentBefore.GetValueOrDefault(b) + o.Count;
                            firstSlot.TryAdd(b, slot);
                        }
                    }
                    int[] frames = [.. plan.Frames.Select(f => byObject.TryGetValue(f.ObjectId, out int b) ? b : -1)];
                    int[] lengths = [.. plan.Frames.Select(f => f.ToBytes().Length + MailcastFrame.Ax25UiOverhead)];
                    int[] bursts = [.. new SlotAirtime(headEnd.Settings, waveform.Airtime).BurstSizes(lengths)];
                    foreach (int b in frames.Where(b => b >= 0))
                    {
                        trace.Started.Add(b);
                    }
                    var rec = new SlotRec(slot, d, inDay++, frames, bursts, headEnd.OnAir(plan, waveform).TotalMinutes, retired)
                    {
                        InRotation = inRotation.Count,
                        LeftoverFrames = frames.Count(b => b >= 0 && leftover.Contains(b)),
                        BulkFrames = frames.Count(b => b >= 0 && infos[b].Bulk),
                    };
                    trace.Slots.Add(rec);
                    foreach (var r in reporters)
                    {
                        r.Hear(rec, trace.K);
                    }
                }
            }
            return trace;
        }
        finally
        {
            BroadcastScheduler.SimRetire.Value = null;
        }
    }

    private sealed record Result(double CompletedNormal, double CompletedBulk, double GivenUpNormal, double GivenUpBulk, double Rescued, double NormalIn24h, double NormalMedianHours);

    /// <summary>The cohort's outcomes (the middle pass), per day of intake, averaged over <paramref name="listeners"/> listeners.</summary>
    private static Result Evaluate(Trace trace, List<Info> infos, Pool pool, int listeners, int span)
    {
        double cn = 0, cb = 0, gn = 0, gb = 0, rescued = 0, in24 = 0;
        var delays = new List<double>();
        for (int l = 0; l < listeners; l++)
        {
            var listener = new Listener(pool, 17 + (l * 7919));
            foreach (var slot in trace.Slots)
            {
                foreach (int b in slot.RetiredBefore)
                {
                    if (infos[b].Pass == 1 && !listener.Done.ContainsKey(b) && trace.K.TryGetValue(b, out int k) && listener.Held.GetValueOrDefault(b) >= (int)Math.Ceiling(k / 2.0))
                    {
                        if (infos[b].Bulk)
                        {
                            gb++;
                        }
                        else
                        {
                            gn++;
                        }
                    }
                }
                listener.Hear(slot, trace.K);
            }
            foreach (var (b, at) in listener.Done)
            {
                var info = infos[b];
                if (info.Pass != 1)
                {
                    continue;
                }
                if (info.Bulk)
                {
                    cb++;
                }
                else
                {
                    cn++;
                    double hours = (at - info.TakenIn).TotalHours;
                    delays.Add(hours);
                    in24 += hours <= 24 ? 1 : 0;
                }
                if (trace.BaseRetire.TryGetValue(b, out var baseRetire) && at >= baseRetire)
                {
                    rescued++;
                }
            }
        }
        double n = listeners * (double)span;
        delays.Sort();
        return new Result(cn / n, cb / n, gn / n, gb / n, rescued / n, in24 / n, delays.Count == 0 ? double.NaN : delays[delays.Count / 2]);
    }

    private static readonly Config[] Configs =
    [
        new("10 K / 48 h (today)", 10, 48),
        new("12 K / 48 h", 12, 48),
        new("15 K / 48 h", 15, 48),
        new("20 K / 48 h", 20, 48),
        new("10 K / 72 h", 10, 72),
        new("12 K / 72 h", 12, 72),
        new("15 K / 72 h", 15, 72),
        new("20 K / 72 h", 20, 72),
        new("10 K / 96 h", 10, 96),
        new("12 K / 96 h", 12, 96),
        new("15 K / 96 h", 15, 96),
        new("20 K / 96 h", 20, 96),
        new("normal only: 15 K / 72 h", 10, 48, 15, 72, NormalOnly: true),
        new("normal only: 20 K / 96 h", 10, 48, 20, 96, NormalOnly: true),
        new("near, half held, any reporter: 15 K / 72 h", 10, 48, 15, 72, 0.5),
        new("near, half held, any reporter: 20 K / 96 h", 10, 48, 20, 96, 0.5),
        new("near, 0.8 K held, any reporter: 20 K / 96 h", 10, 48, 20, 96, 0.8),
        new("near, half held, strong reporters: 20 K / 96 h", 10, 48, 20, 96, 0.5, StrongReportersOnly: true),
    ];

    private static readonly double[] Shares = [0.15, 0.25, 0.50, 0.75];

    private static readonly int[] Seeds = [2026, 7, 99];

    private sealed record Load(double Minutes, double Slots, double Flagged, double InRotation, double Frames, double Directory, double Bulk, double Leftover, double NormalNeverSent, double BulkNeverSent);

    [Fact]
    public void Sweep_LeftoverTier()
    {
        string? path = Environment.GetEnvironmentVariable("SIM241_OUT");
        if (string.IsNullOrEmpty(path))
        {
            output.WriteLine("SIM241_OUT not set: skipped");
            return;
        }
        int listeners = int.TryParse(Environment.GetEnvironmentVariable("SIM241_LISTENERS"), out int n) ? n : 200;
        int span = Intake(Seeds[0]).Span;
        var report = new StringBuilder();
        void Line(string s)
        {
            lock (report)
            {
                report.AppendLine(s);
                File.WriteAllText(path, report.ToString());
            }
        }

        // Calibration: the real listeners, each on their own days, on the October season the reports came from.
        {
            var infos = new List<Info>();
            var trace = Record(Configs[0], new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), infos, "\"modes\": [\"ms110d-wn4\", \"ms110d-wn3\"]", Seeds[0]);
            Line("Calibration, October season, rule without bulk last (as late October ran), each real listener on their own days (per day):");
            Line("");
            Line("| Listener | Share | Completed normal+bulk | Given up (half held) |");
            Line("|---|---|---|---|");
            foreach (string call in Weak.Concat(Strong))
            {
                var pool = Pool.For([call], null);
                var r = Evaluate(trace, infos, pool, listeners, span);
                Line(string.Create(CultureInfo.InvariantCulture, $"| {call} | {pool.Days.SelectMany(x => x).Average():P0} | {r.CompletedNormal + r.CompletedBulk:0.0} | {r.GivenUpNormal + r.GivenUpBulk:0.0} |"));
            }
            Line("");
        }

        var start = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        var results = new ConcurrentDictionary<(int, int, double), Result>();
        var loads = new ConcurrentDictionary<(int, int), Load>();
        var pools = Shares.ToDictionary(s => s, s => Pool.For(s < 0.4 ? Weak : Strong, s));
        var jobs = Enumerable.Range(0, Configs.Length).SelectMany(c => Seeds.Select(seed => (c, seed))).ToList();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, job =>
        {
            var (c, seed) = job;
            var infos = new List<Info>();
            var trace = Record(Configs[c], start, infos, Schedule, seed);
            var window = trace.Slots.Where(s => s.Day >= span && s.Day < 2 * span).ToList();
            var sending = window.Where(s => s.Frames.Length > 0).ToList();
            var cohort = Enumerable.Range(0, infos.Count).Where(b => infos[b].Pass == 1).ToList();
            loads[(c, seed)] = new Load(window.Sum(s => s.OnAirMinutes) / span, sending.Count / (double)span, trace.NearFlagged, window.Average(s => s.InRotation),
                sending.Average(s => s.Frames.Length), sending.Average(s => s.Frames.Count(b => b < 0)), sending.Average(s => s.BulkFrames), sending.Average(s => s.LeftoverFrames),
                cohort.Count(b => !infos[b].Bulk && !trace.Started.Contains(b)), cohort.Count(b => infos[b].Bulk && !trace.Started.Contains(b)));
            foreach (double share in Shares)
            {
                results[(c, seed, share)] = Evaluate(trace, infos, pools[share], listeners, span);
            }
            Line(string.Create(CultureInfo.InvariantCulture, $"<!-- done: {Configs[c].Name}, seed {seed} -->"));
        });

        Load L(int c) => new(
            Seeds.Average(s => loads[(c, s)].Minutes), Seeds.Average(s => loads[(c, s)].Slots), Seeds.Average(s => loads[(c, s)].Flagged), Seeds.Average(s => loads[(c, s)].InRotation),
            Seeds.Average(s => loads[(c, s)].Frames), Seeds.Average(s => loads[(c, s)].Directory), Seeds.Average(s => loads[(c, s)].Bulk), Seeds.Average(s => loads[(c, s)].Leftover),
            Seeds.Average(s => loads[(c, s)].NormalNeverSent), Seeds.Average(s => loads[(c, s)].BulkNeverSent));
        Result R(int c, double share) => new(
            Seeds.Average(s => results[(c, s, share)].CompletedNormal), Seeds.Average(s => results[(c, s, share)].CompletedBulk),
            Seeds.Average(s => results[(c, s, share)].GivenUpNormal), Seeds.Average(s => results[(c, s, share)].GivenUpBulk),
            Seeds.Average(s => results[(c, s, share)].Rescued), Seeds.Average(s => results[(c, s, share)].NormalIn24h), Seeds.Average(s => results[(c, s, share)].NormalMedianHours));

        Line(string.Create(CultureInfo.InvariantCulture, $"November season (from 3 Nov), cohort = the middle of three passes of the real intake ({span} days, {Intake(Seeds[0]).Arrivals.Count(a => a.Pass == 1 && !a.Bulk)} normal and {Intake(Seeds[0]).Arrivals.Count(a => a.Pass == 1 && a.Bulk)} bulk bulletins), {listeners} listeners per share, averaged over {Seeds.Length} arrival-time seeds.");
        Line("");
        Line("Head end, per day and per sending slot (cohort window):");
        Line("");
        Line("| Rule | On air min/day | Slots/day | In rotation | Frames per slot | Directory and envelope frames | Bulk frames | Leftover-tier frames | Normal never sent (cohort) | Bulk never sent (cohort) | Bulletins flagged near |");
        Line("|---|---|---|---|---|---|---|---|---|---|---|");
        for (int c = 0; c < Configs.Length; c++)
        {
            var l = L(c);
            Line(string.Create(CultureInfo.InvariantCulture, $"| {Configs[c].Name} | {l.Minutes:0.0} | {l.Slots:0.0} | {l.InRotation:0.0} | {l.Frames:0} | {l.Directory:0.0} | {l.Bulk:0.0} | {l.Leftover:0.0} | {l.NormalNeverSent:0.0} | {l.BulkNeverSent:0.0} | {(Configs[c].Reports ? l.Flagged.ToString("0", CultureInfo.InvariantCulture) : "")} |"));
        }
        foreach (double share in Shares)
        {
            Line("");
            Line(string.Create(CultureInfo.InvariantCulture, $"Listener hearing {share:P0} of frames, per day:"));
            Line("");
            Line("| Rule | Completed normal | Completed bulk | Given up normal | Given up bulk | Rescued (finished after today's retirement point) | Normal done within 24 h | Normal median hours |");
            Line("|---|---|---|---|---|---|---|---|");
            for (int c = 0; c < Configs.Length; c++)
            {
                var r = R(c, share);
                Line(string.Create(CultureInfo.InvariantCulture, $"| {Configs[c].Name} | {r.CompletedNormal:0.00} | {r.CompletedBulk:0.00} | {r.GivenUpNormal:0.00} | {r.GivenUpBulk:0.00} | {r.Rescued:0.00} | {r.NormalIn24h:0.00} | {r.NormalMedianHours:0.0} |"));
            }
        }
        output.WriteLine(report.ToString());
    }
}
