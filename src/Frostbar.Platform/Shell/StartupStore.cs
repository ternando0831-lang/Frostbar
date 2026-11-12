using System.Diagnostics;
using Frostbar.Core.Shell;
using Frostbar.Platform.Infrastructure;
using Microsoft.Win32;

namespace Frostbar.Platform.Shell;

/// <summary>
/// The programs that start at sign-in, read from where Task Manager's "Startup apps" reads them (stage 4-1): the Run
/// keys of the user and of the machine (64- and 32-bit), the two Startup folders and the packaged apps' startup tasks,
/// each with its StartupApproved mark. Only the current user's are changed, with the mark Task Manager writes; every
/// user's take an administrator (Frostbar does not run as one) and a packaged app's are Windows' settings'.
/// </summary>
public static class StartupStore
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    const string TasksKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    /// <summary>
    /// Where they are read. Windows' places, or a spec's own: everything under one key of the user's (machine entries
    /// included, so a spec reads none of the PC's) and two folders, no packaged apps.
    /// </summary>
    public sealed record Places(RegistryKey User, string UserRun, string UserApproved, RegistryKey Machine, string MachineRun, string MachineRun32,
        string MachineApproved, string UserFolder, string CommonFolder, bool Packages);

    public static Places WindowsPlaces => new(Registry.CurrentUser, RunKey, ApprovedKey, RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        RunKey, Run32Key, ApprovedKey, Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Packages: true);

    /// <summary>The prefix a spec's key must have (under HKCU), and the folder its files must be in: nothing else is taken.</summary>
    public const string SpecKeyPrefix = @"Software\frostbar.e2e\";

    /// <summary>A spec's places (key under HKCU, folder), or null when they are not the spec's own.</summary>
    public static Places? ForSpec(string? key, string? folder)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(folder)) return null;
        key = key.Trim().Trim('\\');
        string mine = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "frostbar-e2e")) + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(folder.Trim()); } catch (Exception) { return null; }
        if (!key.StartsWith(SpecKeyPrefix, StringComparison.OrdinalIgnoreCase) || !full.StartsWith(mine, StringComparison.OrdinalIgnoreCase)) return null;
        return new Places(Registry.CurrentUser, key + @"\Run", key + @"\Approved", Registry.CurrentUser, key + @"\MachineRun", key + @"\MachineRun32",
            key + @"\MachineApproved", Path.Combine(full, "User"), Path.Combine(full, "Common"), Packages: false);
    }

    /// <summary>The places read and changed: Windows', unless a spec gave its own at start.</summary>
    public static Places Current { get; set; } = WindowsPlaces;

    /// <summary>Everything that starts at sign-in, in the page's order (slow: file versions and packages; not on the UI thread).</summary>
    public static IReadOnlyList<StartupEntry> List()
    {
        var p = Current;
        var list = new List<StartupEntry>();
        ReadRun(list, p.User, p.UserRun, p.UserApproved + @"\Run", StartupSource.UserRun);
        ReadFolder(list, p.UserFolder, p.User, p.UserApproved + @"\StartupFolder", StartupSource.UserFolder);
        ReadRun(list, p.Machine, p.MachineRun, p.MachineApproved + @"\Run", StartupSource.MachineRun);
        ReadRun(list, p.Machine, p.MachineRun32, p.MachineApproved + @"\Run32", StartupSource.MachineRun32);
        ReadFolder(list, p.CommonFolder, p.Machine, p.MachineApproved + @"\StartupFolder", StartupSource.MachineFolder);
        if (p.Packages) ReadPackages(list);
        return StartupItems.Order(list);
    }

    /// <summary>
    /// Turns one of the user's on or off: the StartupApproved mark Task Manager writes (the Run value or the file stays).
    /// Returns what went wrong, or null.
    /// </summary>
    public static string? Set(StartupEntry entry, bool on)
    {
        if (!entry.CanChange) return "not the user's own";
        var p = Current;
        try
        {
            using var key = p.User.CreateSubKey(p.UserApproved + (entry.Source == StartupSource.UserRun ? @"\Run" : @"\StartupFolder"));
            key.SetValue(entry.Name, StartupApproval.Mark(on, DateTime.UtcNow), RegistryValueKind.Binary);
            Log.Info($"login items: {entry.Title} ({entry.Source}) {(on ? "on" : "off")}");
            return null;
        }
        catch (Exception ex) { Log.Warn($"login items: could not turn {entry.Title} {(on ? "on" : "off")}", ex); return ex.Message; }
    }

    static void ReadRun(List<StartupEntry> into, RegistryKey hive, string runKey, string approvedKey, StartupSource source)
    {
        try
        {
            using var run = hive.OpenSubKey(runKey);
            if (run == null) return;
            using var approved = hive.OpenSubKey(approvedKey);
            foreach (var name in run.GetValueNames())
            {
                if (name.Length == 0 || run.GetValue(name) is not string command || command.Trim().Length == 0) continue;
                string? program = StartupItems.ProgramPath(Environment.ExpandEnvironmentVariables(command));
                var (title, publisher) = Describe(program, name);
                into.Add(new StartupEntry(source, name, command, program, title, publisher, StartupApproval.IsOn(approved?.GetValue(name) as byte[])));
            }
        }
        catch (Exception ex) { Log.Warn($"login items: could not read {source}", ex); }
    }

    static void ReadFolder(List<StartupEntry> into, string folder, RegistryKey hive, string approvedKey, StartupSource source)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            using var approved = hive.OpenSubKey(approvedKey);
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                // desktop.ini and other hidden files are not started (Task Manager leaves them out too).
                if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                string title = Path.GetFileNameWithoutExtension(file.Name);
                into.Add(new StartupEntry(source, file.Name, file.FullName, file.FullName, title.Length > 0 ? title : file.Name, null,
                    StartupApproval.IsOn(approved?.GetValue(file.Name) as byte[])));
            }
        }
        catch (Exception ex) { Log.Warn($"login items: could not read {source}", ex); }
    }

    /// <summary>The packaged apps' startup tasks (the State Windows keeps for each), named by their package.</summary>
    static void ReadPackages(List<StartupEntry> into)
    {
        try
        {
            using var apps = Registry.CurrentUser.OpenSubKey(TasksKey);
            if (apps == null) return;
            global::Windows.Management.Deployment.PackageManager? packages = null;
            foreach (var family in apps.GetSubKeyNames())
            {
                using var app = apps.OpenSubKey(family);
                if (app == null) continue;
                foreach (var task in app.GetSubKeyNames())
                {
                    using var t = app.OpenSubKey(task);
                    if (t?.GetValue("State") is not int state) continue;
                    string title = family, program = "";
                    try
                    {
                        packages ??= new global::Windows.Management.Deployment.PackageManager();
                        var package = packages.FindPackagesForUser(string.Empty, family).FirstOrDefault();
                        if (package != null)
                        {
                            title = package.DisplayName is { Length: > 0 } d ? d : family;
                            var aumid = package.GetAppListEntries().FirstOrDefault()?.AppUserModelId;
                            if (!string.IsNullOrEmpty(aumid)) program = @"shell:AppsFolder\" + aumid;
                        }
                    }
                    catch (Exception) { } // the family's name stays
                    into.Add(new StartupEntry(StartupSource.Package, family + "!" + task, "", program.Length > 0 ? program : null, title, null, StartupItems.PackagedOn(state)));
                }
            }
        }
        catch (Exception ex) { Log.Warn("login items: could not read the packaged apps' startup tasks", ex); }
    }

    /// <summary>What Task Manager shows: the program's description (or product name) and publisher, else the value's name.</summary>
    static (string title, string? publisher) Describe(string? program, string name)
    {
        try
        {
            if (program != null && File.Exists(program))
            {
                var v = FileVersionInfo.GetVersionInfo(program);
                string? title = !string.IsNullOrWhiteSpace(v.FileDescription) ? v.FileDescription.Trim()
                    : !string.IsNullOrWhiteSpace(v.ProductName) ? v.ProductName.Trim() : null;
                string? company = string.IsNullOrWhiteSpace(v.CompanyName) ? null : v.CompanyName.Trim();
                return (title ?? name, company);
            }
        }
        catch (Exception) { }
        return (name, null);
    }
}
