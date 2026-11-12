using Frostbar.Core.Shell;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var o = StartupOptions.Parse(args);
        // The icon helper (IconExtractor): no profile, log, single instance or WPF application of its own.
        if (o.Mode == StartMode.Icons) return Frostbar.Platform.Icons.IconExtractor.RunHelper(o.IconRequest!, o.OwnerPid);
        AppPaths.Init(o.Profile);

        // Paths that must not load WPF.
        if (o.Mode == StartMode.Watchdog) return Watchdog.Run(o.OwnerPid, o.OwnerStart);
        if (o.Mode == StartMode.LaunchWatchdog) return Watchdog.Launch(o.OwnerPid, o.OwnerStart, o.WatchdogArgs);

        Log.Init(AppPaths.Logs, "frostbar", LogLevel.Info);
        if (LeavePackageCopy(o, args) is int handedOn) return handedOn;
        using var instance = SingleInstance.Acquire();
        if (!instance.IsPrimary)
        {
            if (o.Replace)
            {
                Log.Info("--replace: asking the running instance to quit");
                SingleInstance.Send(AppPaths.PipeName, "quit");
                if (!instance.WaitForPrimary(TimeSpan.FromSeconds(15))) { Log.Warn("the running instance did not quit"); return 2; }
            }
            else
            {
                var reply = SingleInstance.Send(AppPaths.PipeName, o.Command ?? "activate");
                Log.Info($"handed '{o.Command ?? "activate"}' to the running instance: {reply ?? "no answer"}");
                Log.Flush();
                return reply != null ? 0 : 3;
            }
        }

        // Nothing is running: commands for a running instance have nothing to do, except putting the taskbar back.
        if (o.Mode == StartMode.RestoreTaskbar)
        {
            var store = new StateStore(AppPaths.StateFile);
            Log.Info("--restore-taskbar (no shell running): " + Watchdog.RestoreTaskbar(store));
            string display = Frostbar.Platform.Shell.VolumeDisplay.GiveBack(store);
            if (display.Length > 0) Log.Info(display);
            Log.Flush();
            return 0;
        }
        if (o.Command == "quit") return 0;

        Native.SetCurrentProcessExplicitAppUserModelID(AppPaths.Id);
        var app = new ShellApp(o);
        int code = app.Run();
        Log.Flush();
        return code;
    }

    /// <summary>
    /// Started from inside another app's job (a Claude session), the data folders are that app's copy of AppData and its
    /// updates close the shell, and every app the shell starts with it (2026-10-19, <see cref="PackageCopy"/>): start again
    /// outside it, before taking the single instance. The exit code to leave with, or null to go on here.
    /// </summary>
    static int? LeavePackageCopy(StartupOptions o, string[] args)
    {
        var copy = AppDataCopy.Of(AppPaths.Local, AppPaths.Roaming);
        var step = PackageCopy.Decide(o, copy != null);
        if (o.Outside && copy == null) Log.Info($"Frostbar {AppPaths.Version} ({AppPaths.ExePath}) started again outside another app's job: the data folders are the user's own");
        if (step == PackageCopyStep.None) return null;
        Log.Warn($"settings, state and logs go to the {copy!.Value.Family} package's copy of AppData ({copy.Value.Where}): started inside that app's job, whose updates close this process");
        if (step == PackageCopyStep.Warn) return null;

        string line = CommandLine.Quote(AppPaths.ExePath) + " " + CommandLine.Join(args.Append("--outside"));
        if (OutsideStart.Start(line, Environment.CurrentDirectory, out string why) is not int pid)
        {
            Log.Warn($"could not start again outside it ({why}): going on here");
            return null;
        }
        Native.AllowSetForegroundWindow(pid);
        Log.Info($"started again outside it: pid {pid}");
        if (step == PackageCopyStep.StartOutside) { Log.Flush(); return 0; }
        var (finished, code) = OutsideStart.Wait(pid, TimeSpan.FromMinutes(1));
        Log.Info($"pid {pid} " + (!finished ? "has not finished in a minute" : code is int c ? $"exited with {c}" : "has finished (its exit code was not read)"));
        Log.Flush();
        return !finished ? 4 : code ?? 0;
    }
}
