using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Frostbar.Core.MenuBar;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Wm;

namespace Frostbar.App.MenuBar;

/// <summary>
/// How bright the wallpaper is under the menu bar (Core's <see cref="BarTone"/>: "C2", 2026-10-19), read from the picture
/// itself, small, off the UI thread. The shell's ticker looks every few seconds whether the picture or its layout changed
/// (a few registry values and a file's time: no picture is read then); a change reads it again.
/// </summary>
public sealed class BarWallpaper
{
    const string Job = "bar-wallpaper";
    readonly Dispatcher _ui;
    readonly Func<(int W, int H, int BarH)> _screen;
    string _seen = "";
    int _reading;

    /// <summary>The picture's brightness under the bar (0–1; null: not read yet), and whether the bar's words are dark over it.</summary>
    public double? Luma { get; private set; }
    public bool DarkWords { get; private set; }
    /// <summary>Pictures read (diagnostics).</summary>
    public int Reads { get; private set; }

    /// <summary>The words turned (UI thread).</summary>
    public event Action? Changed;

    /// <param name="screen">The primary monitor's size and the bar's height, in pixels (asked on the ticker's thread: no UI objects).</param>
    public BarWallpaper(Dispatcher ui, Func<(int W, int H, int BarH)> screen)
    {
        _ui = ui;
        _screen = screen;
    }

    public void Watch(BackgroundTicker ticker)
    {
        ticker.Add(Job, TimeSpan.FromSeconds(5), Check);
        Task.Run(Check);
    }

    public void Unwatch(BackgroundTicker ticker) => ticker.Remove(Job);

    void Check()
    {
        try
        {
            var info = Wallpapers.For(VirtualDesktops.Current(), current: true);
            var screen = _screen();
            long stamp = info.Path != null && File.Exists(info.Path) ? File.GetLastWriteTimeUtc(info.Path).Ticks : 0;
            string key = $"{info.Path}|{stamp}|{info.Fit}|{info.R},{info.G},{info.B}|{screen}";
            if (key == _seen || Interlocked.Exchange(ref _reading, 1) == 1) return;
            _seen = key;
            Task.Run(() =>
            {
                try
                {
                    double luma = Read(info, screen.W, screen.H, screen.BarH);
                    _ui.BeginInvoke(() => Take(luma));
                }
                catch (Exception ex) { Log.Debug("bar wallpaper: not read: " + ex.Message); }
                finally { Volatile.Write(ref _reading, 0); }
            });
        }
        catch (Exception ex) { Log.Debug("bar wallpaper: " + ex.Message); }
    }

    bool _spec;

    /// <summary>A spec's shell: the user's wallpaper is not read; the bar keeps the appearance until the spec gives a brightness.</summary>
    public void ForSpec(BackgroundTicker ticker)
    {
        _spec = true;
        Unwatch(ticker);
    }

    /// <summary>The spec's own brightness under the bar (diagnostics bar-tone), or none: the appearance's words again.</summary>
    public void SetForSpec(double? luma)
    {
        _spec = true;
        if (luma is double l) { Take(l, spec: true); return; }
        Luma = null;
        DarkWords = false;
        Changed?.Invoke();
    }

    void Take(double luma, bool spec = false)
    {
        if (_spec && !spec) return; // a read begun before the spec took over
        Reads++;
        bool first = Luma == null;
        Luma = luma;
        bool dark = BarTone.DarkWords(luma, DarkWords);
        if (!first && dark == DarkWords) return;
        DarkWords = dark;
        Log.Info($"bar wallpaper: brightness {luma:0.00} under the bar: {(dark ? "dark" : "light")} words"); // once a picture
        Changed?.Invoke();
    }

    static PictureFit Fit(WallpaperFit f) => f switch
    {
        WallpaperFit.Fit => PictureFit.Fit,
        WallpaperFit.Stretch => PictureFit.Stretch,
        WallpaperFit.Center => PictureFit.Center,
        WallpaperFit.Tile => PictureFit.Tile,
        WallpaperFit.Span => PictureFit.Span,
        _ => PictureFit.Fill,
    };

    /// <summary>The mean brightness of the picture's strip under the bar (its colour where the bar stands on the plain colour).</summary>
    static double Read(WallpaperInfo info, int screenW, int screenH, int barH)
    {
        double around = BarTone.Luma(info.R, info.G, info.B);
        if (info.Path == null) return around;
        int nativeW, nativeH;
        using (var head = File.OpenRead(info.Path))
        {
            var frame = BitmapDecoder.Create(head, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            (nativeW, nativeH) = (frame.PixelWidth, frame.PixelHeight);
        }
        if (BarTone.Band(nativeW, nativeH, screenW, screenH, barH, Fit(info.Fit)) is not { } band) return around;
        var small = new BitmapImage();
        small.BeginInit();
        small.UriSource = new Uri(info.Path);
        small.CacheOption = BitmapCacheOption.OnLoad;
        small.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        small.DecodePixelWidth = Math.Min(nativeW, 192);
        small.EndInit();
        small.Freeze();
        var px = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        px.Freeze();
        double k = (double)px.PixelWidth / nativeW, kh = (double)px.PixelHeight / nativeH;
        int x0 = Math.Clamp((int)Math.Floor(band.X * k), 0, px.PixelWidth - 1), y0 = Math.Clamp((int)Math.Floor(band.Y * kh), 0, px.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Ceiling(band.W * k), 1, px.PixelWidth - x0), h = Math.Clamp((int)Math.Ceiling(band.H * kh), 1, px.PixelHeight - y0);
        var bytes = new byte[w * h * 4];
        px.CopyPixels(new System.Windows.Int32Rect(x0, y0, w, h), bytes, w * 4, 0);
        double sum = 0;
        for (int i = 0; i < bytes.Length; i += 4) sum += BarTone.Luma(bytes[i + 2], bytes[i + 1], bytes[i]);
        return sum / (w * h);
    }
}
