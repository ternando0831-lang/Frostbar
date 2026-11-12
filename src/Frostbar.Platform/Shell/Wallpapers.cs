using Microsoft.Win32;
using Frostbar.Platform.Wm;

namespace Frostbar.Platform.Shell;

/// <summary>How a wallpaper is laid on the screen (Control Panel\Desktop's WallpaperStyle and TileWallpaper).</summary>
public enum WallpaperFit { Fill, Fit, Stretch, Center, Tile, Span }

/// <summary>A desktop's wallpaper: the picture's file (null: a plain colour), how it is laid, and the colour around or instead of it.</summary>
public sealed record WallpaperInfo(string? Path, WallpaperFit Fit, byte R, byte G, byte B);

/// <summary>
/// The wallpapers the virtual desktop strip draws (stage 3-4), read from the registry only. The desktop in front shows
/// the picture Windows made for the screen (TranscodedWallpaper: already turned the right way up), another desktop its
/// own wallpaper (VirtualDesktops\Desktops\{id}\Wallpaper), else the one of all desktops. Read on 2026-10-16 on this PC:
/// the current desktop's own value and Control Panel's were the same file of the Photos app, style 10 (fill).
/// </summary>
public static class Wallpapers
{
    public static readonly string Transcoded = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");

    /// <param name="current">The desktop in front (its picture is the one Windows made for the screen).</param>
    public static WallpaperInfo For(Guid desktop, bool current)
    {
        var (fit, r, g, b) = Layout();
        string? global = GlobalPath();
        string? own = VirtualDesktops.WallpaperOf(desktop);
        string? path = current && File.Exists(Transcoded) ? Transcoded
            : own != null && File.Exists(own) ? own
            : global != null && File.Exists(global) ? global
            : File.Exists(Transcoded) ? Transcoded : null;
        // No picture at all (a plain colour background): Control Panel\Desktop's WallPaper is empty.
        if (global == null && own == null) path = null;
        return new WallpaperInfo(path, fit, r, g, b);
    }

    static string? GlobalPath()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return k?.GetValue("WallPaper") is string s && s.Length > 0 ? s : null;
    }

    static (WallpaperFit fit, byte r, byte g, byte b) Layout()
    {
        WallpaperFit fit = WallpaperFit.Fill;
        using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
        {
            string style = k?.GetValue("WallpaperStyle") as string ?? "10", tile = k?.GetValue("TileWallpaper") as string ?? "0";
            fit = style switch
            {
                "6" => WallpaperFit.Fit,
                "2" => WallpaperFit.Stretch,
                "22" => WallpaperFit.Span,
                "0" when tile == "1" => WallpaperFit.Tile,
                "0" => WallpaperFit.Center,
                _ => WallpaperFit.Fill,
            };
        }
        byte r = 0, g = 0, b = 0;
        using (var c = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors"))
        {
            var p = (c?.GetValue("Background") as string)?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p is { Length: 3 } && byte.TryParse(p[0], out var pr) && byte.TryParse(p[1], out var pg) && byte.TryParse(p[2], out var pb)) (r, g, b) = (pr, pg, pb);
        }
        return (fit, r, g, b);
    }
}
