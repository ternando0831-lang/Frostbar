using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Frostbar.Core.Dock;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Tray;

/// <summary>A window's taskbar marks as received: <see cref="WindowMarks"/> and its overlay's picture (frozen), if any.</summary>
public sealed record ReceivedMarks(WindowMarks Marks, BitmapSource? Overlay);

/// <summary>
/// The apps' taskbar progress and overlay icons (ITaskbarList3), received in place of Explorer's taskbar (2026-10-15).
/// ITaskbarList sends them to the window named in the first Shell_TrayWnd's "TaskbandHWND" property. While this shell's
/// tray runs, the first is its own tray window (ManagedShell's), which had no such property: they went nowhere, and
/// Explorer's hidden taskbar got none either. Here a message-only window on a thread of its own takes them (each app
/// waits for the answer), keeps them per window, and tells the UI thread. Only this shell's own window gets the
/// property; nothing of Explorer's is changed (ManagedShell's TasksService takes it off Explorer's taskbar for good,
/// which is why it is not used). Spike S15 (Lab taskband) found the messages and their values.
/// </summary>
public sealed class TaskbandListener : IDisposable
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetProp(IntPtr h, string name, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr h, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr h, string name);
    [DllImport("shlwapi.dll", EntryPoint = "#8")] static extern IntPtr SHLockShared(IntPtr h, uint pid);
    [DllImport("shlwapi.dll", EntryPoint = "#9")] static extern bool SHUnlockShared(IntPtr p);

    const string Property = "TaskbandHWND";
    const int WM_USER = 0x400, SetProgressValue = WM_USER + 64, SetProgressState = WM_USER + 65, SetOverlayIcon = WM_USER + 79, SetOverlayDescription = WM_USER + 85;

    readonly Dispatcher _ui;
    readonly ConcurrentDictionary<IntPtr, ReceivedMarks> _marks = new();
    readonly IntPtr _tray;
    IntPtr _window;
    Dispatcher? _loop;
    int _telling;
    long _order;

    /// <summary>Marks changed (UI thread; several changes close together are told once).</summary>
    public event Action? Changed;

    /// <summary>Messages taken, by kind (diagnostics).</summary>
    public int Received { get; private set; }

    TaskbandListener(Dispatcher ui, IntPtr tray)
    {
        _ui = ui;
        _tray = tray;
    }

    /// <summary>
    /// Starts taking the marks: this process's own Shell_TrayWnd (the tray's) is given the property. Null when there is
    /// no such window (the tray off) or the window could not be made.
    /// </summary>
    public static TaskbandListener? Start(Dispatcher ui)
    {
        IntPtr tray = OwnTray();
        if (tray == IntPtr.Zero) { Log.Info("taskband: no tray window of ours: the apps' taskbar progress and badges are not shown"); return null; }
        var listener = new TaskbandListener(ui, tray);
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                // Message-only (HWND_MESSAGE) and never visible: it only answers.
                var source = new HwndSource(new HwndSourceParameters("Frostbar.Taskband") { WindowStyle = 0, ParentWindow = new IntPtr(-3) });
                source.AddHook(listener.Hook);
                listener._window = source.Handle;
                listener._loop = Dispatcher.CurrentDispatcher;
                Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { Log.Error("taskband: unhandled exception", e.Exception); e.Handled = true; };
            }
            catch (Exception ex) { Log.Warn("taskband: the window could not be made", ex); }
            finally { ready.Set(); }
            if (listener._window != IntPtr.Zero) Dispatcher.Run();
        }) { IsBackground = true, Name = "taskband" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(5000);
        if (listener._window == IntPtr.Zero) return null;
        if (GetProp(tray, Property) is var was && was != IntPtr.Zero) Log.Info($"taskband: the tray had {Property} {was}; replaced");
        SetProp(tray, Property, listener._window);
        Log.Info($"taskband: taking the apps' taskbar progress and badges (tray {tray}, window {listener._window})");
        return listener;
    }

    /// <summary>The Shell_TrayWnd of this process (the tray's), or zero.</summary>
    static IntPtr OwnTray()
    {
        uint me = (uint)Environment.ProcessId;
        for (IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null); h != IntPtr.Zero; h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null))
            if (Native.ProcessId(h) == me) return h;
        return IntPtr.Zero;
    }

    /// <summary>The marks a window asked for, or null (UI thread or any).</summary>
    public ReceivedMarks? MarksOf(IntPtr window) => _marks.TryGetValue(window, out var m) ? m : null;

    /// <summary>The windows with marks (diagnostics).</summary>
    public int Windows => _marks.Count;

    /// <summary>Forgets the marks of windows no longer among <paramref name="alive"/> (gone without clearing them).</summary>
    public void Forget(IEnumerable<IntPtr> alive)
    {
        var keep = new HashSet<IntPtr>(alive);
        foreach (var h in _marks.Keys) if (!keep.Contains(h)) _marks.TryRemove(h, out _);
    }

    /// <summary>
    /// Asks <paramref name="windows"/> to set their marks again ("TaskbarButtonCreated", as Explorer does when it makes
    /// a button): marks set before this listener started reach it then.
    /// </summary>
    public void AskAgain(IEnumerable<IntPtr> windows)
    {
        uint created = Native.RegisterWindowMessage("TaskbarButtonCreated");
        int n = 0;
        foreach (var h in windows) if (Native.PostMessage(h, created, IntPtr.Zero, IntPtr.Zero)) n++;
        Log.Debug($"taskband: asked {n} window(s) to set their taskbar marks again");
    }

    // ---- on the listener's thread ----------------------------------------------------------------------------------

    IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg < WM_USER || msg > WM_USER + 100) return IntPtr.Zero;
        handled = true; // every ITaskbarList call is answered at once (the thumbnail buttons and the tabs are not kept)
        if (msg is not (SetProgressValue or SetProgressState or SetOverlayIcon or SetOverlayDescription)) return IntPtr.Zero;
        try
        {
            IntPtr window = wp;
            var old = _marks.TryGetValue(window, out var r) ? r : new ReceivedMarks(default, null);
            var m = old.Marks;
            var overlay = old.Overlay;
            switch (msg)
            {
                case SetProgressValue: m = m with { Value = (int)Math.Clamp(lp.ToInt64(), 0, DockMarks.Full) }; break;
                case SetProgressState:
                    var state = (ProgressState)(int)lp.ToInt64();
                    m = m with { State = state, Value = state == ProgressState.None ? 0 : m.Value };
                    break;
                case SetOverlayIcon:
                    // The app may free the icon once the call returns: its picture is taken now.
                    overlay = lp == IntPtr.Zero ? null : Picture(lp);
                    m = m with { Overlay = overlay != null, Description = overlay != null ? m.Description : null, At = Interlocked.Increment(ref _order) };
                    break;
                case SetOverlayDescription:
                    m = m with { Description = Shared(lp, Native.ProcessId(window)) };
                    break;
            }
            Received++;
            if (m.State == ProgressState.None && !m.Overlay) _marks.TryRemove(window, out _);
            else _marks[window] = new ReceivedMarks(m, overlay);
            Tell();
        }
        catch (Exception ex) { Log.Debug("taskband: a message could not be read: " + ex.Message); }
        return IntPtr.Zero;
    }

    static BitmapSource? Picture(IntPtr icon)
    {
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A string the app put in shared memory (the overlay's description).</summary>
    static string? Shared(IntPtr handle, uint pid)
    {
        if (handle == IntPtr.Zero) return null;
        IntPtr p = SHLockShared(handle, pid);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { SHUnlockShared(p); }
    }

    /// <summary>Tells the UI thread once for changes close together (a progress comes several times a second).</summary>
    void Tell()
    {
        if (Interlocked.Exchange(ref _telling, 1) == 1) return;
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _telling, 0);
            Changed?.Invoke();
        });
    }

    public void Dispose()
    {
        try { if (GetProp(_tray, Property) == _window) RemoveProp(_tray, Property); }
        catch (Exception) { }
        _loop?.InvokeShutdown();
        _marks.Clear();
    }
}
