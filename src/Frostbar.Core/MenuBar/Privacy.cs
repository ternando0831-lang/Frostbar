using Frostbar.Core.Localization;

namespace Frostbar.Core.MenuBar;

/// <summary>What the menu bar's privacy indicator shows: nothing, the microphone (orange) or the camera (green, it wins).</summary>
public enum PrivacyKind { None, Microphone, Camera }

/// <summary>
/// The apps that use the microphone and the camera now, by the names they show (stage 5-2, macOS's privacy indicator).
/// The menu bar shows a capsule while any is in use, left of the Control Center (the user's choice B of three,
/// 2026-10-16): orange with the microphone, green while the camera is in use, with the camera's picture and the
/// microphone's when both are. The Control Center lists them at its top.
/// </summary>
public sealed record PrivacyState(IReadOnlyList<string> Microphone, IReadOnlyList<string> Camera)
{
    public static readonly PrivacyState None = new([], []);

    public PrivacyKind Kind => Camera.Count > 0 ? PrivacyKind.Camera : Microphone.Count > 0 ? PrivacyKind.Microphone : PrivacyKind.None;

    /// <summary>The same devices in use by the same apps (the lists compared, not the record's references).</summary>
    public bool Same(PrivacyState other) => Microphone.SequenceEqual(other.Microphone) && Camera.SequenceEqual(other.Camera);

    /// <summary>The devices in use, the camera first, each with its apps (the Control Center's lines).</summary>
    public IReadOnlyList<(PrivacyKind Kind, IReadOnlyList<string> Apps)> Lines()
    {
        var lines = new List<(PrivacyKind, IReadOnlyList<string>)>();
        if (Camera.Count > 0) lines.Add((PrivacyKind.Camera, Camera));
        if (Microphone.Count > 0) lines.Add((PrivacyKind.Microphone, Microphone));
        return lines;
    }

    /// <summary>The capsule's tooltip: a line per device in use with its apps ("カメラ: Teams" / "マイク: LINE、Discord").</summary>
    public string Describe(Strings s) => string.Join("\n", Lines().Select(l => s.F("privacy.line", Device(l.Kind, s), string.Join(s["privacy.and"], l.Apps))));

    public static string Device(PrivacyKind kind, Strings s) => kind == PrivacyKind.Camera ? s["privacy.camera"] : s["privacy.microphone"];
}

/// <summary>
/// A record of an app's use of a device in Windows' ConsentStore: the device, the app's key there (a packaged app's
/// family name, or a desktop app's program with '#' for '\'), and when its last use started and stopped (FILETIME;
/// 0: not written).
/// </summary>
public sealed record PrivacyRecord(PrivacyKind Device, string App, bool Packaged, long Start, long Stop);

/// <summary>What Windows' records say is in use now (the user's choice "windowsの記録", 2026-10-16).</summary>
public static class PrivacyRecords
{
    /// <summary>In use now: a start and no stop (Windows writes the stop when the app lets the device go).</summary>
    public static bool InUse(PrivacyRecord r) => r.Start != 0 && r.Stop == 0;

    /// <summary>
    /// The apps using each device now, by <paramref name="name"/> (the names they show), the latest to start first,
    /// each name once.
    /// </summary>
    public static PrivacyState State(IEnumerable<PrivacyRecord> records, Func<PrivacyRecord, string> name)
    {
        var inUse = records.Where(InUse).OrderByDescending(r => r.Start).ToList();
        List<string> Apps(PrivacyKind device) => [.. inUse.Where(r => r.Device == device).Select(name).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        return new PrivacyState(Apps(PrivacyKind.Microphone), Apps(PrivacyKind.Camera));
    }

    /// <summary>A desktop app's program from its key in the store ("C:#Program Files#App#app.exe" → "C:\Program Files\App\app.exe").</summary>
    public static string ProgramOf(string key) => key.Replace('#', '\\');

    /// <summary>
    /// A packaged app's name when its package says none: its family name's last part before the publisher's hash
    /// ("Microsoft.WindowsCamera_8wekyb3d8bbwe" → "WindowsCamera").
    /// </summary>
    public static string PackageName(string familyName)
    {
        string name = familyName.Split('_')[0];
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }
}
