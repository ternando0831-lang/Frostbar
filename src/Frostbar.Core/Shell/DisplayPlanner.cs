namespace Frostbar.Core.Shell;

/// <summary>A rectangle of the screen in pixels (right and bottom exclusive).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>A monitor as the shell lays out on it: its GDI name ("\\.\DISPLAY1"), its rectangle, its scale, primary or not.</summary>
public sealed record Display(string Device, ScreenRect Bounds, uint Dpi, bool Primary);

public enum DisplayChange
{
    /// <summary>Nothing the shell lays out by changed (the same layout again, or no monitor at all for a moment).</summary>
    None,
    /// <summary>The primary monitor kept its device and scale but moved or resized, or other monitors came or went: place the bars and windows again.</summary>
    Replace,
    /// <summary>The primary monitor is another one, or has another scale: make the shell's windows again (Windows 11 sends tool windows no WM_DPICHANGED).</summary>
    Rebuild,
}

/// <summary>
/// What a change of the displays means for the shell. The menu bar, its strip and the Dock live on the primary monitor:
/// when another monitor becomes the primary at the same scale, they have to move to it (2026-10-15: only the scale was
/// compared, and the menu bar and its strip stayed on the old primary while the Dock moved). Several WM_DISPLAYCHANGE
/// come for one change (plugging a monitor in): the caller waits for them to settle and plans once.
/// </summary>
public static class DisplayPlanner
{
    public static DisplayChange Plan(IReadOnlyList<Display> before, IReadOnlyList<Display> after)
    {
        if (after.Count == 0) return DisplayChange.None; // between two layouts (a monitor going away) there can be none
        var now = after.FirstOrDefault(d => d.Primary) ?? after[0];
        var was = before.FirstOrDefault(d => d.Primary) ?? before.FirstOrDefault();
        if (was == null) return DisplayChange.Rebuild;
        if (!string.Equals(was.Device, now.Device, StringComparison.OrdinalIgnoreCase) || was.Dpi != now.Dpi) return DisplayChange.Rebuild;
        if (was.Bounds != now.Bounds) return DisplayChange.Replace;
        bool same = before.Count == after.Count && before.All(b => after.Any(a =>
            string.Equals(a.Device, b.Device, StringComparison.OrdinalIgnoreCase) && a.Bounds == b.Bounds && a.Dpi == b.Dpi));
        return same ? DisplayChange.None : DisplayChange.Replace;
    }
}

/// <summary>
/// When the GPU's device is lost (a driver update, a reset after a hang), the shell makes a new one; a GPU that keeps
/// failing must not keep it making new ones forever: after <see cref="Max"/> within <see cref="WindowMs"/> it uses the
/// software device (WARP) instead.
/// </summary>
public sealed class DeviceRecovery
{
    public const int Max = 3;
    public const long WindowMs = 60_000;
    readonly Queue<long> _replaced = new();

    /// <summary>Records a replacement at <paramref name="nowMs"/>; true while a hardware device is still worth trying.</summary>
    public bool Hardware(long nowMs)
    {
        while (_replaced.Count > 0 && nowMs - _replaced.Peek() >= WindowMs) _replaced.Dequeue();
        _replaced.Enqueue(nowMs);
        return _replaced.Count <= Max;
    }

    /// <summary>The HRESULTs of a lost device (DXGI_ERROR_DEVICE_REMOVED, _HUNG, _RESET, DRIVER_INTERNAL_ERROR).</summary>
    public static bool IsLost(int hr) => unchecked((uint)hr) is 0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020;
}
