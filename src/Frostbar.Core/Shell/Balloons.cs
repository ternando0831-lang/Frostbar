namespace Frostbar.Core.Shell;

/// <summary>
/// The tray apps' balloon notifications (Shell_NotifyIcon with NIF_INFO) for the notification centre. Windows turns
/// them into its own notifications, but the shell's tray takes them first, so Windows never sees them: until
/// 2026-10-15 they were dropped. Their ids have the top bit set (Windows' notification ids are small numbers), from the
/// tray's own number; newest first, at most <see cref="Max"/>. The same app with the same title and text again within
/// <see cref="SameWithinMs"/> replaces the earlier one (apps that repeat a status do not fill the list).
/// </summary>
public sealed class BalloonList
{
    public const int Max = 50;
    public const long SameWithinMs = 60_000;
    const uint Top = 0x8000_0000;

    readonly List<Notice> _items = new();

    public IReadOnlyList<Notice> Items => _items;

    /// <summary>A balloon's id in the notification centre (the tray's number with the top bit), or not a balloon's.</summary>
    public static uint IdOf(int trayNumber) => Top | (uint)(trayNumber & 0x7FFF_FFFF);
    public static bool IsBalloon(uint id) => (id & Top) != 0;
    public static int TrayNumberOf(uint id) => (int)(id & 0x7FFF_FFFF);

    /// <returns>The notice as listed.</returns>
    public Notice Add(int trayNumber, string app, string appId, DateTimeOffset received, string title, string text)
    {
        _items.RemoveAll(n => n.AppId == appId && n.Title == title && n.Body == text
            && Math.Abs((received - n.Created).TotalMilliseconds) <= SameWithinMs);
        var notice = new Notice(IdOf(trayNumber), app, appId, received, title, text);
        _items.Insert(0, notice);
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
        return notice;
    }

    public bool Remove(uint id) => _items.RemoveAll(n => n.Id == id) > 0;

    public void Clear() => _items.Clear();
}
