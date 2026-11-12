using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;
using Microsoft.Win32;

namespace Frostbar.Platform.Sys;

/// <summary>
/// The shell's sound effects (macOS's Sound › Sound Effects, 2026-10-22): the sound of an event of Windows' sound scheme
/// (HKCU\AppEvents\Schemes\Apps\&lt;app&gt;\&lt;event&gt;\.Current, what Windows' sound settings set), or, where the scheme
/// has none for it, a sound of Windows' own media folder: Windows 11's default scheme has none for emptying the Recycle
/// Bin (this PC's, 2026-10-22), and macOS's trash makes a sound. A spec's shell counts them without a sound (<see cref="Silent"/>).
/// </summary>
public static class UiSounds
{
    /// <summary>The volume's feedback: Windows' "Default Beep" (macOS plays its own "pop" there).</summary>
    public static readonly Sound Feedback = new("feedback", @".Default\.Default", null);
    /// <summary>Emptying the trash: Windows' "Empty Recycle Bin", or its "Windows Recycle" sound.</summary>
    public static readonly Sound EmptyTrash = new("emptyTrash", @"Explorer\EmptyRecycleBin", "Windows Recycle.wav");

    /// <param name="Name">The name it is counted by (diagnostics).</param>
    /// <param name="Event">Its event in the sound scheme ("app\event").</param>
    /// <param name="Fallback">A file of Windows' media folder for when the scheme has none (null: then nothing).</param>
    public sealed record Sound(string Name, string Event, string? Fallback);

    /// <summary>Counted, not played (a spec's shell: FROSTBAR_E2E_SOUNDS=spec, with --diag only).</summary>
    public static bool Silent { get; set; }

    static readonly Dictionary<string, int> _played = new();

    /// <summary>How many times each was asked for since the start, by name (diagnostics).</summary>
    public static IReadOnlyDictionary<string, int> Played { get { lock (_played) return new Dictionary<string, int>(_played); } }

    /// <summary>Plays <paramref name="sound"/> without waiting (nothing when there is no file for it).</summary>
    public static void Play(Sound sound)
    {
        lock (_played) _played[sound.Name] = _played.GetValueOrDefault(sound.Name) + 1;
        if (Silent) return;
        if (FileOf(sound) is not { } file) { Log.Debug($"sounds: none for {sound.Name}"); return; }
        if (!PlaySound(file, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT)) Log.Debug($"sounds: {sound.Name} could not play {file}");
    }

    /// <summary>The file the scheme has for the sound's event, or its fallback in Windows' media folder; null for neither.</summary>
    public static string? FileOf(Sound sound)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"AppEvents\Schemes\Apps\{sound.Event}\.Current");
            if (k?.GetValue("") is string set && set.Trim().Length > 0) return Environment.ExpandEnvironmentVariables(set.Trim());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        if (sound.Fallback == null) return null;
        string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", sound.Fallback);
        return File.Exists(media) ? media : null;
    }

    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_FILENAME = 0x00020000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
