using System.Text;
using System.Text.RegularExpressions;

namespace Frostbar.Core.Shell;

public enum PackageCopyStep
{
    /// <summary>The data folders are the user's own.</summary>
    None,
    /// <summary>Say so in the log and go on here.</summary>
    Warn,
    /// <summary>Start again outside the other app, and leave.</summary>
    StartOutside,
    /// <summary>Start again outside the other app, wait for it and pass on its exit code (--quit, --restore-taskbar: the caller waits).</summary>
    StartOutsideAndWait,
}

/// <summary>
/// Whether the shell's data folders are another app's copy of AppData. The Claude desktop app (MSIX) keeps the processes
/// its sessions start in its job, with no package of their own: their new files in %APPDATA% and %LOCALAPPDATA% go to
/// %LOCALAPPDATA%\Packages\Claude_…\LocalCache, and Claude's updates close them. From 2026-10-15 the installed shell was
/// started from Claude sessions (install.ps1, the starts after the tests): its settings, state and logs went to that
/// copy, the user's own stayed as they were on 10-14, and on 10-19 an update closed it, closed the shell its watchdog
/// started again 30 s later, and left the taskbar hidden. What tells is where a new file really goes (its final path):
/// the folders themselves and their old files can still be the user's own.
/// </summary>
public static class PackageCopy
{
    static readonly Regex Copy = new(@"\\Packages\\([^\\]+)\\LocalCache(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The package's family name when a new file in <paramref name="folder"/> went to <paramref name="whereItWent"/> (its
    /// folder's final path) in that package's LocalCache; null when it went to the folder asked for, elsewhere (a junction:
    /// AppData moved to another drive) or when the folder asked for is itself in a LocalCache.
    /// </summary>
    public static string? Family(string folder, string whereItWent)
    {
        string asked = Plain(folder), went = Plain(whereItWent);
        if (string.Equals(asked, went, StringComparison.OrdinalIgnoreCase) || Copy.IsMatch(asked)) return null;
        var m = Copy.Match(went);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// What to do when the data folders are another app's copy. Only a start on the user's own data (no --profile, no
    /// --diag) goes outside: a spec's or a developer's shell stays where it was started, and the watchdog and the icon
    /// helper are where their shell is. Never twice (--outside): if the copy is still there, it is only said. The short
    /// commands are waited for (install.ps1 and uninstall.ps1 wait for --quit and --restore-taskbar, and --restore-taskbar
    /// reads the state); --settings is not, because with no shell running it starts one.
    /// </summary>
    public static PackageCopyStep Decide(StartupOptions o, bool copied)
    {
        if (!copied || o.Mode is StartMode.Watchdog or StartMode.LaunchWatchdog or StartMode.Icons) return PackageCopyStep.None;
        if (o.Outside || o.Diag || o.Profile != null) return PackageCopyStep.Warn;
        return o.Command is "quit" or "restore-taskbar" ? PackageCopyStep.StartOutsideAndWait : PackageCopyStep.StartOutside;
    }

    /// <summary>The path without the \\?\ prefix and the separator at the end.</summary>
    static string Plain(string path)
    {
        string p = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
                 : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        return p.Length > 3 ? p.TrimEnd('\\', '/') : p;
    }
}

/// <summary>Windows command lines, as programs split them again (CommandLineToArgvW, the C runtime).</summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    /// <summary>
    /// One argument: as it is when it has no blank or quote; else in quotes, its quotes escaped and the backslashes
    /// before a quote (or before the closing quote) doubled.
    /// </summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }
}
