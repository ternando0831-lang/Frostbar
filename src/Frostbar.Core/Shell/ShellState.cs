using System.Text.Json.Serialization;

namespace Frostbar.Core.Shell;

/// <summary>
/// Machine state (%LOCALAPPDATA%\Frostbar\state.json) shared by the app, its watchdog, the next start and the emergency
/// script (which reads <c>taskbar.abmState</c>). It records how Explorer's taskbar looked before we touched it.
/// </summary>
public sealed class ShellState
{
    public int Version { get; set; } = 1;
    public OwnerInfo? Owner { get; set; }
    public TaskbarSavedState? Taskbar { get; set; }
    /// <summary>Handles of the app-bar windows we registered (removed again after a crash).</summary>
    public List<long> AppBars { get; set; } = new();
    public bool CleanExit { get; set; } = true;
    public List<DateTimeOffset> Relaunches { get; set; } = new();
    /// <summary>
    /// Why the last run ended when Windows asked it to end, or null when it did not: <see cref="ClosedForAnotherProgram"/>
    /// (Restart Manager's ENDSESSION_CLOSEAPP: the watchdog starts it again) or <see cref="SessionEnded"/> (signing out,
    /// shutting down, restarting: nothing starts it again, even when Windows ended the process before it had stopped).
    /// Written first, before the stop.
    /// </summary>
    public string? EndedBy { get; set; }
    public const string ClosedForAnotherProgram = "close-app", SessionEnded = "session-end";
    /// <summary>When the watchdog started it again after it closed for another program (<see cref="CloseAppRelaunchPolicy"/>).</summary>
    public List<DateTimeOffset> CloseAppRelaunches { get; set; } = new();
    /// <summary>
    /// Windows' volume and brightness display once told apart (ADR-015), and whether we keep it from being drawn (an
    /// empty window region on Explorer's window): the watchdog and the next start give it back, and the next start knows
    /// it from its first key. Null until it is told apart, and when its Explorer has gone.
    /// </summary>
    public VolumeDisplayState? VolumeDisplay { get; set; }
}

public sealed class VolumeDisplayState
{
    public long Hwnd { get; set; }
    /// <summary>Explorer's process (a window number of an Explorer gone by is somebody else's later).</summary>
    public int ExplorerPid { get; set; }
    /// <summary>The empty region is on it (ours to take off).</summary>
    public bool Hidden { get; set; }
}

public sealed class OwnerInfo
{
    public int Pid { get; set; }
    /// <summary>Process start time: guards against a reused process id.</summary>
    public long StartTimeUtcTicks { get; set; }
    /// <summary>A window on the UI thread the watchdog pings to detect a hang.</summary>
    public long HeartbeatHwnd { get; set; }
    /// <summary>Command line to start it again with after a crash (profile, diagnostics…).</summary>
    public string Args { get; set; } = "";
}

public sealed class TaskbarSavedState
{
    public bool HiddenByUs { get; set; }

    /// <summary>
    /// Saved before the tray took always-on-top off the taskbar, by a run that has not hidden it: only that is put back
    /// (the user's own auto-hide and the taskbar's showing are theirs). A run with Windows' taskbar kept set the user's
    /// auto-hide back at each quit and sign-out to what it was when the shell started (2026-11-06 review).
    /// </summary>
    public bool OnlyTopmost { get; set; }

    public string Strategy { get; set; } = "hide";

    /// <summary>Explorer's auto-hide state (ABM_GETSTATE) when saved; -1: Explorer did not answer (not known).</summary>
    public int AbmState { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public List<TaskbarWindowState> Windows { get; set; } = new();
}

public sealed class TaskbarWindowState
{
    public string Class { get; set; } = "";
    public bool Visible { get; set; }
    public long ExStyle { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ShellState))]
public sealed partial class ShellStateJson : JsonSerializerContext;
