using System.Xml.Linq;

namespace Frostbar.Core.Shell;

/// <summary>
/// Starting Frostbar when the user signs in: a Task Scheduler task (<see cref="TaskName"/>, at the library's root) with a
/// logon trigger for this user. Until 2026-10-21 it was a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run,
/// and Explorer starts those one after another: behind Edge's update (17 s), Discord and Google Drive (24 s), Frostbar
/// came 60 s after the sign-in, and the user started it by hand, taking it for not started (Shell-Core 9707, 2026-10-20).
/// A Run value an older version left is moved to the task (<see cref="Next"/>); Task Manager's "Startup apps" lists
/// only the Run value, which can be turned off there without removing it (a mark under Explorer\StartupApproved\Run).
/// </summary>
public static class LoginItem
{
    /// <summary>The old Run value's name (the internal ID, kept if the app is renamed).</summary>
    public const string ValueName = "frostbar";

    /// <summary>The task's name: the same ID.</summary>
    public const string TaskName = ValueName;

    /// <summary>
    /// On when the Run value is there and Task Manager has not turned it off: StartupApproved's first byte is even when
    /// on (2 or 6) and odd when off (3 or 7, followed by the time it was turned off); no mark counts as on.
    /// </summary>
    public static bool IsOn(string? runValue, byte[]? approved) =>
        !string.IsNullOrWhiteSpace(runValue) && StartupApproval.IsOn(approved);

    /// <summary>
    /// On: the task that starts this program is enabled; with no task for it, a Run value starting it that Task Manager
    /// left on (not moved yet).
    /// </summary>
    public static bool IsOn(LoginTask? task, string? runValue, byte[]? approved, string exePath) =>
        task != null && PointsAt(task.Command, exePath) ? task.Enabled : IsOn(runValue, approved) && PointsAt(runValue, exePath);

    /// <summary>
    /// What to do at start with a Run value for this program: move it to the task when it is on and no task starts this
    /// program; drop it when the task does (both would start Frostbar twice, and the second start opens the settings);
    /// otherwise leave it (turned off in Task Manager, or another copy's).
    /// </summary>
    public static LoginItemMove Next(LoginTask? task, string? runValue, byte[]? approved, string exePath)
    {
        if (!PointsAt(runValue, exePath)) return LoginItemMove.None;
        if (task != null && PointsAt(task.Command, exePath)) return LoginItemMove.DropRun;
        return IsOn(runValue, approved) ? LoginItemMove.ToTask : LoginItemMove.None;
    }

    /// <summary>The Run value older versions wrote for <paramref name="exePath"/>: the path in quotes (it may hold spaces).</summary>
    public static string Command(string exePath) => $"\"{exePath}\"";

    /// <summary>Whether a Run value or the task's command starts this very program (not an older copy elsewhere).</summary>
    public static bool PointsAt(string? command, string exePath) =>
        command != null && string.Equals(command.Trim().Trim('"'), exePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The task's definition (Task Scheduler's XML): at the sign-in of the user <paramref name="userSid"/>, start
    /// <paramref name="exePath"/> as them, not elevated, and without what a new task has by default that would stop or
    /// slow Frostbar: starting only on AC power and stopped on battery (a laptop), stopped after 3 days, and priority 7
    /// (below normal, with low I/O and memory priority; 4 is normal). Started again while it runs, nothing happens.
    /// </summary>
    public static string TaskXml(string exePath, string userSid, string description)
    {
        XNamespace t = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = new XElement(t + "Task", new XAttribute("version", "1.4"),
            new XElement(t + "RegistrationInfo", new XElement(t + "Description", description)),
            new XElement(t + "Triggers",
                new XElement(t + "LogonTrigger", new XElement(t + "Enabled", "true"), new XElement(t + "UserId", userSid))),
            new XElement(t + "Principals",
                new XElement(t + "Principal", new XAttribute("id", "Author"), new XElement(t + "UserId", userSid),
                    new XElement(t + "LogonType", "InteractiveToken"), new XElement(t + "RunLevel", "LeastPrivilege"))),
            new XElement(t + "Settings",
                new XElement(t + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(t + "DisallowStartIfOnBatteries", "false"),
                new XElement(t + "StopIfGoingOnBatteries", "false"),
                new XElement(t + "ExecutionTimeLimit", "PT0S"),
                new XElement(t + "Priority", "4"),
                new XElement(t + "Enabled", "true")),
            new XElement(t + "Actions", new XAttribute("Context", "Author"),
                new XElement(t + "Exec", new XElement(t + "Command", exePath),
                    new XElement(t + "WorkingDirectory", Path.GetDirectoryName(exePath) ?? ""))));
        return task.ToString(SaveOptions.DisableFormatting);
    }
}

/// <summary>The sign-in task as read: what it starts (its first program action) and whether it is enabled.</summary>
public sealed record LoginTask(string? Command, bool Enabled);

/// <summary>What <see cref="LoginItem.Next"/> says to do with an old Run value.</summary>
public enum LoginItemMove { None, ToTask, DropRun }
