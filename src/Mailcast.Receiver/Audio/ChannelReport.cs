using System.Globalization;

namespace Mailcast.Receiver;

/// <summary>One propagation mode over a slot.</summary>
public sealed record ChannelMode
{
    /// <summary>Which hop it is, <c>1F</c>, <c>2F</c>, <c>3F</c> or <c>1E</c>; null if it fits none, or the receiver's place is not known.</summary>
    public string? Label { get; init; }

    /// <summary>Its delay after the first mode, in ms.</summary>
    public double DelayMs { get; init; }

    /// <summary>Its power against the strongest mode's, in dB.</summary>
    public double PowerDb { get; init; }

    /// <summary>Its Doppler shift against the strongest mode's, in Hz.</summary>
    public double? DopplerShiftHz { get; init; }

    /// <summary>Its Doppler spread (two standard deviations), in Hz.</summary>
    public double? DopplerSpreadHz { get; init; }

    /// <summary>How many of the slot's measurements it was seen in.</summary>
    public int SeenIn { get; init; }
}

/// <summary>
/// The radio path from GB7RDG over one slot, as measured from the bursts decoded or from the
/// sounding probe after the tone: <see cref="Basis"/> says which, and <see cref="Other"/> holds
/// the other measurement when there were both.
/// </summary>
public sealed record ChannelReport
{
    /// <summary>The slot: its scheduled start, or when it began without a schedule.</summary>
    public DateTimeOffset Slot { get; init; }

    /// <summary>When it was measured.</summary>
    public DateTimeOffset Measured { get; init; }

    /// <summary>What it was measured from: <c>bursts</c>, or <c>probe</c> for the sounding probe after the tone.</summary>
    public string Basis { get; init; } = "bursts";

    /// <summary>The measurements (bursts, or probes) it is made from.</summary>
    public int Measurements { get; init; }

    /// <summary>The bursts (or probes) kept for it, measurable or not.</summary>
    public int Kept { get; init; }

    /// <summary>The same slot measured the other way, when it was measured both ways; this report is the better of the two (<see cref="Best"/>).</summary>
    public ChannelReport? Other { get; init; }

    /// <summary>The averaged delay profile's floor against its peak, dB: how far below the strongest mode a weaker one can still be seen.</summary>
    public double? FloorDb { get; init; }

    /// <summary>Whether there was enough to say anything.</summary>
    public bool Enough { get; init; }

    /// <summary>The modes, earliest first.</summary>
    public IReadOnlyList<ChannelMode> Modes { get; init; } = [];

    /// <summary>RMS delay spread, ms.</summary>
    public double? DelaySpreadMs { get; init; }

    /// <summary>Doppler spread of all paths together (two standard deviations), Hz.</summary>
    public double? DopplerSpreadHz { get; init; }

    /// <summary>How far the power fell below its median in the deepest tenth of the time, dB.</summary>
    public double? FadeDb { get; init; }

    /// <summary>How long the strongest path keeps half its correlation, seconds.</summary>
    public double? CoherenceS { get; init; }

    /// <summary>The F layer's virtual height from the one and two hop modes, km; null without both. Without the receiver's place it is worked out for <see cref="NominalKm"/>, which changes it little.</summary>
    public double? VirtualHeightKm { get; init; }

    /// <summary>Signal to noise in 3 kHz, dB.</summary>
    public double? SnrDb { get; init; }

    /// <summary>The frequency offset GB7RDG was heard at, Hz: both radios' errors and the Doppler shift together.</summary>
    public double? OffsetHz { get; init; }

    /// <summary>Ground distance from GB7RDG, km; null if the receiver's place is not known.</summary>
    public double? DistanceKm { get; init; }

    /// <summary>The receiver's locator the hops were worked out for.</summary>
    public string? Locator { get; init; }

    /// <summary>The averaged delay profile in dB against its peak, from <see cref="ProfileStartMs"/> after the first mode in steps of <see cref="ProfileStepMs"/>.</summary>
    public IReadOnlyList<double?> ProfileDb { get; init; } = [];

    /// <summary>Where <see cref="ProfileDb"/> starts, ms.</summary>
    public double ProfileStartMs { get; init; }

    /// <summary>The step of <see cref="ProfileDb"/>, ms.</summary>
    public double ProfileStepMs { get; init; }

    /// <summary>How long the measurement took on the worker thread, seconds.</summary>
    public double ComputeSeconds { get; init; }

    /// <summary>The picture in words, for the page and the log.</summary>
    public string Words { get; init; } = "";

    /// <summary>
    /// The ground distance assumed for the height when the receiver's place is not known: a
    /// typical UK path. The height barely depends on it: 1.93 ms between 1F and 2F is 289 km
    /// straight overhead and 307 km at 300 km.
    /// </summary>
    public const double NominalKm = 150;

    /// <summary>What the page says of a probe found but too weak or too spoilt to measure.</summary>
    public const string TooWeakProbe = "The probe was too weak to measure.";

    /// <summary>What the page says when there is too little to measure.</summary>
    public const string TooLittle = "Not enough decoded to measure.";

    /// <summary>
    /// The slot's report from its two measurements: the probe's when the bursts' says too little,
    /// or when both say enough and the probe's profile reaches deeper (a lower floor, so weaker
    /// modes are seen); otherwise the bursts'. The other is kept beside it. A probe that was not
    /// found (an older head end, or nothing heard) leaves the bursts' report as it was.
    /// </summary>
    internal static ChannelReport Best(ChannelReport bursts, ChannelReport? probe)
    {
        if (probe is not { Measurements: > 0 })
        {
            return bursts;
        }
        bool probeBetter = probe.Enough && (!bursts.Enough || (probe.FloorDb ?? 0) < (bursts.FloorDb ?? 0));
        return probeBetter
            ? probe with { Other = bursts.Kept > 0 ? bursts : null }
            : bursts with { Other = probe };
    }

    /// <summary>
    /// The slot's picture from its measurements, as summarize.py makes it: each measurement's
    /// modes against its first, clustered across measurements, kept if seen in enough of them,
    /// and medians of the rest.
    /// </summary>
    internal static ChannelReport Summarise(DateTimeOffset slot, DateTimeOffset measured, IReadOnlyList<PathPicture> pictures, int kept, GroundPlace? place, string? basis = null)
    {
        basis ??= pictures.Count > 0 ? pictures[0].Basis : "bursts";
        var report = new ChannelReport
        {
            Slot = slot,
            Measured = measured,
            Basis = basis,
            Measurements = pictures.Count,
            Kept = kept,
            Locator = place?.Locator,
            DistanceKm = place is null ? null : Math.Round(PathGeometry.DistanceKm(place, PathGeometry.Gb7rdg)),
        };
        // One probe is a measurement in itself; bursts are short, so it takes a few.
        int fewest = basis == ProbeChannel.Basis ? ChannelAnalysis.FewestSnapshots : 2 * ChannelAnalysis.FewestSnapshots;
        if (pictures.Count == 0 || pictures.Sum(p => p.GoodSnapshots) < fewest)
        {
            return report with { Words = basis == ProbeChannel.Basis ? TooWeakProbe : TooLittle };
        }
        int n = pictures.Count;
        // Every mode against each measurement's strongest, with its power and spread, and its
        // Doppler shift against that same strongest mode (packet-net/pdn-mailcast#60: naming
        // modes after whichever arrived first, rather than the strongest, let a sidelobe ahead
        // of the real signal shift every delay and so every hop label).
        var later = new List<(double Delay, double Power, double? Spread, double? Shift)>();
        foreach (var p in pictures)
        {
            int strongest = 0;
            for (int i = 1; i < p.Modes.Count; i++)
            {
                if (p.Modes[i].PowerDb > p.Modes[strongest].PowerDb)
                {
                    strongest = i;
                }
            }
            var m1 = p.Modes[strongest];
            for (int i = 0; i < p.Modes.Count; i++)
            {
                if (i == strongest)
                {
                    continue;
                }
                var m = p.Modes[i];
                later.Add((m.DelayMs - m1.DelayMs, m.PowerDb, m.SpreadHz, m.CentroidHz - m1.CentroidHz));
            }
        }
        var clusters = new List<List<(double Delay, double Power, double? Spread, double? Shift)>>();
        foreach (var x in later.OrderBy(x => x.Delay))
        {
            if (clusters.Count > 0 && x.Delay - clusters[^1][^1].Delay < 0.4)
            {
                clusters[^1].Add(x);
            }
            else
            {
                clusters.Add([x]);
            }
        }
        int need = Math.Max(1, (int)Math.Ceiling(0.4 * n));
        var modes = new List<ChannelMode>
        {
            new()
            {
                DelayMs = 0,
                PowerDb = Power(ChannelMaths.Median(pictures.Select(p => p.Modes.Max(m => m.PowerDb)))),
                DopplerShiftHz = 0,
                DopplerSpreadHz = Rounded(ChannelMaths.Median(pictures.Select(p => p.Modes.MaxBy(m => m.PowerDb)!.SpreadHz ?? double.NaN)), 2),
                SeenIn = n,
            },
        };
        // A cluster seen consistently ahead of the strongest path (a negative delay) cannot be a
        // real longer hop, so it is a processing sidelobe, not a mode: left out here rather than
        // reported with an invented, impossibly early "hop".
        foreach (var c in clusters.Where(c => c.Count >= need && ChannelMaths.Median(c.Select(x => x.Delay)) >= 0))
        {
            modes.Add(new ChannelMode
            {
                DelayMs = Rounded(ChannelMaths.Median(c.Select(x => x.Delay)), 2) ?? 0,
                PowerDb = Power(ChannelMaths.Median(c.Select(x => x.Power))),
                DopplerShiftHz = Rounded(ChannelMaths.Median(c.Select(x => x.Shift ?? double.NaN)), 2),
                DopplerSpreadHz = Rounded(ChannelMaths.Median(c.Select(x => x.Spread ?? double.NaN)), 2),
                SeenIn = c.Count,
            });
        }
        double? height = null;
        // Naming the hops needs the distance; the height hardly does, so without the place it is
        // still given, from the paths as they would be named on a typical UK path.
        var (labels, h) = PathGeometry.Label(place is null ? NominalKm : PathGeometry.DistanceKm(place, PathGeometry.Gb7rdg), modes.Select(m => m.DelayMs).ToArray());
        if (place is not null)
        {
            modes = [.. modes.Select((m, i) => m with { Label = labels[i] })];
        }
        height = h is { } km && double.IsFinite(km) ? Math.Round(km) : null;

        // The profile, averaged in power across the measurements.
        var first = pictures[0];
        int len = pictures.Min(p => p.Profile.Length);
        var profile = new double?[len];
        for (int i = 0; i < len; i++)
        {
            var v = pictures.Select(p => p.Profile[i]).Where(d => !double.IsNaN(d)).ToArray();
            profile[i] = v.Length == 0 || !(v.Average() > 0) ? null : Rounded(10 * Math.Log10(v.Average()), 1);
        }

        report = report with
        {
            Enough = true,
            Modes = modes,
            DelaySpreadMs = Rounded(ChannelMaths.Median(pictures.Select(p => p.DelaySpreadMs)), 2),
            DopplerSpreadHz = Rounded(ChannelMaths.Median(pictures.Select(p => p.DopplerSpreadHz ?? double.NaN)), 2),
            FadeDb = Rounded(ChannelMaths.Median(pictures.Select(p => p.FadeDb)), 1),
            CoherenceS = Rounded(ChannelMaths.Median(pictures.Select(p => p.CoherenceS ?? double.NaN)), 1),
            VirtualHeightKm = height,
            SnrDb = Rounded(ChannelMaths.Median(pictures.Select(p => p.SnrDb)), 1),
            OffsetHz = Rounded(ChannelMaths.Median(pictures.Select(p => p.OffsetHz ?? double.NaN)), 2),
            FloorDb = Rounded(ChannelMaths.Median(pictures.Select(p => p.FloorDb)), 1),
            ProfileDb = profile,
            ProfileStartMs = Rounded(first.ProfileStartMs, 4) ?? 0,
            ProfileStepMs = Rounded(first.ProfileStepMs, 6) ?? 0,
        };
        return report with { Words = Describe(report) };
    }

    /// <summary>Rounded, or null for NaN or infinity: neither may reach the JSON status, which cannot carry them.</summary>
    private static double? Rounded(double v, int digits) => double.IsFinite(v) ? Math.Round(v, digits) : null;

    /// <summary>A mode's power in dB, finite: a mode too weak to say is 99 dB down.</summary>
    private static double Power(double db) => Rounded(Math.Max(db, -99), 1) ?? -99;

    /// <summary>
    /// The picture in a few plain sentences: "2 paths: 1 hop, and 2 hops 1.9 ms later, 17 dB
    /// weaker. Reflection about 290 km up. Doppler spread 0.2 Hz: steady (good for 1200 bps)."
    /// </summary>
    internal static string Describe(ChannelReport r)
    {
        if (!r.Enough || r.Modes.Count == 0)
        {
            return TooLittle;
        }
        var c = CultureInfo.InvariantCulture;
        var modes = r.Modes;
        bool labelled = modes.Any(m => m.Label is not null);
        bool anyE = modes.Any(m => m.Label is { } l && l.EndsWith('E'));
        string Hop(string? label) => label switch
        {
            "1E" => "1 hop off the E layer",
            "1F" => anyE ? "1 hop off the F layer" : "1 hop",
            { Length: 2 } l when l[1] == 'F' => $"{l[0]} hops",
            _ => "another path",
        };
        string Strength(ChannelMode m)
        {
            double d = m.PowerDb - modes[0].PowerDb;
            return d <= -1 ? string.Create(c, $"{-d:0} dB weaker") : d >= 1 ? string.Create(c, $"{d:0} dB stronger") : "about as strong";
        }
        string text;
        if (modes.Count == 1)
        {
            text = labelled ? $"1 path: {Hop(modes[0].Label)}." : "1 path.";
        }
        else if (!labelled)
        {
            var rest = modes.Skip(1).Select(m => string.Create(c, $"{m.DelayMs:0.0} ms after the first, {Strength(m)}")).ToList();
            text = modes.Count == 2
                ? $"2 paths: the second {rest[0]}."
                : $"{modes.Count} paths: {string.Join("; ", rest.SkipLast(1))}; and {rest[^1]}.";
        }
        else
        {
            var rest = modes.Skip(1).Select(m => string.Create(c, $"{Hop(m.Label)} {m.DelayMs:0.0} ms later, {Strength(m)}")).ToList();
            text = modes.Count == 2
                ? $"2 paths: {Hop(modes[0].Label)}, and {rest[0]}."
                : $"{modes.Count} paths: {Hop(modes[0].Label)}; {string.Join("; ", rest.SkipLast(1))}; and {rest[^1]}.";
        }
        if (r.VirtualHeightKm is { } h)
        {
            text += string.Create(c, $" Reflection about {Math.Round(h / 10) * 10:0} km up.");
        }
        if (r.DopplerSpreadHz is { } spread)
        {
            string amount = spread < 0.05 ? "under 0.1 Hz" : string.Create(c, $"{spread:0.0} Hz");
            text += $" Doppler spread {amount}: {Steadiness(spread)}.";
        }
        return text;
    }

    /// <summary>What a Doppler spread means for the broadcast, in words.</summary>
    internal static string Steadiness(double spreadHz) => spreadHz switch
    {
        < 0.5 => "steady (good for 1200 bps)",
        < 1.0 => "a little unsteady (1200 bps should still cope)",
        < 2.0 => "unsteady (600 bps copes better)",
        _ => "fast fading (hard going even at 600 bps)",
    };
}
