namespace Ro3Emu.ClientHooks.Tests;

public class TargetMathTests
{
    private static TargetPoint P(long id, float x, float z) => new(id, x, z);

    [Fact]
    public void AngleIsZeroAheadAndHalfTurnBehind()
    {
        Assert.Equal(0f, TargetMath.AngleTo(0, 1, 0, 5), 3);
        Assert.Equal(90f, TargetMath.AngleTo(0, 1, 3, 0), 3);
        Assert.Equal(180f, TargetMath.AngleTo(0, 1, 0, -2), 3);
        Assert.Equal(0f, TargetMath.AngleTo(0, 0, 1, 1)); // no facing: every direction passes
    }

    [Fact]
    public void ConeKeepsOnlyTheWedgeAndOrdersNearestFirst()
    {
        var pts = new[] { P(1, 0, 10), P(2, 1, 3), P(3, 8, 0), P(4, 0, -4), P(5, 0, 40) };
        var hits = TargetMath.Cone(0, 0, 0, 1, pts, 45f, 20f);
        Assert.Equal(new long[] { 2, 1 }, hits); // 3 is 90 degrees off, 4 behind, 5 out of range
    }

    [Fact]
    public void ConeTiesPreferTheSmallerAngle()
    {
        var pts = new[] { P(1, 3, 4), P(2, 0, 5) };
        Assert.Equal(new long[] { 2, 1 }, TargetMath.Cone(0, 0, 0, 1, pts, 90f, 10f));
    }

    [Fact]
    public void ConeWithNoFacingIsAPlainRangeSortAndHonoursOffsets()
    {
        var pts = new[] { P(1, 12, 10), P(2, 12, 13), P(3, 12, -30) };
        Assert.Equal(new long[] { 1, 2 }, TargetMath.Cone(10, 10, 0, 0, pts, 10f, 5f));
    }

    [Fact]
    public void CycleOrdersByDistancePlusAnglePenalty()
    {
        // 1: 6 m dead ahead -> 6; 2: 4 m but behind -> 4 + 10 = 14; 3: 8 m, 90 degrees -> 13.
        var pts = new[] { P(2, 0, -4), P(3, 8, 0), P(1, 0, 6), P(4, 0, 99) };
        Assert.Equal(new long[] { 1, 3, 2 }, TargetMath.Cycle(0, 0, 0, 1, pts, 30f));
    }

    [Fact]
    public void NextWrapsAndStartsAtTheFirst()
    {
        var order = new List<long> { 7, 8, 9 };
        Assert.Equal(7L, TargetMath.Next(order, null));
        Assert.Equal(7L, TargetMath.Next(order, 42L)); // current gone from the list
        Assert.Equal(9L, TargetMath.Next(order, 8L));
        Assert.Equal(7L, TargetMath.Next(order, 9L));
        Assert.Null(TargetMath.Next(new List<long>(), 1L));
    }
}
