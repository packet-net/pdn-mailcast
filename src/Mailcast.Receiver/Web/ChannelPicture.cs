using System.Globalization;
using System.Net;
using System.Text;

namespace Mailcast.Receiver.Web;

/// <summary>A point in one picture's own plane: km across its centre line, km up its curve (not plain real-world height).</summary>
internal readonly record struct PicturePoint(double X, double Y);

/// <summary>
/// The Channel tile's side view (packet-net/pdn-mailcast#50): GB7RDG to the receiver over a
/// deliberately curved earth, with the reflecting layer as a concentric curved band and one ray
/// per path found, bouncing off both. Built here, as inline SVG markup, rather than in the
/// page's own script, so its geometry and labelling can be unit tested like the rest of the hop
/// maths, and the page just drops the markup in.
/// </summary>
internal static class ChannelPicture
{
    /// <summary>
    /// The ground arc's total angular span, radians: the same for every picture regardless of
    /// distance, so the curvature always reads as a clearly visible bulge rather than fading out
    /// on a short path or wrapping round on a long one. The layer arcs share it, concentric with
    /// the ground arc, so a ray's bounce points fall exactly on the curves it bounces off.
    /// </summary>
    public const double ArcSpanRadians = 0.8;

    /// <summary>The height a path is drawn at, dashed, when it could not be measured.</summary>
    public const double NominalHeightKm = 300;

    private const int ArcSamples = 24;

    /// <summary>
    /// A point <paramref name="atKm"/> along the ground track (0 at GB7RDG, <paramref name="groundKm"/>
    /// at the receiver), <paramref name="heightKm"/> above it, in the picture's own exaggerated,
    /// but internally consistent, curved plane.
    /// </summary>
    public static PicturePoint Point(double groundKm, double atKm, double heightKm)
    {
        double r = (groundKm / ArcSpanRadians) + heightKm;
        double theta = ((atKm / groundKm) - 0.5) * ArcSpanRadians;
        return new PicturePoint(r * Math.Sin(theta), -r * Math.Cos(theta));
    }

    /// <summary>The curved ground (<paramref name="heightKm"/> 0) or a layer at <paramref name="heightKm"/>, as a smooth curve of points.</summary>
    public static PicturePoint[] Arc(double groundKm, double heightKm) =>
        [.. Enumerable.Range(0, ArcSamples + 1).Select(i => Point(groundKm, groundKm * i / ArcSamples, heightKm))];

    /// <summary>
    /// The bounce points of <paramref name="hops"/> hops off a layer at <paramref name="heightKm"/>
    /// over <paramref name="groundKm"/>: ground, layer, ground, layer, ..., ground, one more
    /// ground touch than there are layer bounces, evenly spaced along the ground track.
    /// </summary>
    public static PicturePoint[] Hop(double groundKm, int hops, double heightKm) =>
        [.. Enumerable.Range(0, (2 * hops) + 1)
            .Select(i => Point(groundKm, groundKm * i / (2 * hops), (i % 2 == 1) ? heightKm : 0))];

    /// <summary>A mode's height for drawing: the E layer's fixed height, the slot's measured F height, or <see cref="NominalHeightKm"/> when that is not known.</summary>
    internal static (double Km, bool Known) HeightOf(string label, double? virtualHeightKm) =>
        label.EndsWith('E') ? (PathGeometry.EHeightKm, true)
        : virtualHeightKm is { } h ? (h, true)
        : (NominalHeightKm, false);

    /// <summary>How far along a mode's hop count is read off its label, "1F", "2E", and so on.</summary>
    internal static int HopsOf(string label) => label[0] - '0';

    /// <summary>Stroke width and opacity scale with a path's power the same way: full at the strongest, fading out by about -25 dB, never past a small floor.</summary>
    internal static double Strength(double powerDb) => Math.Max(0.15, 1 + (powerDb / 25));

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Points(IEnumerable<PicturePoint> pts, Func<PicturePoint, double> sx, Func<PicturePoint, double> sy) =>
        string.Join(' ', pts.Select(p => $"{F(sx(p))},{F(sy(p))}"));

    /// <summary>One ray: the mode it is, its height, and its bounce points.</summary>
    private sealed record Ray(ChannelMode Mode, double HeightKm, bool HeightKnown, PicturePoint[] Points);

    private static List<Ray> Rays(IEnumerable<ChannelMode> modes, double groundKm, double? virtualHeightKm) =>
        [.. modes.Where(m => m.Label is not null).Select(m =>
        {
            var (height, known) = HeightOf(m.Label!, virtualHeightKm);
            return new Ray(m, height, known, Hop(groundKm, HopsOf(m.Label!), height));
        })];

    /// <summary>
    /// The side view's SVG for one slot: null when there is nothing plausible to draw (no
    /// distance known, or no labelled mode at all). <paramref name="endLocator"/> and the
    /// distance are only captioned when <paramref name="withLabels"/> is set (the main picture,
    /// not the day strip's small ones).
    /// </summary>
    public static string? Svg(ChannelReport report, string? endLocator, int widthPx, bool withLabels)
    {
        if (report is not { Enough: true, DistanceKm: { } groundKm } || report.Modes.Count == 0)
        {
            return null;
        }
        var rays = Rays(report.Modes, groundKm, report.VirtualHeightKm);
        if (rays.Count == 0)
        {
            return null;
        }
        var ground = Arc(groundKm, 0);
        var layers = rays.Select(r => (r.HeightKm, r.HeightKnown)).Distinct()
            .Select(h => (h.HeightKm, h.HeightKnown, Points: Arc(groundKm, h.HeightKm))).ToList();

        var all = ground.Concat(layers.SelectMany(l => l.Points)).Concat(rays.SelectMany(r => r.Points)).ToList();
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X);
        double minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        double padTop = withLabels ? 22 : 4, padSide = 8, padBottom = withLabels ? 50 : 14;
        double scale = (widthPx - (2 * padSide)) / (maxX - minX);
        double height = ((maxY - minY) * scale) + padTop + padBottom;
        double Sx(PicturePoint p) => ((p.X - minX) * scale) + padSide;
        double Sy(PicturePoint p) => ((p.Y - minY) * scale) + padTop;

        var aria = $"Side view of the path from GB7RDG to {WebUtility.HtmlEncode(endLocator ?? "the receiver")}, {Math.Round(groundKm)} km, over a deliberately curved earth";
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg viewBox=\"0 0 {widthPx} {F(height)}\" role=\"img\" aria-label=\"{aria}\">");
        sb.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{Points(ground, Sx, Sy)}\" fill=\"none\" stroke=\"var(--soft)\" stroke-width=\"{(withLabels ? 2 : 1.5)}\"/>");
        foreach (var l in layers)
        {
            string dash = l.HeightKnown ? "" : " stroke-dasharray=\"4 3\"";
            sb.Append(CultureInfo.InvariantCulture,
                $"<polyline points=\"{Points(l.Points, Sx, Sy)}\" fill=\"none\" stroke=\"#c9a227\" stroke-width=\"{(withLabels ? 3 : 2)}\" stroke-opacity=\"0.35\"{dash}/>");
        }
        foreach (var r in rays)
        {
            double strength = Strength(r.Mode.PowerDb);
            double width = Math.Max(1, (withLabels ? 6 : 3) * strength);
            double opacity = Math.Min(1, strength);
            string dash = r.HeightKnown ? "" : " stroke-dasharray=\"5 3\"";
            string title = string.Create(CultureInfo.InvariantCulture,
                $"{r.Mode.Label}: {r.Mode.DelayMs:+0.00;-0.00;0.00} ms, {r.Mode.PowerDb:0.0} dB{(r.HeightKnown ? "" : " (height not measured: drawn at a nominal 300 km)")}");
            sb.Append(CultureInfo.InvariantCulture,
                $"<polyline points=\"{Points(r.Points, Sx, Sy)}\" fill=\"none\" stroke=\"var(--accent)\" stroke-linejoin=\"round\" "
                + $"stroke-width=\"{F(width)}\" stroke-opacity=\"{F(opacity)}\"{dash}><title>{WebUtility.HtmlEncode(title)}</title></polyline>");
            if (withLabels)
            {
                var apex = r.Points[1];
                string label = string.Create(CultureInfo.InvariantCulture, $"{r.Mode.Label}: {r.Mode.DelayMs:+0.00;-0.00;0.00} ms, {r.Mode.PowerDb:0} dB");
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text x=\"{F(Sx(apex))}\" y=\"{F(Sy(apex) - 8)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"var(--accent)\">{WebUtility.HtmlEncode(label)}</text>");
            }
        }
        if (withLabels)
        {
            var start = ground[0];
            var end = ground[^1];
            sb.Append(CultureInfo.InvariantCulture,
                $"<circle cx=\"{F(Sx(start))}\" cy=\"{F(Sy(start))}\" r=\"4\" fill=\"var(--bad)\"/>"
                + $"<text x=\"{F(Sx(start))}\" y=\"{F(Sy(start) + 16)}\" font-size=\"11\">GB7RDG IO91lk</text>"
                + $"<circle cx=\"{F(Sx(end))}\" cy=\"{F(Sy(end))}\" r=\"4\" fill=\"var(--ink)\"/>"
                + $"<text x=\"{F(Sx(end))}\" y=\"{F(Sy(end) + 16)}\" text-anchor=\"end\" font-size=\"11\">{WebUtility.HtmlEncode(endLocator ?? "receiver")}</text>");
            double midX = (Sx(start) + Sx(end)) / 2;
            bool anyNominal = rays.Any(r => !r.HeightKnown);
            string scaleNote = anyNominal
                ? "The earth's curve and the layer's height are both drawn several times too deep so the bend is easy to see; the ground distance is to scale. The dashed path's height could not be measured, so it is drawn at a nominal 300 km."
                : "The earth's curve and the layer's height are both drawn several times too deep so the bend is easy to see; the ground distance is to scale.";
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{F(midX)}\" y=\"{F(Sy(start) + 30)}\" text-anchor=\"middle\" font-size=\"10\" fill=\"var(--soft)\">{Math.Round(groundKm)} km</text>");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{F(midX)}\" y=\"{F(Sy(start) + 42)}\" text-anchor=\"middle\" font-size=\"9\" fill=\"var(--soft)\">{WebUtility.HtmlEncode(scaleNote)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
