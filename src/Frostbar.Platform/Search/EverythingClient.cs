using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Search;

/// <summary>A file or folder a file search found.</summary>
public sealed record FoundItem(string Path, bool IsFolder, DateTime Modified);

/// <summary>
/// Everything (voidtools) as the search's file source: when it runs, a name search over every drive answers in a few
/// milliseconds from its database. Its IPC (everything_ipc.h): WM_COPYDATA carrying a QUERY2 to the window of class
/// EVERYTHING_TASKBAR_NOTIFICATION; the answer comes back as WM_COPYDATA to a window of ours. That window lives on a
/// thread of its own with its own message loop, so a busy UI thread never holds the answer (ported from Bipane).
/// </summary>
public sealed class EverythingClient : IDisposable
{
    const int WM_USER = 0x0400, IPC_IS_DB_LOADED = 401, COPYDATA_QUERY2W = 18;
    const uint REQUEST_FULL_PATH_AND_FILE_NAME = 0x4, REQUEST_DATE_MODIFIED = 0x40;
    const uint SORT_DATE_MODIFIED_DESCENDING = 14;
    const uint ITEM_FOLDER = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);

    sealed class Pending { public readonly ManualResetEventSlim Done = new(); public byte[]? Data; }

    readonly Dictionary<uint, Pending> _pending = new();
    readonly Thread _thread;
    HwndSource? _reply;
    Dispatcher? _dispatcher;
    int _nextId = 0x4D430000; // "MC" and a counter

    public EverythingClient()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _reply = new HwndSource(new HwndSourceParameters("Frostbar.Everything") { WindowStyle = 0, ParentWindow = new IntPtr(-3) /*HWND_MESSAGE*/ });
            _reply.AddHook(Hook);
            _dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "everything replies" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(5000);
    }

    static IntPtr Window()
    {
        var h = Native.FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
        return h != IntPtr.Zero ? h : Native.FindWindow("EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)", null);
    }

    /// <summary>Everything is running (its window is there): cheap, without asking it anything.</summary>
    public static bool Running() => Window() != IntPtr.Zero;

    /// <summary>Everything is running, has loaded its database, and answers us (an elevated Everything does not: UIPI).</summary>
    public static bool Available()
    {
        var h = Window();
        if (h == IntPtr.Zero) return false;
        return Native.SendMessageTimeout(h, WM_USER, new IntPtr(IPC_IS_DB_LOADED), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 500, out var loaded) != IntPtr.Zero && loaded != IntPtr.Zero;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x004A /*WM_COPYDATA*/ || lParam == IntPtr.Zero) return IntPtr.Zero;
        var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
        uint id = unchecked((uint)cds.dwData.ToInt64());
        lock (_pending)
        {
            if (_pending.TryGetValue(id, out var p) && p.Data == null)
            {
                p.Data = new byte[Math.Max(0, cds.cbData)];
                if (p.Data.Length > 0) Marshal.Copy(cds.lpData, p.Data, 0, p.Data.Length);
                p.Done.Set();
                handled = true;
                return new IntPtr(1);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Items whose name contains <paramref name="text"/> (Everything's own search syntax), newest first, without the
    /// places a person does not look for their files (Windows, program folders, app data, the Recycle Bin, build and
    /// package folders). Null when Everything is not there or does not answer within <paramref name="timeoutMs"/>.
    /// Blocks: call it off the UI thread.
    /// </summary>
    public List<FoundItem>? Search(string text, int max, int timeoutMs = 1000)
    {
        var h = Window();
        if (h == IntPtr.Zero || _reply == null) return null;
        string q = text.Replace("\"", "").Trim();
        if (q.Length == 0) return [];
        string search = $"{q} !\\appdata\\ !c:\\windows\\ !c:\\programdata\\ !\"c:\\program files\" !\"c:\\program files (x86)\" !\\$recycle.bin\\ !\"\\system volume information\\\" !\\node_modules\\ !\\.git\\ !\\obj\\ !\\bin\\debug\\ !\\bin\\release\\";
        uint id = unchecked((uint)Interlocked.Increment(ref _nextId));
        var p = new Pending();
        lock (_pending) _pending[id] = p;
        try
        {
            byte[] chars = Encoding.Unicode.GetBytes(search + "\0");
            byte[] body = new byte[28 + chars.Length];
            Put(body, 0, unchecked((uint)_reply.Handle.ToInt64())); // reply_hwnd (a window handle fits in 32 bits)
            Put(body, 4, id);                                       // reply_copydata_message: our id in dwData
            Put(body, 8, 0);                                        // search_flags: no case, no whole word, no path, no regex
            Put(body, 12, 0);                                       // offset
            Put(body, 16, (uint)max);                               // max_results
            Put(body, 20, REQUEST_FULL_PATH_AND_FILE_NAME | REQUEST_DATE_MODIFIED);
            Put(body, 24, SORT_DATE_MODIFIED_DESCENDING);
            Buffer.BlockCopy(chars, 0, body, 28, chars.Length);
            IntPtr mem = Marshal.AllocHGlobal(body.Length);
            try
            {
                Marshal.Copy(body, 0, mem, body.Length);
                var cds = new COPYDATASTRUCT { dwData = new IntPtr(COPYDATA_QUERY2W), cbData = body.Length, lpData = mem };
                if (SendMessageTimeout(h, 0x004A, _reply.Handle, ref cds, Native.SMTO_ABORTIFHUNG, (uint)timeoutMs, out var ok) == IntPtr.Zero || ok == IntPtr.Zero)
                    return null;
            }
            finally { Marshal.FreeHGlobal(mem); }
            if (!p.Done.Wait(timeoutMs)) { Log.Debug("everything: no answer in time"); return null; }
            return Parse(p.Data!);
        }
        finally
        {
            lock (_pending) _pending.Remove(id);
        }
    }

    /// <summary>
    /// EVERYTHING_IPC_LIST2: totitems, numitems, offset, request_flags, sort_type, then numitems × (flags, data_offset);
    /// at data_offset the requested fields in bit order: the full path (DWORD length in characters, the characters, a
    /// null), then the date modified (FILETIME).
    /// </summary>
    static List<FoundItem> Parse(byte[] d)
    {
        var list = new List<FoundItem>();
        if (d.Length < 20) return list;
        uint n = Get(d, 4), flags = Get(d, 12);
        for (uint i = 0; i < n; i++)
        {
            int item = 20 + (int)i * 8;
            if (item + 8 > d.Length) break;
            uint itemFlags = Get(d, item);
            int at = (int)Get(d, item + 4);
            string? path = null;
            if ((flags & REQUEST_FULL_PATH_AND_FILE_NAME) != 0) path = Str(d, ref at);
            DateTime modified = default;
            if ((flags & REQUEST_DATE_MODIFIED) != 0 && at >= 0 && at + 8 <= d.Length)
            {
                long ft = BitConverter.ToInt64(d, at);
                at += 8;
                if (ft > 0) try { modified = DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { }
            }
            if (!string.IsNullOrEmpty(path)) list.Add(new FoundItem(path, (itemFlags & ITEM_FOLDER) != 0, modified));
        }
        return list;
    }

    static string? Str(byte[] d, ref int at)
    {
        if (at < 0 || at + 4 > d.Length) { at = -1; return null; }
        int len = (int)Get(d, at);
        if (len < 0 || at + 4 + (len + 1) * 2 > d.Length) { at = -1; return null; }
        string s = Encoding.Unicode.GetString(d, at + 4, len * 2);
        at += 4 + (len + 1) * 2;
        return s;
    }

    static void Put(byte[] b, int at, uint v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
    static uint Get(byte[] b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

    public void Dispose()
    {
        // Not InvokeShutdown: it waits for the other thread (M2: a waiting shutdown hung the exit).
        try { _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal); } catch (Exception) { }
    }
}
