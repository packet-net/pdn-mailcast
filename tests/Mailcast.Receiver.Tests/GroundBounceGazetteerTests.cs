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
        // Deep in the Sahara: outside the gazetteer's region (Britain, Ireland and nearby
        // Europe) entirely, so neither a sea nor a plausible nearest town.
        Assert.Null(GroundBounceGazetteer.Label(23.0, 10.0));
    }
}
