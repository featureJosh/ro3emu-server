using Ro3Emu.ClientHooks;

namespace Ro3Emu.ClientHooks.Tests;

public class CooldownMirrorTests
{
    [Theory]
    [InlineData("4s", "4s")]
    [InlineData("1.5s", "1.5s")]
    [InlineData("2m", "2m")]
    [InlineData("12", "12s")]            // potions show a bare number
    [InlineData("<color=#fff>7</color>", "7s")]
    [InlineData(" 3 ", "3s")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("获取", "")]              // idle placeholder text is not a time
    public void SecondsNormalisesStockText(string stock, string expected) =>
        Assert.Equal(expected, CooldownMirror.Seconds(stock));

    [Fact]
    public void StaleTextOfAHiddenLabelIsIgnored()
    {
        var r = CooldownMirror.Read(true, 0.5f, textActive: false, "4s");
        Assert.True(r.Sweep);
        Assert.Equal("", r.Seconds);
    }

    [Fact]
    public void MaskOffMeansNoSweepEvenWithFill()
    {
        var r = CooldownMirror.Read(false, 0.8f, true, "3s");
        Assert.False(r.Sweep);
        Assert.Equal("3s", r.Seconds);
        Assert.True(r.Active);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 1f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(0.25f, 0.25f)]
    public void FillIsClamped(float raw, float expected) =>
        Assert.Equal(expected, CooldownMirror.Read(true, raw, false, null).Fill);

    [Fact]
    public void ZeroFillIsNoSweep() => Assert.False(CooldownMirror.Read(true, 0f, false, null).Active);

    [Fact]
    public void ReadyFiresOnceWhenCooldownEnds()
    {
        var e = new CooldownMirror.ReadyEdge();
        Assert.False(e.Update(false));   // never cooled
        Assert.False(e.Update(true));
        Assert.False(e.Update(true));
        Assert.True(e.Update(false));    // ended
        Assert.False(e.Update(false));
    }

    [Fact]
    public void RebindingOrEmptySlotDoesNotFlash()
    {
        var e = new CooldownMirror.ReadyEdge();
        e.Update(true);
        Assert.False(e.Update(false, reset: true));
        Assert.False(e.Update(false));
    }
}
