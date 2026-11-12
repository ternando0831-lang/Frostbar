using Frostbar.Core.Devices;

namespace Frostbar.Core.Tests;

public class DisplayChoicesTests
{
    static ScreenMode M(int w, int h, int hz = 60) => new(w, h, hz);

    [Fact]
    public void ResolutionsLargestFirstEachOnce()
    {
        ScreenMode[] modes = [M(1600, 900), M(1920, 1080), M(1366, 768), M(1680, 1050), M(1920, 1080, 48), M(1360, 768), M(1280, 1024)];
        Assert.Equal([(1920, 1080), (1680, 1050), (1600, 900), (1280, 1024), (1366, 768), (1360, 768)], DisplayChoices.Resolutions(modes));
    }

    [Fact]
    public void OfTwoTheSameSizeTheWiderFirst()
    {
        Assert.Equal([(1200, 800), (1000, 960)], DisplayChoices.Resolutions([M(1000, 960), M(1200, 800)]));
    }

    [Fact]
    public void RatesFastestFirst()
    {
        ScreenMode[] modes = [M(2560, 1440, 60), M(2560, 1440, 144), M(2560, 1440, 120), M(1920, 1080, 60), M(2560, 1440, 60)];
        Assert.Equal([144, 120, 60], DisplayChoices.Rates(modes, 2560, 1440));
        Assert.Empty(DisplayChoices.Rates(modes, 1280, 720));
    }

    [Fact]
    public void AResolutionKeepsTheRateShownWhenItHasIt()
    {
        ScreenMode[] modes = [M(2560, 1440, 144), M(2560, 1440, 60), M(1920, 1080, 144), M(1920, 1080, 120)];
        Assert.Equal(M(2560, 1440, 60), DisplayChoices.ForResolution(modes, 2560, 1440, 60));
        Assert.Equal(M(1920, 1080, 144), DisplayChoices.ForResolution(modes, 1920, 1080, 60)); // no 60 there: its fastest
        Assert.Null(DisplayChoices.ForResolution(modes, 1280, 720, 60));
    }

    [Fact]
    public void ScalesOfThisPc()
    {
        // S20 (2026-10-16): 125% now (120 dpi), DisplayConfig's steps from the recommended: min -2, now -1, max 1.
        var s = DisplayChoices.Scales(125, -2, -1, 1)!;
        Assert.Equal([100, 125, 150, 175], s.Percents);
        Assert.Equal(150, s.Recommended);
        Assert.Equal(125, s.Now);
        Assert.Equal(-2, s.Relative(100));
        Assert.Equal(-1, s.Relative(125));
        Assert.Equal(0, s.Relative(150));
        Assert.Equal(1, s.Relative(175));
    }

    [Fact]
    public void ScalesOfAHighResolutionDisplay()
    {
        var s = DisplayChoices.Scales(200, -4, 0, 1)!;
        Assert.Equal([100, 125, 150, 175, 200, 225], s.Percents);
        Assert.Equal(200, s.Recommended);
        Assert.Equal(1, s.Relative(225));
    }

    [Theory]
    [InlineData(110, -1, 0, 1)] // a custom scale: not one of Windows' steps
    [InlineData(125, 1, 1, 2)]  // the smallest above the recommended: not understood
    [InlineData(125, -2, 0, -1)] // the largest below it
    [InlineData(125, -3, -1, 1)] // below 100%
    [InlineData(500, -1, 0, 2)] // above 500%
    [InlineData(125, -1, 2, 1)] // now outside its own range
    public void ScalesNotUnderstoodLeaveItToWindows(int percent, int min, int now, int max)
    {
        Assert.Null(DisplayChoices.Scales(percent, min, now, max));
    }

    [Fact]
    public void TheCountdownWritesWholeSecondsRoundedUp()
    {
        var c = new RevertCountdown(1000);
        Assert.Equal(15, c.Left(1000));
        Assert.Equal(15, c.Left(1001));
        Assert.Equal(1, c.Left(15_999));
        Assert.Equal(0, c.Left(16_000));
        Assert.Equal(RevertCountdown.Outcome.Waiting, c.State);
    }

    [Fact]
    public void NoAnswerPutsTheChangeBackOnce()
    {
        var c = new RevertCountdown(1000);
        Assert.False(c.Tick(15_999));
        Assert.True(c.Tick(16_000));
        Assert.False(c.Tick(17_000));
        Assert.Equal(RevertCountdown.Outcome.PutBack, c.State);
        Assert.False(c.Keep()); // too late
        Assert.Equal(RevertCountdown.Outcome.PutBack, c.State);
        Assert.Equal(0, c.Left(16_500));
    }

    [Fact]
    public void KeptInTimeStaysKept()
    {
        var c = new RevertCountdown(1000, seconds: 5);
        Assert.True(c.Keep());
        Assert.False(c.Tick(60_000));
        Assert.False(c.PutBack());
        Assert.Equal(RevertCountdown.Outcome.Kept, c.State);
        Assert.Equal(0, c.Left(2000));
    }

    [Fact]
    public void PutBackByTheUser()
    {
        var c = new RevertCountdown(0);
        Assert.True(c.PutBack());
        Assert.False(c.Keep());
        Assert.False(c.Tick(20_000));
        Assert.Equal(RevertCountdown.Outcome.PutBack, c.State);
    }
}
