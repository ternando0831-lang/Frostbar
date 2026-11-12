using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Wm;

/// <summary>
/// Windows' "Snap windows" (its window arrangement: a window dragged to a screen's edge fills that half), as macOS's
/// Desktop &amp; Dock › "Drag windows to screen edges to tile" (2026-10-22). Windows' own setting, read and written
/// through SystemParametersInfo (SPI_GET/SETWINARRANGING, what Windows' settings app writes), or, under a spec, a value
/// of the spec's own kept in memory, so that no spec changes the user's (FROSTBAR_E2E_SNAP=spec, with --diag only). The
/// write announces the change to every window: made off the UI thread.
/// </summary>
public static class WindowSnap
{
    const uint SPI_GETWINARRANGING = 0x0082, SPI_SETWINARRANGING = 0x0083;

    static bool? _spec;
    static int _writes;

    /// <summary>A spec's shell keeps its own value from now on, starting at <paramref name="on"/>.</summary>
    public static void UseSpec(bool on) => _spec = on;

    /// <summary>The value is the spec's own (diagnostics).</summary>
    public static bool Spec => _spec != null;

    /// <summary>Writes made (diagnostics: a spec counts them).</summary>
    public static int Writes => _writes;

    /// <summary>Whether windows snap to the screen's edges (true when it cannot be read: Windows' default).</summary>
    public static bool On()
    {
        if (_spec is { } own) return own;
        return !Native.SystemParametersInfo(SPI_GETWINARRANGING, 0, out int v, 0) || v != 0;
    }

    /// <summary>Turns it on or off; what went wrong, or null.</summary>
    public static string? Set(bool on)
    {
        Interlocked.Increment(ref _writes);
        if (_spec != null) { _spec = on; return null; }
        // The value goes in uiParam: given in pvParam with uiParam 0, it turned Snap off (2026-10-22, written unsaved
        // and put back at once), though Windows' documentation names pvParam for this one.
        if (Native.SystemParametersInfo(SPI_SETWINARRANGING, on ? 1u : 0u, IntPtr.Zero, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE)) return null;
        int error = Marshal.GetLastWin32Error();
        Log.Warn($"snap: could not turn it {(on ? "on" : "off")} ({error})");
        return $"SystemParametersInfo {error}";
    }
}
