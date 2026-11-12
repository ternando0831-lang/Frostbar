using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Icons;

/// <summary>
/// App icons for the Dock, the search and the app grid (ADR-006). The shell's images are made by a helper process
/// (<see cref="IconExtractor"/>; in this process they would keep about 45 MB) as PNG files in the cache folder, one per
/// source and size, and this process only reads those. A file made more than a day ago is shown at once and made again
/// in the background once the requests have settled (an app's new icon shows up then). Files are read on a worker
/// thread; icons are kept frozen on the UI thread. Sources: an AppsFolder parsing name ("shell:AppsFolder\…"), a file
/// path, or "stock:&lt;SIID&gt;" for a system stock icon at jumbo size (the Recycle Bin: read here, it changes and the
/// system image list is cheap).
/// </summary>
public sealed class IconService : IDisposable
{
    const int RetryMs = 60_000, SettleMs = 10_000, HelperTimeoutMs = 60_000;
    static readonly TimeSpan Stale = TimeSpan.FromDays(1), Unused = TimeSpan.FromDays(60);
    readonly Dispatcher _ui;
    readonly int _px;
    readonly string _dir;
    readonly BlockingCollection<(string key, string source)> _queue = new();
    readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase); // UI thread
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);                 // UI thread
    /// <summary>
    /// Icons that could not be loaded, and when (UI thread). Owners ask again for every icon when one has loaded, so
    /// without this a failing icon would be loaded again and again; it is tried once more after a minute.
    /// </summary>
    readonly Dictionary<string, long> _failed = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files older than a day that were shown: made again once the requests settle (worker thread).</summary>
    readonly Dictionary<string, (string source, List<string> keys)> _stale = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Process> _helpers = new();
    readonly Thread _thread;
    bool _cleaned;
    volatile bool _disposed;

    /// <summary>An icon finished loading, or changed (UI thread).</summary>
    public event Action<string>? Loaded;

    /// <param name="px">Pixel size the icons are kept at (the largest drawn size).</param>
    /// <param name="cacheDir">Where the PNG files go (default: the profile's cache\icons).</param>
    public IconService(Dispatcher ui, int px, string? cacheDir = null)
    {
        _ui = ui;
        _px = Math.Clamp(px, 16, 256);
        _dir = cacheDir ?? Path.Combine(AppPaths.Cache, "icons");
        _thread = new Thread(Work) { IsBackground = true, Name = "icons" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>The cached icon, or null after queueing it (Loaded fires when it is ready).</summary>
    public ImageSource? Get(string key, string source)
    {
        if (_cache.TryGetValue(key, out var img)) return img;
        if (_failed.TryGetValue(key, out long at) && Environment.TickCount64 - at < RetryMs) return null;
        if (_pending.Add(key)) _queue.Add((key, source));
        return null;
    }

    /// <summary>Forgets a cached icon (the Recycle Bin's changes between empty and full).</summary>
    public void Forget(string key)
    {
        _cache.Remove(key);
        _failed.Remove(key);
    }

    /// <summary>How many icons are cached, and how many could not be loaded (diagnostics).</summary>
    public (int cached, int failed) Counts => (_cache.Count, _failed.Count);

    string FileFor(string source) =>
        Path.Combine(_dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source.ToLowerInvariant() + "|" + _px)), 0, 10) + ".png");

    void Work()
    {
        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) { Log.Warn("icons: the cache folder cannot be made", ex); }
        while (!_queue.IsCompleted)
        {
            int wait = _stale.Count > 0 || !_cleaned ? SettleMs : Timeout.Infinite;
            if (!_queue.TryTake(out var first, wait))
            {
                if (_disposed) return;
                if (_stale.Count > 0) Refresh();
                if (!_cleaned) { CleanUp(); _cleaned = true; }
                continue;
            }
            // A burst of requests (the Dock's items, the grid's apps) goes to one helper run.
            var batch = new List<(string key, string source)> { first };
            while (batch.Count < 500 && _queue.TryTake(out var more, 30)) batch.Add(more);
            var missing = new List<(string key, string source, string file)>();
            foreach (var (key, source) in batch)
            {
                if (source.StartsWith("stock:", StringComparison.Ordinal)) { Post(key, Stock(source)); continue; }
                string file = FileFor(source);
                var image = File.Exists(file) ? IconExtractor.ReadPng(file) : null;
                if (image == null) { missing.Add((key, source, file)); continue; }
                Post(key, image);
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > Stale) Remember(file, source, key);
            }
            if (missing.Count == 0) continue;
            var answers = RunHelper(missing.Select(m => (m.source, m.file)).Distinct().ToList());
            foreach (var m in missing)
                Post(m.key, answers.GetValueOrDefault(m.file) is "new" or "same" ? IconExtractor.ReadPng(m.file) : null);
        }
    }

    void Remember(string file, string source, string key)
    {
        if (!_stale.TryGetValue(file, out var s)) _stale[file] = s = (source, new List<string>());
        if (!s.keys.Contains(key)) s.keys.Add(key);
    }

    /// <summary>Makes the old files again; the icons that changed are shown anew.</summary>
    void Refresh()
    {
        var work = _stale.ToList();
        _stale.Clear();
        var answers = RunHelper(work.Select(w => (w.Value.source, w.Key)).ToList());
        int changed = 0;
        foreach (var (file, (_, keys)) in work)
        {
            if (answers.GetValueOrDefault(file) != "new" || IconExtractor.ReadPng(file) is not { } image) continue;
            changed++;
            foreach (var key in keys) Replace(key, image);
        }
        Log.Debug($"icons: {work.Count} made again in the background, {changed} changed");
    }

    /// <summary>
    /// Has the helper make these files (one process for the lot, below normal priority, killed after a minute);
    /// returns its answer per file ("new", "same", "fail"), nothing for files it never got to.
    /// </summary>
    Dictionary<string, string> RunHelper(List<(string source, string file)> work)
    {
        var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (work.Count == 0 || _disposed) return answers;
        string request = Path.Combine(_dir, $"request-{Environment.ProcessId}-{_px}-{Environment.TickCount64}.txt");
        var sw = Stopwatch.StartNew();
        try
        {
            File.WriteAllLines(request, work.Select(w => $"{w.source}\t{_px}\t{w.file}"));
            var psi = new ProcessStartInfo(AppPaths.ExePath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("--icons");
            psi.ArgumentList.Add(request);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            lock (_helpers) _helpers.Add(p);
            try
            {
                using var timeout = new Timer(_ => { try { p.Kill(); } catch (Exception) { } }, null, HelperTimeoutMs, Timeout.Infinite);
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0) answers[line[(tab + 1)..]] = line[..tab];
                }
                p.WaitForExit();
            }
            finally { lock (_helpers) _helpers.Remove(p); }
            Log.Debug($"icons: the helper made {answers.Count(a => a.Value == "new")} new and {answers.Count(a => a.Value == "same")} unchanged, " +
                      $"{answers.Count(a => a.Value == "fail")} failed, of {work.Count} at {_px} px in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { Log.Warn("icons: the helper could not run", ex); }
        finally { try { File.Delete(request); } catch (Exception) { } }
        return answers;
    }

    /// <summary>Removes icons not shown for two months, and what helpers left behind (requests, half-written files).</summary>
    void CleanUp()
    {
        try
        {
            var now = DateTime.UtcNow;
            int removed = 0;
            foreach (var f in Directory.EnumerateFiles(_dir))
            {
                var age = now - File.GetLastWriteTimeUtc(f);
                if (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? age > Unused : age > Stale) { File.Delete(f); removed++; }
            }
            if (removed > 0) Log.Debug($"icons: {removed} old file(s) removed from the cache");
        }
        catch (Exception ex) { Log.Debug("icons: clean-up: " + ex.Message); }
    }

    void Post(string key, BitmapSource? image) => _ui.BeginInvoke(() =>
    {
        _pending.Remove(key);
        if (image != null) { _cache[key] = image; _failed.Remove(key); }
        else _failed[key] = Environment.TickCount64;
        Loaded?.Invoke(key);
    });

    void Replace(string key, BitmapSource image) => _ui.BeginInvoke(() =>
    {
        if (!_cache.ContainsKey(key)) return; // forgotten since
        _cache[key] = image;
        Loaded?.Invoke(key);
    });

    BitmapSource? Stock(string source)
    {
        try { return IconExtractor.Scale(StockIcon(int.Parse(source[6..])), _px); }
        catch (Exception ex) { Log.Debug($"icon {source} failed: {ex.Message}"); return null; }
    }

    /// <summary>A stock icon (SHSTOCKICONID) from the system's jumbo image list (256 px).</summary>
    static BitmapSource? StockIcon(int siid)
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (ShellApi.SHGetStockIconInfo(siid, 0x4000 /*SHGSI_SYSICONINDEX*/, ref info) != 0) return null;
        var iid = typeof(IImageList).GUID;
        if (ShellApi.SHGetImageList(4 /*SHIL_JUMBO*/, ref iid, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        object? o = null;
        try
        {
            // One wrapper per use: the image list is a process-wide object (Bipane's lesson).
            o = Marshal.GetUniqueObjectForIUnknown(p);
            IntPtr hicon = IntPtr.Zero;
            ((IImageList)o).GetIcon(info.iSysImageIndex, 1 /*ILD_TRANSPARENT*/, ref hicon);
            if (hicon == IntPtr.Zero) return null;
            try
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            finally { Native.DestroyIcon(hicon); }
        }
        finally
        {
            if (o != null) Marshal.ReleaseComObject(o);
            Marshal.Release(p);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _queue.CompleteAdding();
        lock (_helpers)
            foreach (var p in _helpers)
                try { p.Kill(); } catch (Exception) { }
    }
}
