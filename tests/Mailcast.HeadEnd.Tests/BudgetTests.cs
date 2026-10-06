using System.Collections.Concurrent;
using System.Globalization;
using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Slot;
using Xunit.Abstractions;

namespace Mailcast.HeadEnd.Tests;

/// <summary>
/// The budget rule as GB7RDG runs it: hourly daylight slots at IO91lk, 240-octet symbols, 18 s
/// bursts, a 10 s tone, a 10 minute hard stop and a 2 minute clear-channel wait, so each slot is
/// filled to 8 minutes on the air. Everything goes through the head end's own store and planner,
/// and every frame of a plan counts as sent.
/// </summary>
public class BudgetTests(ITestOutputHelper output)
{
    private static readonly ConcurrentDictionary<string, LinearAirtime> Measured = new(StringComparer.Ordinal);

    private static LinearAirtime Measure(string mode) => Measured.GetOrAdd(mode, LinearAirtime.Measure);

    // The hours (UTC) GB7RDG's bulletins of 27 September to 5 October were written in.
    private static readonly int[] Hours =
        [21, 5, 13, 21, 3, 9, 9, 10, 13, 19, 0, 1, 13, 18, 23, 0, 13, 0, 11, 13, 21, 10, 11, 13, 15, 15, 17, 17, 0, 13, 15, 16, 16, 9, 9, 10, 12, 13, 13, 15, 15, 16, 16, 0, 3, 5];

    /// <summary>
    /// A head end in memory on GB7RDG's settings, with the schedule keys given: its own planner's
    /// waveforms and budget, and the core's scheduler, with each plan's frames all counted as sent
    /// as the store would count them. (The store itself writes every state durably, which is too
    /// slow for days of slots; <see cref="StrandedBulletins_ComeBackFromTheirNextEsi"/> goes through it.)
    /// </summary>
    private sealed class HeadEnd : IDisposable
    {
        private readonly TempDirectory _state = new();
        private readonly Dictionary<string, CarriedBulletin> _held = new(StringComparer.Ordinal);
        private readonly Dictionary<ulong, uint> _directories = [];

        public HeadEnd(string schedule = "")
        {
            Config = Gb7rdg(schedule);
            Options = Config.ToScheduleOptions();
            Settings = Config.ToSlotSettings();
            Waveforms = Config.ToWaveforms(Measure);
            Planner = new StoreSlotPlanner(new RotationStore(_state.Path, Compression.Default, Options, new MemoryJournal()), Compression.Default, Options, Waveforms, Settings, Config.FillLimit);
        }

        public HeadEndConfig Config { get; }

        public ScheduleOptions Options { get; }

        public SlotSettings Settings { get; }

        public Waveforms Waveforms { get; }

        public StoreSlotPlanner Planner { get; }

        public void Offer(Bulletin bulletin, DateOnly seen)
        {
            if (!_held.ContainsKey(bulletin.Bid))
            {
                var transfer = TransferObject.ForBulletin(bulletin, Options.DictionaryId, Compression.Default, Options.SymbolSize, Options.Alignment);
                _held[bulletin.Bid] = new CarriedBulletin(bulletin.Bid, bulletin.Title, bulletin.Serialize().Length, seen, transfer, 0);
            }
        }

        public IReadOnlyList<CarriedBulletin> InRotation(DateTimeOffset slot) =>
            [.. _held.Values.Where(c => BroadcastScheduler.InRotation(c, slot, Options))];

        public CarriedBulletin Held(string bid) => _held[bid];

        /// <summary>Plans a slot and sends all of it.</summary>
        public (SlotBroadcast Plan, SlotWaveform Waveform) Run(DateTimeOffset slot)
        {
            var waveform = Waveforms.For(slot);
            var plan = BroadcastScheduler.Plan(InRotation(slot), slot, StoreSlotPlanner.Seed(slot), Compression.Default, Options, id => _directories.GetValueOrDefault(id), Planner.Budget(waveform), waveform.Mode);
            if (plan.BulletinFrames == 0)
            {
                return (plan, waveform);
            }
            foreach (var o in plan.Objects.Where(o => o.Count > 0))
            {
                if (o.Bid is null)
                {
                    _directories[o.Transfer.ObjectId] = o.NextEsi;
                    continue;
                }
                var c = _held[o.Bid];
                _held[o.Bid] = c with { NextEsi = o.NextEsi, FirstSlot = c.FirstSlot ?? slot };
            }
            return (plan, waveform);
        }

        /// <summary>The slot's estimated time on the air, from its start to the closing ident; zero for one that keys nothing.</summary>
        public TimeSpan OnAir(SlotBroadcast plan, SlotWaveform waveform) => plan.BulletinFrames == 0 ? TimeSpan.Zero :
            new SlotAirtime(Settings, waveform.Airtime).ForPayloads([.. plan.Frames.Select(f => f.ToBytes().Length)]);

        public void Dispose() => _state.Dispose();
    }

    private static HeadEndConfig Gb7rdg(string schedule = "") => HeadEndConfig.Parse($$"""
        {
          "station": { "apiKey": "k", "mode": "ms110d-wn4", "maxBurstSeconds": 18 },
          "slot": {
            "timeUtc": "00:00", "everyMinutes": 60, "maxMinutes": 10, "toneSeconds": 10, "channelWaitSeconds": 120,
            "daylight": { "locator": "IO91lk", "afterSunriseMinutes": 120, "beforeSunsetMinutes": 30 }
          },
          "schedule": { "symbolSize": 240 {{(schedule.Length > 0 ? ", " + schedule : "")}} }
        }
        """);

    /// <summary>One simulated slot.</summary>
    private sealed record SlotResult(DateTimeOffset Slot, string Mode, int Frames, int BulletinsSent, int NewBulletins, int InRotation, TimeSpan OnAir, IReadOnlyDictionary<string, int> Symbols);

    /// <summary>Bulletins arriving over <paramref name="days"/> days, <paramref name="perDay"/> a day, at the hours GB7RDG's arrive.</summary>
    private static List<(DateTimeOffset At, Bulletin Bulletin)> Arrivals(DateTimeOffset start, int days, int perDay, int seed = 2026)
    {
        var rng = new Random(seed);
        var arrivals = new List<(DateTimeOffset At, Bulletin Bulletin)>();
        for (int day = 0; day < days; day++)
        {
            for (int i = 0; i < perDay; i++)
            {
                int size = rng.Next(4) == 0 ? 8000 + rng.Next(12000) : 1500 + rng.Next(4000);
                arrivals.Add((start.AddDays(day).AddHours(Hours[rng.Next(Hours.Length)]).AddMinutes(rng.Next(60)), Bulletins.Make((day * 1000) + i, size)));
            }
        }
        arrivals.Sort((a, b) => a.At.CompareTo(b.At));
        return arrivals;
    }

    /// <summary>Runs every daylight slot from <paramref name="start"/> for <paramref name="days"/> days, taking the bulletins in as they arrive.</summary>
    private static List<SlotResult> Simulate(HeadEnd headEnd, DateTimeOffset start, int days, List<(DateTimeOffset At, Bulletin Bulletin)> arrivals)
    {
        var timetable = headEnd.Config.ToSlotTimetable();
        var results = new List<SlotResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int next = 0;
        foreach (var slot in Enumerable.Range(0, days).SelectMany(d => timetable.ActiveSlotsOn(DateOnly.FromDateTime(start.AddDays(d).UtcDateTime))))
        {
            while (next < arrivals.Count && arrivals[next].At <= slot)
            {
                headEnd.Offer(arrivals[next].Bulletin, DateOnly.FromDateTime(arrivals[next].At.UtcDateTime));
                next++;
            }
            int inRotation = headEnd.InRotation(slot).Count;
            var (plan, waveform) = headEnd.Run(slot);
            var symbols = plan.Objects.Where(o => o.Bid is not null && o.Count > 0).ToDictionary(o => o.Bid!, o => o.Count, StringComparer.Ordinal);
            int fresh = symbols.Keys.Count(seen.Add);
            results.Add(new SlotResult(slot, waveform.Mode, plan.BulletinFrames == 0 ? 0 : plan.Frames.Count, symbols.Count, fresh, inRotation, headEnd.OnAir(plan, waveform), symbols));
        }
        return results;
    }

    private void Print(IEnumerable<SlotResult> results)
    {
        foreach (var r in results)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Slot.UtcDateTime:yyyy-MM-dd HH:mm} {r.Mode,-11} {r.Frames,4} frames, {r.BulletinsSent,3} of {r.InRotation,3} bulletins ({r.NewBulletins,2} first heard), {r.OnAir.TotalMinutes:0.0} min"));
        }
    }

    private static readonly DateTimeOffset October = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    // 2026-10-06 09:00 UTC: 33 frames in 6 bursts after a 10 s tone, 119.7 s from the start to the release.
    [InlineData(33, 10, 6, 119.7)]
    // 2026-10-05 16:00 UTC: 41 frames in 7 bursts; that head end was still daily, so its tone was 30 s.
    [InlineData(41, 30, 7, 161.5)]
    public void Estimate_MatchesTheRealSlots(int frames, int toneSeconds, int bursts, double seconds)
    {
        var settings = new SlotSettings { MaxBurst = TimeSpan.FromSeconds(18), ToneLength = TimeSpan.FromSeconds(toneSeconds) };
        var model = new SlotAirtime(settings, Measure("ms110d-wn4"));
        // 271-octet mailcast frames (240-octet symbols) as AX.25 UI frames.
        int[] lengths = [.. Enumerable.Repeat(271 + MailcastFrame.Ax25UiOverhead, frames)];
        var sizes = model.BurstSizes(lengths);
        TimeSpan toRelease = model.Slot(lengths, sizes) - SlotAirtime.ClosingIdent;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{frames} frames: {sizes.Count} bursts ({string.Join(" ", sizes)}), {model.Bursts(lengths, sizes).TotalSeconds:0.0} s of bursts, {toRelease.TotalSeconds:0.0} s to the release against {seconds:0.0} s measured, {(toRelease + SlotAirtime.ClosingIdent).TotalMinutes:0.00} min with the closing ident"));
        Assert.Equal(bursts, sizes.Count);
        Assert.InRange(toRelease.TotalSeconds, seconds - 5, seconds + 5);
    }

    [Fact]
    public void Estimate_FollowsTheWaveform_Wn3FramesTakeAboutTwiceAsLong()
    {
        var settings = new SlotSettings { MaxBurst = TimeSpan.FromSeconds(18), ToneLength = TimeSpan.FromSeconds(10) };
        int[] lengths = [.. Enumerable.Repeat(271 + MailcastFrame.Ax25UiOverhead, 60)];
        var wn4 = new SlotAirtime(settings, Measure("ms110d-wn4"));
        var wn3 = new SlotAirtime(settings, Measure("ms110d-wn3"));
        TimeSpan fixedPart = wn4.Slot([]) + SlotAirtime.StartDelay + settings.ToneLength + SlotAirtime.ToneDelay + settings.PauseAfterTone + SlotAirtime.ClosingIdent;
        double ratio = (wn3.Slot(lengths) - fixedPart) / (wn4.Slot(lengths) - fixedPart);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"60 frames: {wn4.Slot(lengths).TotalMinutes:0.00} min on WN4 in {wn4.BurstSizes(lengths).Count} bursts, {wn3.Slot(lengths).TotalMinutes:0.00} min on WN3 in {wn3.BurstSizes(lengths).Count} bursts; the frames take {ratio:0.00} times as long"));
        Assert.InRange(ratio, 1.8, 2.2);
    }

    [Theory]
    [InlineData("\"modes\": [\"ms110d-wn4\"]")]
    [InlineData("\"modes\": [\"ms110d-wn3\"]")]
    [InlineData("\"modes\": [\"ms110d-wn4\", \"ms110d-wn3\"]")]
    public void BusyDay_FillsEverySlot_AndNeverPassesTheBudget(string modes)
    {
        using var headEnd = new HeadEnd(modes);
        Assert.Equal(TimeSpan.FromMinutes(8), headEnd.Config.FillLimit);
        // Three times GB7RDG's volume, most of it there before the first slot.
        var arrivals = Arrivals(October, 2, 60);
        var results = Simulate(headEnd, October, 2, arrivals);
        Print(results);

        Assert.All(results, r => Assert.True(r.OnAir <= headEnd.Config.FillLimit, $"{r.Slot:HH:mm} on {r.Mode} would take {r.OnAir.TotalMinutes:0.00} min"));
        Assert.All(results, r => Assert.True(r.OnAir + headEnd.Config.Margin <= TimeSpan.FromMinutes(headEnd.Config.Slot.Max)));
        // Once there is enough in rotation, a busy day fills each slot to within a burst of its budget.
        Assert.All(results.Skip(3), r => Assert.True(r.OnAir > headEnd.Config.FillLimit - TimeSpan.FromSeconds(30), $"{r.Slot:HH:mm} on {r.Mode} is only {r.OnAir.TotalMinutes:0.00} min"));

        // At 600 bps a full slot carries about half the frames of one at 1200 bps.
        var byMode = results.GroupBy(r => r.Mode).ToDictionary(g => g.Key, g => g.Average(r => r.Frames), StringComparer.Ordinal);
        if (byMode.TryGetValue("ms110d-wn4", out double fast) && byMode.TryGetValue("ms110d-wn3", out double slow))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"a full slot: {fast:0} frames on WN4, {slow:0} on WN3"));
            Assert.InRange(slow / fast, 0.4, 0.6);
        }
        else
        {
            double frames = byMode.Values.Single();
            Assert.InRange(frames, modes.Contains("wn3", StringComparison.Ordinal) ? 60 : 130, modes.Contains("wn3", StringComparison.Ordinal) ? 90 : 170);
        }
    }

    [Fact]
    public void QuietDay_SpreadsEachBulletinOverTheDay_ABusyDayFillsEverySlot()
    {
        // Quiet: two bulletins overnight, the 2026-10-06 morning's.
        using var quiet = new HeadEnd();
        var day = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        var two = new List<(DateTimeOffset, Bulletin)> { (day.AddHours(3), Bulletins.Make(1, 7500)), (day.AddHours(5), Bulletins.Make(2, 3000)) };
        var calm = Simulate(quiet, day, 1, two);
        output.WriteLine("quiet day");
        Print(calm);

        var options = quiet.Options;
        var k = two.ToDictionary(b => b.Item2.Bid, b => quiet.Held(b.Item2.Bid).Transfer.SourceSymbols, StringComparer.Ordinal);
        // No slot gives a bulletin more than its cap, so each is spread over several slots, and
        // every slot is short rather than long: no bulletin eats a slot.
        Assert.All(calm.SelectMany(r => r.Symbols), s => Assert.True(s.Value <= BroadcastScheduler.CapSymbols(k[s.Key], options.Budget!)));
        Assert.All(calm, r => Assert.True(r.OnAir < TimeSpan.FromMinutes(2), $"{r.Slot:HH:mm}: {r.OnAir.TotalMinutes:0.0} min"));
        Assert.True(calm.Count(r => r.BulletinsSent == 2) >= 8, "both bulletins in nearly every slot");
        // Any three slots carry more than K of each: a listener who hears three rebuilds them.
        foreach (var (bid, kk) in k)
        {
            var perSlot = calm.Select(r => r.Symbols.GetValueOrDefault(bid)).OrderBy(x => x).ToList();
            Assert.True(perSlot.Take(3).Sum() >= kk + 1, $"{bid}: the three thinnest slots carry {perSlot.Take(3).Sum()} of K {kk}");
        }

        // Busy: fifty overnight. Every slot is full, and the slot after a bulletin arrives always carries some of it.
        using var busy = new HeadEnd();
        var fifty = Enumerable.Range(0, 50).Select(i => (day.AddHours(1).AddMinutes(i), Bulletins.Make(100 + i, i % 4 == 0 ? 12000 : 3500))).ToList();
        fifty.Add((day.AddHours(13).AddMinutes(30), Bulletins.Make(999, 2000)));
        var rush = Simulate(busy, day, 1, fifty);
        output.WriteLine("busy day");
        Print(rush);
        Assert.All(rush, r => Assert.True(r.OnAir > TimeSpan.FromMinutes(7.5) && r.OnAir <= TimeSpan.FromMinutes(8), $"{r.Slot:HH:mm}: {r.OnAir.TotalMinutes:0.0} min"));
        var afternoon = rush.Single(r => r.Slot == day.AddHours(14));
        Assert.True(afternoon.Symbols.ContainsKey(Bulletins.Make(999, 2000).Bid), "a bulletin arriving mid-afternoon goes in the next slot");
        Assert.Equal(51, rush.SelectMany(r => r.Symbols.Keys).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("\"modes\": [\"ms110d-wn4\"]")]
    [InlineData("\"modes\": [\"ms110d-wn4\", \"ms110d-wn3\"]")]
    public void OctoberDay_TwentyBulletinsADay(string modes)
    {
        // Five days of 20 a day; the fourth, 2026-10-06, is the one shown: 9 daylight slots, 09:00 to 17:00.
        using var headEnd = new HeadEnd(modes);
        var results = Simulate(headEnd, October, 5, Arrivals(October, 5, 20));
        var shown = October.AddDays(3);
        var day = results.Where(r => r.Slot >= shown && r.Slot < shown.AddDays(1)).ToList();
        Print(day);
        Assert.Equal(9, day.Count);
        Assert.All(results, r => Assert.True(r.OnAir <= headEnd.Config.FillLimit));

        // The bulletins first carried that day: how much of each went out in all, and whether a
        // listener who hears only one, two or three of the slots it was in rotation for (from its
        // first to its last), chosen at random, rebuilds it with a spare piece.
        var first = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var lastSlot = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            foreach (string bid in r.Symbols.Keys)
            {
                first.TryAdd(bid, r.Slot);
                lastSlot[bid] = r.Slot;
            }
        }
        var todays = first.Where(f => f.Value >= shown && f.Value < shown.AddDays(1)).Select(f => f.Key).ToList();
        var rng = new Random(7);
        double Rebuilt(int heard)
        {
            int ok = 0;
            int tries = 0;
            foreach (string bid in todays)
            {
                int k = headEnd.Held(bid).Transfer.SourceSymbols;
                var slots = results.Where(r => r.Slot >= first[bid] && r.Slot <= lastSlot[bid]).ToList();
                for (int t = 0; t < 100; t++)
                {
                    tries++;
                    ok += slots.OrderBy(_ => rng.Next()).Take(heard).Sum(s => s.Symbols.GetValueOrDefault(bid)) >= k + 1 ? 1 : 0;
                }
            }
            return ok / (double)tries;
        }
        double coverage = todays.Average(b => headEnd.Held(b).NextEsi / (double)headEnd.Held(b).Transfer.SourceSymbols);
        double[] rebuilt = [.. Enumerable.Range(1, 5).Select(Rebuilt)];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{todays.Count} bulletins first carried that day, {coverage:0.0} K each in all, over {todays.Average(b => results.Count(r => r.Slot >= first[b] && r.Slot <= lastSlot[b])):0.0} slots; a listener hearing 1 to 5 of its slots at random rebuilds {string.Join(", ", rebuilt.Select(x => x.ToString("P0", CultureInfo.InvariantCulture)))}; {day.Average(r => r.OnAir.TotalMinutes):0.0} min in an average slot that day"));
        if (headEnd.Waveforms.Modes.Count == 1)
        {
            // At 1200 bps every slot has room for every bulletin's floor: any three slots rebuild it.
            Assert.True(rebuilt[2] > 0.95, $"three slots rebuild only {rebuilt[2]:P0}");
        }
        else
        {
            // Every other slot at 600 bps carries half as much, so a listener needs a couple more.
            Assert.True(rebuilt[4] > 0.9, $"five slots rebuild only {rebuilt[4]:P0}");
        }
    }

    /// <summary>
    /// Two bulletins as GB7RDG's store held them on 2026-10-06: sent by an older head end, so with
    /// pieces gone (NextEsi above 0) but no FirstSlot in their state.txt.
    /// </summary>
    private static void Strand(string root, IEnumerable<Bulletin> bulletins, DateOnly seen)
    {
        var shares = ScheduleOptions.HourlyDaylight with { SymbolSize = 240 };
        var old = new HeadEndStore(root, Compression.Default, shares);
        foreach (var b in bulletins)
        {
            old.Offer(b, seen);
        }
        var slot = BroadcastScheduler.Midnight(seen).AddHours(7);
        old.Commit(BroadcastScheduler.Plan(old.InRotation(slot), slot, 1, Compression.Default, shares));
        foreach (string state in Directory.EnumerateFiles(Path.Combine(root, "bulletins"), "state.txt", SearchOption.AllDirectories))
        {
            File.WriteAllLines(state, File.ReadAllLines(state).Where(l => !l.StartsWith("FirstSlot:", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void StrandedBulletins_ComeBackFromTheirNextEsi()
    {
        using var state = new TempDirectory();
        var seen = new DateOnly(2026, 10, 5);
        var stranded = new[] { Bulletins.Make(41, 6000), Bulletins.Make(42, 2500) };
        Strand(state.Path, stranded, seen);
        var morning = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

        // The shares rule counts them as first carried at midnight on 5 October, long out of rotation.
        var shares = ScheduleOptions.HourlyDaylight with { SymbolSize = 240, Timetable = Gb7rdg().ToSlotTimetable() };
        Assert.Empty(new HeadEndStore(state.Path, Compression.Default, shares).InRotation(morning));

        // The budget rule has them back, from where they left off, through the head end's own store and planner.
        var config = Gb7rdg();
        var options = config.ToScheduleOptions();
        var store = new RotationStore(state.Path, Compression.Default, options, new MemoryJournal());
        var before = store.InRotation(morning).ToDictionary(c => c.Bid, StringComparer.Ordinal);
        Assert.Equal(stranded.Select(b => b.Bid).Order(), before.Keys.Order());
        Assert.All(before.Values, c => Assert.True(c.NextEsi > 0 && c.FirstSlot is null));
        var planner = new StoreSlotPlanner(store, Compression.Default, options, config.ToWaveforms(Measure), config.ToSlotSettings(), config.FillLimit);
        var plan = planner.Plan(morning);
        planner.RecordQueued(plan, plan.Frames.Count);
        Assert.All(plan.Broadcast!.Objects.Where(o => o.Bid is not null), o =>
        {
            Assert.Equal(before[o.Bid!].NextEsi, o.FirstEsi);
            Assert.True(o.Count > 0);
        });

        // Their first slot under the budget rule is this one, kept in state.txt, which their age counts from.
        var reopened = new HeadEndStore(state.Path, Compression.Default, options);
        Assert.All(reopened.InRotation(morning.AddHours(1)), c => Assert.Equal(morning, c.FirstSlot));
        Assert.Equal(2, reopened.InRotation(morning.AddHours(35)).Count);
        Assert.Empty(reopened.InRotation(morning.AddHours(36)));
    }

    [Fact]
    public void BulletinsInRotationButNothingDueUnderTheSharesRule_StillGoOutUnderTheBudgetRule()
    {
        // 2026-10-06 10:00 UTC at GB7RDG: two bulletins first carried at 09:00, their repeats due at
        // 14:00, so the shares rule had nothing to send and keyed nothing, not even the directory.
        var config = Gb7rdg();
        var first = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
        var shares = ScheduleOptions.HourlyDaylight with { SymbolSize = 240, Timetable = config.ToSlotTimetable() };
        var held = new List<CarriedBulletin>();
        foreach (var b in new[] { Bulletins.Make(51, 3000), Bulletins.Make(52, 7000) })
        {
            var transfer = TransferObject.ForBulletin(b, shares.DictionaryId, Compression.Default, 240, shares.Alignment);
            held.Add(new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, DateOnly.FromDateTime(first.UtcDateTime), transfer, (uint)BroadcastScheduler.SymbolsPerCarrying(transfer.SourceSymbols, shares)[0], first));
        }
        var ten = first.AddHours(1);
        Assert.Equal(0, BroadcastScheduler.Plan(held, ten, 1, Compression.Default, shares).BulletinFrames);

        using var state = new TempDirectory();
        var options = config.ToScheduleOptions();
        var planner = new StoreSlotPlanner(new RotationStore(state.Path, Compression.Default, options, new MemoryJournal()), Compression.Default, options, config.ToWaveforms(Measure), config.ToSlotSettings(), config.FillLimit);
        var plan = BroadcastScheduler.Plan(held, ten, 1, Compression.Default, options, null, planner.Budget(config.ToWaveforms(Measure).For(ten)), "ms110d-wn4");
        Assert.True(plan.BulletinFrames > 0);
        Assert.All(plan.Objects.Where(o => o.Bid is not null), o => Assert.True(o.Count > 0));
        // The directory goes too, at least its K and two spare pieces.
        var directory = plan.Objects.Single(o => o.Bid is null);
        Assert.True(directory.Count >= directory.Transfer.SourceSymbols + 2);
        Assert.Equal(2, plan.Directory.Entries.Count);
    }

    [Fact]
    public void EmptyStore_StillKeysNothing()
    {
        using var state = new TempDirectory();
        var config = Gb7rdg();
        var options = config.ToScheduleOptions();
        var planner = new StoreSlotPlanner(new RotationStore(state.Path, Compression.Default, options, new MemoryJournal()), Compression.Default, options, config.ToWaveforms(Measure), config.ToSlotSettings(), config.FillLimit);
        var plan = planner.Plan(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero));
        Assert.Empty(plan.Frames);
        Assert.Null(plan.Broadcast);
        Assert.Equal("ms110d-wn4", plan.Waveform!.Mode);
    }

    [Fact]
    public void RetiredBulletins_LeaveTheSlotsAndTheDirectory_AndAnEmptyRotationKeysNothing()
    {
        // One small bulletin on a quiet day: 0.6 K a slot until 6 K, then gone.
        using var headEnd = new HeadEnd();
        var day = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        var results = Simulate(headEnd, day, 2, [(day.AddHours(2), Bulletins.Make(61, 9000))]);
        Print(results);
        var c = headEnd.Held(Bulletins.Make(61, 9000).Bid);
        int retire = BroadcastScheduler.RetireSymbols(c.Transfer.SourceSymbols, headEnd.Options.Budget!);
        Assert.Equal(retire, (int)c.NextEsi);
        var last = results.FindLastIndex(r => r.Frames > 0);
        Assert.True(last < results.Count - 1, "it retired before the end");
        Assert.All(results.Skip(last + 1), r => Assert.Equal(0, r.Frames));
        Assert.Equal(0, results[last + 1].InRotation);
    }
}
