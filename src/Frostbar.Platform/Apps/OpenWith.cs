using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Frostbar.Platform.Apps;

/// <summary>
/// Opening files with a chosen app (files dropped onto a Dock icon): a packaged app through
/// IApplicationActivationManager.ActivateForFile; a program, or a shortcut resolved to its program, started with the
/// files as arguments.
/// </summary>
public static class OpenWith
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);
    [DllImport("shell32.dll")]
    static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out IntPtr array);

    /// <summary>Opens <paramref name="files"/> with the packaged app <paramref name="appId"/>. Returns an error, or null.</summary>
    public static string? Packaged(string appId, IReadOnlyList<string> files)
    {
        var pidls = new List<IntPtr>();
        IntPtr array = IntPtr.Zero;
        try
        {
            foreach (var f in files)
                if (SHParseDisplayName(f, IntPtr.Zero, out var pidl, 0, out _) == 0) pidls.Add(pidl);
            if (pidls.Count == 0) return "no files to open";
            int hr = SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out array);
            if (hr < 0) return $"SHCreateShellItemArrayFromIDLists 0x{hr:X8}";
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            try
            {
                hr = manager.ActivateForFile(appId, array, "open", out _);
                return hr < 0 ? $"ActivateForFile 0x{hr:X8}" : null;
            }
            finally { Marshal.ReleaseComObject(manager); }
        }
        finally
        {
            if (array != IntPtr.Zero) Marshal.Release(array);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>A shortcut's program and arguments (null when it does not point at a program).</summary>
    public static (string path, string args)? Resolve(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnk, 0 /*STGM_READ*/);
            var shell = (IShellLinkW)link;
            var path = new StringBuilder(1024);
            shell.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(4096);
            shell.GetArguments(args, args.Capacity);
            return path.Length == 0 ? null : (path.ToString(), args.ToString());
        }
        catch (Exception) { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    /// <summary>Arguments for files: each quoted.</summary>
    public static string Quote(IEnumerable<string> files) => string.Join(' ', files.Select(f => $"\"{f}\""));
}
