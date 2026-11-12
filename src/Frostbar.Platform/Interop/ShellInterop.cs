using System.Runtime.InteropServices;

namespace Frostbar.Platform.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

[StructLayout(LayoutKind.Sequential)]
public struct PROPERTYKEY { public Guid fmtid; public uint pid; public PROPERTYKEY(Guid f, uint p) { fmtid = f; pid = p; } }

[StructLayout(LayoutKind.Sequential)]
public struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumShellItems
{
    [PreserveSig] int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumShellItems ppenum);
}

[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SHSTOCKICONINFO
{
    public uint cbSize;
    public IntPtr hIcon;
    public int iSysImageIndex, iIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SHFILEOPSTRUCT
{
    public IntPtr hwnd;
    public uint wFunc;
    public IntPtr pFrom, pTo;
    public ushort fFlags;
    [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
    public IntPtr hNameMappings;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
}

/// <summary>The system image list (only GetIcon is called; the earlier slots keep the vtable order).</summary>
[ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IImageList
{
    [PreserveSig] int Add(IntPtr a, IntPtr b, ref int i);
    [PreserveSig] int ReplaceIcon(int i, IntPtr h, ref int r);
    [PreserveSig] int SetOverlayImage(int a, int b);
    [PreserveSig] int Replace(int i, IntPtr a, IntPtr b);
    [PreserveSig] int AddMasked(IntPtr a, int c, ref int i);
    [PreserveSig] int Draw(IntPtr p);
    [PreserveSig] int Remove(int i);
    [PreserveSig] int GetIcon(int i, int flags, ref IntPtr hicon);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint c);
    [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
    [PreserveSig] int Commit();
}

public static class ShellApi
{
    public const uint SIGDN_NORMALDISPLAY = 0, SIGDN_PARENTRELATIVEPARSING = 0x80018001, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000, SIGDN_FILESYSPATH = 0x80058000;
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    public static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    public static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    public static readonly Guid FOLDERID_AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    public static readonly Guid PKEY_AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll")]
    public static extern int SHCreateItemFromIDList(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderItem([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll")]
    public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ps);
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pv);
    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);
    [DllImport("shell32.dll")] public static extern int SHGetStockIconInfo(int siid, uint flags, ref SHSTOCKICONINFO info);
    [DllImport("shell32.dll", EntryPoint = "#727")] public static extern int SHGetImageList(int list, ref Guid riid, out IntPtr ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);
    public const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOSOUND = 0x4;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);

    public static string? DisplayName(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); } finally { CoTaskMemFree(p); }
    }

    static readonly Guid BHID_PropertyStore = new("0384e1a4-1523-439c-a4c8-ab911052f586");
    static readonly Guid PKEY_Link = new("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"); // System.Link.*: 2 is TargetParsingPath

    /// <summary>
    /// The program a shell item stands for, when it is a link (an AppsFolder entry of a Start menu shortcut: "shell:AppsFolder\…"):
    /// its System.Link.TargetParsingPath; null for anything else (a packaged app, a bad name).
    /// </summary>
    public static string? LinkTarget(string parsingName)
    {
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, IID_IShellItem, out var item) != 0 || item == null) return null;
        try
        {
            if (item.BindToHandler(IntPtr.Zero, BHID_PropertyStore, IID_IPropertyStore, out var p) != 0 || p == IntPtr.Zero) return null;
            var ps = (IPropertyStore)Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p);
            try
            {
                var key = new PROPERTYKEY(PKEY_Link, 2);
                if (ps.GetValue(ref key, out var pv) != 0) return null;
                try { return pv.vt == 31 /*VT_LPWSTR*/ ? Marshal.PtrToStringUni(pv.p) : null; }
                finally { PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(ps); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    /// <summary>The window's AppUserModel property (pid 5 = ID, 2 = relaunch command, 3 = relaunch icon).</summary>
    public static string? WindowAppProperty(IntPtr hwnd, uint pid)
    {
        if (SHGetPropertyStoreForWindow(hwnd, IID_IPropertyStore, out var ps) != 0 || ps == null) return null;
        try
        {
            var key = new PROPERTYKEY(PKEY_AppUserModel, pid);
            if (ps.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == 31 /*VT_LPWSTR*/ ? Marshal.PtrToStringUni(pv.p) : null; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(ps); }
    }
}
