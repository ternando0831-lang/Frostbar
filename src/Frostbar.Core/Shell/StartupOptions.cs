namespace Frostbar.Core.Shell;

// In Core (2026-10-15) so its parsing and the relaunch line can be tested: nothing in it needs Windows.
public enum StartMode { Shell, Watchdog, LaunchWatchdog, RestoreTaskbar, Icons }

/// <summary>
/// Command line. Commands for a running instance: --quit, --settings, --restore-taskbar.
/// Development switches: --profile &lt;dir&gt; (separate settings/state/logs), --diag (diagnostics pipe),
/// --hide-taskbar (development builds do not hide the taskbar without it), --no-hide-taskbar, --replace,
/// --no-tray, --real-power (development builds only log power actions without it), --trash-folder &lt;dir&gt; (with --diag:
/// the Dock watches this folder instead of the Recycle Bin, so tests can fill and empty a bin of their own), --welcome
/// (the first start's welcome again).
/// Internal: --watchdog / --launch-watchdog (the watchdog process), --icons &lt;request file&gt; &lt;pid&gt; (the icon helper),
/// --outside (started again outside another app's job and copy of AppData: <see cref="PackageCopy"/>).
/// </summary>
public sealed class StartupOptions
{
    public StartMode Mode { get; private set; } = StartMode.Shell;
    public int OwnerPid { get; private set; }
    public long OwnerStart { get; private set; }
    public string? Profile { get; private set; }
    public bool Diag { get; private set; }
    public bool HideTaskbar { get; private set; }
    public bool NoHideTaskbar { get; private set; }
    public bool Replace { get; private set; }
    public bool Relaunched { get; private set; }
    /// <summary>Development builds only log power actions unless this is given.</summary>
    public bool RealPower { get; private set; }
    /// <summary>Leave tray icons to Windows (overrides the setting).</summary>
    public bool NoTray { get; private set; }
    /// <summary>Tests (with --diag): a folder the Dock's trash watches instead of the real Recycle Bin.</summary>
    public string? TrashFolder { get; private set; }
    /// <summary>Command for an already running instance ("quit", "show-settings", "restore-taskbar").</summary>
    public string? Command { get; private set; }
    /// <summary>--icons &lt;request file&gt; &lt;pid&gt;: make the icons listed there and leave (the shell's icon helper).</summary>
    public string? IconRequest { get; private set; }
    /// <summary>--welcome: the first start's welcome even when it was answered before (tests, screenshots).</summary>
    public bool Welcome { get; private set; }
    /// <summary>--outside: this start was moved out of another app's copy of AppData once already (never again).</summary>
    public bool Outside { get; private set; }

    public static StartupOptions Parse(string[] args)
    {
        var o = new StartupOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--watchdog" when i + 2 < args.Length:
                    o.Mode = StartMode.Watchdog; o.OwnerPid = int.Parse(args[++i]); o.OwnerStart = long.Parse(args[++i]); break;
                case "--launch-watchdog" when i + 2 < args.Length:
                    o.Mode = StartMode.LaunchWatchdog; o.OwnerPid = int.Parse(args[++i]); o.OwnerStart = long.Parse(args[++i]); break;
                case "--restore-taskbar": o.Mode = StartMode.RestoreTaskbar; o.Command = "restore-taskbar"; break;
                case "--icons" when i + 2 < args.Length:
                    o.Mode = StartMode.Icons; o.IconRequest = args[++i]; o.OwnerPid = int.Parse(args[++i]); break;
                case "--profile" when i + 1 < args.Length: o.Profile = args[++i]; break;
                case "--diag": o.Diag = true; break;
                case "--hide-taskbar": o.HideTaskbar = true; break;
                case "--no-hide-taskbar": o.NoHideTaskbar = true; break;
                case "--replace": o.Replace = true; break;
                case "--relaunched": o.Relaunched = true; break;
                case "--real-power": o.RealPower = true; break;
                case "--no-tray": o.NoTray = true; break;
                case "--trash-folder" when i + 1 < args.Length: o.TrashFolder = args[++i]; break;
                case "--quit": o.Command = "quit"; break;
                case "--settings": o.Command = "show-settings"; break;
                case "--welcome": o.Welcome = true; break;
                case "--outside": o.Outside = true; break;
            }
        }
        return o;
    }

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    /// <summary>What the watchdog passes on: the profile only (it must read the same state).</summary>
    public string WatchdogArgs => Profile != null ? $"--profile {Quote(Profile)}" : "";

    /// <summary>
    /// How to start the shell again (after a crash, for a new language). Everything that changes what the shell does
    /// goes along: without --real-power a language change quietly turned the power items into log lines (2026-10-12),
    /// without --trash-folder a spec's shell started again on the user's Recycle Bin.
    /// </summary>
    public string RelaunchArgs => string.Join(' ', new[]
    {
        Profile != null ? $"--profile {Quote(Profile)}" : null,
        Diag ? "--diag" : null,
        HideTaskbar ? "--hide-taskbar" : null,
        NoHideTaskbar ? "--no-hide-taskbar" : null,
        NoTray ? "--no-tray" : null,
        RealPower ? "--real-power" : null,
        TrashFolder != null ? $"--trash-folder {Quote(TrashFolder)}" : null,
    }.Where(s => s != null));
}
