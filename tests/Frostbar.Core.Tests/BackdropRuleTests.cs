using Frostbar.Core.Settings;

namespace Frostbar.Core.Tests;

public sealed class BackdropRuleTests
{
    [Theory]
    [InlineData("auto", true, false, Backdrop.Blur)]
    [InlineData("auto", true, true, Backdrop.Blur)]
    [InlineData("translucent", true, false, Backdrop.Blur)] // the older name of "auto"
    [InlineData("auto", false, false, Backdrop.Solid)]     // transparency effects off
    [InlineData("solid", true, true, Backdrop.Solid)]
    [InlineData("solid", true, false, Backdrop.Solid)]
    public void Auto_and_solid(string material, bool blurAllowed, bool menuBar, Backdrop want) =>
        Assert.Equal(want, BackdropRule.For(material, blurAllowed, menuBar));

    [Fact]
    public void Transparent_clears_the_menu_bar_only() => Assert.Equal(Backdrop.Clear, BackdropRule.For("transparent", true, menuBar: true));

    [Fact]
    public void Transparent_leaves_menus_panels_and_the_dock_frosted() => Assert.Equal(Backdrop.Blur, BackdropRule.For("transparent", true, menuBar: false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_transparency_effects_off_everything_is_solid(bool menuBar) =>
        Assert.Equal(Backdrop.Solid, BackdropRule.For("transparent", false, menuBar));
}
