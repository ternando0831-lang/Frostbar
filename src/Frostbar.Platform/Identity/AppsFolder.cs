using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Identity;

/// <summary>Everything in the Start menu's "All apps" (shell:AppsFolder): desktop and packaged apps, with their names.</summary>
public static class AppsFolder
{
    public sealed record Entry(string Name, string ParsingName);

    /// <summary>Must run on an STA thread. Takes a few hundred ms (M0 S6: 177 entries in 335–765 ms).</summary>
    public static List<Entry> Enumerate()
    {
        var list = new List<Entry>();
        if (ShellApi.SHGetKnownFolderItem(ShellApi.FOLDERID_AppsFolder, 0, IntPtr.Zero, ShellApi.IID_IShellItem, out var folder) != 0) return list;
        try
        {
            if (folder.BindToHandler(IntPtr.Zero, ShellApi.BHID_EnumItems, ShellApi.IID_IEnumShellItems, out IntPtr penum) != 0) return list;
            var e = (IEnumShellItems)Marshal.GetObjectForIUnknown(penum);
            Marshal.Release(penum);
            try
            {
                while (e.Next(1, out var child, out uint got) == 0 && got == 1)
                {
                    try
                    {
                        var name = ShellApi.DisplayName(child, ShellApi.SIGDN_NORMALDISPLAY) ?? "";
                        var parsing = ShellApi.DisplayName(child, ShellApi.SIGDN_PARENTRELATIVEPARSING) ?? "";
                        list.Add(new Entry(name, parsing));
                    }
                    finally { Marshal.ReleaseComObject(child); }
                }
            }
            finally { Marshal.ReleaseComObject(e); }
        }
        catch (Exception ex) { Log.Warn("AppsFolder enumeration failed", ex); }
        finally { Marshal.ReleaseComObject(folder); }
        return list;
    }

    /// <summary>Runs <see cref="Enumerate"/> on its own STA thread.</summary>
    public static Task<List<Entry>> EnumerateAsync()
    {
        var tcs = new TaskCompletionSource<List<Entry>>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(Enumerate()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "appsfolder" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
