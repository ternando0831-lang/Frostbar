using Frostbar.Core.Time;
using Xunit;

namespace Frostbar.Core.Tests;

public class RefreshStepsTests
{
    const double P = 1000.0 / 60;

    static long[] Frames(RefreshSteps s, params double[] ticks) => ticks.Select(e => { s.Next(e, P, 100); return s.Frame; }).ToArray();

    [Fact]
    public void EachTickIsTheRefreshItIsShownAt()
    {
        var s = new RefreshSteps();
        Assert.Equal(1 / 6.0, s.Next(P + 0.3, P, 6 * P), 9);
        Assert.Equal(2 / 6.0, s.Next(2 * P - 0.2, P, 6 * P), 9);
        Assert.Equal(3 / 6.0, s.Next(3 * P + 1, P, 6 * P), 9);
    }

    [Fact]
    public void ATickInTheSameRefreshIsOneMoreStill() =>
        Assert.Equal(new long[] { 1, 2, 3 }, Frames(new RefreshSteps(), 2, 4, 2.2 * P));

    [Fact]
    public void ALateTickSkipsTheRefreshesItMissed() =>
        Assert.Equal(new long[] { 1, 4, 5 }, Frames(new RefreshSteps(), P, 4 * P + 1, 5 * P));

    [Fact]
    public void ItEndsAtOne()
    {
        var s = new RefreshSteps();
        Assert.Equal(1 / 3.0, s.Next(P, P, 3 * P), 9);
        Assert.Equal(2 / 3.0, s.Next(2 * P, P, 3 * P), 9);
        Assert.Equal(1, s.Next(3 * P, P, 3 * P), 9);
        Assert.Equal(1, s.Next(4 * P, P, 3 * P));
        Assert.Equal(1, new RefreshSteps().Next(5, P, 0));
    }

    [Fact]
    public void TwoBegunMicrosecondsApartCanBeARefreshApartInEveryFrame()
    {
        // Ticks about half a refresh after the beginnings, the first a refresh late (a clock started from its rest), the
        // second movement begun 8 µs after the first: why a scale across and down is one movement (Tween), not one
        // for each (GlassBounce's circle 68 by 70, 2026-10-18).
        double[] ticks = [1.5 * P + 0.004, 2.5 * P + 0.004, 3.5 * P - 0.001, 4.5 * P + 0.005];
        Assert.Equal(new long[] { 2, 3, 4, 5 }, Frames(new RefreshSteps(), ticks));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, Frames(new RefreshSteps(), ticks.Select(e => e - 0.008).ToArray()));
    }
}
