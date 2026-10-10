namespace Mailcast.Receiver.Tests;

/// <summary>
/// <see cref="PathGeometry.IntermediatePoint"/>: the point a fraction of the way along the great
/// circle between two places, as used to put a ground bounce (packet-net/pdn-mailcast#89) on the
/// real path rather than just the picture's own abstracted plane.
/// </summary>
public class PathGeometryTests
{
    private static readonly GroundPlace Gb7rdg = GroundPlace.FromLocator("IO91lk")!;

    [Fact]
    public void IntermediatePoint_IsTheStartAt0_AndTheEndAt1()
    {
        var end = GroundPlace.FromLocator("IO51uu")!;
        var start = PathGeometry.IntermediatePoint(Gb7rdg, end, 0);
        var finish = PathGeometry.IntermediatePoint(Gb7rdg, end, 1);
        Assert.Equal(Gb7rdg.Latitude, start.Latitude, 6);
        Assert.Equal(Gb7rdg.Longitude, start.Longitude, 6);
        Assert.Equal(end.Latitude, finish.Latitude, 6);
        Assert.Equal(end.Longitude, finish.Longitude, 6);
    }

    [Fact]
    public void IntermediatePoint_AtOneHalf_IsTheSameDistanceFromEachEnd()
    {
        var end = GroundPlace.FromLocator("IO51uu")!;
        var mid = PathGeometry.IntermediatePoint(Gb7rdg, end, 0.5);
        double toStart = PathGeometry.DistanceKm(Gb7rdg, mid);
        double toEnd = PathGeometry.DistanceKm(mid, end);
        Assert.Equal(toStart, toEnd, 1);
        Assert.Equal(PathGeometry.DistanceKm(Gb7rdg, end), toStart + toEnd, 1);
    }

    [Fact]
    public void IntermediatePoint_AtOneThirdAndTwoThirds_SplitTheDistanceEvenly()
    {
        // The same equal-hops assumption the picture's own ground bounces use for a 3-hop path.
        var end = GroundPlace.FromLocator("IO68")!;
        var a = PathGeometry.IntermediatePoint(Gb7rdg, end, 1.0 / 3);
        var b = PathGeometry.IntermediatePoint(Gb7rdg, end, 2.0 / 3);
        double total = PathGeometry.DistanceKm(Gb7rdg, end);
        Assert.Equal(total / 3, PathGeometry.DistanceKm(Gb7rdg, a), 1);
        Assert.Equal(total / 3, PathGeometry.DistanceKm(a, b), 1);
        Assert.Equal(total / 3, PathGeometry.DistanceKm(b, end), 1);
    }

    [Fact]
    public void IntermediatePoint_AtTheSamePlace_IsThatPlace()
    {
        var here = PathGeometry.IntermediatePoint(Gb7rdg, Gb7rdg, 0.5);
        Assert.Equal(Gb7rdg.Latitude, here.Latitude, 6);
        Assert.Equal(Gb7rdg.Longitude, here.Longitude, 6);
    }
}
