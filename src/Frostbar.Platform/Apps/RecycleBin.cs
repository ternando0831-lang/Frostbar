using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Apps;

/// <summary>
/// The Recycle Bin for the Dock: empty or full, moving files into it, opening it, emptying it. "Full" is read from
/// this user's bin folders (X:\$Recycle.Bin\&lt;SID&gt;: one $I record per item) on fixed drives, and watched there
/// with FileSystemWatcher, so nothing runs while it does not change. It is emptied only when the user has confirmed it
/// in the Dock's menu; tests give it a folder of their own (onlyFolder) and never empty the user's bins.
/// </summary>
public sealed class RecycleBin : IDisposable
{
    public const int StockEmpty = 31, StockFull = 32; // SIID_RECYCLER, SIID_RECYCLERFULL

    readonly List<FileSystemWatcher> _watchers = new();
    readonly DispatcherTimer _settle;
    readonly string? _only;

    public bool Full { get; private set; }
    /// <summary>Diagnostics: how often the bin was read again after a change, and when last (TickCount64).</summary>
    public int Checks { get; private set; }
    public long LastCheck { get; private set; }
    public event Action? Changed;

    /// <param name="onlyFolder">Tests: watch this folder (its $I* files) instead of the user's bins.</param>
    public RecycleBin(Dispatcher ui, string? onlyFolder = null)
    {
        _only = onlyFolder;
        _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _settle!.Stop(); Refresh(); }, ui);
        foreach (var dir in Folders())
        {
            try
            {
                var w = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                void Kick(object? s, FileSystemEventArgs e) => ui.BeginInvoke(() => { if (!_settle.IsEnabled) _settle.Start(); });
                w.Created += Kick;
                w.Deleted += Kick;
                w.Renamed += (s, e) => Kick(s, e);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { Log.Debug($"recycle bin: cannot watch {dir}: {ex.Message}"); }
        }
        Full = Query();
    }

    public void Refresh()
    {
        bool full = Query();
        Checks++;
        LastCheck = Environment.TickCount64;
        if (full == Full) return;
        Full = full;
        Changed?.Invoke();
    }

    IEnumerable<string> Folders()
    {
        if (_only != null)
        {
            if (Directory.Exists(_only)) yield return _only;
            yield break;
        }
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) yield break;
        foreach (var d in DriveInfo.GetDrives())
        {
            // Fixed drives only: removable ones keep no bin, network ones can be slow to answer.
            if (d.DriveType != DriveType.Fixed) continue;
            string dir;
            try { if (!d.IsReady) continue; dir = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid); }
            catch (IOException) { continue; }
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    bool Query()
    {
        foreach (var dir in Folders())
        {
            try { if (Directory.EnumerateFiles(dir, "$I*").Any()) return true; }
            catch (Exception) { }
        }
        return false;
    }

    /// <summary>
    /// Deletes everything in the bin for good. Only once the user has confirmed it in the Dock; in a test folder only
    /// that folder's files go. On a thread of its own: Windows shows its progress while a large bin empties.
    /// <paramref name="done"/> runs on the UI thread: null, or what went wrong.
    /// </summary>
    public void Empty(Action<string?> done, bool sound = true)
    {
        if (_only != null)
        {
            string? error = null;
            foreach (var p in Directory.Exists(_only) ? Directory.GetFileSystemEntries(_only) : [])
            {
                try { if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p); }
                catch (Exception ex) { error ??= ex.Message; }
            }
            if (sound && error == null) Sys.UiSounds.Play(Sys.UiSounds.EmptyTrash);
            Refresh();
            done(error);
            return;
        }
        var ui = Dispatcher.CurrentDispatcher;
        var thread = new Thread(() =>
        {
            // Windows' own sound left out: the shell plays the same one when its sound effects are on (2026-10-22).
            int hr = ShellApi.SHEmptyRecycleBinW(IntPtr.Zero, null, ShellApi.SHERB_NOCONFIRMATION | ShellApi.SHERB_NOSOUND);
            // E_UNEXPECTED: it was empty already
            bool emptied = hr == 0;
            ui.BeginInvoke(() =>
            {
                if (sound && emptied) Sys.UiSounds.Play(Sys.UiSounds.EmptyTrash);
                Refresh();
                done(hr == 0 || hr == unchecked((int)0x8000FFFF) ? null : $"SHEmptyRecycleBin 0x{hr:X8}");
            });
        }) { IsBackground = true, Name = "recycle-empty" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// Moves files and folders to the Recycle Bin (undoable, no confirmation). Names the shell would read as other
    /// items (a trailing dot or space, wildcards) are refused rather than risk the wrong file (Bipane's lesson).
    /// Returns an error message, or null.
    /// </summary>
    public static string? MoveToBin(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        foreach (var p in paths)
        {
            if (p.Length >= 260) return "path too long for the Recycle Bin: " + Path.GetFileName(p);
            string name = Path.GetFileName(p.TrimEnd('\\'));
            if (name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(['*', '?']) >= 0) return "a name the shell reads as another: " + name;
        }
        IntPtr from = Marshal.StringToHGlobalUni(string.Join("\0", paths) + "\0\0");
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = 3, // FO_DELETE
                pFrom = from,
                // WANTNUKEWARNING: what cannot be recycled (a USB stick, a network share, larger than the bin) is asked
                // about before it is deleted for good. Without it NOCONFIRMATION deleted it outright, without a word.
                fFlags = 0x40 | 0x10 | 0x4 | 0x400 | 0x4000, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI | WANTNUKEWARNING
            };
            int rc = ShellApi.SHFileOperationW(ref op);
            if (op.fAnyOperationsAborted) return "aborted";
            return rc == 0 ? null : $"SHFileOperation 0x{rc:X}";
        }
        finally { Marshal.FreeHGlobal(from); }
    }

    public static void Open()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("could not open the Recycle Bin", ex); }
    }

    public void Dispose()
    {
        _settle.Stop();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}

/// <summary>An app pinned to Explorer's taskbar: a shortcut (.lnk path) or a packaged app (its AppID, no shortcut).</summary>
public sealed record TaskbarPin(bool IsShortcut, string Id);

/// <summary>
/// The apps pinned to Explorer's taskbar, for the Dock's first start, in the taskbar's order. Explorer keeps them in
/// HKCU\…\Explorer\Taskband "Favorites": entries of [type byte][size][item ID list], ended by 0xFF; the shell names
/// each item — a shortcut in User Pinned\TaskBar, or an AppsFolder item whose parsing name is a packaged app's AppID
/// (Settings, Store apps: they have no shortcut, 2026-10-12). Undocumented, so anything unexpected falls back to the
/// shortcuts in that folder, oldest first.
/// </summary>
public static class TaskbarPins
{
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    public static List<TaskbarPin> Read()
    {
        try
        {
            if (FromTaskband() is { Count: > 0 } pins) return pins;
        }
        catch (Exception ex) { Log.Debug("taskbar pins: could not read Explorer's list: " + ex.Message); }
        return Shortcuts().Select(p => new TaskbarPin(true, p)).ToList();
    }

    static List<TaskbarPin>? FromTaskband()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
        if (key?.GetValue("Favorites") is not byte[] b) return null;
        var pins = new List<TaskbarPin>();
        int pos = 0;
        while (pos < b.Length && b[pos] != 0xFF)
        {
            if (pos + 5 > b.Length) return null;
            int size = BitConverter.ToInt32(b, pos + 1);
            if (size <= 2 || pos + 5 + size > b.Length) return null;
            string? name = ParsingName(b, pos + 5, size);
            pos += 5 + size;
            if (name == null) continue;
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(name)) pins.Add(new TaskbarPin(true, name));
            else if (name.Contains('!') && !name.Contains('\\')) pins.Add(new TaskbarPin(false, name));
        }
        return pos < b.Length ? pins : null; // no end marker: not the layout we know
    }

    static string? ParsingName(byte[] b, int offset, int size)
    {
        IntPtr pidl = Marshal.AllocCoTaskMem(size + 2);
        try
        {
            Marshal.Copy(b, offset, pidl, size);
            Marshal.WriteInt16(pidl, size, 0); // a terminator, in case the list is not closed
            if (ShellApi.SHCreateItemFromIDList(pidl, ShellApi.IID_IShellItem, out var item) != 0 || item == null) return null;
            try { return ShellApi.DisplayName(item, ShellApi.SIGDN_DESKTOPABSOLUTEPARSING); }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    static List<string> Shortcuts()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.GetFiles(Folder, "*.lnk").OrderBy(File.GetCreationTimeUtc).ToList()
                : new List<string>();
        }
        catch (Exception) { return new List<string>(); }
    }
}
