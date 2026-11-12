using System.Windows.Threading;
using Frostbar.Core.Menus;
using Frostbar.Platform.Apps;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Shell;

/// <summary>
/// The documents Windows lists as used lately (shell:recent, %APPDATA%\Microsoft\Windows\Recent), for the system
/// menu's Recent Items (stage 5-2): the newest links there that point at files (folders left out, and files no longer
/// on a disk of this PC), read on a worker thread at start and half a second after the folder changes. Opening the
/// menu reads nothing (it opens in about 18 ms). A spec's shell reads a folder of its own (FROSTBAR_E2E_RECENT, with
/// --diag only), never the user's.
/// </summary>
public sealed class RecentDocuments : IDisposable
{
    /// <summary>Links looked at for each document wanted, the newest first: enough to find ten files among the folders Explorer lists too (40 for 10).</summary>
    const int LookedEach = 4;
    readonly Dispatcher _ui;
    volatile int _wanted = RecentItems.Count;

    /// <summary>
    /// How many documents to find (the settings' count of Recent Items; at least the default's, which the search's
    /// listing of recent documents also reads). Raised, the folder is read again.
    /// </summary>
    public int Wanted
    {
        get => _wanted;
        set
        {
            int v = Math.Clamp(value, RecentItems.Count, RecentItems.Max);
            if (v == _wanted) return;
            bool more = v > _wanted;
            _wanted = v;
            if (more) _wake.Set();
        }
    }
    readonly FileSystemWatcher? _watcher;
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _disposed;

    /// <summary>The newest documents (UI thread; empty until the first read).</summary>
    public IReadOnlyList<RecentDocument> Items { get; private set; } = [];
    /// <summary>The list changed (UI thread).</summary>
    public event Action? Changed;
    /// <summary>The folder read (diagnostics).</summary>
    public string Folder { get; }
    /// <summary>How many times the folder was read (diagnostics: a change is read once, not once per event).</summary>
    public int Reads { get; private set; }

    public RecentDocuments(Dispatcher ui, string? folder = null)
    {
        _ui = ui;
        Folder = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        try
        {
            _watcher = new FileSystemWatcher(Folder, "*.lnk") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, IncludeSubdirectories = false };
            FileSystemEventHandler soon = (_, _) => _wake.Set();
            _watcher.Created += soon;
            _watcher.Changed += soon;
            _watcher.Deleted += soon;
            _watcher.Renamed += (_, _) => _wake.Set();
            _watcher.Error += (_, e) => { Log.Debug("recent documents: the watch overflowed: " + e.GetException().Message); _wake.Set(); };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Log.Info("recent documents: the folder cannot be watched (read once): " + ex.Message);
        }
        // STA: the links are read through the shell (as the icons' thread does).
        _thread = new Thread(Work) { IsBackground = true, Name = "recent" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Reads the folder again soon (after a menu's "Clear Menu", say).</summary>
    public void Refresh() => _wake.Set();

    void Work()
    {
        Thread.Sleep(1500); // after the start's busy moment
        while (!_disposed)
        {
            var list = ReadFolder(Folder, _wanted);
            _ = _ui.BeginInvoke(() =>
            {
                Reads++;
                if (Items.SequenceEqual(list)) return;
                Items = list;
                Changed?.Invoke();
            });
            _wake.WaitOne();
            if (_disposed) return;
            // Explorer writes a document's link and then its folder's: one read for the burst.
            Thread.Sleep(500);
            _wake.Reset();
        }
    }

    /// <summary>
    /// The newest links of <paramref name="folder"/> that point at files, at most <paramref name="count"/> (an STA
    /// thread: the links are read through the shell). Public for the E2E runner's probe, which reads a folder of its own.
    /// </summary>
    public static List<RecentDocument> ReadFolder(string folder, int count = RecentItems.Count)
    {
        var list = new List<RecentDocument>();
        try
        {
            var links = new DirectoryInfo(folder).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(count * LookedEach).ToList();
            foreach (var link in links)
            {
                if (list.Count >= count) break;
                // The link's own path (IShellLink: the shell item's System.Link.TargetParsingPath is empty for a link
                // file, 2026-10-16); none for what is not a file (a web address, a shell place).
                if (OpenWith.Resolve(link.FullName)?.path is not { Length: > 0 } target) continue;
                bool local = ShellFiles.IsLocal(target);
                // A folder Explorer opened, or a file that has gone; one on a network share is listed unchecked (asking a
                // share that has gone away can take a long time).
                if (local && !File.Exists(target)) continue;
                string name = local ? ShellFiles.DisplayName(target) : Path.GetFileName(target);
                list.Add(new RecentDocument(name.Length > 0 ? name : Path.GetFileNameWithoutExtension(link.Name), link.FullName, target, link.LastWriteTimeUtc));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            Log.Info("recent documents: the folder cannot be read: " + ex.Message);
        }
        return list;
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        _watcher?.Dispose();
    }
}
