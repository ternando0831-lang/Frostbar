using System.Runtime.InteropServices;
using System.Security.Principal;
using Frostbar.Platform.Infrastructure;
using Microsoft.Win32;

namespace Frostbar.Platform.Sys;

/// <summary>The signed-in user as the settings' sidebar shows them (macOS's Apple Account row at its top).</summary>
/// <param name="Name">The account's full name (the user name when it has none).</param>
/// <param name="Picture">The account picture's file (96 px), or null for none.</param>
/// <param name="Microsoft">Signed in with a Microsoft account (else a local one).</param>
public sealed record Account(string Name, string? Picture, bool Microsoft);

/// <summary>
/// Reads the signed-in user (2026-10-22): the full name from the local account (NetUserGetInfo, level 10), the picture
/// Windows keeps for it (HKLM\…\AccountPicture\Users\&lt;SID&gt;\Image96), and whether a Microsoft account is signed in
/// (HKCU\…\IdentityCRL\UserExtendedProperties has an entry). Each part falls back on its own; nothing throws.
/// </summary>
public static class AccountInfo
{
    public static Account Read()
    {
        string user = Environment.UserName;
        string name = FullName(user) is { Length: > 0 } full ? full : user;
        return new Account(name, Picture(), MicrosoftAccount());
    }

    static string? FullName(string user)
    {
        try
        {
            if (NetUserGetInfo(null, user, 10, out var buffer) != 0) return null;
            try { return Marshal.PtrToStructure<USER_INFO_10>(buffer).full_name?.Trim(); }
            finally { NetApiBufferFree(buffer); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    static string? Picture()
    {
        try
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return null;
            using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
            foreach (var size in new[] { "Image96", "Image192", "Image64", "Image48" })
                if (k?.GetValue(size) is string path && File.Exists(path)) return path;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Debug("account: the picture cannot be read: " + ex.Message);
        }
        return null;
    }

    static bool MicrosoftAccount()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\IdentityCRL\UserExtendedProperties");
            return k != null && k.SubKeyCount > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct USER_INFO_10 { public string? name, comment, usr_comment, full_name; }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);
    [DllImport("netapi32.dll")] static extern int NetApiBufferFree(IntPtr buffer);
}
