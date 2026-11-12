namespace Frostbar.Core.Time;

/// <summary>
/// A movement's progress on the display's refreshes (the App's DisplaySteps, for Tween and Fades): each tick placed by
/// the refresh it is shown at, the refreshes since the movement began rounded, one more than the last at least.
/// Two movements begun one after the other (GlassBounce's scale across and down) were a refresh apart in 10 of a
/// bounce's 29 frames, and in none in the other bounces (2026-10-18, cc-seams-motion: the circle 68 by 70). The likely
/// way, not measured: the display-paced clock ticks at nearly the same moment of each refresh, so in a movement begun
/// about half a refresh before that moment its ticks round either way, and two begun microseconds apart round apart.
/// What must agree is one movement.
/// </summary>
public sealed class RefreshSteps
{
    /// <summary>The refresh the last tick was placed at (0 before the first).</summary>
    public long Frame { get; private set; }

    /// <summary>0 to 1 over <paramref name="ms"/>, for a tick <paramref name="elapsedMs"/> after the beginning, refreshes <paramref name="periodMs"/> apart.</summary>
    public double Next(double elapsedMs, double periodMs, double ms)
    {
        long frame = (long)Math.Round(elapsedMs / periodMs);
        Frame = frame <= Frame ? Frame + 1 : frame;
        return ms <= 0 ? 1 : Math.Min(1, Frame * periodMs / ms);
    }
}
