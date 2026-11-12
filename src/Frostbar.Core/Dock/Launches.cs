namespace Frostbar.Core.Dock;

/// <summary>What a launch opens: an app by its AppID (the app grid's), a shortcut, a program, or something an app opens.</summary>
public enum LaunchKind { AppsFolder, Shortcut, Program, Document, Folder, Uri }

/// <summary>How a launch found its app's item in the Dock.</summary>
public enum LaunchMatchBy { None, AppId, Program }

/// <summary>
/// What the Dock does when an app is opened from elsewhere (2026-10-23, the user's "A どこから開いても跳ねる" and "macOS
/// と同じく前面に出す"): <see cref="BringForward"/> instead of starting it again (it runs); <see cref="Bounce"/> its item
/// until its first window comes; with <see cref="AddItem"/> an item of its own first (the Dock does not have it), as
/// macOS puts a starting app's icon in the Dock at once. Nothing at all for a document, a folder or an address: some
/// other app opens them (which one is not looked up yet).
/// </summary>
public readonly record struct LaunchDecision(bool Bounce, bool AddItem, bool BringForward)
{
    public static readonly LaunchDecision None = new(false, false, false);
}

public static class LaunchTargets
{
    public const string AppsFolder = @"shell:AppsFolder\";

    public static LaunchKind Kind(string target, bool isDirectory)
    {
        if (target.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase)) return LaunchKind.AppsFolder;
        if (isDirectory) return LaunchKind.Folder;
        // An address (https://, ms-settings:, shell:…): a scheme before a colon that is not a drive's.
        int colon = target.IndexOf(':');
        if (colon > 1 && !target.StartsWith(@"\\", StringComparison.Ordinal)) return LaunchKind.Uri;
        return Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".lnk" => LaunchKind.Shortcut,
            ".exe" or ".com" => LaunchKind.Program,
            ".url" => LaunchKind.Uri,
            _ => LaunchKind.Document,
        };
    }

    /// <summary>The AppID an app grid's target names (<c>shell:AppsFolder\X</c> → X); null for anything else.</summary>
    public static string? AppsFolderId(string target) =>
        target.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase) && target.Length > AppsFolder.Length ? target[AppsFolder.Length..] : null;
}

public static class LaunchMatch
{
    /// <summary>
    /// The Dock's app item for a launch (its index, or -1): the one with its AppID, else the one whose program is the
    /// same file (<paramref name="programOf"/>: a pin's resolved program, a running app's window's): a pin whose AppID
    /// was kept from an older version of its shortcut is still found by its program.
    /// </summary>
    public static (int index, LaunchMatchBy by) Find(IReadOnlyList<DockItem> items, string? appId, string? program, Func<DockItem, string?> programOf)
    {
        if (!string.IsNullOrEmpty(appId))
            for (int i = 0; i < items.Count; i++)
                if (items[i].Kind == DockItemKind.App && string.Equals(items[i].AppId, appId, StringComparison.OrdinalIgnoreCase)) return (i, LaunchMatchBy.AppId);
        if (!string.IsNullOrEmpty(program))
            for (int i = 0; i < items.Count; i++)
                if (items[i].Kind == DockItemKind.App && SameFile(programOf(items[i]), program)) return (i, LaunchMatchBy.Program);
        return (-1, LaunchMatchBy.None);
    }

    public static bool SameFile(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a.Trim().Trim('"')), Path.GetFullPath(b.Trim().Trim('"')), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // not a path (an AppsFolder target, an address)
        }
    }
}

public static class LaunchPlan
{
    /// <summary>
    /// What to do for a launch of <paramref name="kind"/> whose app is <paramref name="match"/> in the Dock (null: not
    /// there). <paramref name="bounce"/>: the Dock bounces a starting app (else it only gets its item).
    /// </summary>
    public static LaunchDecision Decide(DockItem? match, LaunchKind kind, bool bounce = true)
    {
        if (kind is LaunchKind.Document or LaunchKind.Folder or LaunchKind.Uri) return LaunchDecision.None;
        if (match is { Running: true }) return new(false, false, true);
        if (match != null) return new(bounce, false, false);
        return new(bounce, true, false);
    }
}
