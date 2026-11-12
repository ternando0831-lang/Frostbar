using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class DisplayAndDeviceTests
{
    static readonly ScreenRect Laptop = new(0, 0, 1920, 1080), External = new(1920, 0, 4480, 1440);

    static Display D(string device, ScreenRect bounds, uint dpi = 120, bool primary = false) => new(device, bounds, dpi, primary);

    [Fact]
    public void The_same_layout_again_changes_nothing() =>
        Assert.Equal(DisplayChange.None, DisplayPlanner.Plan(
            [D(@"\\.\DISPLAY1", Laptop, primary: true)],
            [D(@"\\.\DISPLAY1", Laptop, primary: true)]));

    /// <summary>The menu bar, its strip and the Dock live on the primary: another primary at the same scale needs them made there (2026-10-15).</summary>
    [Fact]
    public void Another_primary_at_the_same_scale_makes_the_shell_again() =>
        Assert.Equal(DisplayChange.Rebuild, DisplayPlanner.Plan(
            [D(@"\\.\DISPLAY1", Laptop, primary: true), D(@"\\.\DISPLAY2", External)],
            [D(@"\\.\DISPLAY1", new ScreenRect(-1920, 0, 0, 1080)), D(@"\\.\DISPLAY2", new ScreenRect(0, 0, 2560, 1440), primary: true)]));

    [Fact]
    public void A_new_scale_makes_the_shell_again() =>
        Assert.Equal(DisplayChange.Rebuild, DisplayPlanner.Plan(
            [D(@"\\.\DISPLAY1", Laptop, 120, primary: true)],
            [D(@"\\.\DISPLAY1", Laptop, 144, primary: true)]));

    [Fact]
    public void A_new_resolution_places_everything_again() =>
        Assert.Equal(DisplayChange.Replace, DisplayPlanner.Plan(
            [D(@"\\.\DISPLAY1", Laptop, primary: true)],
            [D(@"\\.\DISPLAY1", new ScreenRect(0, 0, 1600, 900), primary: true)]));

    [Fact]
    public void A_monitor_plugged_in_beside_the_primary_places_everything_again() =>
        Assert.Equal(DisplayChange.Replace, DisplayPlanner.Plan(
            [D(@"\\.\DISPLAY1", Laptop, primary: true)],
            [D(@"\\.\DISPLAY1", Laptop, primary: true), D(@"\\.\DISPLAY2", External, 96)]));

    [Fact]
    public void No_monitor_for_a_moment_changes_nothing() =>
        Assert.Equal(DisplayChange.None, DisplayPlanner.Plan([D(@"\\.\DISPLAY1", Laptop, primary: true)], []));

    [Fact]
    public void A_lost_GPU_gets_three_new_devices_a_minute_then_the_software_one()
    {
        var r = new DeviceRecovery();
        Assert.True(r.Hardware(0));
        Assert.True(r.Hardware(1_000));
        Assert.True(r.Hardware(2_000));
        Assert.False(r.Hardware(3_000));         // a fourth within the minute: WARP
        Assert.True(r.Hardware(3_000 + 60_000)); // the first ones have aged out
    }

    [Theory]
    [InlineData(unchecked((int)0x887A0005), true)]  // DXGI_ERROR_DEVICE_REMOVED
    [InlineData(unchecked((int)0x887A0007), true)]  // DXGI_ERROR_DEVICE_RESET
    [InlineData(unchecked((int)0x80070057), false)] // E_INVALIDARG
    [InlineData(0, false)]
    public void Lost_device_errors_are_told_apart(int hr, bool lost) => Assert.Equal(lost, DeviceRecovery.IsLost(hr));
}
