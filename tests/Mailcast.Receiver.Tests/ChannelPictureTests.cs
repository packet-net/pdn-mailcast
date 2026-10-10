using System.Text.Json;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The Channel tile's side view (packet-net/pdn-mailcast#50): the curved-earth geometry, the
/// labelling cases from the real CT 150 data in packet-net/pdn-mailcast#60, and the SVG it
/// renders.
/// </summary>
public class ChannelPictureTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static ChannelMode Mode(string? label, double delayMs, double powerDb) => new() { Label = label, DelayMs = delayMs, PowerDb = powerDb };

    private static ChannelReport Report(double? distanceKm, double? virtualHeightKm, string? locator, params ChannelMode[] modes) => new()
    {
        Slot = Noon,
        Measured = Noon,
        Enough = true,
        DistanceKm = distanceKm,
        VirtualHeightKm = virtualHeightKm,
        Locator = locator,
        Modes = modes,
    };

    [Fact]
    public void Point_IsSymmetricAboutTheMidpoint_AndHigherForMoreHeight()
    {
        // The two ends of the ground track are a mirror image of each other either side of the
        // midpoint, and climbing in height moves a point further from the ground in the same
        // direction regardless of where along the track it is.
        var start = ChannelPicture.Point(534, 0, 0);
        var end = ChannelPicture.Point(534, 534, 0);
        Assert.Equal(-start.X, end.X, 6);
        Assert.Equal(start.Y, end.Y, 6);

        var mid = ChannelPicture.Point(534, 267, 0);
        var midHigher = ChannelPicture.Point(534, 267, 300);
        Assert.Equal(mid.X, midHigher.X, 6);
        Assert.True(midHigher.Y < mid.Y, "a point higher up should have a smaller SVG-plane Y (further from the ground, drawn further up)");
    }

    [Fact]
    public void Arc_IsTheSameCurveAHopBouncesOff()
    {
        // The ground arc's midpoint is exactly where a 1-hop ray's single bounce point is, and
        // likewise for a layer's arc: the rays bounce exactly on the curves drawn for them.
        var groundArc = ChannelPicture.Arc(400, 0);
        var groundMid = groundArc[groundArc.Length / 2];
        var hop = ChannelPicture.Hop(400, 1, 0);
        Assert.Equal(groundMid.X, hop[1].X, 6);
        Assert.Equal(groundMid.Y, hop[1].Y, 6);

        var layerArc = ChannelPicture.Arc(400, 300);
        var layerMid = layerArc[layerArc.Length / 2];
        var hop300 = ChannelPicture.Hop(400, 1, 300);
        Assert.Equal(layerMid.X, hop300[1].X, 6);
        Assert.Equal(layerMid.Y, hop300[1].Y, 6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Hop_AlternatesGroundAndLayer_EvenlySpaced(int hops)
    {
        const double groundKm = 534, heightKm = 320;
        var pts = ChannelPicture.Hop(groundKm, hops, heightKm);
        Assert.Equal((2 * hops) + 1, pts.Length);
        // Ground touches (even indices) sit on the ground arc; layer bounces (odd indices) sit on
        // the layer's arc at the same height, evenly spaced along the ground track.
        for (int i = 0; i < pts.Length; i++)
        {
            double atKm = groundKm * i / (2 * hops);
            var expected = ChannelPicture.Point(groundKm, atKm, (i % 2 == 1) ? heightKm : 0);
            Assert.Equal(expected.X, pts[i].X, 6);
            Assert.Equal(expected.Y, pts[i].Y, 6);
        }
        // The first and last bounce are both on the ground, at the two ends of the path.
        Assert.Equal(ChannelPicture.Point(groundKm, 0, 0).Y, pts[0].Y, 6);
        Assert.Equal(ChannelPicture.Point(groundKm, groundKm, 0).Y, pts[^1].Y, 6);
    }

    [Fact]
    public void HeightOf_IsTheELayersFixedHeight_OrTheMeasuredF_OrNominalWhenUnknown()
    {
        Assert.Equal((PathGeometry.EHeightKm, true), ChannelPicture.HeightOf("1E", null));
        Assert.Equal((PathGeometry.EHeightKm, true), ChannelPicture.HeightOf("1E", 288));
        Assert.Equal((288.0, true), ChannelPicture.HeightOf("2F", 288));
        Assert.Equal((ChannelPicture.NominalHeightKm, false), ChannelPicture.HeightOf("1F", null));
    }

    [Fact]
    public void HopsOf_ReadsTheLeadingDigit()
    {
        Assert.Equal(1, ChannelPicture.HopsOf("1F"));
        Assert.Equal(2, ChannelPicture.HopsOf("2F"));
        Assert.Equal(3, ChannelPicture.HopsOf("3F"));
        Assert.Equal(1, ChannelPicture.HopsOf("1E"));
    }

    [Fact]
    public void Words_AreHopCountsAndPlainStrength_NotNumbersForTheReference()
    {
        // The reference (strongest) path just says what it is; a later one adds its delay and
        // strength in plain words (coordinator review, 2026-10-09).
        Assert.Equal("1 hop", ChannelPicture.Words(Mode("1F", 0, 0), isReference: true, anyE: false));
        Assert.Equal("1 hop off the E layer", ChannelPicture.Words(Mode("1E", 0, 0), isReference: true, anyE: true));
        Assert.Equal("2 hops: 1.8 ms later, 14 dB weaker", ChannelPicture.Words(Mode("2F", 1.8, -14), isReference: false, anyE: false));
        Assert.Equal("1 hop off the F layer: 0.5 ms later, 3 dB weaker", ChannelPicture.Words(Mode("1F", 0.53, -2.6), isReference: false, anyE: true));
        Assert.Equal("2 hops: 1.0 ms later, 2 dB stronger", ChannelPicture.Words(Mode("2F", 1.0, 2), isReference: false, anyE: false));
        Assert.Equal("3 hops: 4.0 ms later, about as strong", ChannelPicture.Words(Mode("3F", 4.0, -0.5), isReference: false, anyE: false));
    }

    [Fact]
    public void Svg_RayLabelsDoNotMentionRawNumbers_ForTheReferencePath()
    {
        var report = Report(534, 354, "IO86ha", Mode("1F", 0, 0), Mode(null, 0.89, -9.9), Mode("2F", 2.08, -19.6));
        string svg = ChannelPicture.Svg(report, "IO86ha", 340, withLabels: true)!;
        Assert.Contains(">1 hop<", svg, StringComparison.Ordinal);
        Assert.Contains("2 hops: 2.1 ms later,", svg, StringComparison.Ordinal);
        // Just the tooltip (the <title>, with the exact numbers) mentions 0 ms for the reference
        // path; the visible label text does not.
        Assert.DoesNotContain(">0.00 ms<", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Strength_IsFullAtTheStrongest_AndFloorsOutRatherThanGoingNegative()
    {
        Assert.Equal(1, ChannelPicture.Strength(0));
        Assert.True(ChannelPicture.Strength(-14) is > 0.3 and < 0.6);
        Assert.Equal(0.15, ChannelPicture.Strength(-99));
    }

    [Fact]
    public void Svg_IsNull_WithNoDistance_OrNoLabelledMode()
    {
        Assert.Null(ChannelPicture.Svg(Report(null, 300, "IO86ha", Mode("1F", 0, 0)), "IO86ha", 340, withLabels: true));
        Assert.Null(ChannelPicture.Svg(Report(534, null, "IO86ha", Mode(null, 0, 0)), "IO86ha", 340, withLabels: true));
        Assert.Null(ChannelPicture.Svg(new ChannelReport { Enough = false }, null, 340, withLabels: true));
    }

    [Fact]
    public void Svg_DrawsOneRayPerLabelledMode_SkippingASidelobe()
    {
        // CT 150, 534 km, 2026-10-08 11:00 UTC (packet-net/pdn-mailcast#60): the sidelobe at 0.89
        // ms is left unlabelled by PathGeometry.Label and so gets no ray at all, rather than a
        // ray drawn at an invented height.
        var (labels, height) = PathGeometry.Label(534, [0, 0.89, 2.08]);
        var report = Report(534, height, "IO86ha",
            Mode(labels[0], 0, 0), Mode(labels[1], 0.89, -9.9), Mode(labels[2], 2.08, -19.6));
        Assert.Equal(new string?[] { "1F", null, "2F" }, labels);
        Assert.NotNull(height);

        string svg = ChannelPicture.Svg(report, "IO86ha", 340, withLabels: true)!;
        // One ray each for 1F and 2F (stroke "var(--accent)"), one ground arc and one layer arc
        // (1F and 2F share the same measured height, so just the one layer curve for both):
        // nothing at all for the 0.89 ms sidelobe, which has no label to draw a ray for.
        Assert.Equal(2, Count(svg, "stroke=\"var(--accent)\""));
        Assert.Equal(1, Count(svg, "stroke=\"var(--soft)\""));
        Assert.Equal(1, Count(svg, "stroke=\"#c9a227\""));
        Assert.Contains("GB7RDG IO91lk", svg, StringComparison.Ordinal);
        Assert.Contains("534", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("height not measured", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("stroke-dasharray", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Svg_DrawsTheUnmeasuredHeightDashed_AtTheNominal300Km()
    {
        var report = Report(220, null, "IO80qr", Mode("1F", 0, 0));
        string svg = ChannelPicture.Svg(report, "IO80qr", 340, withLabels: true)!;
        Assert.Contains("stroke-dasharray", svg, StringComparison.Ordinal);
        Assert.Contains("height not measured", svg, StringComparison.Ordinal);
        Assert.Contains("nominal 300 km", svg, StringComparison.Ordinal);

        // The E layer's height is always known, even when the F height (here, there is none) is
        // not: an 1E/1F pair should only dash the F ray.
        var mixed = Report(400, null, "IO80qr", Mode("1E", 0, 0), Mode("1F", 0.6, -3));
        string mixedSvg = ChannelPicture.Svg(mixed, "IO80qr", 340, withLabels: true)!;
        Assert.Equal(1, mixedSvg.Split("stroke-dasharray=\"5 3\"").Length - 1);
    }

    [Fact]
    public void Svg_EncodesTheLocator_RatherThanInjectingMarkup()
    {
        var report = Report(300, 280, "<script>", Mode("1F", 0, 0));
        string svg = ChannelPicture.Svg(report, "<script>", 340, withLabels: true)!;
        Assert.DoesNotContain("<script>", svg, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Svg_EndLabelsGrowOutward_WhenAShortPathLeavesNoRoomBetweenThem()
    {
        // A short path with a tall real layer height: the reflecting layer dominates the
        // picture's width far more than the ground track does, so the two ends land closer
        // together than "GB7RDG IO91lk" and the far end's locator are wide. Both labels must then
        // grow away from each other, not towards each other (where they would collide).
        var report = Report(136, 293, "IO80qr", Mode("1F", 0, 0), Mode("2F", 1.93, -17.2));
        string svg = ChannelPicture.Svg(report, "IO80qr", 340, withLabels: true)!;
        Assert.Contains(">GB7RDG IO91lk</text>", svg, StringComparison.Ordinal);
        Assert.Contains("font-size=\"11\" text-anchor=\"end\">GB7RDG IO91lk</text>", svg, StringComparison.Ordinal);
        Assert.Contains("font-size=\"11\">IO80qr</text>", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("font-size=\"11\" text-anchor=\"end\">IO80qr</text>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Svg_IsWellFormedAndFinite_AtPhoneAndDesktopWidths()
    {
        var report = Report(534, 354, "IO86ha",
            Mode("1F", 0, 0), Mode(null, 0.89, -9.9), Mode("2F", 2.08, -19.6));
        foreach (int width in (int[])[340, 1200])
        {
            string svg = ChannelPicture.Svg(report, "IO86ha", width, withLabels: true)!;
            Assert.StartsWith("<svg viewBox=\"0 0 " + width, svg, StringComparison.Ordinal);
            Assert.EndsWith("</svg>", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("NaN", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("Infinity", svg, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Svg_TheStripVersion_HasNoEndLabelsOrDistanceNote()
    {
        // The day strip's small pictures are not captioned on the SVG itself (the page writes
        // the hour and height as ordinary text beside them): no end markers, ray labels or scale
        // note, just the curves and the rays.
        var report = Report(534, 354, "IO86ha", Mode("1F", 0, 0), Mode("2F", 2.08, -19.6));
        string strip = ChannelPicture.Svg(report, "IO86ha", 140, withLabels: false)!;
        Assert.DoesNotContain("<circle", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("<text", strip, StringComparison.Ordinal);
        Assert.Equal(2, Count(strip, "stroke=\"var(--accent)\""));
    }

    private static int Count(string haystack, string needle) => (haystack.Length - haystack.Replace(needle, "", StringComparison.Ordinal).Length) / needle.Length;

    // A made-up receiver west-south-west of Ireland: far enough out, and in the right direction
    // from GB7RDG, that a 2F path's one ground bounce (the midpoint) falls in the Celtic Sea,
    // inside the Lundy shipping forecast area - the issue's own worked example - and a 3F path's
    // two (a third and two thirds of the way along) fall in the Bristol Channel and the Celtic
    // Sea's Fastnet area respectively (packet-net/pdn-mailcast#89).
    private static readonly GroundPlace FarEnd = new(51.0, -10.5, "far end");

    [Fact]
    public void Svg_NamesGroundBounces_ForA2FPath_WhenTheReceiversPlaceIsKnown()
    {
        double groundKm = PathGeometry.DistanceKm(PathGeometry.Gb7rdg, FarEnd);
        var report = Report(groundKm, 300, "far end", Mode("1F", 0, 0), Mode("2F", 2, -10));
        string svg = ChannelPicture.Svg(report, "far end", 340, withLabels: true, FarEnd)!;
        // Each bounce gets its own line (not joined with commas on one line): SVG text does not
        // wrap, and a sea name with a shipping forecast area in brackets can run long.
        Assert.Contains(">Ground bounces:<", svg, StringComparison.Ordinal);
        Assert.Contains(">Celtic Sea (Lundy)<", svg, StringComparison.Ordinal);
        // One dot per named bounce, over and above the two end-point circles.
        Assert.Equal(3, Count(svg, "<circle"));
    }

    [Fact]
    public void Svg_NamesBothBounces_ForA3FPath()
    {
        double groundKm = PathGeometry.DistanceKm(PathGeometry.Gb7rdg, FarEnd);
        var report = Report(groundKm, 300, "far end", Mode("1F", 0, 0), Mode("3F", 4, -12));
        string svg = ChannelPicture.Svg(report, "far end", 340, withLabels: true, FarEnd)!;
        Assert.Contains(">Ground bounces:<", svg, StringComparison.Ordinal);
        Assert.Contains(">Bristol Channel<", svg, StringComparison.Ordinal);
        Assert.Contains(">Celtic Sea (Fastnet)<", svg, StringComparison.Ordinal);
        Assert.Equal(4, Count(svg, "<circle"));
    }

    [Fact]
    public void Svg_HasNoGroundBounceCaption_WithoutTheReceiversPlace_OrForA1FPath()
    {
        var report1F = Report(534, 354, "IO86ha", Mode("1F", 0, 0));
        string svgNoPlace = ChannelPicture.Svg(report1F, "IO86ha", 340, withLabels: true)!;
        Assert.DoesNotContain("Ground bounces", svgNoPlace, StringComparison.Ordinal);

        var end = GroundPlace.FromLocator("IO86ha")!;
        string svg1FWithPlace = ChannelPicture.Svg(report1F, "IO86ha", 340, withLabels: true, end)!;
        Assert.DoesNotContain("Ground bounces", svg1FWithPlace, StringComparison.Ordinal);

        var report2F = Report(534, 354, "IO86ha", Mode("1F", 0, 0), Mode("2F", 2, -10));
        string strip = ChannelPicture.Svg(report2F, "IO86ha", 140, withLabels: false, end)!;
        Assert.DoesNotContain("Ground bounces", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("<circle", strip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusJson_CarriesThePicture_ForTheSlotAndEachHistoryEntry()
    {
        var place = GroundPlace.FromLocator("IO80qr");
        var time = new FakeTimeProvider(Noon.AddMinutes(5));
        var log = new List<string>();
        await using var watch = new ChannelWatch(time, line => { lock (log) { log.Add(line); } }, null, () => place) { Rest = false };
        watch.Offer(ChannelTests.Captured(11, time.GetUtcNow()), Noon);
        watch.SlotOver();
        await watch.IdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var view = ChannelTile.View(watch, null, time.GetUtcNow());
        var channel = JsonSerializer.SerializeToElement(view, ReceiverConfig.JsonLine);
        Assert.StartsWith("<svg", channel.GetProperty("pathSvg").GetString(), StringComparison.Ordinal);
        var history = channel.GetProperty("history").EnumerateArray().ToList();
        Assert.Single(history);
        Assert.StartsWith("<svg", history[0].GetProperty("pathSvg").GetString(), StringComparison.Ordinal);
    }
}
