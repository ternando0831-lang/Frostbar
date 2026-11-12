using Frostbar.Core.AppGrid;
using Frostbar.Core.Dock;
using Frostbar.Core.Settings;
using Xunit;

namespace Frostbar.Core.Tests;

public class DockFrameTests
{
    // A window 600 long along the row and 120 deep, on each edge.
    static readonly DockFrame Bottom = new(DockEdge.Bottom, 600, 120);
    static readonly DockFrame Left = new(DockEdge.Left, 120, 600);
    static readonly DockFrame Right = new(DockEdge.Right, 120, 600);

    [Fact]
    public void TheRowRunsAlongTheEdge()
    {
        Assert.Equal((600, 120), (Bottom.Length, Bottom.Depth));
        Assert.Equal((600, 120), (Left.Length, Left.Depth));
        Assert.Equal((600, 120), (Right.Length, Right.Depth));
        Assert.False(Bottom.Vertical);
        Assert.True(Left.Vertical && Right.Vertical);
    }

    [Fact]
    public void InwardIsAwayFromTheScreensEdge()
    {
        Assert.Equal((100.0, 110.0), Bottom.Point(100, 10)); // 10 up from the bottom
        Assert.Equal((10.0, 100.0), Left.Point(100, 10));    // 10 right of the left edge
        Assert.Equal((110.0, 100.0), Right.Point(100, 10));  // 10 left of the right edge
    }

    [Fact]
    public void AnIconsBoxSitsOnTheEdgeItsDepthInward()
    {
        // An icon 40 long, 14 in from the edge.
        Assert.Equal(new Box(100, 66, 40, 40), Bottom.Box(100, 14, 40, 40));
        Assert.Equal(new Box(14, 100, 40, 40), Left.Box(100, 14, 40, 40));
        Assert.Equal(new Box(66, 100, 40, 40), Right.Box(100, 14, 40, 40));
    }

    [Theory]
    [InlineData(DockEdge.Bottom)]
    [InlineData(DockEdge.Left)]
    [InlineData(DockEdge.Right)]
    public void APointGoesBackToWhereItWasOnTheRow(DockEdge edge)
    {
        var f = edge == DockEdge.Bottom ? Bottom : edge == DockEdge.Left ? Left : Right;
        var (x, y) = f.Point(123, 17);
        Assert.Equal((123.0, 17.0), f.Of(x, y));
    }

    // A screen of 1536 × 864 DIP; an icon of 40 at the bottom, on the left and on the right.
    static readonly Box Screen = new(0, 0, 1536, 864);

    [Fact]
    public void AtTheBottomAMenuOpensAboveItsItemCentred()
    {
        var (left, top) = DockFrame.Beside(DockEdge.Bottom, new Box(700, 790, 40, 40), 220, 300, Screen);
        Assert.Equal((610.0, 480.0), (left, top)); // 720 − 110; 790 − 10 − 300
    }

    [Fact]
    public void OnASideAMenuOpensBesideItsItemCentred()
    {
        var item = new Box(14, 400, 40, 40);
        Assert.Equal((64.0, 270.0), DockFrame.Beside(DockEdge.Left, item, 220, 300, Screen));      // 14 + 40 + 10; 420 − 150
        var right = new Box(1482, 400, 40, 40);
        Assert.Equal((1252.0, 270.0), DockFrame.Beside(DockEdge.Right, right, 220, 300, Screen));  // 1482 − 10 − 220
    }

    [Fact]
    public void AMenuStaysOnTheScreen()
    {
        // Near the top of a side: pushed down to 4 DIP from the screen's top; near a bottom corner: in from the side.
        Assert.Equal(4.0, DockFrame.Beside(DockEdge.Left, new Box(14, 30, 40, 40), 220, 300, Screen).Top);
        Assert.Equal(560.0, DockFrame.Beside(DockEdge.Right, new Box(1482, 800, 40, 40), 220, 300, Screen).Top); // 864 − 300 − 4
        Assert.Equal(1312.0, DockFrame.Beside(DockEdge.Bottom, new Box(1490, 790, 40, 40), 220, 300, Screen).Left); // 1536 − 220 − 4
    }

    [Theory]
    [InlineData("left", DockEdge.Left)]
    [InlineData("right", DockEdge.Right)]
    [InlineData("bottom", DockEdge.Bottom)]
    [InlineData(null, DockEdge.Bottom)]
    public void ThePositionSettingNamesTheEdge(string? position, DockEdge edge)
    {
        Assert.Equal(edge, DockFrame.Parse(position));
        if (position != null) Assert.Equal(position, DockFrame.Name(edge));
    }

    [Theory]
    [InlineData("left", "left")]
    [InlineData("top", "bottom")]
    [InlineData(null, "bottom")]
    public void AnUnknownPositionIsTheBottom(string? position, string kept)
    {
        var s = new AppSettings();
        s.Dock.Position = position!;
        SettingsValidator.Normalize(s);
        Assert.Equal(kept, s.Dock.Position);
    }
}
