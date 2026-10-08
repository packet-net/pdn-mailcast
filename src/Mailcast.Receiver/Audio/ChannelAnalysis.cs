using System.Numerics;
using System.Runtime.CompilerServices;

namespace Mailcast.Receiver;

/// <summary>One propagation mode in one measurement: a group of paths less than 0.6 ms apart.</summary>
/// <param name="DelayMs">Its power-weighted delay after the first path, in ms.</param>
/// <param name="PowerDb">Its power against the strongest mode's, in dB.</param>
/// <param name="CentroidHz">The centre of its Doppler spectrum, in Hz, against the lock offset already taken out; null if it had none.</param>
/// <param name="SpreadHz">Its Doppler spread, two standard deviations, in Hz; null if it could not be measured.</param>
internal sealed record PictureMode(double DelayMs, double PowerDb, double? CentroidHz, double? SpreadHz);

/// <summary>What one run of channel estimates (one burst) says about the path.</summary>
internal sealed record PathPicture
{
    /// <summary>What measured it, as <see cref="ChannelSnapshots.Basis"/>.</summary>
    public required string Basis { get; init; }

    /// <summary>The modes, earliest first.</summary>
    public required IReadOnlyList<PictureMode> Modes { get; init; }

    /// <summary>The first path's delay on the estimates' own delays (<see cref="ChannelSnapshots.Lag"/>), in seconds.</summary>
    public double FirstPathSeconds { get; init; }

    /// <summary>The discrete paths fitted, in ms after the first.</summary>
    public required IReadOnlyList<double> PathsMs { get; init; }

    /// <summary>Which of <see cref="Modes"/> each path belongs to.</summary>
    public IReadOnlyList<int> PathModes { get; init; } = [];

    /// <summary>Their powers against the strongest path, in dB.</summary>
    public required IReadOnlyList<double> PathsDb { get; init; }

    /// <summary>RMS delay spread over the paths, in ms.</summary>
    public required double DelaySpreadMs { get; init; }

    /// <summary>Doppler spread of all paths together, two standard deviations, in Hz.</summary>
    public double? DopplerSpreadHz { get; init; }

    /// <summary>The frequency offset of all paths together, in Hz: the lock's plus the spectrum's centre.</summary>
    public double? OffsetHz { get; init; }

    /// <summary>How far the power fell below its median in the deepest tenth of the time, in dB.</summary>
    public double FadeDb { get; init; }

    /// <summary>How long the strongest path keeps half its correlation, in seconds.</summary>
    public double? CoherenceS { get; init; }

    /// <summary>Signal to noise in 3 kHz, dB.</summary>
    public double SnrDb { get; init; }

    /// <summary>The noise floor of the averaged delay profile against its peak, in dB.</summary>
    public double FloorDb { get; init; }

    /// <summary>The share of snapshots that were fit to use.</summary>
    public double GoodShare { get; init; }

    /// <summary>The usable snapshots.</summary>
    public int GoodSnapshots { get; init; }

    /// <summary>Seconds of signal behind it.</summary>
    public double Seconds { get; init; }

    /// <summary>The averaged delay profile, linear power against its peak, from <see cref="ProfileStartMs"/> after the first path in steps of <see cref="ProfileStepMs"/>.</summary>
    public required double[] Profile { get; init; }

    /// <summary>Where <see cref="Profile"/> starts, ms after the first path.</summary>
    public double ProfileStartMs { get; init; }

    /// <summary>The step of <see cref="Profile"/>, in ms.</summary>
    public double ProfileStepMs { get; init; }
}

/// <summary>
/// From channel estimates to the picture of the path: the averaged delay profile and its noise
/// floor, a fit of discrete paths with delays finer than a symbol, the paths grouped into modes,
/// their Doppler spectra, the delay spread, fading and coherence. A port of analyze.py.
/// </summary>
internal static class ChannelAnalysis
{
    /// <summary>A path is kept if its power is this far above the profile's floor, unless the estimates say otherwise (<see cref="ChannelSnapshots.FloorMarginDb"/>).</summary>
    public const double FloorMarginDb = 6.0;

    /// <summary>How far before the profile's peak paths are looked for, unless the estimates say otherwise (<see cref="ChannelSnapshots.SearchBeforeSeconds"/>).</summary>
    public const double SearchBeforeSeconds = 1.5e-3;

    /// <summary>Paths closer than this are one mode, unless the estimates say otherwise (<see cref="ChannelSnapshots.ModeGapSeconds"/>).</summary>
    public const double ModeGapSeconds = 0.6e-3;

    /// <summary>The most paths fitted.</summary>
    private const int MostPaths = 4;

    /// <summary>The path search's grid: 10 us, refined to 5 us.</summary>
    private const double Grid = 1e-5;

    /// <summary>Paths are kept at least this far apart.</summary>
    private const double Separation = 0.15e-3;

    private const int DopplerFft = 4096;

    /// <summary>The most Doppler a delay is corrected for: the probe's snapshots cover 4.7 Hz either way.</summary>
    private const double MostCoupledHz = 5;

    /// <summary>The fewest usable snapshots worth analysing: about 2 s of WN4.</summary>
    public const int FewestSnapshots = 30;

    /// <summary>The picture from one run of estimates, or null if too few were usable.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static PathPicture? Analyse(ChannelSnapshots s)
    {
        int nseg = s.Estimates.Length, nlag = s.Lags;
        int goodCount = s.Good.Count(g => g);
        if (nseg == 0 || goodCount < FewestSnapshots)
        {
            return null;
        }
        // Averaged delay profile over the usable snapshots.
        var pdp = new double[nlag];
        for (int j = 0; j < nseg; j++)
        {
            if (!s.Good[j])
            {
                continue;
            }
            for (int l = 0; l < nlag; l++)
            {
                pdp[l] += ChannelMaths.Norm(s.Estimates[j][l]);
            }
        }
        for (int l = 0; l < nlag; l++)
        {
            pdp[l] /= goodCount;
        }
        int ipk = 0;
        for (int l = 1; l < nlag; l++)
        {
            if (pdp[l] > pdp[ipk])
            {
                ipk = l;
            }
        }
        double peakLag = s.Lag(ipk);
        // Floor: the empty delays well before the first arrival and the far tail.
        double floorSum = 0;
        int floorCount = 0;
        for (int l = 0; l < nlag; l++)
        {
            double lag = s.Lag(l);
            if (lag < peakLag - s.SearchBeforeSeconds - 0.1e-3 || lag > peakLag + 6.5e-3)
            {
                floorSum += pdp[l];
                floorCount++;
            }
        }
        double floor = floorCount > 0 ? floorSum / floorCount : double.NaN;

        var model = new PathModel(s);
        double spanLo = peakLag - s.SearchBeforeSeconds, spanHi = Math.Min(peakLag + 6.0e-3, s.Lag(nlag - 1) - 0.5e-3);
        var fits = model.Search(spanLo, spanHi);
        (double[] Taus, double[] Powers)? kept = null;
        foreach (var taus in fits)
        {
            var gains = model.Gains(taus, goodOnly: true);
            var p = MeanPower(gains, taus.Length);
            if (kept is null || p.Min() > floor * Math.Pow(10, s.FloorMarginDb / 10))
            {
                kept = (taus, p);
            }
            else
            {
                break;
            }
        }
        if (s.FinerDelays)
        {
            // Below the search's 5 us lattice, for a measurement that is good for it (the probe's).
            var finer = model.Refine(kept!.Value.Taus);
            kept = (finer, MeanPower(model.Gains(finer, goodOnly: true), finer.Length));
        }
        var order = Enumerable.Range(0, kept!.Value.Taus.Length).OrderBy(i => kept.Value.Taus[i]).ToArray();
        double[] tau = [.. order.Select(i => kept.Value.Taus[i])];
        double[] pw = [.. order.Select(i => kept.Value.Powers[i])];
        // Gains for every snapshot (the bad ones are left out of the spectra below).
        var full = model.Gains(tau, goodOnly: false);
        double dt = s.SnapshotSeconds;

        // Modes.
        var groups = new List<List<int>>();
        for (int i = 0; i < tau.Length; i++)
        {
            if (groups.Count > 0 && tau[i] - tau[groups[^1][^1]] < s.ModeGapSeconds)
            {
                groups[^1].Add(i);
            }
            else
            {
                groups.Add([i]);
            }
        }
        double pmax = groups.Max(g => g.Sum(i => pw[i]));
        var modes = new List<PictureMode>();
        var allSpectrum = new double[DopplerFft];
        // The Doppler spectra over every snapshot, or for estimates with gaps that matter (the
        // probe's, after a lost stretch of audio) over the longest unbroken run of good ones: a
        // gap in the series would smear a steady path's spectrum into a spread it does not have.
        var (dopplerFrom, dopplerCount) = s.ContiguousDoppler ? LongestRun(s.Good, s.Missing) : (0, nseg);
        bool spreadKnown = !s.ContiguousDoppler || dopplerCount >= s.FewestDopplerSnapshots;
        double windowSigma = 1 / (Math.Sqrt(3) * dopplerCount * dt);
        var spectra = new double[tau.Length][];
        for (int i = 0; i < tau.Length; i++)
        {
            spectra[i] = DopplerSpectrum(full, i, s.Good, dopplerFrom, dopplerCount);
            for (int f = 0; f < DopplerFft; f++)
            {
                allSpectrum[f] += spectra[i][f];
            }
        }
        var moments = new (double? Centroid, double? Spread)[groups.Count];
        for (int m = 0; m < groups.Count; m++)
        {
            var spectrum = new double[DopplerFft];
            foreach (int i in groups[m])
            {
                for (int f = 0; f < DopplerFft; f++)
                {
                    spectrum[f] += spectra[i][f];
                }
            }
            moments[m] = Moments(spectrum, dt, windowSigma);
            if (!spreadKnown)
            {
                // Too short a run for the resolution a spread is given to: the shift only.
                moments[m] = (moments[m].Centroid, null);
            }
            // A signal whose delay and Doppler are coupled (the probe's): each mode's paths moved
            // back by its own shift, so two modes with different Doppler keep their true spacing.
            if (s.DelayPerHzSeconds != 0 && moments[m].Centroid is double modeShift)
            {
                double back = s.DelayPerHzSeconds * Math.Clamp(modeShift, -MostCoupledHz, MostCoupledHz);
                foreach (int i in groups[m])
                {
                    tau[i] += back;
                }
            }
        }
        double first = tau[0];
        for (int m = 0; m < groups.Count; m++)
        {
            var g = groups[m];
            double power = g.Sum(i => pw[i]);
            double delay = g.Sum(i => tau[i] * pw[i]) / power;
            modes.Add(new PictureMode((delay - first) * 1e3, 10 * Math.Log10(power / pmax), moments[m].Centroid, moments[m].Spread));
        }
        double total = pw.Sum();
        double mu = tau.Zip(pw).Sum(t => t.First * t.Second) / total;
        double rms = Math.Sqrt(tau.Zip(pw).Sum(t => t.Second * (t.First - mu) * (t.First - mu)) / total);
        var (allCentroid, allSpread) = Moments(allSpectrum, dt, windowSigma);
        allSpread = spreadKnown ? allSpread : null;

        int strongest = Array.IndexOf(pw, pw.Max());
        double? coherence = CoherenceSeconds(full, strongest, s.Good, dt);
        double fade = FadeDepthDb(s);

        // The profile again, from 1 ms before the first path to 7 ms after it, against its peak.
        int shift = (int)Math.Round((first - s.FirstLagSeconds) / s.LagStepSeconds);
        double stepMs = s.LagStepSeconds * 1e3;
        int before = (int)Math.Round(1.0 / stepMs), after = (int)Math.Round(7.0 / stepMs);
        var profile = new double[before + after + 1];
        for (int k = 0; k < profile.Length; k++)
        {
            int l = shift - before + k;
            profile[k] = l >= 0 && l < nlag ? pdp[l] / pdp[ipk] : double.NaN;
        }
        return new PathPicture
        {
            Basis = s.Basis,
            Modes = modes,
            FirstPathSeconds = first,
            PathModes = [.. Enumerable.Range(0, tau.Length).Select(i => groups.FindIndex(g => g.Contains(i)))],
            PathsMs = [.. tau.Select(t => (t - first) * 1e3)],
            PathsDb = [.. pw.Select(p => 10 * Math.Log10(p / pw.Max()))],
            DelaySpreadMs = rms * 1e3,
            DopplerSpreadHz = allSpread,
            OffsetHz = allCentroid is { } c ? s.OffsetHz + c : null,
            FadeDb = fade,
            CoherenceS = coherence,
            SnrDb = s.SnrDb,
            FloorDb = 10 * Math.Log10(floor / pdp[ipk]),
            GoodShare = (double)goodCount / nseg,
            GoodSnapshots = goodCount,
            Seconds = nseg * dt,
            Profile = profile,
            ProfileStartMs = -before * stepMs,
            ProfileStepMs = stepMs,
        };
    }

    private static double[] MeanPower(Complex[][] gains, int paths)
    {
        var p = new double[paths];
        foreach (var row in gains)
        {
            for (int i = 0; i < paths; i++)
            {
                p[i] += ChannelMaths.Norm(row[i]);
            }
        }
        for (int i = 0; i < paths; i++)
        {
            p[i] /= Math.Max(gains.Length, 1);
        }
        return p;
    }

    /// <summary>Hann-windowed periodogram of one path's gain over the snapshots, bad ones zeroed, centred on 0 Hz (numpy's fftshift order).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double[] DopplerSpectrum(Complex[][] gains, int path, bool[] good, int from, int n)
    {
        var x = new Complex[DopplerFft];
        for (int j = 0; j < Math.Min(n, DopplerFft); j++)
        {
            double w = n > 1 ? 0.5 - (0.5 * Math.Cos(2 * Math.PI * j / (n - 1))) : 1;
            x[j] = good[from + j] ? gains[from + j][path] * w : Complex.Zero;
        }
        ChannelMaths.Fft(x, false);
        var p = new double[DopplerFft];
        int half = DopplerFft / 2;
        for (int k = 0; k < DopplerFft; k++)
        {
            p[k] = ChannelMaths.Norm(x[(k + half) % DopplerFft]);
        }
        return p;
    }

    /// <summary>
    /// The longest run of good snapshots, bridging gaps of up to <see cref="BridgedGap"/> unfit
    /// ones (a crash, zeroed, which does no harm) but never one where the signal was
    /// <paramref name="missing"/>: where it starts, and how many.
    /// </summary>
    private static (int From, int Count) LongestRun(bool[] good, bool[]? missing)
    {
        bool Gap(int j) => missing is not null && j < missing.Length && missing[j];
        int bestFrom = 0, best = 0;
        int j = 0;
        while (j < good.Length)
        {
            if (!good[j])
            {
                j++;
                continue;
            }
            int start = j, end = j;
            while (end < good.Length)
            {
                int next = end + 1;
                while (next < good.Length && !good[next] && !Gap(next) && next - end <= BridgedGap)
                {
                    next++;
                }
                if (next < good.Length && good[next] && next - end <= BridgedGap + 1)
                {
                    end = next;
                }
                else
                {
                    break;
                }
            }
            if (end - start + 1 > best)
            {
                (bestFrom, best) = (start, end - start + 1);
            }
            j = end + 1;
        }
        return (bestFrom, best);
    }

    /// <summary>The longest gap of bad snapshots a Doppler run is carried across.</summary>
    private const int BridgedGap = 2;

    /// <summary>The frequency of bin <paramref name="k"/> of a centred spectrum, in Hz.</summary>
    private static double BinHz(int k, double dt) => (k - (DopplerFft / 2)) / (DopplerFft * dt);

    /// <summary>
    /// Floor-subtracted centroid and two-sigma width within 2 Hz of the peak, with the window's
    /// own width taken out, as analyze.py's spectrum_moments.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static (double? Centroid, double? Spread) Moments(double[] p, double dt, double windowSigma)
    {
        int kp = 0;
        for (int k = 1; k < p.Length; k++)
        {
            if (p[k] > p[kp])
            {
                kp = k;
            }
        }
        double fpk = BinHz(kp, dt);
        var far = new List<double>();
        for (int k = 0; k < p.Length; k++)
        {
            if (Math.Abs(BinHz(k, dt) - fpk) > 3.0)
            {
                far.Add(p[k]);
            }
        }
        double floor = far.Count > 0 ? ChannelMaths.Median(far) : 0;
        double sq = 0, sf = 0;
        for (int k = 0; k < p.Length; k++)
        {
            double f = BinHz(k, dt);
            if (Math.Abs(f - fpk) <= 2.0)
            {
                double q = Math.Max(p[k] - floor, 0);
                sq += q;
                sf += q * f;
            }
        }
        if (sq <= 0)
        {
            return (null, null);
        }
        double fc = sf / sq;
        double var = 0;
        for (int k = 0; k < p.Length; k++)
        {
            double f = BinHz(k, dt);
            if (Math.Abs(f - fpk) <= 2.0)
            {
                var += Math.Max(p[k] - floor, 0) * (f - fc) * (f - fc);
            }
        }
        var /= sq;
        double sigma = Math.Sqrt(Math.Max(var - (windowSigma * windowSigma), 0));
        return (fc, 2 * sigma);
    }

    /// <summary>
    /// How long the path's gain keeps half its correlation: its autocorrelation over the burst,
    /// normalised at one snapshot's lag (which leaves out the estimation noise, all at lag 0),
    /// and the first lag at which it falls below a half. Where it never does within the half
    /// burst examined, a Gaussian fitted to it says where it would (the Gaussian Doppler
    /// model's shape), up to <see cref="Longest"/>. Null with too few snapshots.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double? CoherenceSeconds(Complex[][] gains, int path, bool[] good, double dt)
    {
        int n = gains.Length;
        int lags = n / 2;
        if (lags < 4)
        {
            return null;
        }
        var r = new double[lags];
        for (int k = 0; k < lags; k++)
        {
            Complex s = Complex.Zero;
            int both = 0;
            for (int j = 0; j + k < n; j++)
            {
                if (good[j] && good[j + k])
                {
                    s += Complex.Conjugate(gains[j][path]) * gains[j + k][path];
                    both++;
                }
            }
            r[k] = both > 0 ? (s / both).Magnitude : double.NaN;
        }
        if (!(r[1] > 0))
        {
            return null;
        }
        for (int k = 2; k < lags; k++)
        {
            double v = r[k] / r[1], before = r[k - 1] / r[1];
            if (v < 0.5)
            {
                // Between the two lags, linearly.
                double frac = before > v ? (before - 0.5) / (before - v) : 0;
                return (k - 1 + frac) * dt;
            }
        }
        // ln r = c - a t^2 over the lags examined; it falls to a half at sqrt((c + ln 2) / a).
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int count = 0;
        for (int k = 1; k < lags; k++)
        {
            double v = r[k] / r[1];
            if (!(v > 0))
            {
                continue;
            }
            double x = (k * dt) * (k * dt), y = Math.Log(v);
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
            count++;
        }
        double det = (count * sxx) - (sx * sx);
        if (count < 3 || Math.Abs(det) < 1e-30)
        {
            return Longest;
        }
        double slope = ((count * sxy) - (sx * sy)) / det;
        double c = (sy - (slope * sx)) / count;
        double a = -slope;
        if (!(a > 0) || c + Math.Log(2) <= 0)
        {
            return Longest;
        }
        return Math.Min(Math.Sqrt((c + Math.Log(2)) / a), Longest);
    }

    /// <summary>The longest coherence time reported, seconds: well past what one burst can show.</summary>
    public const double Longest = 30;

    /// <summary>
    /// Fade depth: how far the total received channel power fell below its median in the deepest
    /// tenth of the usable snapshots, in dB.
    /// </summary>
    private static double FadeDepthDb(ChannelSnapshots s)
    {
        var power = new List<double>();
        for (int j = 0; j < s.Estimates.Length; j++)
        {
            if (s.Good[j])
            {
                power.Add(s.Estimates[j].Sum(ChannelMaths.Norm));
            }
        }
        power.Sort();
        double median = ChannelMaths.Median(power);
        double low = power[(int)Math.Floor(0.1 * (power.Count - 1))];
        return low > 0 ? 10 * Math.Log10(median / low) : 30;
    }

    /// <summary>
    /// The discrete-path model of the estimates: each path a raised cosine at its delay, the
    /// path gains free in each snapshot. The search for the delays minimises what is left over,
    /// over all snapshots together, as chanest.py's search_paths.
    /// </summary>
    private sealed class PathModel
    {
        private readonly ChannelSnapshots _s;
        private readonly int _nlag;
        private readonly Complex[] _q;
        private readonly double[] _qRe;
        private readonly double _trQ;
        private readonly Dictionary<long, (double[] R, double[] Qr)> _cache = [];
        private double _latticeOrigin;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public PathModel(ChannelSnapshots s)
        {
            _s = s;
            _nlag = s.Lags;
            // Q = sum over usable snapshots of c c^H.
            _q = new Complex[_nlag * _nlag];
            for (int j = 0; j < s.Estimates.Length; j++)
            {
                if (!s.Good[j])
                {
                    continue;
                }
                var c = s.Estimates[j];
                for (int a = 0; a < _nlag; a++)
                {
                    var ca = c[a];
                    for (int b = a; b < _nlag; b++)
                    {
                        _q[(a * _nlag) + b] += ca * Complex.Conjugate(c[b]);
                    }
                }
            }
            for (int a = 0; a < _nlag; a++)
            {
                for (int b = 0; b < a; b++)
                {
                    _q[(a * _nlag) + b] = Complex.Conjugate(_q[(b * _nlag) + a]);
                }
                _trQ += _q[(a * _nlag) + a].Real;
            }
            // The path delays are real, so only Q's real part reaches the cost: r_a^T Q r_b has
            // real part r_a^T Re(Q) r_b.
            _qRe = [.. _q.Select(z => z.Real)];
        }

        /// <summary>Delays on a 5 us lattice from the search's start, so each is computed once.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private (double[] R, double[] Qr) At(long k)
        {
            if (_cache.TryGetValue(k, out var hit))
            {
                return hit;
            }
            double t = _latticeOrigin + (k * Grid / 2);
            var r = new double[_nlag];
            for (int l = 0; l < _nlag; l++)
            {
                r[l] = _s.PulseAt(_s.Lag(l) - t);
            }
            var qr = new double[_nlag];
            for (int a = 0; a < _nlag; a++)
            {
                qr[a] = Dot(_qRe.AsSpan(a * _nlag, _nlag), r);
            }
            var entry = (r, qr);
            _cache[k] = entry;
            return entry;
        }

        private double Delay(long k) => _latticeOrigin + (k * Grid / 2);

        /// <summary>What is left over with paths at <paramref name="taus"/>, anywhere, against the total.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private double CostAt(double[] taus)
        {
            int n = taus.Length;
            var rows = new (double[] R, double[] Qr)[n];
            for (int i = 0; i < n; i++)
            {
                var r = new double[_nlag];
                for (int l = 0; l < _nlag; l++)
                {
                    r[l] = _s.PulseAt(_s.Lag(l) - taus[i]);
                }
                var qr = new double[_nlag];
                for (int a = 0; a < _nlag; a++)
                {
                    qr[a] = Dot(_qRe.AsSpan(a * _nlag, _nlag), r);
                }
                rows[i] = (r, qr);
            }
            return CostOf(rows);
        }

        /// <summary>
        /// The delays moved off the lattice to where they fit best, each in turn within half a
        /// lattice step either way by golden section, twice round.
        /// </summary>
        public double[] Refine(double[] taus)
        {
            var t = (double[])taus.Clone();
            double g = (Math.Sqrt(5) - 1) / 2;
            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < t.Length; i++)
                {
                    double a = t[i] - (Grid / 2), b = t[i] + (Grid / 2);
                    double Try(double at)
                    {
                        var trial = (double[])t.Clone();
                        trial[i] = at;
                        return CostAt(trial);
                    }
                    double c = b - (g * (b - a)), d = a + (g * (b - a));
                    double fc = Try(c), fd = Try(d);
                    for (int k = 0; k < 16; k++)
                    {
                        if (fc < fd)
                        {
                            (b, d, fd) = (d, c, fc);
                            c = b - (g * (b - a));
                            fc = Try(c);
                        }
                        else
                        {
                            (a, c, fc) = (c, d, fd);
                            d = a + (g * (b - a));
                            fd = Try(d);
                        }
                    }
                    double best = (a + b) / 2;
                    if (Try(best) < Try(t[i]))
                    {
                        t[i] = best;
                    }
                }
            }
            return t;
        }

        /// <summary>What is left over with paths at lattice points <paramref name="ks"/>, against the total.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private double Cost(ReadOnlySpan<long> ks)
        {
            int n = ks.Length;
            var rows = new (double[] R, double[] Qr)[n];
            for (int i = 0; i < n; i++)
            {
                rows[i] = At(ks[i]);
            }
            return CostOf(rows);
        }

        /// <summary>The cost of paths whose pulses (and Q times them) are <paramref name="rows"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private double CostOf((double[] R, double[] Qr)[] rows)
        {
            int n = rows.Length;
            var g = new double[n * n];
            var mre = new double[n * n];
            for (int a = 0; a < n; a++)
            {
                for (int b = 0; b < n; b++)
                {
                    g[(a * n) + b] = Dot(rows[a].R, rows[b].R);
                    mre[(a * n) + b] = Dot(rows[a].R, rows[b].Qr);
                }
            }
            // tr(G^-1 M), M Hermitian and G real symmetric: only M's real part reaches the trace.
            double tr = 0;
            for (int c = 0; c < n; c++)
            {
                var col = new double[n];
                for (int r = 0; r < n; r++)
                {
                    col[r] = mre[(r * n) + c];
                }
                var x = ChannelMaths.SolveReal(g, col, n);
                if (x is null)
                {
                    return double.PositiveInfinity;
                }
                tr += x[c];
            }
            return (_trQ - tr) / _trQ;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
        {
            int w = Vector<double>.Count, i = 0;
            var acc = Vector<double>.Zero;
            for (; i <= a.Length - w; i += w)
            {
                acc += new Vector<double>(a.Slice(i, w)) * new Vector<double>(b.Slice(i, w));
            }
            double sum = Vector.Sum(acc);
            for (; i < a.Length; i++)
            {
                sum += a[i] * b[i];
            }
            return sum;
        }

        /// <summary>The best delays for 1 path, 2 paths and so on up to <see cref="MostPaths"/>, each greedy then refined.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public List<double[]> Search(double lo, double hi)
        {
            _latticeOrigin = lo;
            int gridPoints = (int)Math.Ceiling((hi - lo) / Grid);
            long sep = (long)Math.Round(Separation / (Grid / 2));
            long reach = (long)Math.Round(0.3e-3 / (Grid / 2));
            var found = new List<long>();
            var results = new List<double[]>();
            var trial = new List<long>();
            for (int paths = 1; paths <= MostPaths; paths++)
            {
                long bestK = -1;
                double bestCost = double.PositiveInfinity;
                for (int gp = 0; gp < gridPoints; gp++)
                {
                    long k = 2L * gp;
                    if (found.Any(f => Math.Abs(k - f) < sep))
                    {
                        continue;
                    }
                    trial.Clear();
                    trial.AddRange(found);
                    trial.Add(k);
                    double cost = Cost(trial.ToArray());
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestK = k;
                    }
                }
                if (bestK < 0)
                {
                    break;
                }
                found.Add(bestK);
                for (int round = 0; round < 3; round++)
                {
                    for (int i = 0; i < found.Count; i++)
                    {
                        long bi = found[i];
                        double bc = double.PositiveInfinity;
                        for (long k = found[i] - reach; k < found[i] + reach; k++)
                        {
                            bool tooClose = false;
                            for (int j = 0; j < found.Count; j++)
                            {
                                if (j != i && Math.Abs(k - found[j]) < sep)
                                {
                                    tooClose = true;
                                    break;
                                }
                            }
                            if (tooClose)
                            {
                                continue;
                            }
                            var t = found.ToArray();
                            t[i] = k;
                            double cost = Cost(t);
                            if (cost < bc)
                            {
                                bc = cost;
                                bi = k;
                            }
                        }
                        found[i] = bi;
                    }
                }
                results.Add([.. found.Select(Delay)]);
            }
            return results;
        }

        /// <summary>Each snapshot's least-squares path gains for paths at <paramref name="taus"/>; the usable snapshots only, or all.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public Complex[][] Gains(double[] taus, bool goodOnly)
        {
            int n = taus.Length;
            var r = new double[n][];
            for (int i = 0; i < n; i++)
            {
                r[i] = new double[_nlag];
                for (int l = 0; l < _nlag; l++)
                {
                    r[i][l] = _s.PulseAt(_s.Lag(l) - taus[i]);
                }
            }
            var g = new double[n * n];
            for (int a = 0; a < n; a++)
            {
                for (int b = 0; b < n; b++)
                {
                    double s = 0;
                    for (int l = 0; l < _nlag; l++)
                    {
                        s += r[a][l] * r[b][l];
                    }
                    g[(a * n) + b] = s;
                }
            }
            // G^-1, one column at a time; then gains = G^-1 R^T c for each snapshot.
            var inv = new double[n * n];
            for (int c = 0; c < n; c++)
            {
                var e = new double[n];
                e[c] = 1;
                var x = ChannelMaths.SolveReal(g, e, n) ?? new double[n];
                for (int row = 0; row < n; row++)
                {
                    inv[(row * n) + c] = x[row];
                }
            }
            var rows = new List<Complex[]>();
            for (int j = 0; j < _s.Estimates.Length; j++)
            {
                if (goodOnly && !_s.Good[j])
                {
                    continue;
                }
                var c = _s.Estimates[j];
                var rc = new Complex[n];
                for (int i = 0; i < n; i++)
                {
                    Complex sum = Complex.Zero;
                    for (int l = 0; l < _nlag; l++)
                    {
                        sum += r[i][l] * c[l];
                    }
                    rc[i] = sum;
                }
                var gain = new Complex[n];
                for (int a = 0; a < n; a++)
                {
                    Complex sum = Complex.Zero;
                    for (int b = 0; b < n; b++)
                    {
                        sum += inv[(a * n) + b] * rc[b];
                    }
                    gain[a] = sum;
                }
                rows.Add(gain);
            }
            return [.. rows];
        }
    }
}
