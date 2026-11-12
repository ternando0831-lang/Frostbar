using System.Security.Principal;
using Frostbar.Core.Shell;
using Frostbar.Platform.Infrastructure;
using Microsoft.Win32;

namespace Frostbar.Platform.Shell;

/// <summary>
/// Reads and writes Frostbar's sign-in start (<see cref="LoginItem"/>): its Task Scheduler task, and the Run value older
/// versions wrote. Task Scheduler is a service, and the first read in a process took 46 to 73 ms (Lab logintask,
/// 2026-10-21; 0.4 ms after), registering 9 to 38 ms: call these off the UI thread.
/// </summary>
public static class LoginItemStore
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const int CreateOrUpdate = 6, InteractiveToken = 3, ExecAction = 0; // TASK_CREATE_OR_UPDATE, TASK_LOGON_INTERACTIVE_TOKEN, TASK_ACTION_EXEC
    const int NotFound = unchecked((int)0x80070002); // ERROR_FILE_NOT_FOUND: no such task

    /// <summary>On, and starting this program (a task or a Run value for another copy counts as off).</summary>
    public static bool IsOn(string exePath)
    {
        try
        {
            var (run, approved) = ReadRun();
            return LoginItem.IsOn(ReadTask(LoginItem.TaskName), run, approved, exePath);
        }
        catch (Exception ex) { Log.Warn("login item: could not read it", ex); return false; }
    }

    /// <summary>
    /// Turns it on (the task for <paramref name="exePath"/>, described by <paramref name="description"/> in Task
    /// Scheduler) or off (the task removed); either way an old Run value goes. Returns what went wrong, or null.
    /// </summary>
    public static string? Set(bool on, string exePath, string description)
    {
        try
        {
            if (on) Register(LoginItem.TaskName, LoginItem.TaskXml(exePath, UserSid(), description));
            else Delete(LoginItem.TaskName);
            DropRun();
            Log.Info($"login item: {(on ? "on" : "off")}");
            return null;
        }
        catch (Exception ex) { Log.Warn("login item: could not change it", ex); return ex.Message; }
    }

    /// <summary>
    /// At start: a Run value an older version wrote is moved to the task, or dropped when the task is already there
    /// (<see cref="LoginItem.Next"/>). If the Run value cannot go, the new task goes again (two starts at sign-in would
    /// open the settings).
    /// </summary>
    public static void MoveFromRun(string exePath, string description)
    {
        try
        {
            var (run, approved) = ReadRun();
            switch (LoginItem.Next(ReadTask(LoginItem.TaskName), run, approved, exePath))
            {
                case LoginItemMove.ToTask:
                    Register(LoginItem.TaskName, LoginItem.TaskXml(exePath, UserSid(), description));
                    try { DropRun(); }
                    catch { Delete(LoginItem.TaskName); throw; }
                    Log.Info("login item: moved from the Run key to a Task Scheduler task (started at sign-in, not after the other startup apps)");
                    break;
                case LoginItemMove.DropRun:
                    DropRun();
                    Log.Info("login item: the Run value dropped (the task starts Frostbar)");
                    break;
            }
        }
        catch (Exception ex) { Log.Warn("login item: could not move it from the Run key", ex); }
    }

    /// <summary>A task in the library's root: what it starts and whether it is enabled; null when there is none.</summary>
    public static LoginTask? ReadTask(string name)
    {
        dynamic task;
        try { task = Root().GetTask(name); }
        catch (Exception ex) when (ex.HResult == NotFound) { return null; }
        dynamic actions = task.Definition.Actions;
        string? command = null;
        for (int i = 1; i <= (int)actions.Count && command == null; i++)
        {
            dynamic a = actions.Item(i);
            if ((int)a.Type == ExecAction) command = (string)a.Path;
        }
        return new LoginTask(command, (bool)task.Enabled);
    }

    /// <summary>Registers (or replaces) a task in the library's root from its XML, run as the signed-in user.</summary>
    public static void Register(string name, string xml) =>
        Root().RegisterTask(name, xml, CreateOrUpdate, null, null, InteractiveToken);

    /// <summary>Removes a task from the library's root; none is fine.</summary>
    public static void Delete(string name)
    {
        try { Root().DeleteTask(name, 0); }
        catch (Exception ex) when (ex.HResult == NotFound) { }
    }

    /// <summary>The current user's SID, which the task's trigger and principal name.</summary>
    public static string UserSid() => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("no user SID");

    static dynamic Root()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("no Task Scheduler");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder(@"\");
    }

    static (string? run, byte[]? approved) ReadRun()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return (run?.GetValue(LoginItem.ValueName) as string, approved?.GetValue(LoginItem.ValueName) as byte[]);
    }

    // The old Run value and Task Manager's mark for it (Frostbar's own names only).
    static void DropRun()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(LoginItem.ValueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(LoginItem.ValueName, throwOnMissingValue: false);
    }
}
