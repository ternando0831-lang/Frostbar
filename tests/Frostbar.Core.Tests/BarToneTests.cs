using Frostbar.Core.MenuBar;

namespace Frostbar.Core.Tests;

/// <summary>The menu bar's words by the wallpaper under it (2026-10-19, "C2").</summary>
public class BarToneTests
{
    [Fact]
    public void AFilledPictureIsReadInItsCoveredMiddle()
    {
        // A 4:3 picture on a 16:9 screen: as wide as the screen, its top and bottom cut.
        var b = BarTone.Band(1200, 900, 1920, 1080, 30, PictureFit.Fill)!.Value;
        double scale = 1920.0 / 1200; // 1.6
        Assert.Equal(0, b.X, 6);
        Assert.Equal((900 - 1080 / scale) / 2, b.Y, 6); // the cut half: 112.5
        Assert.Equal(1200, b.W, 6);
        Assert.Equal(30 / scale, b.H, 6);
    }

    [Fact]
    public void AStretchedPictureIsReadAtItsTop()
    {
        var b = BarTone.Band(1000, 500, 2000, 1000, 40, PictureFit.Stretch)!.Value;
        Assert.Equal((0.0, 0.0, 1000.0, 20.0), b);
    }

    [Fact]
    public void AFittedPictureWithRoomAboveLeavesTheBarOnTheColour()
    {
        // A wide picture fitted: bands of the colour above and below it (135 px each), the bar 30 high in the colour.
        Assert.Null(BarTone.Band(1920, 810, 1920, 1080, 30, PictureFit.Fit));
        // A tall one fitted: as high as the screen, the bar over its top.
        var b = BarTone.Band(900, 1200, 1920, 1080, 30, PictureFit.Fit)!.Value;
        Assert.Equal(0, b.Y, 6);
        Assert.Equal(30 * 1200.0 / 1080, b.H, 6);
        Assert.Equal(900, b.W, 6);
    }

    [Fact]
    public void ACentredPictureLargerThanTheScreenIsReadWhereItShows()
    {
        var b = BarTone.Band(2400, 1400, 1920, 1080, 30, PictureFit.Center)!.Value;
        Assert.Equal(240, b.X, 6);  // (2400 − 1920) / 2
        Assert.Equal(160, b.Y, 6);  // (1400 − 1080) / 2
        Assert.Equal(1920, b.W, 6);
        Assert.Equal(30, b.H, 6);
        Assert.Null(BarTone.Band(800, 600, 1920, 1080, 30, PictureFit.Center)); // small, in the middle: the colour under the bar
    }

    [Fact]
    public void TheWordsTurnPastTheMarksAndStayBetween()
    {
        Assert.True(BarTone.DarkWords(0.8, wereDark: false));
        Assert.False(BarTone.DarkWords(0.3, wereDark: true));
        Assert.True(BarTone.DarkWords(0.55, wereDark: true));   // between: as they were
        Assert.False(BarTone.DarkWords(0.55, wereDark: false));
        Assert.Equal(1, BarTone.Luma(255, 255, 255), 9);
        Assert.Equal(0, BarTone.Luma(0, 0, 0), 9);
    }

    [Fact]
    public void NothingToReadGivesNoBand()
    {
        Assert.Null(BarTone.Band(0, 100, 1920, 1080, 30, PictureFit.Fill));
        Assert.Null(BarTone.Band(100, 100, 1920, 1080, 0, PictureFit.Fill));
    }
}
