namespace Frostbar.Core.Shell;

/// <summary>Where a program that starts at sign-in comes from: the places Task Manager's "Startup apps" lists (stage 4-1).</summary>
public enum StartupSource
{
    /// <summary>HKCU\…\Run: this user's; its on/off mark is in the user's StartupApproved\Run.</summary>
    UserRun,
    /// <summary>This user's Startup folder; its mark is in the user's StartupApproved\StartupFolder.</summary>
    UserFolder,
    /// <summary>HKLM\…\Run: every user's; its mark is in HKLM, which takes an administrator to change.</summary>
    MachineRun,
    /// <summary>HKLM\…\WOW6432Node\…\Run (32-bit programs); its mark is in HKLM's StartupApproved\Run32.</summary>
    MachineRun32,
    /// <summary>The Startup folder of all users; its mark is in HKLM's StartupApproved\StartupFolder.</summary>
    MachineFolder,
    /// <summary>A packaged app's startup task: Windows' settings change it.</summary>
    Package,
}

/// <summary>
/// One program that starts at sign-in: its source, its value or file name there (the name its mark goes by), the
/// command, the program (for its icon), the title and publisher shown, and whether it is on.
/// </summary>
public sealed record StartupEntry(StartupSource Source, string Name, string Command, string? Program, string Title, string? Publisher, bool On)
{
    /// <summary>Frostbar changes only the current user's: every user's takes an administrator, a packaged app's is Windows' settings'.</summary>
    public bool CanChange => Source is StartupSource.UserRun or StartupSource.UserFolder;

    /// <summary>A key for the page's rows and the diagnostics: the source and the name.</summary>
    public string Key => $"{Source}:{Name}";
}

/// <summary>
/// The on/off marks under Explorer\StartupApproved (Task Manager's and Windows' settings'): 12 bytes, the first even
/// when on (2, 6) and odd when off (1, 3, 7), the last eight the time it was turned off (a FILETIME, UTC).
/// </summary>
public static class StartupApproval
{
    public const int Length = 12;

    /// <summary>On unless marked off; no mark counts as on.</summary>
    public static bool IsOn(byte[]? mark) => mark is not { Length: > 0 } || (mark[0] & 1) == 0;

    /// <summary>The mark Task Manager writes: 2 and zeros when on, 3 and the time when turned off.</summary>
    public static byte[] Mark(bool on, DateTime utcNow)
    {
        var mark = new byte[Length];
        mark[0] = on ? (byte)2 : (byte)3;
        if (!on) BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(mark, 4);
        return mark;
    }

    /// <summary>When a mark says it was turned off (UTC); null when it is on or holds no time.</summary>
    public static DateTime? OffSince(byte[]? mark)
    {
        if (IsOn(mark) || mark!.Length < Length) return null;
        long t = BitConverter.ToInt64(mark, 4);
        return t <= 0 ? null : DateTime.FromFileTimeUtc(t);
    }
}

/// <summary>What a Run value starts, and the order the login items page lists them in.</summary>
public static class StartupItems
{
    /// <summary>
    /// The program of a Run value's command (environment variables already expanded): the quoted path, or else the
    /// text up to the first ".exe" that ends a word (Run values hold unquoted paths with spaces:
    /// <c>C:\Program Files\App\app.exe -min</c>), or else the first word; null for an empty command.
    /// </summary>
    public static string? ProgramPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string c = command.Trim();
        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            string quoted = (end > 0 ? c[1..end] : c[1..]).Trim();
            return quoted.Length > 0 ? quoted : null;
        }
        for (int at = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); at >= 0; at = c.IndexOf(".exe", at + 4, StringComparison.OrdinalIgnoreCase))
        {
            int after = at + 4;
            if (after == c.Length || c[after] is ' ' or '\t' or '"' or ',') return c[..after];
        }
        int space = c.IndexOfAny([' ', '\t']);
        return space < 0 ? c : c[..space];
    }

    /// <summary>A packaged app's startup task is on when enabled (2) or enabled by policy (4) (StartupTaskState).</summary>
    public static bool PackagedOn(int state) => state is 2 or 4;

    /// <summary>
    /// The order of the page: by title, then by source and name. Being on or off does not move a row (a click must
    /// not move the row from under the pointer: 2026-10-13).
    /// </summary>
    public static IReadOnlyList<StartupEntry> Order(IEnumerable<StartupEntry> entries) =>
        entries.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Source).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The page's list: all but Frostbar's own Run value (its switch is the page's first row).</summary>
    public static IReadOnlyList<StartupEntry> Others(IEnumerable<StartupEntry> entries) =>
        Order(entries.Where(e => !(e.Source == StartupSource.UserRun && string.Equals(e.Name, LoginItem.ValueName, StringComparison.OrdinalIgnoreCase))));
}
