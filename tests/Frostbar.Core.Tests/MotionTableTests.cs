using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

/// <summary>The shell's movements in one table (2026-10-19, "B15"): its curves.</summary>
public class MotionTableTests
{
    static readonly (string Name, Func<double, double> Curve)[] Curves =
        [("Out", MotionTable.Out), ("In", MotionTable.In), ("InOut", MotionTable.InOut), ("Smooth", MotionTable.Smooth), ("SineInOut", MotionTable.SineInOut)];

    [Fact]
    public void EachCurveRunsFromZeroToOneAndNeverBack()
    {
        foreach (var (name, curve) in Curves)
        {
            Assert.Equal(0, curve(0));
            Assert.Equal(1, curve(1));
            Assert.Equal(0, curve(-0.5)); // before and after: held at the ends
            Assert.Equal(1, curve(1.5));
            double was = 0;
            for (int i = 1; i <= 1000; i++)
            {
                double v = curve(i / 1000.0);
                Assert.True(v >= was && v <= 1, $"{name}({i / 1000.0}) = {v} after {was}");
                was = v;
            }
        }
    }

    [Fact]
    public void TheCurvesStartAsTheySay()
    {
        Assert.True(MotionTable.Out(0.1) > 0.1);   // quick at first
        Assert.True(MotionTable.In(0.1) < 0.1);    // slow at first
        Assert.True(MotionTable.InOut(0.1) < 0.1 && MotionTable.InOut(0.9) > 0.9); // gentle at both ends
        Assert.True(MotionTable.Smooth(0.1) < 0.1 && MotionTable.Smooth(0.9) > 0.9);
        Assert.True(MotionTable.SineInOut(0.1) < 0.1 && MotionTable.SineInOut(0.9) > 0.9);
        foreach (var c in new Func<double, double>[] { MotionTable.InOut, MotionTable.Smooth, MotionTable.SineInOut })
            Assert.Equal(0.5, c(0.5), 9); // symmetric about the middle
    }

    /// <summary>
    /// Two curves were both called EaseInOut (the Dock's slots growing a smoothstep, its sliding away a cubic): they are
    /// not the same curve, and each has its own name now.
    /// </summary>
    [Fact]
    public void SmoothIsNotTheCubicInOut()
    {
        Assert.True(MotionTable.Smooth(0.2) > MotionTable.InOut(0.2) + 0.02, $"{MotionTable.Smooth(0.2)} vs {MotionTable.InOut(0.2)}");
        Assert.Equal(0.104, MotionTable.Smooth(0.2), 9); // 3·0.04 − 2·0.008
        Assert.Equal(0.032, MotionTable.InOut(0.2), 9);  // 4·0.008
    }

    [Fact]
    public void TheCubicOutIsTheOneThePartsHad()
    {
        // 1 − (1 − t)³, as written out in the five places before the table
        foreach (double t in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 }) Assert.Equal(1 - Math.Pow(1 - t, 3), MotionTable.Out(t), 12);
    }

    [Fact]
    public void TheSpringIsTheDocksSpring()
    {
        foreach (double t in new[] { 0.1, 0.3, 0.6, 0.9 }) Assert.Equal(Frostbar.Core.Dock.DockMotion.Spring(t, 0.6), MotionTable.Spring(t, 0.6));
    }

    [Fact]
    public void ASlidersGlassKnobStretchesByItsSpeedEitherWayAndNoFurtherThanItsMost()
    {
        Assert.Equal(0, KnobStretch.Of(0));
        Assert.Equal(0.15, KnobStretch.Of(0.5), 6);
        Assert.Equal(0.15, KnobStretch.Of(-0.5), 6);
        Assert.Equal(KnobStretch.Most, KnobStretch.Of(8));
        Assert.Equal(0, KnobStretch.Of(double.NaN));
        Assert.Equal(0, KnobStretch.Of(double.PositiveInfinity));
        // A move takes what is shown half way to its speed's stretch.
        Assert.Equal(0.15, KnobStretch.Towards(0, 1), 6);
        Assert.Equal(0.15, KnobStretch.Towards(0.3, 0), 6);
    }

    [Fact]
    public void AStretchedKnobIsWiderAndALittleLowerAndPastRoundTheOtherWay()
    {
        Assert.Equal((1.0, 1.0), KnobStretch.Size(0));
        var (wide, high) = KnobStretch.Size(0.3);
        Assert.Equal(1.3, wide, 6);
        Assert.Equal(0.91, high, 6);
        var (narrow, tall) = KnobStretch.Size(-0.1);
        Assert.True(narrow < 1 && tall > 1);
        Assert.Equal(KnobStretch.Size(KnobStretch.Most), KnobStretch.Size(3));
    }
}
