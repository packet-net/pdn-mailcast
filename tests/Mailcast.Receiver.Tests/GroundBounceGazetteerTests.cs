namespace Mailcast.Receiver.Tests;

/// <summary>
/// <see cref="GroundBounceGazetteer"/>: naming where a ground bounce falls (packet-net/pdn-mailcast#89)
/// from the embedded towns, sea areas and shipping forecast areas.
/// </summary>
public class GroundBounceGazetteerTests
{
    [Fact]
    public void Label_OnLand_IsNearestTown()
    {
        // Aberystwyth's own GeoNames coordinates: the nearest town to itself is itself.
        Assert.Equal("near Aberystwyth", GroundBounceGazetteer.Label(52.41548, -4.08292));
    }

    [Fact]
    public void Label_AtGb7rdgsOwnPlace_IsNearestTown_NotASea()
    {
        var gb7rdg = GroundPlace.FromLocator("IO91lk")!;
        string? label = GroundBounceGazetteer.Label(gb7rdg.Latitude, gb7rdg.Longitude);
        Assert.NotNull(label);
        Assert.StartsWith("near ", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Label_AtSea_IsTheSeaAndTheShippingForecastArea()
    {
        // A point inside both the Celtic Sea (IHO) and the Lundy shipping forecast area: the
        // issue's own worked example (packet-net/pdn-mailcast#89).
        Assert.Equal("Celtic Sea (Lundy)", GroundBounceGazetteer.Label(51.1566, -5.8352));
    }

    [Fact]
    public void Label_AtSea_SaysTheNameOnce_WhenTheSeaAndTheForecastAreaAgree()
    {
        // A point inside both the Irish Sea (IHO, "Irish Sea and St. George's Channel" trimmed
        // for display) and the Irish Sea shipping forecast area: said once, not "Irish Sea
        // (Irish Sea)".
        string? label = GroundBounceGazetteer.Label(53.2366, -5.5361);
        Assert.Equal("Irish Sea", label);
    }

    [Fact]
    public void Label_FarFromAnyTownOrSeaInTheGazetteer_IsNull()
    {
        // Deep in the Sahara: outside the gazetteer's region (GB7RDG out to 2000 km) entirely, so
        // neither a sea nor a plausible nearest town.
        Assert.Null(GroundBounceGazetteer.Label(23.0, 10.0));
    }

    [Theory]
    [InlineData(44.0, -2.0)]
    [InlineData(44.9, -1.5)]
    public void Label_InTheSouthernBayOfBiscay_IsTheSea(double latitude, double longitude)
    {
        // Review of issue #89 found the first, narrower gazetteer region (45-62 N) cut off the
        // southern Bay of Biscay: these two points, well inside it, got no sea label at all, and
        // the second (an offshore point) fell back to "near Lesparre-Medoc", a real town about
        // 25 km inland - wrong both ways. The region was widened (GB7RDG out to 2000 km) to cover
        // this properly.
        Assert.Equal("Bay of Biscay", GroundBounceGazetteer.Label(latitude, longitude));
    }

    [Fact]
    public void Label_OnLand_FarFromEveryTown_IsNull_RatherThanAMisleadinglyDistantOne()
    {
        // The Cairngorms: on land, outside every sea polygon, but the nearest town (Westhill,
        // near Aberdeen) is around 53 km away - well past MaxTownKm, so this is left unlabelled
        // rather than called "near" a town the point is not actually close to. Falling outside
        // every sea polygon is not the same as being confidently on land near somewhere named:
        // the simplified sea polygons have real gaps (packet-net/pdn-mailcast#89 review).
        Assert.Null(GroundBounceGazetteer.Label(57.05, -3.75));
    }
}
