namespace Frostbar.Core.Shell;

/// <summary>
/// Which banners show under the menu bar at the right, newest on top (stage 2-1: the tray apps' balloons, in the shape
/// of the notification centre's cards, the user's choice B). At most <see cref="MaxShown"/>; each for
/// <see cref="ShowMs"/>, not counting the time the pointer is over them; once it leaves, at least
/// <see cref="AfterLeaveMs"/> more. A new one from an app that already shows one takes its place (an app that repeats
/// a status does not stack up). None while it is quiet (do not disturb, a fullscreen app, the notification centre
/// open, the setting off): they are in the notification centre all the same. Times are milliseconds of any clock.
/// </summary>
public sealed class BannerQueue
{
    public const int MaxShown = 3;
    public const long ShowMs = 5000, AfterLeaveMs = 2000;

    sealed class Slot(Notice notice)
    {
        public Notice Notice { get; set; } = notice;
        /// <summary>When it goes (while not paused).</summary>
        public long DueAt;
        /// <summary>The time it still had when the countdown stopped (while paused).</summary>
        public long Left;
    }

    readonly List<Slot> _shown = new(); // newest first

    public IReadOnlyList<Notice> Shown => _shown.Select(s => s.Notice).ToList();
    /// <summary>The pointer is over the banners: nothing counts down.</summary>
    public bool Paused { get; private set; }

    /// <summary>The next moment one goes (null while paused or with none shown).</summary>
    public long? NextDue => Paused || _shown.Count == 0 ? null : _shown.Min(s => s.DueAt);

    /// <summary>A notice came. Returns whether what shows changed.</summary>
    public bool Add(Notice n, long now, bool quiet)
    {
        if (quiet) return false;
        string key = Key(n);
        _shown.RemoveAll(s => Key(s.Notice) == key);
        var slot = new Slot(n);
        if (Paused) slot.Left = ShowMs; else slot.DueAt = now + ShowMs;
        _shown.Insert(0, slot);
        if (_shown.Count > MaxShown) _shown.RemoveRange(MaxShown, _shown.Count - MaxShown);
        return true;
    }

    /// <summary>The pointer came over the banners.</summary>
    public void Pause(long now)
    {
        if (Paused) return;
        Paused = true;
        foreach (var s in _shown) s.Left = Math.Max(0, s.DueAt - now);
    }

    /// <summary>The pointer left them.</summary>
    public void Resume(long now)
    {
        if (!Paused) return;
        Paused = false;
        foreach (var s in _shown) s.DueAt = now + Math.Max(s.Left, AfterLeaveMs);
    }

    /// <summary>Takes out those whose time is up. Returns whether what shows changed.</summary>
    public bool Expire(long now) => !Paused && _shown.RemoveAll(s => s.DueAt <= now) > 0;

    /// <summary>Its × or a click on it. Returns whether it was shown.</summary>
    public bool Dismiss(uint id) => _shown.RemoveAll(s => s.Notice.Id == id) > 0;

    public bool Clear()
    {
        bool any = _shown.Count > 0;
        _shown.Clear();
        return any;
    }

    /// <summary>The app a notice is from (its id, else its name; neither: a notice of its own).</summary>
    static string Key(Notice n) => n.AppId.Length > 0 ? "id:" + n.AppId : n.App.Length > 0 ? "name:" + n.App : $"#{n.Id}";
}
