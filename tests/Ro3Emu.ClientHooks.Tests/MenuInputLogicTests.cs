using Ro3Emu.ClientHooks;

public class MenuInputLogicTests
{
    [Fact]
    public void SecondQuickPressOnSameTargetIsDoubleClick()
    {
        var c = new MenuInputLogic.ClickCounter();
        Assert.Equal(1, c.Press(7, 10f, 0.3f));
        Assert.Equal(2, c.Press(7, 10.2f, 0.3f));
        Assert.Equal(1, c.Press(7, 10.4f, 0.3f)); // a third press starts a new pair, as a mouse does
    }

    [Fact]
    public void SlowOrOtherTargetOrDisabledIsSingle()
    {
        var c = new MenuInputLogic.ClickCounter();
        Assert.Equal(1, c.Press(7, 10f, 0.3f));
        Assert.Equal(1, c.Press(7, 10.5f, 0.3f));
        Assert.Equal(1, c.Press(8, 10.6f, 0.3f));
        Assert.Equal(1, c.Press(8, 10.7f, 0f));
        c.Reset();
        Assert.Equal(1, c.Press(8, 10.8f, 0.3f));
    }

    [Theory]
    [InlineData(300, 300, 1920, 1080, 0)]
    [InlineData(768, 432, 1920, 1080, 0)]
    [InlineData(900, 300, 1920, 1080, 1)]
    [InlineData(1000, 900, 1920, 1080, 1)]
    [InlineData(1728, 972, 1920, 1080, 2)]
    [InlineData(1920, 1080, 1920, 1080, 2)]
    [InlineData(1920, 500, 1920, 1080, 1)]
    public void ClassifiesByScreenShare(float w, float h, float sw, float sh, int want) =>
        Assert.Equal(want, (int)MenuInputLogic.Classify(w, h, sw, sh));

    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(1, 2, false)]
    [InlineData(0, 0, false)]
    public void LargeCardsOnlyWhenNothingElseFocusable(int focusable, int large, bool want) =>
        Assert.Equal(want, MenuInputLogic.UseLarge(focusable, large));

    [Fact]
    public void SwipeDeltaHonoursDeadzoneAndSign()
    {
        Assert.Equal(0f, MenuInputLogic.SwipeDelta(0.1f, 0.2f, 600f, 0.016f));
        Assert.True(MenuInputLogic.SwipeDelta(1f, 0.2f, 600f, 0.1f) > 59.9f);
        Assert.True(MenuInputLogic.SwipeDelta(-1f, 0.2f, 600f, 0.1f) < -59.9f);
        Assert.Equal(0f, MenuInputLogic.SwipeDelta(1f, 1f, 600f, 0.1f));
    }
}
