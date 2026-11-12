using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;

namespace Frostbar.Platform.Shell;

/// <summary>
/// Where the Wallpaper page and the light/dark pair read and put the wallpaper (stage 4-3): Windows' own through the
/// public IDesktopWallpaper, or, under a spec, a picture of the spec's own kept in memory, so that no spec changes the
/// user's wallpaper unless it asks for Windows'.
/// </summary>
public interface IWallpaperStore
{
    /// <summary>The picture on every monitor, or null when they differ (a picture per monitor) or there is none.</summary>
    string? Current();
    WallpaperFit Fit();
    /// <summary>Puts <paramref name="path"/> on every monitor; what went wrong, or null. Explorer shows it a moment later (S20: about 0.3 s).</summary>
    string? Set(string path);
    string? SetFit(WallpaperFit fit);
    int Writes { get; }
}

public static class WallpaperStore
{
    /// <summary>The store the pages use: Windows', unless a spec asked for its own at start.</summary>
    public static IWallpaperStore Current { get; set; } = new WindowsWallpaper();

    /// <summary>Windows' own wallpapers (Web\Wallpaper): the Windows folder's first (its light and dark bloom), then the themes'.</summary>
    public static IReadOnlyList<string> BuiltIns()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper");
        try
        {
            if (!Directory.Exists(root)) return [];
            string[] pictures = [".jpg", ".jpeg", ".png", ".bmp"];
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => pictures.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => Path.GetFileName(Path.GetDirectoryName(f)) == "Windows" ? 0 : Path.GetDirectoryName(f) == root ? 2 : 1)
                .ThenBy(f => Path.GetDirectoryName(f), StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Length).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("wallpaper: could not list Windows' own", ex); return []; }
    }

    /// <summary>Windows' light and dark bloom (Web\Wallpaper\Windows\img0.jpg and img19.jpg) when they are there: the pair's first choice.</summary>
    public static (string? light, string? dark) Bloom()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Windows");
        string light = Path.Combine(folder, "img0.jpg"), dark = Path.Combine(folder, "img19.jpg");
        return (File.Exists(light) ? light : null, File.Exists(dark) ? dark : null);
    }
}

/// <summary>
/// Windows' wallpaper through IDesktopWallpaper (S20, 2026-10-16): SetWallpaper returns at once and Explorer shows the
/// picture about 0.3 s later. A picture put on every monitor is set as one (no monitor named): set monitor by monitor,
/// "all monitors" reads empty afterwards and Control Panel's WallPaper becomes TranscodedWallpaper.
/// </summary>
public sealed class WindowsWallpaper : IWallpaperStore
{
    enum Position { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        Interop.RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(Position position);
        Position GetPosition();
    }

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")] class DesktopWallpaperClass { }

    int _writes;
    public int Writes => _writes;

    static IDesktopWallpaper Make() => (IDesktopWallpaper)new DesktopWallpaperClass();

    public string? Current()
    {
        try
        {
            string path = Make().GetWallpaper(null);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (COMException) { return null; } // a picture per monitor (S_FALSE with an empty string, or an error)
    }

    public WallpaperFit Fit()
    {
        try
        {
            return Make().GetPosition() switch
            {
                Position.Center => WallpaperFit.Center, Position.Tile => WallpaperFit.Tile, Position.Stretch => WallpaperFit.Stretch,
                Position.Fit => WallpaperFit.Fit, Position.Span => WallpaperFit.Span, _ => WallpaperFit.Fill,
            };
        }
        catch (COMException) { return WallpaperFit.Fill; }
    }

    public string? Set(string path)
    {
        if (!File.Exists(path)) return "the picture is not there";
        Interlocked.Increment(ref _writes);
        try { Make().SetWallpaper(null, path); Log.Info($"wallpaper: {Path.GetFileName(path)} on every monitor"); return null; }
        catch (COMException ex) { Log.Warn("wallpaper: not set", ex); return $"0x{ex.HResult:X8}"; }
    }

    public string? SetFit(WallpaperFit fit)
    {
        Interlocked.Increment(ref _writes);
        var position = fit switch
        {
            WallpaperFit.Center => Position.Center, WallpaperFit.Tile => Position.Tile, WallpaperFit.Stretch => Position.Stretch,
            WallpaperFit.Fit => Position.Fit, WallpaperFit.Span => Position.Span, _ => Position.Fill,
        };
        try { Make().SetPosition(position); return null; }
        catch (COMException ex) { Log.Warn("wallpaper: position not set", ex); return $"0x{ex.HResult:X8}"; }
    }
}

/// <summary>A spec's own wallpaper, in memory (FROSTBAR_E2E_WALLPAPER=spec with --diag): Windows' light bloom at first, filled.</summary>
public sealed class SpecWallpaper : IWallpaperStore
{
    readonly object _gate = new();
    string? _path = WallpaperStore.Bloom().light;
    WallpaperFit _fit = WallpaperFit.Fill;
    int _writes;
    public int Writes => _writes;
    public string? Current() { lock (_gate) return _path; }
    public WallpaperFit Fit() { lock (_gate) return _fit; }
    public string? Set(string path) { lock (_gate) { _path = path; _writes++; } return null; }
    public string? SetFit(WallpaperFit fit) { lock (_gate) { _fit = fit; _writes++; } return null; }
}
