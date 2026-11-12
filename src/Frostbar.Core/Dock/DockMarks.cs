namespace Frostbar.Core.Dock;

/// <summary>A taskbar progress's state (ITaskbarList3's TBPFLAG).</summary>
public enum ProgressState { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

/// <summary>
/// What a window asked the taskbar to show (ITaskbarList3): a progress (its state and value, 0–65535 as it comes), and
/// whether it has an overlay icon with its description; <paramref name="At"/> orders the overlays (the latest shows).
/// </summary>
public readonly record struct WindowMarks(ProgressState State, int Value, bool Overlay, string? Description, long At);

/// <summary>
/// What the Dock shows of an app's windows' marks: one progress and one badge. <paramref name="BadgeWindow"/> is the
/// index of the window whose overlay is the badge (-1: none), and <paramref name="Count"/> the number read from its
/// description, drawn as macOS's red badge (null: its picture is shown).
/// </summary>
public readonly record struct AppMarks(ProgressState State, double Progress, int BadgeWindow, string? Count)
{
    public bool Any => State != ProgressState.None || BadgeWindow >= 0;
}

/// <summary>
/// An app's taskbar marks as the Dock shows them (macOS: one progress bar on the icon, one red badge at its corner).
/// Apps report per window; the Dock has one item per app.
/// </summary>
public static class DockMarks
{
    /// <summary>The progress's full value as the taskbar's messages carry it.</summary>
    public const int Full = 65535;

    /// <summary>
    /// One progress (the most telling state: an error, then a pause, then one running, then one with no measure; of
    /// those alike the least done) and one badge (the overlay set last).
    /// </summary>
    public static AppMarks Combine(IReadOnlyList<WindowMarks> windows)
    {
        var state = ProgressState.None;
        double progress = 0;
        int badge = -1;
        long badgeAt = long.MinValue;
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            if (w.State != ProgressState.None)
            {
                double value = Math.Clamp(w.Value / (double)Full, 0, 1);
                int rank = Rank(w.State), was = Rank(state);
                if (rank > was || rank == was && value < progress) { state = w.State; progress = value; }
            }
            if (w.Overlay && w.At >= badgeAt) { badge = i; badgeAt = w.At; }
        }
        if (state == ProgressState.Indeterminate) progress = 0;
        return new AppMarks(state, progress, badge, badge >= 0 ? CountOf(windows[badge].Description) : null);
    }

    static int Rank(ProgressState s) => s switch
    {
        ProgressState.Error => 4, ProgressState.Paused => 3, ProgressState.Normal => 2, ProgressState.Indeterminate => 1, _ => 0,
    };

    /// <summary>
    /// The number an overlay's description gives ("3", "3 unread messages", "未読 12 件"), as the badge shows it (over
    /// 999: "999+"); null when it gives none (the overlay's picture is shown then) or zero.
    /// </summary>
    public static string? CountOf(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        int start = -1;
        for (int i = 0; i < description.Length; i++)
            if (char.IsAsciiDigit(description[i])) { start = i; break; }
        if (start < 0) return null;
        long n = 0;
        for (int i = start; i < description.Length; i++)
        {
            char c = description[i];
            if (char.IsAsciiDigit(c)) { n = Math.Min(n * 10 + (c - '0'), 1_000_000); continue; }
            if (c == ',' && i + 1 < description.Length && char.IsAsciiDigit(description[i + 1])) continue; // "1,234"
            break;
        }
        return n <= 0 ? null : n > 999 ? "999+" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
