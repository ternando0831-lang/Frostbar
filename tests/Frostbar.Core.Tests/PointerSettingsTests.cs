using Frostbar.Core.Devices;

namespace Frostbar.Core.Tests;

public class PointerSettingsTests
{
    // What this PC's touchpad read (2026-10-16, Lab pointer): present, an external mouse, enabled, active; every
    // setting on but the mouse's acceleration and the reversed scrolling; sensitivity 2, speed 10.
    const uint Present = 0b1_1101, Settings = 0b1_1011_1111;

    [Fact]
    public void TheBitsAreReadInTheirOrder()
    {
        var s = TouchpadBits.Read(Present, Settings, 2, 10);
        Assert.Equal(new TouchpadSettings(Present: true, Enabled: true, LeaveOnWithMouse: true, TapToClick: true, TapAndDrag: true, TwoFingerTap: true,
            RightClickZone: true, TwoFingerScroll: true, PinchZoom: true, NaturalScrolling: true, Sensitivity: 2, CursorSpeed: 10), s);
    }

    [Theory]
    [InlineData(TouchpadBits.Tap)]
    [InlineData(TouchpadBits.TapAndDrag)]
    [InlineData(TouchpadBits.TwoFingerTap)]
    [InlineData(TouchpadBits.RightClickZone)]
    [InlineData(TouchpadBits.Pan)]
    [InlineData(TouchpadBits.Zoom)]
    [InlineData(TouchpadBits.LeaveOnWithMouse)]
    public void EachSettingIsItsOwnBit(int bit)
    {
        var on = TouchpadBits.Read(Present, Settings, 2, 10);
        var off = TouchpadBits.Read(Present, Settings & ~(1u << bit), 2, 10);
        Assert.NotEqual(on, off);
        // Written back, only that bit differs.
        Assert.Equal(1u << bit, TouchpadBits.WriteSettings(Settings, on) ^ TouchpadBits.WriteSettings(Settings, off));
    }

    [Fact]
    public void NaturalScrollingIsWindowsDefaultTheReversedBitOff()
    {
        Assert.True(TouchpadBits.Read(Present, Settings, 2, 10).NaturalScrolling);
        var reversed = TouchpadBits.Read(Present, Settings | (1u << TouchpadBits.ScrollReversed), 2, 10);
        Assert.False(reversed.NaturalScrolling);
        Assert.Equal(Settings | (1u << TouchpadBits.ScrollReversed), TouchpadBits.WriteSettings(Settings, reversed));
    }

    [Fact]
    public void WritingKeepsTheBitsThePageDoesNotHave()
    {
        // Feedback (1), the mouse's acceleration (6) and a reserved bit (12) are left as read.
        uint others = (1u << 1) | (1u << 6) | (1u << 12);
        var s = TouchpadBits.Read(Present, Settings, 2, 10) with { TapToClick = false, PinchZoom = false };
        uint written = TouchpadBits.WriteSettings(Settings | others, s);
        Assert.Equal(others, written & others);
        Assert.Equal(0u, written & (1u << TouchpadBits.Tap));
        Assert.Equal(0u, written & (1u << TouchpadBits.Zoom));
        Assert.Equal(1u << TouchpadBits.Pan, written & (1u << TouchpadBits.Pan));
    }

    [Fact]
    public void TurningTheTouchpadOffChangesOnlyItsEnabledBit()
    {
        uint off = TouchpadBits.WritePresent(Present, false);
        Assert.Equal(Present & ~(1u << TouchpadBits.Enabled), off);
        Assert.False(TouchpadBits.Read(off, Settings, 2, 10).Enabled);
        Assert.Equal(Present, TouchpadBits.WritePresent(off, true));
    }

    [Fact]
    public void NumbersAreKeptInTheirRanges()
    {
        var s = TouchpadBits.Read(Present, Settings, 9, 0);
        Assert.Equal(4, s.Sensitivity);
        Assert.Equal(TouchpadSettings.SpeedMin, s.CursorSpeed);
        Assert.Equal(TouchpadSettings.SpeedMax, TouchpadBits.Read(Present, Settings, 2, 99).CursorSpeed);
    }

    [Fact]
    public void TheMousesValuesComeIntoTheSlidersRanges()
    {
        Assert.Equal(new MouseSettings(1, true, 20, false, 900), new MouseSettings(0, true, -1, false, 1500).Clamped());
        Assert.Equal(new MouseSettings(20, false, 1, true, 200), new MouseSettings(25, false, 0, true, 100).Clamped());
        Assert.Equal(new MouseSettings(10, true, 8, false, 500), new MouseSettings(10, true, 8, false, 500).Clamped());
    }

    [Theory]
    [InlineData(0, 250)]
    [InlineData(1, 500)]
    [InlineData(3, 1000)]
    [InlineData(-2, 250)]
    [InlineData(7, 1000)]
    public void TheKeysRepeatDelayInMilliseconds(int delay, int ms) => Assert.Equal(ms, KeyRepeatSettings.DelayMs(delay));

    [Fact]
    public void TheKeysRepeatIsKeptInRange()
    {
        Assert.Equal(new KeyRepeatSettings(3, 31), new KeyRepeatSettings(5, 40).Clamped());
        Assert.Equal(new KeyRepeatSettings(0, 0), new KeyRepeatSettings(-1, -3).Clamped());
    }
}
