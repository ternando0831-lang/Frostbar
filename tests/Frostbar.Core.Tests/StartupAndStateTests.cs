using System.Text;
using System.Text.Json;
using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

/// <summary>
/// The command line the shell starts again with, and the shape of state.json: the watchdog, the next start and the
/// emergency script (tools\emergency\restore-taskbar.ps1, which reads taskbar.abmState) all depend on it.
/// </summary>
public sealed class StartupAndStateTests
{
    /// <summary>Splits a command line as Windows does for these arguments (spaces, double-quoted paths; no escapes).</summary>
    static string[] Split(string line)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (c == ' ' && !quoted)
            {
                if (any) args.Add(cur.ToString());
                cur.Clear();
                any = false;
                continue;
            }
            cur.Append(c);
            any = true;
        }
        if (any) args.Add(cur.ToString());
        return args.ToArray();
    }

    /// <summary>
    /// Everything that changes what the shell does goes along when it starts again: without --real-power a language
    /// change quietly turned the power items into log lines (2026-10-12), without --trash-folder a spec's shell started
    /// again on the user's Recycle Bin. Commands for a running instance and the one-time welcome do not.
    /// </summary>
    [Fact]
    public void The_relaunch_line_keeps_what_changes_the_shell()
    {
        var o = StartupOptions.Parse([
            "--profile", @"C:\Temp\macos e2e", "--diag", "--hide-taskbar", "--no-tray", "--real-power",
            "--trash-folder", @"C:\Temp\a bin", "--settings", "--welcome", "--replace", "--relaunched",
        ]);
        var again = StartupOptions.Parse(Split(o.RelaunchArgs));
        Assert.Equal(@"C:\Temp\macos e2e", again.Profile);
        Assert.True(again.Diag);
        Assert.True(again.HideTaskbar);
        Assert.True(again.NoTray);
        Assert.True(again.RealPower);
        Assert.Equal(@"C:\Temp\a bin", again.TrashFolder);
        Assert.Null(again.Command);
        Assert.False(again.Welcome);
        Assert.False(again.Replace);
        Assert.Equal(StartMode.Shell, again.Mode);
    }

    [Fact]
    public void Not_hiding_the_taskbar_goes_along_too()
    {
        var again = StartupOptions.Parse(Split(StartupOptions.Parse(["--no-hide-taskbar", "--quit"]).RelaunchArgs));
        Assert.True(again.NoHideTaskbar);
        Assert.False(again.HideTaskbar);
        Assert.Null(again.Command);
    }

    [Fact]
    public void The_watchdog_gets_the_profile_only()
    {
        var o = StartupOptions.Parse(["--profile", @"C:\p q", "--diag", "--hide-taskbar"]);
        Assert.Equal(@"C:\p q", StartupOptions.Parse(Split(o.WatchdogArgs)).Profile);
        Assert.DoesNotContain("--diag", o.WatchdogArgs);
        var dog = StartupOptions.Parse(["--watchdog", "123", "456", "--profile", @"C:\p q"]);
        Assert.Equal(StartMode.Watchdog, dog.Mode);
        Assert.Equal(123, dog.OwnerPid);
        Assert.Equal(456, dog.OwnerStart);
    }

    static ShellState Sample() => new()
    {
        Owner = new OwnerInfo { Pid = 4242, StartTimeUtcTicks = 638000000000000000, HeartbeatHwnd = 0x1A2B, Args = "--diag" },
        Taskbar = new TaskbarSavedState
        {
            HiddenByUs = true, Strategy = "hide", AbmState = 2, SavedAt = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(9)),
            Windows = [new TaskbarWindowState { Class = "Shell_TrayWnd", Visible = true, ExStyle = 0x88 }],
        },
        AppBars = [0x10, 0x20],
        CleanExit = false,
        VolumeDisplay = new VolumeDisplayState { Hwnd = 0xBD03BA, ExplorerPid = 9876, Hidden = true },
        EndedBy = ShellState.ClosedForAnotherProgram,
    };

    /// <summary>The names and kinds the other readers expect (camelCase; the emergency script reads taskbar.abmState).</summary>
    [Fact]
    public void State_json_has_the_names_its_readers_expect()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Sample(), ShellStateJson.Default.ShellState));
        var root = doc.RootElement;
        var taskbar = root.GetProperty("taskbar");
        Assert.Equal(2, taskbar.GetProperty("abmState").GetInt32());
        Assert.True(taskbar.GetProperty("hiddenByUs").GetBoolean());
        Assert.Equal("hide", taskbar.GetProperty("strategy").GetString());
        var window = taskbar.GetProperty("windows")[0];
        Assert.Equal("Shell_TrayWnd", window.GetProperty("class").GetString());
        Assert.True(window.GetProperty("visible").GetBoolean());
        Assert.Equal(0x88, window.GetProperty("exStyle").GetInt64());
        var owner = root.GetProperty("owner");
        Assert.Equal(4242, owner.GetProperty("pid").GetInt32());
        Assert.Equal(0x1A2B, owner.GetProperty("heartbeatHwnd").GetInt64());
        Assert.Equal(2, root.GetProperty("appBars").GetArrayLength());
        Assert.False(root.GetProperty("cleanExit").GetBoolean());
        // Windows' volume display (ADR-015): the watchdog of another build gives it back from these.
        var display = root.GetProperty("volumeDisplay");
        Assert.Equal(0xBD03BA, display.GetProperty("hwnd").GetInt64());
        Assert.Equal(9876, display.GetProperty("explorerPid").GetInt32());
        Assert.True(display.GetProperty("hidden").GetBoolean());
        // Why Windows ended it (the watchdog of another build starts it again only after "close-app").
        Assert.Equal("close-app", root.GetProperty("endedBy").GetString());
        Assert.Equal(0, root.GetProperty("closeAppRelaunches").GetArrayLength());
    }

    /// <summary>A file as the build of 2026-10-15 wrote it still reads back the same (a new build after a crash reads the old one's state).</summary>
    [Fact]
    public void State_json_written_by_an_earlier_build_reads_back()
    {
        const string Earlier = """
            {
              "version": 1,
              "owner": { "pid": 4242, "startTimeUtcTicks": 638000000000000000, "heartbeatHwnd": 6699, "args": "--diag" },
              "taskbar": {
                "hiddenByUs": true,
                "strategy": "hide",
                "abmState": 2,
                "savedAt": "2026-10-15T09:00:00+09:00",
                "windows": [ { "class": "Shell_TrayWnd", "visible": true, "exStyle": 136 } ]
              },
              "appBars": [ 16, 32 ],
              "cleanExit": false,
              "relaunches": []
            }
            """;
        var s = JsonSerializer.Deserialize(Earlier, ShellStateJson.Default.ShellState)!;
        Assert.Equal(2, s.Taskbar!.AbmState);
        Assert.True(s.Taskbar.HiddenByUs);
        Assert.Equal(0x88, Assert.Single(s.Taskbar.Windows).ExStyle);
        Assert.Equal(4242, s.Owner!.Pid);
        Assert.Equal(6699, s.Owner.HeartbeatHwnd);
        Assert.Equal([16L, 32L], s.AppBars);
        Assert.False(s.CleanExit);
        Assert.Null(s.VolumeDisplay); // before stage 2: Windows' display was never touched
        Assert.Null(s.EndedBy); // before the end-of-session reasons: a crash, as the watchdog read it then
        Assert.Empty(s.CloseAppRelaunches);
    }
}
