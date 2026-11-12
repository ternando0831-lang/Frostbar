namespace Frostbar.Core.Shell;

/// <summary>
/// What the shell under an E2E spec may act on (the runner's FROSTBAR_E2E_ONLY, honoured with --diag only): the apps whose
/// program lies in the test target's folder (the test target, and its copies under other names: menu-profiles). The
/// menus and the shortcuts act on the app in front, whatever it is; a spec's target once lost the foreground to the
/// user's app, whose window was then sent Ctrl+S, minimized and closed (2026-10-16, elevated).
/// </summary>
public static class SpecGuard
{
    /// <summary>The folder as <see cref="Allows"/> compares it (full, no separator at its end); null for none.</summary>
    public static string? Folder(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim())); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>An app the spec's shell may act on: its program is in <paramref name="folder"/> (or below), and it is not the desktop.</summary>
    public static bool Allows(string folder, string? exePath, bool isDesktop) =>
        !isDesktop && !string.IsNullOrEmpty(exePath)
        && exePath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
