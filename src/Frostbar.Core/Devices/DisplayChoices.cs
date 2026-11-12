namespace Frostbar.Core.Devices;

/// <summary>A mode a display offers: its pixels and refresh rate (the Displays page offers 32-bit colour only).</summary>
public readonly record struct ScreenMode(int Width, int Height, int Hz)
{
    public override string ToString() => $"{Width}x{Height} {Hz} Hz";
}

/// <summary>
/// The scales a display offers (Windows' steps from the smallest to the largest it allows), the one Windows recommends
/// and the one it shows now, in per cent. DisplayConfig counts its steps from the recommended one (S20, 2026-10-16).
/// </summary>
public sealed record ScaleRange(IReadOnlyList<int> Percents, int Recommended, int Now)
{
    /// <summary>The step for <paramref name="percent"/> as DisplayConfig takes it: steps from the recommended one.</summary>
    public int Relative(int percent) => DisplayChoices.ScaleSteps.IndexOf(percent) - DisplayChoices.ScaleSteps.IndexOf(Recommended);
}

/// <summary>What the Displays page offers for a display (stage 4-4), worked out from what Windows lists.</summary>
public static class DisplayChoices
{
    /// <summary>Windows' scale steps (Settings › System › Display › Scale), in per cent.</summary>
    public static readonly IReadOnlyList<int> ScaleSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>The resolutions in <paramref name="modes"/>, each once, the largest first (and the wider of two the same size).</summary>
    public static IReadOnlyList<(int Width, int Height)> Resolutions(IEnumerable<ScreenMode> modes) =>
        modes.Select(m => (m.Width, m.Height)).Distinct().OrderByDescending(r => (long)r.Width * r.Height).ThenByDescending(r => r.Width).ToList();

    /// <summary>The refresh rates <paramref name="width"/> × <paramref name="height"/> is offered at, the fastest first.</summary>
    public static IReadOnlyList<int> Rates(IEnumerable<ScreenMode> modes, int width, int height) =>
        modes.Where(m => m.Width == width && m.Height == height).Select(m => m.Hz).Distinct().OrderByDescending(hz => hz).ToList();

    /// <summary>
    /// The mode for a resolution chosen: at the rate shown now when it has it (a change of resolution does not change the
    /// rate), else at its fastest; null when the display does not offer the resolution.
    /// </summary>
    public static ScreenMode? ForResolution(IEnumerable<ScreenMode> modes, int width, int height, int hzNow)
    {
        var rates = Rates(modes, width, height);
        if (rates.Count == 0) return null;
        return new ScreenMode(width, height, rates.Contains(hzNow) ? hzNow : rates[0]);
    }

    /// <summary>
    /// The scales from DisplayConfig's answer: the steps from the recommended one to the smallest (<paramref name="min"/>,
    /// at most 0), to the one now (<paramref name="now"/>) and to the largest (<paramref name="max"/>), placed on Windows'
    /// steps by the scale shown now (<paramref name="percentNow"/>, from the monitor's DPI). Null when they do not fit
    /// Windows' steps (a custom scale, or an answer not understood): the page then leaves the scale to Windows' settings.
    /// </summary>
    public static ScaleRange? Scales(int percentNow, int min, int now, int max)
    {
        int at = ScaleSteps.IndexOf(percentNow);
        if (at < 0 || min > now || now > max || min > 0 || max < 0) return null;
        int recommended = at - now;
        if (recommended + min < 0 || recommended + max >= ScaleSteps.Count) return null;
        var percents = Enumerable.Range(recommended + min, max - min + 1).Select(i => ScaleSteps[i]).ToList();
        return new ScaleRange(percents, ScaleSteps[recommended], percentNow);
    }

    internal static int IndexOf(this IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return -1;
    }
}

/// <summary>
/// The seconds after a display change in which the user keeps it ("Keep these display settings?"): when no answer
/// comes, the change is put back by itself. Counted on a clock that does not go back (milliseconds, Environment.TickCount64).
/// </summary>
public sealed class RevertCountdown(long startMs, int seconds = RevertCountdown.DefaultSeconds)
{
    public const int DefaultSeconds = 15;
    public enum Outcome { Waiting, Kept, PutBack }

    readonly long _untilMs = startMs + seconds * 1000L;

    public Outcome State { get; private set; }

    /// <summary>The whole seconds left at <paramref name="nowMs"/> as the dialog writes them (rounded up; 0 once answered).</summary>
    public int Left(long nowMs) => State != Outcome.Waiting ? 0 : (int)Math.Max(0, (_untilMs - nowMs + 999) / 1000);

    /// <summary>A look at the time: true when this look finds it up and puts the change back.</summary>
    public bool Tick(long nowMs)
    {
        if (State != Outcome.Waiting || nowMs < _untilMs) return false;
        State = Outcome.PutBack;
        return true;
    }

    /// <summary>The user keeps the change: true when it was still waiting (a late answer does not undo a put-back).</summary>
    public bool Keep()
    {
        if (State != Outcome.Waiting) return false;
        State = Outcome.Kept;
        return true;
    }

    /// <summary>The user puts the change back now: true when it was still waiting.</summary>
    public bool PutBack()
    {
        if (State != Outcome.Waiting) return false;
        State = Outcome.PutBack;
        return true;
    }
}
