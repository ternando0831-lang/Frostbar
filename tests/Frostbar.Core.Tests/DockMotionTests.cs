using Frostbar.Core.Dock;
using Xunit;

namespace Frostbar.Core.Tests;

public class DockMotionTests
{
    [Fact]
    public void AHopIsABallsArc()
    {
        Assert.Equal(0, DockMotion.Hop(0, 0.5, 20));
        Assert.Equal(20, DockMotion.Hop(0.25, 0.5, 20), 6);
        Assert.Equal(0, DockMotion.Hop(0.5, 0.5, 20), 6);
        Assert.Equal(DockMotion.Hop(0.1, 0.5, 20), DockMotion.Hop(0.4, 0.5, 20), 6);
        Assert.Equal(DockMotion.Hop(0.1, 0.5, 20), DockMotion.Hop(0.6, 0.5, 20), 6); // the next hop
        // Quickest at the bottom: higher just after leaving it than a sine's arch (a softer, floating hop).
        Assert.True(DockMotion.Hop(0.025, 0.5, 20) > 20 * Math.Sin(0.05 * Math.PI) + 0.5);
    }

    [Fact]
    public void ALandingFalls()
    {
        Assert.Equal(10, DockMotion.Land(10, 0, 0.12), 6);
        Assert.Equal(0, DockMotion.Land(10, 0.12, 0.12));
        Assert.Equal(0, DockMotion.Land(10, 0.5, 0.12));
        double half = DockMotion.Land(10, 0.06, 0.12);
        // Quicker as it goes: the second half of the time drops further than the first (a constant speed drops alike).
        Assert.True(half - 0 > 2 * (10 - half), $"halfway at {half}");
    }

    [Fact]
    public void AnApproachSettlesSoftlyWithinItsTime()
    {
        double v = 0, t = 0, dt = 1 / 60.0;
        var steps = new List<double>();
        while (v < 1 && t < 1)
        {
            double next = DockMotion.Approach(v, 1, dt, 0.18);
            Assert.True(next >= v && next <= 1);
            steps.Add(next - v);
            v = next;
            t += dt;
        }
        Assert.Equal(1, v);
        Assert.InRange(t, 0.15, 0.2 + dt); // there by its time (1% left is snapped)
        // Settling: the last steps are far smaller than the first (a constant speed stops dead at full speed).
        Assert.True(steps[^2] < steps[0] / 4, $"first {steps[0]:0.000}, last but one {steps[^2]:0.000}");
    }

    [Fact]
    public void AnApproachFollowsAChangedTargetWithoutAJump()
    {
        double v = 0, dt = 1 / 60.0;
        for (int i = 0; i < 3; i++) v = DockMotion.Approach(v, 1, dt, 0.12);
        double turned = DockMotion.Approach(v, 0, dt, 0.18);
        Assert.True(turned < v && turned > 0, $"{v} → {turned}");
        Assert.Equal(0.5, DockMotion.Approach(0.5, 0.5, dt, 0.12));
        Assert.Equal(0.3, DockMotion.Approach(0.3, 1, 0, 0.12)); // no time, no move
    }

    // ---- a spring let go (macOS 27's glass, 2026-10-18) ----

    static double Peak(double damping) => Enumerable.Range(0, 1001).Max(i => DockMotion.Spring(i / 1000.0, damping));

    [Fact]
    public void ASpringStartsAtZeroAndEndsExactlyAtOne()
    {
        foreach (double z in new[] { 0.35, 0.6, 0.75 })
        {
            Assert.Equal(0, DockMotion.Spring(0, z));
            Assert.Equal(1, DockMotion.Spring(1, z));
            Assert.InRange(DockMotion.Spring(0.97, z), 0.98, 1.02); // settled near the end
        }
    }

    [Theory]
    [InlineData(0.75, 1.015, 1.045)] // about 3%
    [InlineData(0.6, 1.06, 1.12)]    // about 9%
    [InlineData(0.35, 1.25, 1.36)]   // about 31%
    public void ASpringGoesPastItsMarkByItsDamping(double damping, double low, double high) =>
        Assert.InRange(Peak(damping), low, high);

    [Fact]
    public void ASpringRisesWithoutAStopUntilItsPeak()
    {
        double last = -1;
        for (int i = 0; i <= 1000; i++)
        {
            double v = DockMotion.Spring(i / 1000.0, 0.6);
            if (v < last) break; // past the peak
            last = v;
        }
        Assert.True(last > 1.0);
        Assert.True(DockMotion.Spring(0.2, 0.6) > 0.5); // most of the way early (it starts from rest, gathering speed)
    }
}
