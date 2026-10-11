using System.Globalization;
using Xunit.Abstractions;
using P = Mailcast.Receiver.Tests.ProbeSlot.Path;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// Issue #82: the probe's phantom second mode (<see cref="ProbeChannel.WeakestModeDb"/>) and its
/// crash filter (the 6x-typical window-power test) each misfire now and then on a realistic
/// fading channel. This measures both rates over many trials, as the issue asks, rather than
/// trusting the handful that first found them.
/// </summary>
/// <remarks>
/// Gated by <c>MAILCAST_PROBE_SAFETY</c> (a trial count) since a few hundred trials each are slow;
/// with it unset a handful run as a smoke test only. Writes a table to the test output and, with
/// <c>MAILCAST_PROBE_SAFETY_OUT</c> set, to that file too.
/// </remarks>
public class ProbeSafetyTests(ITestOutputHelper output)
{
    private const int Rate = OnAir.SampleRate;

    /// <summary>150 by default: enough that a reverted fix reliably trips the assertions below without making the normal test run slow. <c>MAILCAST_PROBE_SAFETY</c> overrides it for a fuller measurement.</summary>
    private static int Trials => int.TryParse(Environment.GetEnvironmentVariable("MAILCAST_PROBE_SAFETY"), out int n) ? n : 150;

    /// <summary>A realistic HF single path: moderate fading, a wide SNR range, a little static offset and CFO error as a real receiver would see.</summary>
    private static (ProbeSlot.Made Made, double SnrDb, double OffsetHz) SinglePath(int seed)
    {
        var rng = new Random(seed * 7919 + 1);
        double snr = -3 + (rng.NextDouble() * 23); // -3..20 dB, the range the real modems care about and below
        double spread = 0.05 + (rng.NextDouble() * 1.45); // 0.1 to 3 Hz two-sigma: quiet to disturbed HF
        double offset = (rng.NextDouble() * 6) - 3;
        var path = new P(0.25 + (rng.NextDouble() * 2.0), 0, SpreadHz: spread);
        var made = ProbeSlot.Make([path], snr, seed: rng.Next(), offsetHz: offset);
        return (made, snr, offset);
    }

    /// <summary>A genuine 2-mode HF channel: second path 1 to 3 ms later, 5 to 20 dB weaker, both with their own realistic Doppler spread.</summary>
    private static (ProbeSlot.Made Made, double SnrDb, double OffsetHz, double DelayMs, double PowerDb) TwoMode(int seed)
    {
        var rng = new Random(seed * 104_729 + 2);
        double snr = -3 + (rng.NextDouble() * 23);
        double offset = (rng.NextDouble() * 6) - 3;
        double baseMs = 0.25 + (rng.NextDouble() * 2.0);
        double delay = 1.0 + (rng.NextDouble() * 2.0); // 1..3 ms
        double power = -(5 + (rng.NextDouble() * 15)); // -5..-20 dB
        double spread1 = 0.05 + (rng.NextDouble() * 1.0);
        double spread2 = 0.05 + (rng.NextDouble() * 1.0);
        var paths = new[] { new P(baseMs, 0, SpreadHz: spread1), new P(baseMs + delay, power, SpreadHz: spread2) };
        var made = ProbeSlot.Make(paths, snr, seed: rng.Next(), offsetHz: offset);
        return (made, snr, offset, delay, power);
    }

    /// <summary>Adds a short broadband noise burst into <paramref name="audio"/> (as kept, 48 kHz) at the window starting <paramref name="windowStartSeconds"/> into it, its power <paramref name="extraDb"/> over that window's own (pre-burst) rms: an atmospheric static crash.</summary>
    private static float[] InjectCrash(float[] audio, double windowStartSeconds, double windowSeconds, double durationMs, double extraDb, Random rng)
    {
        var x = (float[])audio.Clone();
        int wFrom = Math.Max(0, (int)Math.Round(windowStartSeconds * Rate));
        int wTo = Math.Min(x.Length, (int)Math.Round((windowStartSeconds + windowSeconds) * Rate));
        double sumSq = 0;
        for (int i = wFrom; i < wTo; i++)
        {
            sumSq += (double)x[i] * x[i];
        }
        double rms = Math.Sqrt(sumSq / Math.Max(1, wTo - wFrom));
        double amp = rms * Math.Pow(10, extraDb / 20);
        int dur = Math.Max(1, (int)Math.Round(durationMs / 1000.0 * Rate));
        int centre = (wFrom + wTo) / 2;
        int from = Math.Max(0, centre - (dur / 2));
        int to = Math.Min(x.Length, from + dur);
        for (int i = from; i < to; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            x[i] += (float)(amp * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        return x;
    }

    private sealed record PhantomTrial(bool Found, bool Phantom, double SnrDb, double Spread, double ExtraMs, double ExtraDb);

    [Fact]
    public void SinglePath_PhantomSecondMode_Rate()
    {
        int n = Trials;
        var results = new PhantomTrial?[n];
        Parallel.For(0, n, seed =>
        {
            var rng = new Random(seed * 7919 + 1);
            double snr = -3 + (rng.NextDouble() * 23);
            double spread = 0.05 + (rng.NextDouble() * 1.45);
            double offset = (rng.NextDouble() * 6) - 3;
            var path = new P(0.25 + (rng.NextDouble() * 2.0), 0, SpreadHz: spread);
            var made = ProbeSlot.Make([path], snr, seed: rng.Next(), offsetHz: offset);
            var (audio, end) = ProbeSlot.Kept(made);
            var found = ProbeChannel.Analyse(audio, end, OnAir.CentreAudioHz + offset);
            if (found is not { } f)
            {
                results[seed] = null;
                return;
            }
            bool phantom = f.Picture.Modes.Count > 1;
            // The phantom is whichever mode is not the strongest (PowerDb 0): it can land
            // before the real path (SearchBefore) as well as after, so "index 1" is not safe.
            var strongest = f.Picture.Modes.OrderByDescending(m => m.PowerDb).First();
            var weakest = f.Picture.Modes.OrderBy(m => m.PowerDb).First();
            double extraMs = phantom ? Math.Abs(weakest.DelayMs - strongest.DelayMs) : double.NaN;
            double extraDb = phantom ? weakest.PowerDb : double.NaN;
            results[seed] = new PhantomTrial(true, phantom, snr, spread, extraMs, extraDb);
        });
        var ok = results.Where(r => r is not null).Select(r => r!).ToList();
        var phantoms = ok.Where(r => r.Phantom).ToList();
        output.WriteLine($"single-path phantom mode: {phantoms.Count}/{ok.Count} ({(n - ok.Count > 0 ? $"{n - ok.Count} skipped, " : "")}{(double)phantoms.Count / ok.Count * 100:F1}%)");
        if (phantoms.Count > 0)
        {
            var db = phantoms.Select(p => p.ExtraDb).Order().ToArray();
            var ms = phantoms.Select(p => p.ExtraMs).Order().ToArray();
            output.WriteLine($"  phantom dB: min {db[0]:F1}, median {Median(db):F1}, p90 {Pct(db, 0.9):F1}, max {db[^1]:F1}");
            output.WriteLine($"  phantom offset ms: min {ms[0]:F3}, median {Median(ms):F3}, p90 {Pct(ms, 0.9):F3}, max {ms[^1]:F3}");
            // The near-lobe band (where the pulse's own truncation shoulders land) against anything farther out.
            var near = phantoms.Where(p => p.ExtraMs <= 1.0).ToList();
            var far = phantoms.Where(p => p.ExtraMs > 1.0).ToList();
            output.WriteLine($"  within 1.0 ms: {near.Count} of {phantoms.Count}, max dB there {(near.Count > 0 ? near.Max(p => p.ExtraDb).ToString("F1", CultureInfo.InvariantCulture) : "-")}" +
                $"; beyond 1.0 ms: {far.Count}, max dB there {(far.Count > 0 ? far.Max(p => p.ExtraDb).ToString("F1", CultureInfo.InvariantCulture) : "-")}");
        }
        foreach (var p in phantoms.OrderByDescending(p => p.ExtraDb).Take(40))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  SNR {p.SnrDb,5:F1} dB, spread {p.Spread,4:F2} Hz: extra at {p.ExtraMs:F3} ms, {p.ExtraDb:F1} dB"));
        }
        // Before the fix this was about 7% (packet-net/pdn-mailcast#82); measured at 0/2000 after it.
        // A couple of trials' worth of margin, not zero, so a rebuild on different hardware is not flaky.
        Assert.True(phantoms.Count <= Math.Max(2, ok.Count / 20), $"{phantoms.Count} of {ok.Count} single-path trials reported a phantom second mode");
    }

    private static double Pct(double[] sorted, double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * sorted.Length))];

    private sealed record MissedTrial(bool Missed, double SnrDb, double Delay, double Power);

    [Fact]
    public void TwoMode_MissedRealMode_Rate()
    {
        int n = Trials;
        var results = new MissedTrial?[n];
        Parallel.For(0, n, seed =>
        {
            var (made, snr, offset, delay, power) = TwoMode(seed);
            var (audio, end) = ProbeSlot.Kept(made);
            var found = ProbeChannel.Analyse(audio, end, OnAir.CentreAudioHz + offset);
            results[seed] = found is not { } f ? null : new MissedTrial(f.Picture.Modes.Count < 2, snr, delay, power);
        });
        var ok = results.Where(r => r is not null).Select(r => r!).ToList();
        var missed = ok.Where(r => r.Missed).ToList();
        output.WriteLine($"two-mode missed real mode: {missed.Count}/{ok.Count} ({(n - ok.Count > 0 ? $"{n - ok.Count} skipped, " : "")}{(double)missed.Count / ok.Count * 100:F1}%)");
        foreach (var m in missed.Take(25))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  SNR {m.SnrDb:F1} dB, delay {m.Delay:F2} ms, power {m.Power:F1} dB"));
        }
        // Measured at 1.6% (32/2000), unchanged by the #82 fix (NearLobeMs sits under this
        // scenario's 1 ms floor): the near-lobe guard must never start eating genuine modes.
        Assert.True(missed.Count <= Math.Max(2, ok.Count / 10), $"{missed.Count} of {ok.Count} genuine 2-mode trials missed the real second mode");
    }

    /// <summary>
    /// False-crash rate (a window excluded though nothing crashed) against missed-crash rate (an
    /// injected crash window kept) across a range of extra-power ratios, both single-path and
    /// 2-mode channels. The threshold that matters is <see cref="ProbeChannel.WeakestModeDb"/>'s
    /// sibling, the crash filter's own ratio, which this measures directly against the window
    /// powers a real pass sees rather than against a synthetic array.
    /// </summary>
    [Fact]
    public void CrashFilter_FalseAndMissedRates()
    {
        int n = Trials;
        double[] ratios = [4, 6, 8, 10, 12, 15, 20, 30];
        double[] crashDbs = [10, 15, 20, 25, 30, 40];
        // Per trial: whether it had a usable baseline, whether each ratio false-triggered on it,
        // and (separately) whether each ratio missed the injected crash at each severity.
        var hadBaseline = new bool[n];
        var falseHit = new bool[n][];
        var hadCrash = new bool[n];
        var missedHit = new bool[n][,];

        Parallel.For(0, n, seed =>
        {
            bool twoMode = seed % 2 == 0;
            var rng = new Random(seed * 1337 + 3);
            double offset;
            float[] audio;
            double end;
            if (twoMode)
            {
                var (made, _, off, _, _) = TwoMode(seed);
                (audio, end) = ProbeSlot.Kept(made);
                offset = off;
            }
            else
            {
                var (made, _, off) = SinglePath(seed);
                (audio, end) = ProbeSlot.Kept(made);
                offset = off;
            }

            // Baseline, no crash: any window excluded by the power-ratio test alone is a false positive.
            var baseline = ProbeChannel.Measure(audio, end, OnAir.CentreAudioHz + offset);
            if (baseline is null || WindowPowers(audio, end, offset) is not { } power)
            {
                return;
            }
            hadBaseline[seed] = true;
            falseHit[seed] = [.. ratios.Select(ratio => AnyFalseCrash(power, ratio))];

            // A real crash in the middle of the run: does it get caught at each ratio?
            int target = 20 + (rng.Next(0, 15)); // well inside the run, away from the edges
            double windowStart = baseline.StartSeconds + (target * baseline.Snapshots.SnapshotSeconds);
            var missed = new bool[ratios.Length, crashDbs.Length];
            bool anyCrash = false;
            for (int ci = 0; ci < crashDbs.Length; ci++)
            {
                var crashed = InjectCrash(audio, windowStart, baseline.Snapshots.SnapshotSeconds, durationMs: 8, extraDb: crashDbs[ci], rng);
                if (WindowPowers(crashed, end, offset) is not { } crashedPower)
                {
                    continue;
                }
                // Which window in crashedPower now covers windowStart (should be the same index; checked, not assumed).
                int idx = (int)Math.Round((windowStart - crashedPower.Start) / crashedPower.Step);
                if (idx < 0 || idx >= crashedPower.Power.Length)
                {
                    continue;
                }
                anyCrash = true;
                for (int ri = 0; ri < ratios.Length; ri++)
                {
                    missed[ri, ci] = !IsExcluded(crashedPower.Power, idx, ratios[ri]);
                }
            }
            hadCrash[seed] = anyCrash;
            missedHit[seed] = missed;
        });

        int falseTrials = hadBaseline.Count(x => x);
        int crashTrials = hadCrash.Count(x => x);
        var falseCounts = ratios.Select((_, ri) => Enumerable.Range(0, n).Count(seed => hadBaseline[seed] && falseHit[seed][ri])).ToArray();
        var missedCounts = ratios.Select((_, ri) => crashDbs.Select((_, ci) => Enumerable.Range(0, n).Count(seed => hadCrash[seed] && missedHit[seed][ri, ci])).ToArray()).ToArray();

        output.WriteLine($"false-crash baseline trials: {falseTrials}, crash-injected trials: {crashTrials}");
        output.WriteLine("ratio   false-crash%   " + string.Join("  ", crashDbs.Select(d => $"missed@{d}dB%")));
        for (int ri = 0; ri < ratios.Length; ri++)
        {
            double falsePct = falseTrials > 0 ? 100.0 * falseCounts[ri] / falseTrials : double.NaN;
            var missedPct = crashDbs.Select((_, ci) => crashTrials > 0 ? 100.0 * missedCounts[ri][ci] / crashTrials : double.NaN).ToArray();
            string line = string.Create(CultureInfo.InvariantCulture, $"{ratios[ri],5:F0}   {falsePct,10:F1}    ");
            line += string.Join("  ", missedPct.Select(pct => pct.ToString("F1", CultureInfo.InvariantCulture).PadLeft(10)));
            output.WriteLine(line);
        }

        // The shipped ratio: false-crash near gone, severe (40 dB) crashes still almost always caught.
        // Measured at ratio 10: false-crash about 1%, missed@40dB about 0.1% (was 8%/0% at the old 6x).
        int shipped = Array.IndexOf(ratios, ProbeChannel.CrashRatio);
        Assert.True(shipped >= 0, $"{nameof(ProbeChannel.CrashRatio)} ({ProbeChannel.CrashRatio}) is not one of the measured ratios");
        if (falseTrials > 0)
        {
            Assert.True(falseCounts[shipped] <= Math.Max(2, falseTrials / 10), $"{falseCounts[shipped]} of {falseTrials} trials false-triggered the crash filter at ratio {ProbeChannel.CrashRatio}");
        }
        if (crashTrials > 0)
        {
            int severe = Array.IndexOf(crashDbs, 40.0);
            Assert.True(missedCounts[shipped][severe] <= Math.Max(2, crashTrials / 10), $"{missedCounts[shipped][severe]} of {crashTrials} severe (40 dB) crashes were missed at ratio {ProbeChannel.CrashRatio}");
        }
    }

    /// <summary>One trial's per-window power and the run's own timing, recomputed the same way <see cref="ProbeChannel"/> does internally, so the crash filter's ratio can be swept cheaply.</summary>
    private sealed record Powers(double[] Power, bool[] Present, double Start, double Step);

    /// <summary>
    /// Re-derives each window's received power the same way the probe's crash filter sees it
    /// (<see cref="ProbeChannel.Measure(ReadOnlySpan{float}, double, double, double)"/>'s own
    /// <c>windowPower</c>), by calling <see cref="ProbeChannel.Measure"/> and reading its
    /// <see cref="ProbeMeasurement"/>: the estimates carry no raw power, so this mixes the kept
    /// audio on the same frequency and sums each window's energy itself, anchored on the same
    /// run the real pass chose.
    /// </summary>
    private static Powers? WindowPowers(ReadOnlySpan<float> audio, double toneEndSeconds, double offsetHz)
    {
        if (ProbeChannel.Measure(audio, toneEndSeconds, OnAir.CentreAudioHz + offsetHz) is not { } m)
        {
            return null;
        }
        var baseband = ChannelMaths.ToBaseband(audio, OnAir.CentreAudioHz);
        int l = ProbeChannel.Period;
        double w = -2 * Math.PI * offsetHz / ChannelMaths.Rate;
        int windows = m.Snapshots.Estimates.Length;
        var power = new double[windows];
        int startWindow = (int)Math.Round(m.StartSeconds / (l / (double)ChannelMaths.Rate));
        for (int j = 0; j < windows; j++)
        {
            int k = startWindow + j;
            double e = 0;
            for (int n = 0; n < l; n++)
            {
                int idx = (k * l) + n;
                if (idx >= baseband.Length)
                {
                    break;
                }
                var v = baseband[idx] * System.Numerics.Complex.FromPolarCoordinates(1, w * idx);
                e += (v.Real * v.Real) + (v.Imaginary * v.Imaginary);
            }
            power[j] = e;
        }
        return new Powers(power, [.. Enumerable.Repeat(true, windows)], m.StartSeconds, l / (double)ChannelMaths.Rate);
    }

    private static bool IsExcluded(double[] power, int index, double ratio)
    {
        double typical = Median(power);
        return power[index] > ratio * typical;
    }

    private static bool AnyFalseCrash(Powers p, double ratio)
    {
        double typical = Median(p.Power);
        for (int i = 0; i < p.Power.Length; i++)
        {
            if (p.Power[i] > ratio * typical)
            {
                return true;
            }
        }
        return false;
    }

    private static double Median(double[] v)
    {
        var s = v.Order().ToArray();
        int n = s.Length;
        return n % 2 == 1 ? s[n / 2] : (s[(n / 2) - 1] + s[n / 2]) / 2;
    }
}
