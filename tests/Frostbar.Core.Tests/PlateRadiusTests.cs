using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class PlateRadiusTests
{
    [Theory]
    // Focus's circle in its bounce (2026-10-18): the panel's radius from the size before it was rounded.
    [InlineData(37.48f, 75, 75, 37.5f)]
    [InlineData(37.476f, 75, 75, 37.5f)]
    [InlineData(36.373f, 73, 73, 36.5f)]
    // Its height off its width (cc-seams uneven): the radius a hair under half the shorter side.
    [InlineData(33.49999f, 69, 67, 33.5f)]
    [InlineData(36.49999f, 75, 73, 36.5f)]
    public void A_radius_a_hair_under_half_its_side_is_half(float wanted, float w, float h, float expected) =>
        Assert.Equal(expected, PlateRadius.Of(wanted, w, h));

    [Theory]
    [InlineData(37.5f, 75, 75, 37.5f)] // a circle
    [InlineData(400f, 900, 75, 37.5f)] // the Dock's pill: far more asked, its ends half circles
    [InlineData(26f, 200, 82, 26f)] // a module: far from half
    [InlineData(37f, 75, 75, 37f)] // a pixel's straight part
    [InlineData(-3f, 75, 75, 0f)]
    [InlineData(10f, 0, 0, 0f)] // a plate not laid out yet
    public void Any_other_radius_is_as_asked_up_to_half(float wanted, float w, float h, float expected) =>
        Assert.Equal(expected, PlateRadius.Of(wanted, w, h));

    [Fact]
    public void The_straight_part_is_never_a_sliver()
    {
        // The seams came where the shorter side's straight part was more than none and less than a pixel's tenth.
        for (int w = 1; w <= 120; w++)
            for (int h = w; h <= w + 3; h++)
                for (float wanted = 0; wanted <= 70; wanted += 0.0137f)
                {
                    float straight = Math.Min(w, h) - 2 * PlateRadius.Of(wanted, w, h);
                    if (straight != 0 && straight < 2 * PlateRadius.Snap - 1e-4f) Assert.Fail($"{w}x{h} radius {wanted}: straight {straight}");
                }
    }
}
