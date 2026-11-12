namespace Frostbar.Core.Settings;

/// <summary>
/// Whether the first start's welcome still waits for an answer. The settings file says it: "welcomed" true (answered),
/// false (written by a start that showed it, not answered yet), or absent (a file from before the welcome existed, or
/// written by a spec: counts as answered). 2026-10-12: an unanswered first start that crashed or was killed left
/// welcomed:false, the next start took that file for an old one and hid the taskbar without the user's consent.
/// </summary>
public static class WelcomeRule
{
    /// <param name="fileExisted">The settings file was there at the start.</param>
    /// <param name="welcomed">Its "welcomed" (null: absent).</param>
    /// <param name="forced">--welcome.</param>
    public static bool Pending(bool fileExisted, bool? welcomed, bool forced) => forced || !fileExisted || welcomed == false;
}
