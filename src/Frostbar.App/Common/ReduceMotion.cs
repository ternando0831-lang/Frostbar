namespace Frostbar.App.Common;

/// <summary>
/// Accessibility's "Reduce motion" (macOS's, 2026-10-22): what moves, grows or bounces fades in place instead (a panel
/// growing out of its item, a piece's glass growing into a view, the springs and the press's bounce, the app grid's
/// and Mission Control's zoom, a banner, a stack or the menu bar sliding in). The fades stay, and so do the moves that
/// say where a thing went (the Dock's magnification and hiding, a window gliding into its tile). Not the animations'
/// off (the appearance's choice), which leaves no motion at all. Set by the shell from the settings.
/// </summary>
public static class ReduceMotion
{
    public static bool On { get; set; }

    /// <summary>Motions turned into fades since the start, by name (diagnostics).</summary>
    public static IReadOnlyDictionary<string, int> Calmed => _calmed;
    static readonly Dictionary<string, int> _calmed = new();

    /// <summary>Whether <paramref name="what"/> should only fade now (counted when it should).</summary>
    public static bool Calms(string what)
    {
        if (!On) return false;
        _calmed[what] = _calmed.GetValueOrDefault(what) + 1;
        return true;
    }
}
