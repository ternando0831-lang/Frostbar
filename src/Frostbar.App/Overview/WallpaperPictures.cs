using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;

namespace Frostbar.App.Overview;

/// <summary>
/// Wallpapers made small for the desktop strip (stage 3-4): decoded off the UI thread at about the size they are shown
/// (a photo decoded whole is many megapixels), turned the way its EXIF orientation says, frozen, and kept per file, its
/// time, its length and the width asked for (a new wallpaper under the same name is decoded again).
/// </summary>
public sealed class WallpaperPictures
{
    readonly int _keep;
    readonly Dispatcher _ui;
    readonly Dictionary<string, BitmapSource?> _done = new();
    readonly HashSet<string> _pending = new();

    /// <summary>A picture asked for earlier is ready (on the UI thread).</summary>
    public event Action? Loaded;

    /// <param name="keep">How many pictures are kept (more than are shown at once: a page that asks for more again after each would never end).</param>
    public WallpaperPictures(Dispatcher ui, int keep = 16)
    {
        _ui = ui;
        _keep = keep;
    }

    /// <summary>The picture of <paramref name="path"/> about <paramref name="widthPx"/> wide; null while it is being made (<see cref="Loaded"/> follows) or when it cannot be read.</summary>
    public BitmapSource? Get(string? path, int widthPx)
    {
        if (path == null || Key(path, widthPx) is not { Length: > 0 } key) return null;
        lock (_done)
        {
            if (_done.TryGetValue(key, out var made)) return made;
            if (!_pending.Add(key)) return null;
        }
        Task.Run(() =>
        {
            var made = Decode(path, widthPx);
            lock (_done)
            {
                if (_done.Count >= _keep) _done.Clear();
                _done[key] = made;
                _pending.Remove(key);
            }
            _ui.BeginInvoke(() => Loaded?.Invoke());
        });
        return null;
    }

    static string Key(string path, int widthPx)
    {
        try
        {
            var f = new FileInfo(path);
            return f.Exists ? $"{f.FullName}|{f.LastWriteTimeUtc.Ticks}|{f.Length}|{widthPx}" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    static BitmapSource? Decode(string path, int widthPx)
    {
        try
        {
            BitmapSource picture;
            using (var stream = File.OpenRead(path))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad; // read now: the file is not kept open
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = Math.Max(16, widthPx);
                image.StreamSource = stream;
                image.EndInit();
                picture = image;
            }
            picture = Orientation(path) switch
            {
                3 => new TransformedBitmap(picture, new RotateTransform(180)),
                6 => new TransformedBitmap(picture, new RotateTransform(90)),
                8 => new TransformedBitmap(picture, new RotateTransform(270)),
                _ => picture, // upright, or mirrored (rare for photos): as stored
            };
            picture.Freeze();
            return picture;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or InvalidOperationException or FileFormatException or System.Runtime.InteropServices.COMException)
        {
            Log.Debug($"wallpaper picture {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The photo's EXIF orientation (1: upright). TranscodedWallpaper, made by Windows, is already upright.</summary>
    static int Orientation(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return frame.Metadata is BitmapMetadata m && m.ContainsQuery("System.Photo.Orientation") && m.GetQuery("System.Photo.Orientation") is ushort o ? o : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or InvalidOperationException or FileFormatException or System.Runtime.InteropServices.COMException) { return 1; }
    }
}
