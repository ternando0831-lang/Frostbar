using System.Diagnostics;
using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Identity;

/// <summary>
/// "Which app is this window?" — the same AppID Explorer's taskbar uses (ADR-005), from the undocumented
/// IApplicationResolver (Windows 8+ layout, measured on 26H2 in M0 S9b). If it is missing or fails, a fallback chain:
/// window AUMID → packaged process AUMID → exe path. Display names come from the Start menu (AppsFolder) when known.
/// Call on an STA thread (the UI thread).
/// </summary>
public sealed class AppIdentity : IDisposable
{
    static readonly Guid CLSID_StartMenuCacheAndAppResolver = new("660b90c8-73a9-4b58-8cae-355b7f55341b");
    static readonly Guid IID_IApplicationResolver8 = new("de25675a-72de-44b4-9373-05170450c140");
    IntPtr _resolver;
    bool _resolverFailed;
    readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _parsingNames = new(StringComparer.OrdinalIgnoreCase);
    public bool CatalogLoaded { get; private set; }

    unsafe IntPtr Resolver()
    {
        if (_resolver != IntPtr.Zero || _resolverFailed) return _resolver;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_StartMenuCacheAndAppResolver)!;
            object o = Activator.CreateInstance(type)!;
            IntPtr unk = Marshal.GetIUnknownForObject(o);
            try { Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in IID_IApplicationResolver8, out _resolver)); }
            finally { Marshal.Release(unk); }
        }
        catch (Exception ex)
        {
            _resolverFailed = true;
            Log.Warn("IApplicationResolver unavailable: using the fallback identity chain", ex);
        }
        return _resolver;
    }

    public unsafe string AppIdForWindow(IntPtr hwnd)
    {
        var r = Resolver();
        if (r != IntPtr.Zero)
        {
            var vt = *(IntPtr**)r;
            var forWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, IntPtr, IntPtr, IntPtr, int>)vt[5];
            IntPtr s = IntPtr.Zero;
            if (forWindow(r, hwnd, &s, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0 && s != IntPtr.Zero)
            {
                try { var id = Marshal.PtrToStringUni(s); if (!string.IsNullOrEmpty(id)) return id; }
                finally { ShellApi.CoTaskMemFree(s); }
            }
        }
        return Fallback(hwnd);
    }

    public unsafe string? AppIdForShortcut(string lnkPath)
    {
        var r = Resolver();
        if (r == IntPtr.Zero) return null;
        if (ShellApi.SHCreateItemFromParsingName(lnkPath, IntPtr.Zero, ShellApi.IID_IShellItem, out var item) != 0) return null;
        IntPtr psi = Marshal.GetComInterfaceForObject(item, typeof(IShellItem));
        try
        {
            var vt = *(IntPtr**)r;
            var forShortcut = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vt[3];
            IntPtr s = IntPtr.Zero;
            if (forShortcut(r, psi, &s) != 0 || s == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(s); } finally { ShellApi.CoTaskMemFree(s); }
        }
        finally { Marshal.Release(psi); Marshal.ReleaseComObject(item); }
    }

    static string Fallback(IntPtr hwnd)
    {
        var aumid = ShellApi.WindowAppProperty(hwnd, 5);
        if (!string.IsNullOrEmpty(aumid)) return aumid;
        uint pid = Native.ProcessId(hwnd);
        return Native.ProcessPath(pid) ?? $"pid:{pid}";
    }

    /// <summary>Start menu names keyed by AUMID and by full exe path (loaded once, off the UI thread is fine: own STA).</summary>
    public void LoadCatalog(IEnumerable<(string Name, string ParsingName)> apps)
    {
        foreach (var (name, parsing) in apps)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parsing)) continue;
            _names.TryAdd(parsing, name);
            _parsingNames.Add(parsing);
            if (ExpandKnownFolder(parsing) is { } path) _names.TryAdd(path, name);
        }
        _nameCache.Clear();
        CatalogLoaded = true;
    }

    /// <summary>"{GUID}\relative\app.exe" (how AppsFolder names desktop apps) → full path.</summary>
    public static string? ExpandKnownFolder(string parsing)
    {
        if (parsing.Length < 40 || parsing[0] != '{') return null;
        int end = parsing.IndexOf('}');
        if (end < 0 || !Guid.TryParse(parsing[1..end], out var id)) return null;
        var root = Native.KnownFolderPath(id);
        return root == null ? null : Path.Combine(root, parsing[(end + 1)..].TrimStart('\\'));
    }

    /// <summary>The AppID is a Start menu (AppsFolder) entry: it can be pinned and started by that name.</summary>
    public bool IsCatalogEntry(string appId) => _parsingNames.Contains(appId);

    /// <summary>Where an app's icon comes from: its Start menu entry when the AppID is one, else its executable.</summary>
    public string IconSource(string appId, string? exePath)
    {
        if (_parsingNames.Contains(appId)) return @"shell:AppsFolder\" + appId;
        if (exePath != null) return exePath;
        return appId.Contains('\\') ? appId : @"shell:AppsFolder\" + appId;
    }

    public string DisplayName(string appId, string? exePath)
    {
        if (_nameCache.TryGetValue(appId, out var cached)) return cached;
        string name = _names.TryGetValue(appId, out var n) ? n
            : exePath != null && _names.TryGetValue(exePath, out var byPath) ? byPath
            : FromFile(exePath ?? (appId.Contains('\\') ? appId : null)) ?? appId;
        _nameCache[appId] = name;
        return name;
    }

    static string? FromFile(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(v.FileDescription)) return v.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(v.ProductName)) return v.ProductName.Trim();
        }
        catch (Exception) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    public void Dispose()
    {
        if (_resolver != IntPtr.Zero) Marshal.Release(_resolver);
        _resolver = IntPtr.Zero;
    }
}
