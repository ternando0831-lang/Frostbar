namespace Frostbar.Core.Shell;

/// <summary>
/// macOS's "Play feedback when volume is changed" (2026-10-22): whether a change of the volume plays the feedback. A
/// volume key's change (the volume moves within <see cref="KeyWindowMs"/> of the key) or the Control Center's slider let
/// go plays it, at the new volume: not while the output is muted, nor for a change of another cause (an app, the mixer),
/// and at most once in <see cref="GapMs"/> (a held key repeats).
/// </summary>
public static class VolumeFeedback
{
    public const long KeyWindowMs = 1000, GapMs = 80;

    /// <param name="on">The setting.</param>
    /// <param name="muted">The output is muted now.</param>
    /// <param name="fromKey">The volume moved (true: a key may have moved it), or the slider was let go (false).</param>
    /// <param name="sinceKeyMs">Since a volume key was last pressed.</param>
    /// <param name="sinceLastMs">Since the feedback last played.</param>
    public static bool Plays(bool on, bool muted, bool fromKey, long sinceKeyMs, long sinceLastMs) =>
        on && !muted && (!fromKey || sinceKeyMs <= KeyWindowMs) && sinceLastMs >= GapMs;
}
