namespace Frostbar.Core.Shell;

/// <summary>What lives in the island while it goes on: something playing, or an app's progress (its taskbar progress).</summary>
public enum IslandKind { Playing, Progress }

/// <summary>
/// One live activity: <paramref name="Key"/> the playing app's id or the progressing app's key, <paramref name="Progress"/>
/// 0–1 (a progress; -1 when it does not know how far).
/// </summary>
public sealed record IslandActivity(IslandKind Kind, string Key, double Progress = 0);

/// <summary>A moment the island shows for a while: headphones came, the output changed, Focus turned, the battery runs low.</summary>
public enum NoticeKind { Headphones, Output, Focus, Battery }

/// <summary>
/// A notice: <paramref name="Title"/> and <paramref name="Detail"/> as shown, <paramref name="Percent"/> a level beside
/// them (a headset's battery, the battery's), <paramref name="On"/> Focus turned on (off: false).
/// </summary>
public sealed record IslandNotice(NoticeKind Kind, string Title, string Detail = "", int? Percent = null, bool On = true);

/// <summary>The island's shapes: a small pill, a band with an activity at each end, a notice, opened.</summary>
public enum IslandShape { Idle, Compact, Notice, Open }

/// <summary>What the island shows now: its shape, the activity in it, a second one in a small circle beside it, a notice.</summary>
public sealed record IslandView(IslandShape Shape, IslandActivity? Main, IslandActivity? Second, IslandNotice? Notice);

/// <summary>
/// The Dynamic Island's choices (2026-11-08, the user's "A いつも上中央にある" and "Aでさらに本物に寄せて", with what is
/// played, headphones and the output, Focus and the battery, and apps' progress; ADR-017). macOS 27 has it only on the
/// OLED MacBook Pro, around its camera: Live Activities in it, a tap or a click opens one; its system views are
/// AirPods connecting, Focus, noise control, low battery and the output switching. Here it always stands at the top's
/// middle: a small black pill with nothing going on; with something playing or progressing, a band holding it at its
/// two ends (what plays first; a second one in a small circle beside it, as the iPhone's two activities); a notice for
/// <see cref="NoticeMs"/>, over the band; opened by a click while there is an activity, until it is closed.
/// </summary>
public sealed class IslandModel
{
    /// <summary>How long a notice shows (the iPhone's are about two to three seconds; not measured on a Mac).</summary>
    public const double NoticeMs = 2600;

    IslandActivity? _playing;
    List<IslandActivity> _progress = [];
    IslandNotice? _notice;
    double _noticeUntil = double.NegativeInfinity;
    bool _open;

    /// <summary>Something plays (<paramref name="appId"/>), or nothing does (null; paused counts as nothing).</summary>
    public void SetPlaying(string? appId) => _playing = appId == null ? null : new IslandActivity(IslandKind.Playing, appId);

    /// <summary>The apps progressing now, the latest first.</summary>
    public void SetProgress(IEnumerable<IslandActivity> progress) => _progress = progress.Where(p => p.Kind == IslandKind.Progress).ToList();

    /// <summary>A notice from <paramref name="nowMs"/> on, in place of one showing. While the island is open it is let go (the user is reading what is open).</summary>
    public void Notify(IslandNotice notice, double nowMs)
    {
        if (_open) return;
        _notice = notice;
        _noticeUntil = nowMs + NoticeMs;
    }

    /// <summary>Opens the island on its activity (a click). False with nothing going on (the pill does nothing, as Apple's).</summary>
    public bool Open(double nowMs)
    {
        if (Main() == null) return false;
        _open = true;
        _notice = null; // a click on a notice opens what goes on under it
        _noticeUntil = double.NegativeInfinity;
        return true;
    }

    public void Close() => _open = false;

    IslandActivity? Main() => _playing ?? _progress.FirstOrDefault();

    IslandActivity? Second()
    {
        if (_playing != null) return _progress.FirstOrDefault();
        return _progress.Count > 1 ? _progress[1] : null;
    }

    /// <summary>What shows at <paramref name="nowMs"/>. Opened with its activity gone, it closes.</summary>
    public IslandView At(double nowMs)
    {
        var main = Main();
        if (_open && main == null) _open = false;
        if (_open) return new IslandView(IslandShape.Open, main, null, null);
        if (_notice != null && nowMs < _noticeUntil) return new IslandView(IslandShape.Notice, main, Second(), _notice);
        _notice = null;
        return main == null ? new IslandView(IslandShape.Idle, null, null, null) : new IslandView(IslandShape.Compact, main, Second(), null);
    }

    /// <summary>When what shows changes by itself (a notice ending), or null.</summary>
    public double? NextChange(double nowMs) => _notice != null && !_open && nowMs < _noticeUntil ? _noticeUntil : null;
}

/// <summary>
/// When the battery's notice comes: as it falls past <see cref="Levels"/> on the battery (macOS warns about 20% and
/// 10%), once each way down; not on the charger, nor at the first reading.
/// </summary>
public static class LowBattery
{
    public static readonly int[] Levels = [20, 10];

    /// <summary>The level crossed going from <paramref name="before"/> to <paramref name="now"/> percent, or null.</summary>
    public static int? Crossed(int? before, int now, bool onBattery)
    {
        if (before is not int b || !onBattery || now >= b) return null;
        foreach (int level in Levels.OrderBy(l => l)) if (now <= level && b > level) return level;
        return null;
    }
}

/// <summary>
/// The output's notice: when the default output changes to another device (not at the first reading), headphones as
/// macOS's AirPods (their name, connected), anything else as the output switching.
/// </summary>
public static class OutputNotice
{
    /// <summary>The notice's kind when the output went from <paramref name="before"/> to <paramref name="now"/> (names; null before the first reading), or null.</summary>
    public static NoticeKind? For(string? before, string now, bool headphones)
    {
        if (before == null || string.IsNullOrEmpty(now) || string.Equals(before, now, StringComparison.Ordinal)) return null;
        return headphones ? NoticeKind.Headphones : NoticeKind.Output;
    }
}
