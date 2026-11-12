using System.Runtime.InteropServices;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Apps;

/// <summary>Folders as File Explorer shows them, and files copied into one the way it copies them.</summary>
public static class ShellFiles
{
    /// <summary>FOLDERID_Downloads.</summary>
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    /// <summary>The name File Explorer shows ("ダウンロード" for the Downloads folder in Japanese); the file name if it cannot tell.</summary>
    public static string DisplayName(string path)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x200 /*SHGFI_DISPLAYNAME*/) != IntPtr.Zero && !string.IsNullOrEmpty(info.szDisplayName))
            return info.szDisplayName;
        string name = Path.GetFileName(path.TrimEnd('\\'));
        return name.Length > 0 ? name : path;
    }

    /// <summary>
    /// On a disk of this PC, not a network share or a mapped network drive: the Dock looks at its folders on the UI
    /// thread, and a share that has gone away would hold it up.
    /// </summary>
    public static bool IsLocal(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Copies <paramref name="paths"/> into <paramref name="folder"/> with the shell's own progress and "replace?" dialogs
    /// (undoable, as a copy in File Explorer). Null, or what went wrong.
    /// </summary>
    public static string? CopyInto(IReadOnlyList<string> paths, string folder)
    {
        if (paths.Count == 0) return null;
        IntPtr from = Marshal.StringToHGlobalUni(string.Join("\0", paths) + "\0\0");
        IntPtr to = Marshal.StringToHGlobalUni(folder + "\0\0");
        try
        {
            var op = new SHFILEOPSTRUCT { wFunc = 2 /*FO_COPY*/, pFrom = from, pTo = to, fFlags = 0x40 /*ALLOWUNDO*/ };
            int rc = ShellApi.SHFileOperationW(ref op);
            if (op.fAnyOperationsAborted) return "aborted";
            return rc == 0 ? null : $"SHFileOperation 0x{rc:X}";
        }
        finally
        {
            Marshal.FreeHGlobal(from);
            Marshal.FreeHGlobal(to);
        }
    }
}
