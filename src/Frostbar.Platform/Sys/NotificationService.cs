using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Frostbar.Platform.Sys;

/// <summary>A notification in Windows' notification centre: its app (name, AppUserModelID to open it), when, and its text.</summary>
public sealed record NotificationItem(uint Id, string App, string AppId, DateTimeOffset Created, string Title, string Body);

/// <summary>
/// Windows' notifications as the notification centre keeps them (UserNotificationListener): read, dismiss one, clear
/// them all. Windows lets a desktop app read them when "Notification access" allows it (allowed on the test PC). The
/// change event is not given to an app without a package (0x80070490, measured 2026-10-13): the list is read when the
/// shell's notification centre opens and while it is open.
/// </summary>
public static class NotificationService
{
    /// <summary>"Allowed", "Denied", "Unspecified", or why it cannot be asked.</summary>
    public static string Access()
    {
        try { return UserNotificationListener.Current.GetAccessStatus().ToString(); }
        catch (Exception ex) { return "error " + ex.HResult.ToString("X8"); }
    }

    /// <summary>
    /// The toasts in the notification centre, newest first (empty when they cannot be read). Each one's names and words
    /// are asked of Windows' notification platform, another process: off the caller's thread. On the UI thread they
    /// stopped the shell for up to 0.19 s every 2 s while the notification centre was open, and a card being dismissed
    /// was not drawn on its way out (2026-10-17, panels-feel).
    /// </summary>
    public static async Task<IReadOnlyList<NotificationItem>> ReadAsync()
    {
        try
        {
            var all = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast).AsTask().ConfigureAwait(false);
            var list = new List<NotificationItem>(all.Count);
            foreach (var n in all)
            {
                string app = "", appId = "";
                try
                {
                    app = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
                    appId = n.AppInfo?.AppUserModelId ?? "";
                }
                catch (Exception) { } // an app that is gone
                var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
                string title = texts is { Count: > 0 } ? texts[0].Text ?? "" : "";
                string body = texts is { Count: > 1 } ? string.Join("\n", texts.Skip(1).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t))) : "";
                list.Add(new NotificationItem(n.Id, app, appId, n.CreationTime, title, body));
            }
            return list.OrderByDescending(x => x.Created).ToList();
        }
        catch (Exception ex) { Log.Debug("notifications: " + ex.Message); return []; }
    }

    /// <summary>Dismisses one, as its × in Windows' notification centre does.</summary>
    public static bool Remove(uint id)
    {
        try { UserNotificationListener.Current.RemoveNotification(id); return true; }
        catch (Exception ex) { Log.Debug("notifications: remove: " + ex.Message); return false; }
    }

    /// <summary>
    /// Clears them all ("Clear all"), each dismissed by its id: the listener's ClearNotifications is refused to an app
    /// without a package (0x80070490, as its change event is; S34, 2026-10-21), and "Clear All" left every one where it
    /// was. Dismissing by id works for any app's (S34: Claude's, DMMGamePlayer's, PowerShell's, each gone at the next
    /// read). Read and dismissed off the caller's thread: each dismissal asks another process (about 9 ms).
    /// </summary>
    /// <param name="onlyApp">Only this app's (a spec's shell: its own, posted as that app; null: all).</param>
    /// <returns>True when every one read was dismissed.</returns>
    public static Task<bool> ClearAsync(string? onlyApp = null) => Task.Run(async () =>
    {
        try
        {
            var all = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast).AsTask().ConfigureAwait(false);
            var mine = all.Where(n => onlyApp == null || AppIdOf(n) == onlyApp).ToList();
            int done = mine.Count(n => Remove(n.Id));
            Log.Info($"notifications: cleared {done} of {mine.Count}");
            return done == mine.Count;
        }
        catch (Exception ex) { Log.Info("notifications: clear: " + ex.Message); return false; }
    });

    static string AppIdOf(UserNotification n)
    {
        try { return n.AppInfo?.AppUserModelId ?? ""; }
        catch (Exception) { return ""; } // an app that is gone
    }
}

/// <summary>
/// Windows' focus sessions (Windows.UI.Shell.FocusSessionManager, documented since 22H2): a timed session that turns on
/// do not disturb and hides badges until it ends; Windows ends it itself at the time given.
/// </summary>
public static class FocusSessions
{
    public static bool Supported
    {
        get
        {
            try { return Windows.UI.Shell.FocusSessionManager.IsSupported; }
            catch (Exception) { return false; }
        }
    }

    public static bool Active
    {
        get
        {
            try { return Supported && Windows.UI.Shell.FocusSessionManager.GetDefault().IsFocusActive; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Starts one ending after <paramref name="length"/>; false when Windows refused.</summary>
    public static bool Start(TimeSpan length)
    {
        try
        {
            var session = Windows.UI.Shell.FocusSessionManager.GetDefault().TryStartFocusSession(DateTimeOffset.Now + length);
            Log.Info($"focus session for {length.TotalMinutes:0} min: {(session != null ? "started" : "refused")}");
            return session != null;
        }
        catch (Exception ex) { Log.Warn("focus session could not start", ex); return false; }
    }

    public static void Stop()
    {
        try { Windows.UI.Shell.FocusSessionManager.GetDefault().DeactivateFocus(); Log.Info("focus session ended"); }
        catch (Exception ex) { Log.Warn("focus session could not end", ex); }
    }

    /// <summary>
    /// Tells <paramref name="changed"/> when a session starts or ends (Windows' event, on a thread of its own: the
    /// caller goes back to its own). Returns what stops it, or null when Windows has no sessions.
    /// </summary>
    public static Action? Watch(Action changed)
    {
        try
        {
            if (!Supported) return null;
            var manager = Windows.UI.Shell.FocusSessionManager.GetDefault();
            Windows.Foundation.TypedEventHandler<Windows.UI.Shell.FocusSessionManager, object> handler = (_, _) => changed();
            manager.IsFocusActiveChanged += handler;
            return () => { try { manager.IsFocusActiveChanged -= handler; } catch (Exception) { } };
        }
        catch (Exception ex) { Log.Debug("focus sessions: no events: " + ex.Message); return null; }
    }
}

/// <summary>
/// Do not disturb (Windows 11's switch of that name, Focus assist's "priority only" on Windows 10): read and set
/// through the shell's own (undocumented) quiet hours settings, as Windows' quick settings do. "Unrestricted" is off;
/// the other profiles are on. When the object cannot be made, the state is read from the toast mode Windows reports.
/// </summary>
public static class DoNotDisturb
{
    [ComImport, Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IQuietHoursSettings
    {
        [PreserveSig] int get_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] out string profileId);
        [PreserveSig] int put_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profileId);
    }

    static readonly Guid QuietHoursSettings = new("f53321fa-34f8-4b7f-b9a3-361877cb94cf");
    public const string Off = "Microsoft.QuietHoursProfile.Unrestricted", On = "Microsoft.QuietHoursProfile.PriorityOnly";

    static IQuietHoursSettings? Make()
    {
        try { return Type.GetTypeFromCLSID(QuietHoursSettings) is { } t ? Activator.CreateInstance(t) as IQuietHoursSettings : null; }
        catch (Exception ex) { Log.Debug("do not disturb: " + ex.Message); return null; }
    }

    /// <summary>The profile chosen ("…Unrestricted" off), or null when it cannot be read.</summary>
    public static string? Profile()
    {
        var q = Make();
        if (q == null) return null;
        try { return q.get_UserSelectedProfile(out string id) == 0 ? id : null; }
        finally { Marshal.ReleaseComObject(q); }
    }

    /// <summary>On (any profile but unrestricted, or Windows holding toasts back), off, or null when unknown.</summary>
    public static bool? IsOn()
    {
        if (Profile() is { } p) return !string.Equals(p, Off, StringComparison.OrdinalIgnoreCase);
        try { return ToastNotificationManager.GetDefault().NotificationMode != ToastNotificationMode.Unrestricted; }
        catch (Exception) { return null; }
    }

    /// <summary>Turns it on or off; false when Windows refused.</summary>
    public static bool Set(bool on)
    {
        var q = Make();
        if (q == null) return false;
        try
        {
            int hr = q.put_UserSelectedProfile(on ? On : Off);
            Log.Info($"do not disturb {(on ? "on" : "off")}: 0x{hr:X8}");
            return hr == 0;
        }
        catch (Exception ex) { Log.Warn("do not disturb could not be set", ex); return false; }
        finally { Marshal.ReleaseComObject(q); }
    }
}
