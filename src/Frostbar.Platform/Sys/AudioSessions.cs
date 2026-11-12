using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Sys;

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
    [PreserveSig] int GetGroupingParam(out Guid group);
    [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid ctx);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference(bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

/// <summary>
/// An app's sound on the default output (a session of Windows' volume mixer): <see cref="Key"/> is the session's
/// instance (several per app are common: a browser's tabs), <see cref="ExePath"/> the process behind it (none for the
/// system sounds).
/// </summary>
public sealed record AppAudio(string Key, int Pid, string ExePath, string DisplayName, bool SystemSounds, bool Playing, float Volume, bool Muted);

/// <summary>
/// The volume mixer (Core Audio's sessions on the default output): each app's volume and mute, read and set as
/// Windows' Volume mixer does. Read on demand (the Sound settings ask while they are open); call off the UI thread.
/// </summary>
public static class AudioSessions
{
    static Guid _context = Guid.NewGuid();

    /// <summary>The sessions that are not over, playing ones first.</summary>
    public static IReadOnlyList<AppAudio> List()
    {
        var list = new List<AppAudio>();
        Each((s, v) =>
        {
            if (s.GetState(out int state) != 0 || state == 2 /*expired*/) return false;
            s.GetSessionInstanceIdentifier(out string key);
            s.GetProcessId(out uint pid);
            s.GetDisplayName(out string name);
            v.GetMasterVolume(out float level);
            v.GetMute(out bool muted);
            bool system = s.IsSystemSoundsSession() == 0;
            string exe = system || pid == 0 ? "" : Native.ProcessPath(pid) ?? "";
            list.Add(new AppAudio(key ?? "", (int)pid, exe, name ?? "", system, state == 1, level, muted));
            return false;
        });
        return list.OrderByDescending(a => a.Playing).ThenBy(a => a.SystemSounds).ToList();
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation { [PreserveSig] int GetPeakValue(out float peak); }

    /// <summary>
    /// The loudest peak of each process's sessions over <paramref name="ms"/> (0 = silence), by process ID (tests: is
    /// a session that says it plays making sound, a call say, before the volume is changed under it).
    /// </summary>
    public static IReadOnlyDictionary<int, float> Peaks(int ms = 1000)
    {
        var peaks = new Dictionary<int, float>();
        var end = Environment.TickCount64 + ms;
        do
        {
            Each((s, _) =>
            {
                if (s is not IAudioMeterInformation meter || s.GetProcessId(out uint pid) != 0) return false;
                if (meter.GetPeakValue(out float p) == 0) peaks[(int)pid] = Math.Max(peaks.GetValueOrDefault((int)pid), p);
                return false;
            });
            Thread.Sleep(50);
        } while (Environment.TickCount64 < end);
        return peaks;
    }

    /// <summary>Sets the volume and/or mute of the sessions in <paramref name="keys"/>; how many it set.</summary>
    public static int Set(IReadOnlyCollection<string> keys, float? volume, bool? mute)
    {
        int n = 0;
        Each((s, v) =>
        {
            if (s.GetSessionInstanceIdentifier(out string key) != 0 || !keys.Contains(key)) return false;
            if (volume is float level) v.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref _context);
            if (mute is bool m) v.SetMute(m, ref _context);
            n++;
            return false;
        });
        return n;
    }

    /// <summary>Runs <paramref name="use"/> on each session of the default output until it returns true.</summary>
    static void Each(Func<IAudioSessionControl2, ISimpleAudioVolume, bool> use)
    {
        try
        {
            var e = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            if (e.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var dev) != 0 || dev == null) return;
            if (dev.Activate(typeof(IAudioSessionManager2).GUID, 23, IntPtr.Zero, out object o) != 0) return;
            var manager = (IAudioSessionManager2)o;
            if (manager.GetSessionEnumerator(out var sessions) != 0) return;
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var s) != 0 || s == null) continue;
                try
                {
                    if (s is not ISimpleAudioVolume v) continue;
                    if (use(s, v)) return;
                }
                finally { Marshal.ReleaseComObject(s); }
            }
        }
        catch (Exception ex) { Log.Debug("audio sessions: " + ex.Message); }
    }
}
