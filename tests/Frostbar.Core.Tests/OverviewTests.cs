using Frostbar.Core.HotCorners;
using Frostbar.Core.Overview;
using Xunit;

namespace Frostbar.Core.Tests;

public class OverviewLayoutTests
{
    static readonly Rect2 Area = new(0, 30, 1920, 900);

    static void AssertSound(IReadOnlyList<(double Width, double Height)> windows, IReadOnlyList<Rect2> thumbs, double gap = 32)
    {
        Assert.Equal(windows.Count, thumbs.Count);
        for (int i = 0; i < thumbs.Count; i++)
        {
            var t = thumbs[i];
            Assert.True(t.X >= Area.X - 0.001 && t.Y >= Area.Y - 0.001 && t.Right <= Area.Right + 0.001 && t.Bottom <= Area.Bottom + 0.001, $"{i} outside: {t}");
            Assert.True(t.Width > 0 && t.Height > 0, $"{i} empty");
            Assert.True(t.Width <= windows[i].Width + 0.001 && t.Height <= windows[i].Height + 0.001, $"{i} larger than the window");
            Assert.Equal(windows[i].Width / windows[i].Height, t.Width / t.Height, 3);
            for (int j = i + 1; j < thumbs.Count; j++)
            {
                var u = thumbs[j];
                bool apart = t.Right + gap - 0.001 <= u.X || u.Right + gap - 0.001 <= t.X || t.Bottom + gap - 0.001 <= u.Y || u.Bottom + gap - 0.001 <= t.Y;
                Assert.True(apart, $"{i} and {j} overlap or touch: {t} {u}");
            }
        }
    }

    [Fact]
    public void NothingToShow() => Assert.Empty(OverviewLayout.Arrange([], Area));

    [Fact]
    public void ASmallWindowAloneKeepsItsSizeInTheMiddle()
    {
        var w = new[] { (640.0, 420.0) };
        var t = OverviewLayout.Arrange(w, Area);
        AssertSound(w, t);
        Assert.Equal(640, t[0].Width, 3);
        Assert.Equal(Area.X + (Area.Width - 640) / 2, t[0].X, 3);
        Assert.Equal(Area.Y + (Area.Height - 420) / 2, t[0].Y, 3);
    }

    [Fact]
    public void TenWindowsFitWithoutOverlapping()
    {
        var w = Enumerable.Range(0, 10).Select(i => (600.0 + i * 40, 400.0 + (i % 3) * 60)).ToArray();
        AssertSound(w, OverviewLayout.Arrange(w, Area));
    }

    [Fact]
    public void AMaximizedWindowDoesNotShrinkTheOthersToNothing()
    {
        var w = new[] { (1920.0, 1000.0), (640.0, 420.0), (640.0, 420.0), (800.0, 600.0) };
        var t = OverviewLayout.Arrange(w, Area);
        AssertSound(w, t);
        Assert.True(t[1].Width > 300, $"the small ones stay readable: {t[1].Width}");
        Assert.True(t[0].Width * t[0].Height <= 2.5 * t[1].Width * t[1].Height, "the big one no longer dwarfs them");
    }

    [Fact]
    public void FiftyWindowsStillFit()
    {
        var w = Enumerable.Range(0, 50).Select(i => (500.0 + (i % 7) * 90, 350.0 + (i % 5) * 70)).ToArray();
        AssertSound(w, OverviewLayout.Arrange(w, Area, gap: 16), gap: 16);
    }

    [Fact]
    public void RowsKeepTheOrder()
    {
        var w = Enumerable.Range(0, 6).Select(_ => (800.0, 500.0)).ToArray();
        var t = OverviewLayout.Arrange(w, Area);
        for (int i = 1; i < t.Count; i++)
            Assert.True(t[i].Y > t[i - 1].Y + 1 || Math.Abs(t[i].Y - t[i - 1].Y) < 1 && t[i].X > t[i - 1].X, $"{i - 1} then {i}");
    }

    [Fact]
    public void ArrowsMoveToTheNeighbourThatWay()
    {
        var t = new[] { new Rect2(0, 0, 100, 100), new Rect2(200, 0, 100, 100), new Rect2(0, 200, 100, 100), new Rect2(200, 200, 100, 100) };
        Assert.Equal(1, OverviewLayout.Move(t, 0, 1, 0));
        Assert.Equal(2, OverviewLayout.Move(t, 0, 0, 1));
        Assert.Equal(0, OverviewLayout.Move(t, 0, -1, 0)); // nothing to the left
        Assert.Equal(3, OverviewLayout.Move(t, 2, 1, 0));
        Assert.Equal(0, OverviewLayout.Move(t, -1, 1, 0)); // nothing chosen: the first
        Assert.Equal(3, OverviewLayout.HitTest(t, 250, 250));
        Assert.Equal(-1, OverviewLayout.HitTest(t, 150, 150));
    }

    [Fact]
    public void WindowsAreLaidOutRoughlyWhereTheyAre()
    {
        // Given front to back: a window on the right, one on the left, one low on the left, one high on the right.
        var w = new[] { new Rect2(1000, 100, 600, 400), new Rect2(0, 120, 600, 400), new Rect2(50, 600, 500, 300), new Rect2(1100, 0, 300, 200) };
        Assert.Equal(new[] { 1, 3, 0, 2 }, OverviewLayout.Order(w, band: 360)); // the top band by centre x (300, 1250, 1300), then the low one
        // Same place: the front one first.
        var same = new[] { new Rect2(100, 100, 800, 600), new Rect2(100, 100, 800, 600) };
        Assert.Equal(new[] { 0, 1 }, OverviewLayout.Order(same, band: 360));
    }

    /// <summary>The close button of the window pointed at: at its thumbnail's top right corner (the user's choice B, 2026-10-16).</summary>
    [Fact]
    public void The_close_button_sits_in_the_top_right_corner()
    {
        // 25 px across, 15 px in from the corner (20 and 12 DIP at 125%).
        var b = OverviewLayout.CloseButton(new Rect2(100, 200, 400, 300), 25, 15);
        Assert.Equal(new Rect2(472.5, 202.5, 25, 25), b);
        Assert.True(b.Right <= 500 && b.Y >= 200); // within the thumbnail
    }

    [Fact]
    public void The_close_button_stays_within_a_small_thumbnail()
    {
        var thumb = new Rect2(100, 200, 20, 16); // narrower and shorter than the button and its inset
        var b = OverviewLayout.CloseButton(thumb, 25, 15);
        Assert.Equal(100, b.X);   // its left edge on the thumbnail's
        Assert.Equal(200, b.Y);   // its top edge on the thumbnail's
    }
}

public class HotCornerTrackerTests
{
    static readonly ScreenEdges S = new(0, 0, 1920, 1080);
    static bool All(Corner _) => true;

    static Corner Feed(HotCornerTracker t, int x, int y, long ms, bool button = false, bool modifier = true, bool full = false) =>
        t.Update(x, y, S, ms, button, modifier, full, All);

    [Fact]
    public void FiresAfterTheDwellInTheCorner()
    {
        var t = new HotCornerTracker(250);
        Assert.Equal(Corner.None, Feed(t, 1919, 1079, 1000));
        Assert.Equal(Corner.None, Feed(t, 1919, 1079, 1200));
        Assert.Equal(Corner.BottomRight, Feed(t, 1919, 1079, 1250));
    }

    [Fact]
    public void LeavingTooSoonStartsAgain()
    {
        var t = new HotCornerTracker(250);
        Feed(t, 0, 1079, 1000);
        Feed(t, 300, 600, 1100);
        Assert.Equal(Corner.None, Feed(t, 0, 1079, 1200));
        Assert.Equal(Corner.None, Feed(t, 0, 1079, 1400));
        Assert.Equal(Corner.BottomLeft, Feed(t, 0, 1079, 1450));
    }

    [Fact]
    public void NeverWithAButtonDownNorInFullScreenNorWithoutTheModifier()
    {
        foreach (var (button, modifier, full) in new[] { (true, true, false), (false, true, true), (false, false, false) })
        {
            var t = new HotCornerTracker(250);
            for (long ms = 1000; ms < 3000; ms += 20)
                Assert.Equal(Corner.None, Feed(t, 1919, 1079, ms, button, modifier, full));
        }
    }

    [Fact]
    public void RestingInTheCornerFiresOnceUntilThePointerHasGoneAway()
    {
        var t = new HotCornerTracker(250);
        Feed(t, 1919, 1079, 1000);
        Assert.Equal(Corner.BottomRight, Feed(t, 1919, 1079, 1250));
        for (long ms = 1270; ms < 3000; ms += 20) Assert.Equal(Corner.None, Feed(t, 1919, 1079, ms));
        Feed(t, 1909, 1069, 3000); // 10 px away: not far enough
        Feed(t, 1919, 1079, 3020);
        Assert.Equal(Corner.None, Feed(t, 1919, 1079, 3400));
        Feed(t, 1890, 1050, 3500); // 29 px away: again
        Feed(t, 1919, 1079, 3520);
        Assert.Equal(Corner.BottomRight, Feed(t, 1919, 1079, 3770));
    }

    [Fact]
    public void TheDwellCountsFromWhenThePointerGotThere()
    {
        // Looks 100 ms apart: the pointer got to the corner at 1010, the first look that sees it is at 1100.
        var t = new HotCornerTracker(250);
        Assert.Equal(Corner.None, t.Update(500, 500, S, 1000, false, true, false, All));
        Assert.Equal(Corner.None, t.Update(0, 0, S, 1100, false, true, false, All, enteredMs: 1010));
        Assert.Equal(Corner.None, t.Update(0, 0, S, 1250, false, true, false, All, enteredMs: 1010));
        Assert.Equal(Corner.TopLeft, t.Update(0, 0, S, 1260, false, true, false, All, enteredMs: 1010));
    }

    [Fact]
    public void ButNotFromBeforeThePointerWasSeenElsewhere()
    {
        // An old input time (a key pressed long ago; the pointer was seen away from the corner at 1000).
        var t = new HotCornerTracker(250);
        t.Update(500, 500, S, 1000, false, true, false, All);
        Assert.Equal(Corner.None, t.Update(0, 0, S, 1100, false, true, false, All, enteredMs: 400));
        Assert.Equal(Corner.None, t.Update(0, 0, S, 1240, false, true, false, All, enteredMs: 400));
        Assert.Equal(Corner.TopLeft, t.Update(0, 0, S, 1250, false, true, false, All, enteredMs: 400));
    }

    [Fact]
    public void LooksSkippedFarAwayStillBoundTheArrival()
    {
        // The watcher skips the full look far from the corners and only says it looked (Away).
        var t = new HotCornerTracker(250);
        t.Update(500, 500, S, 1000, false, true, false, All);
        Assert.True(t.Idle);
        t.Away(1050);
        Assert.Equal(Corner.None, t.Update(0, 0, S, 1100, false, true, false, All, enteredMs: 400));
        Assert.Equal(Corner.None, t.Update(0, 0, S, 1290, false, true, false, All, enteredMs: 400));
        Assert.Equal(Corner.TopLeft, t.Update(0, 0, S, 1300, false, true, false, All, enteredMs: 400));
        Assert.False(t.Idle); // waiting to be re-armed: the watcher keeps looking fully
    }

    [Fact]
    public void CornersWithNothingToDoNeverFire()
    {
        var t = new HotCornerTracker(250);
        for (long ms = 1000; ms < 2000; ms += 20) Assert.Equal(Corner.None, t.Update(0, 0, S, ms, false, true, false, c => c != Corner.TopLeft));
    }

    [Theory]
    [InlineData(0, 0, Corner.TopLeft)]
    [InlineData(1, 1, Corner.TopLeft)]
    [InlineData(2, 0, Corner.None)]
    [InlineData(1919, 0, Corner.TopRight)]
    [InlineData(0, 1079, Corner.BottomLeft)]
    [InlineData(1918, 1078, Corner.BottomRight)]
    [InlineData(960, 1079, Corner.None)]
    public void Corners(int x, int y, Corner want) => Assert.Equal(want, HotCornerTracker.At(x, y, S));
}
