using System.Runtime.InteropServices;
using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Sys;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore props);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

/// <summary>
/// Windows' own (undocumented, stable since Windows 7) way to change the default audio device, as the sound settings
/// do; used by the open-source device switchers. Only <see cref="SetDefaultEndpoint"/> is called; the methods before it
/// hold its place in the table.
/// </summary>
[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr period, IntPtr minimum);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
}

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }

/// <summary>An audio output device.</summary>
public sealed record AudioOutput(string Id, string Name, bool IsDefault);

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolumeCallback { [PreserveSig] int OnNotify(IntPtr notifyData); }

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int GetChannelCount(out uint n);
    [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float v, ref Guid ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float v);
    [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float v, ref Guid ctx);
    [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float v);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
    [PreserveSig] int VolumeStepUp(ref Guid ctx);
    [PreserveSig] int VolumeStepDown(ref Guid ctx);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
    [PreserveSig] int GetVolumeRange(out float min, out float max, out float inc);
}

/// <summary>
/// The default output device's volume and mute (Core Audio), live in both directions: changes by keyboard keys or
/// other apps arrive through the endpoint callback, a new default device through the notification client.
/// Callbacks come on Core Audio's threads and are marshalled to the UI thread.
/// </summary>
public sealed class AudioService : IDisposable
{
    static Guid _context = Guid.NewGuid();
    readonly Dispatcher _ui;
    readonly IMMDeviceEnumerator _enumerator;
    readonly Notifications _notifications;
    readonly VolumeCallback _callback, _inputCallback;
    IAudioEndpointVolume? _volume, _inputVolume;

    public float Volume { get; private set; }
    public bool Muted { get; private set; }
    public string DeviceName { get; private set; } = "";
    public bool Available => _volume != null;
    /// <summary>The output devices that are there now (the default one marked).</summary>
    public IReadOnlyList<AudioOutput> Outputs { get; private set; } = [];
    /// <summary>The input devices (microphones) that are there now (the default one marked).</summary>
    public IReadOnlyList<AudioOutput> Inputs { get; private set; } = [];
    /// <summary>Left (−1) to right (1) on a two-channel output, 0 when centred or not two channels.</summary>
    public float Balance { get; private set; }
    /// <summary>The default output has two channels (a balance can be set).</summary>
    public bool Stereo { get; private set; }
    /// <summary>The default input's volume (0–1) and mute; <see cref="InputAvailable"/> false without one.</summary>
    public float InputVolume { get; private set; }
    public bool InputMuted { get; private set; }
    public bool InputAvailable => _inputVolume != null;
    public event Action? Changed;
    /// <summary>How many listen to <see cref="Changed"/> (diagnostics: a closed panel must not stay subscribed).</summary>
    public int Listeners => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Start-up warm-up, any thread: loads the audio stack (an enumerator and the default output, let go).</summary>
    public static void WarmUp()
    {
        try
        {
            var e = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            if (e.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var dev) == 0 && dev != null) Marshal.ReleaseComObject(dev);
            Marshal.ReleaseComObject(e);
        }
        catch (Exception) { }
    }

    public AudioService(Dispatcher ui)
    {
        _ui = ui;
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        _callback = new VolumeCallback(this, input: false);
        _inputCallback = new VolumeCallback(this, input: true);
        _notifications = new Notifications(this);
        _enumerator.RegisterEndpointNotificationCallback(_notifications);
        Bind();
        BindInput();
    }

    /// <summary>The default input's volume, followed like the output's (the Sound settings show it).</summary>
    void BindInput()
    {
        try
        {
            if (_inputVolume != null) { _inputVolume.UnregisterControlChangeNotify(_inputCallback); _inputVolume = null; }
            if (_enumerator.GetDefaultAudioEndpoint(1 /*eCapture*/, 1 /*eMultimedia*/, out var dev) != 0 || dev == null) { InputVolume = 0; Changed?.Invoke(); return; }
            if (dev.Activate(new Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), 23, IntPtr.Zero, out object o) != 0) return;
            _inputVolume = (IAudioEndpointVolume)o;
            _inputVolume.RegisterControlChangeNotify(_inputCallback);
            ReadInput(force: true); // another device: told even with the same values
        }
        catch (Exception ex) { Log.Debug("audio: no input: " + ex.Message); }
    }

    /// <summary>The input's volume and mute, told only when they changed (a call's automatic gain reports often).</summary>
    void ReadInput() => ReadInput(force: false);

    void ReadInput(bool force)
    {
        if (_inputVolume == null) return;
        _inputVolume.GetMasterVolumeLevelScalar(out float v);
        _inputVolume.GetMute(out bool m);
        if (!force && v == InputVolume && m == InputMuted) return;
        InputVolume = v; InputMuted = m;
        Changed?.Invoke();
    }

    public void SetInputVolume(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        _inputVolume?.SetMasterVolumeLevelScalar(v, ref _context);
        if (v > 0 && InputMuted) _inputVolume?.SetMute(false, ref _context);
    }

    public void SetInputMute(bool m) => _inputVolume?.SetMute(m, ref _context);

    /// <summary>
    /// Sets the balance of a two-channel output: the far side keeps the volume, the near one is lowered (what Windows'
    /// sound settings do). The overall volume stays where it was.
    /// </summary>
    public void SetBalance(float b)
    {
        if (_volume == null || !Stereo) return;
        b = Math.Clamp(b, -1f, 1f);
        _volume.GetMasterVolumeLevelScalar(out float master);
        _volume.SetChannelVolumeLevelScalar(0, b > 0 ? master * (1 - b) : master, ref _context);
        _volume.SetChannelVolumeLevelScalar(1, b < 0 ? master * (1 + b) : master, ref _context);
    }

    void Bind()
    {
        ReadOutputs();
        try
        {
            if (_volume != null) { _volume.UnregisterControlChangeNotify(_callback); _volume = null; }
            if (_enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var dev) != 0 || dev == null)
            {
                DeviceName = "";
                Headphones = false;
                DeviceContainer = null;
                Changed?.Invoke();
                return;
            }
            DeviceName = FriendlyName(dev);
            (Headphones, DeviceContainer) = Describe(dev);
            if (dev.Activate(new Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), 23, IntPtr.Zero, out object o) != 0) return;
            _volume = (IAudioEndpointVolume)o;
            _volume.RegisterControlChangeNotify(_callback);
            Read();
        }
        catch (Exception ex) { Log.Warn("audio endpoint unavailable", ex); }
    }

    /// <summary>The default output is headphones or a headset (the menu bar then shows headphones, as macOS's does).</summary>
    public bool Headphones { get; private set; }

    /// <summary>The default output's device container (a Bluetooth headset's battery is read by it), or null.</summary>
    public Guid? DeviceContainer { get; private set; }

    /// <summary>An endpoint's form factor (headphones 3, headset 5: PKEY_AudioEndpoint_FormFactor) and container (PKEY_Device_ContainerId).</summary>
    static (bool headphones, Guid? container) Describe(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var ps) != 0) return (false, null);
        try
        {
            bool headphones = false;
            Guid? container = null;
            var formFactor = new PROPERTYKEY(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
            if (ps.GetValue(ref formFactor, out var ff) == 0)
            {
                try { headphones = ff.vt == 19 /*VT_UI4*/ && (ff.p.ToInt64() & 0xFFFFFFFF) is 3 or 5; }
                finally { ShellApi.PropVariantClear(ref ff); }
            }
            var containerId = new PROPERTYKEY(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
            if (ps.GetValue(ref containerId, out var ci) == 0)
            {
                try { if (ci.vt == 72 /*VT_CLSID*/ && ci.p != IntPtr.Zero) container = Marshal.PtrToStructure<Guid>(ci.p); }
                finally { ShellApi.PropVariantClear(ref ci); }
            }
            return (headphones, container);
        }
        catch (Exception ex) { Log.Debug("audio: the output's form: " + ex.Message); return (false, null); }
        finally { Marshal.ReleaseComObject(ps); }
    }

    static string FriendlyName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var ps) != 0) return "";
        try
        {
            var key = new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
            if (ps.GetValue(ref key, out var pv) != 0) return "";
            try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) ?? "" : ""; }
            finally { ShellApi.PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(ps); }
    }

    void OutputsChanged()
    {
        ReadOutputs();
        Changed?.Invoke();
    }

    void Read()
    {
        Stats.Inc("audio.read");
        if (_volume == null) return;
        _volume.GetMasterVolumeLevelScalar(out float v);
        _volume.GetMute(out bool m);
        Volume = v; Muted = m;
        Stereo = _volume.GetChannelCount(out uint channels) == 0 && channels == 2;
        Balance = 0;
        if (Stereo && _volume.GetChannelVolumeLevelScalar(0, out float left) == 0 && _volume.GetChannelVolumeLevelScalar(1, out float right) == 0)
            Balance = left >= right ? (left <= 0 ? 0 : -(1 - right / left)) : 1 - left / right;
        Changed?.Invoke();
    }

    public void SetVolume(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        _volume?.SetMasterVolumeLevelScalar(v, ref _context);
        if (v > 0 && Muted) _volume?.SetMute(false, ref _context);
    }

    public void SetMute(bool m) => _volume?.SetMute(m, ref _context);

    void ReadOutputs()
    {
        Outputs = List(_enumerator, 0);
        Inputs = List(_enumerator, 1);
    }

    /// <summary>The active outputs read at once, without events (tests compare the Sound view with it).</summary>
    public static IReadOnlyList<AudioOutput> ListOutputs() => ListNow(0);

    /// <summary>The active inputs read at once, without events (tests).</summary>
    public static IReadOnlyList<AudioOutput> ListInputs() => ListNow(1);

    /// <summary>Whether the default output is headphones or a headset, read at once (tests compare the menu bar with it).</summary>
    public static bool DefaultOutputIsHeadphones()
    {
        try
        {
            var e = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            if (e.GetDefaultAudioEndpoint(0, 1, out var dev) != 0 || dev == null) return false;
            try { return Describe(dev).headphones; }
            finally { Marshal.ReleaseComObject(dev); Marshal.ReleaseComObject(e); }
        }
        catch (Exception) { return false; }
    }

    static IReadOnlyList<AudioOutput> ListNow(int flow)
    {
        try { return List((IMMDeviceEnumerator)new MMDeviceEnumeratorCo(), flow); }
        catch (Exception) { return []; }
    }

    /// <summary>The active devices of <paramref name="flow"/> (0 output, 1 input) and which is the default (for multimedia, as the sound settings show it).</summary>
    static List<AudioOutput> List(IMMDeviceEnumerator enumerator, int flow)
    {
        var list = new List<AudioOutput>();
        try
        {
            string? current = enumerator.GetDefaultAudioEndpoint(flow, 1, out var def) == 0 && def != null && def.GetId(out var id) == 0 ? id : null;
            if (enumerator.EnumAudioEndpoints(flow, 1 /*DEVICE_STATE_ACTIVE*/, out IntPtr p) == 0 && p != IntPtr.Zero)
            {
                var all = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(p);
                Marshal.Release(p);
                try
                {
                    all.GetCount(out uint n);
                    for (uint i = 0; i < n; i++)
                    {
                        if (all.Item(i, out var d) != 0 || d == null) continue;
                        try
                        {
                            if (d.GetId(out var did) != 0) continue;
                            list.Add(new AudioOutput(did, FriendlyName(d), string.Equals(did, current, StringComparison.OrdinalIgnoreCase)));
                        }
                        finally { Marshal.ReleaseComObject(d); }
                    }
                }
                finally { Marshal.ReleaseComObject(all); }
            }
        }
        catch (Exception ex) { Log.Debug("audio: outputs: " + ex.Message); }
        return list.OrderByDescending(o => o.IsDefault).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Makes <paramref name="id"/> the default input for every role; false if Windows refused.</summary>
    public bool SetDefaultInput(string id) => SetDefaultOutput(id);

    /// <summary>Makes <paramref name="id"/> the default output (or input: the id says which) for every role (what the sound settings do); false if Windows refused.</summary>
    public bool SetDefaultOutput(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                bool ok = true;
                for (int role = 0; role < 3; role++) ok &= policy.SetDefaultEndpoint(id, role) == 0; // console, multimedia, communications
                Log.Info($"audio: default output changed ({(ok ? "ok" : "refused")})");
                return ok;
            }
            finally { Marshal.ReleaseComObject(policy); }
        }
        catch (Exception ex) { Log.Warn("audio: the default output could not be changed", ex); return false; }
    }

    /// <summary>The default output's volume (0–1) and mute, read at once without events (tests); null without one.</summary>
    public static (float volume, bool muted)? ReadDefault() => WithDefault(v =>
    {
        v.GetMasterVolumeLevelScalar(out float level);
        v.GetMute(out bool muted);
        return ((float, bool)?)(level, muted);
    });

    /// <summary>Sets the default output's volume and mute directly, as another app would (tests).</summary>
    public static void WriteDefault(float volume, bool muted) => WithDefault(v =>
    {
        var other = Guid.NewGuid();
        v.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0f, 1f), ref other);
        v.SetMute(muted, ref other);
        return ((float, bool)?)null;
    });

    /// <summary>The default output's balance (−1 left … 1 right), read at once without events (tests); null when not two channels.</summary>
    public static float? ReadBalanceNow()
    {
        float? balance = null;
        WithDefault(v =>
        {
            if (v.GetChannelCount(out uint n) != 0 || n != 2) return null;
            v.GetChannelVolumeLevelScalar(0, out float l);
            v.GetChannelVolumeLevelScalar(1, out float r);
            balance = l >= r ? (l <= 0 ? 0 : -(1 - r / l)) : 1 - l / r;
            return null;
        });
        return balance;
    }

    /// <summary>Sets the default output's balance directly (tests put it back with this).</summary>
    public static void WriteBalanceNow(float b) => WithDefault(v =>
    {
        if (v.GetChannelCount(out uint n) != 0 || n != 2) return null;
        var other = Guid.NewGuid();
        v.GetMasterVolumeLevelScalar(out float master);
        v.SetChannelVolumeLevelScalar(0, b > 0 ? master * (1 - b) : master, ref other);
        v.SetChannelVolumeLevelScalar(1, b < 0 ? master * (1 + b) : master, ref other);
        return null;
    });

    static (float, bool)? WithDefault(Func<IAudioEndpointVolume, (float, bool)?> use)
    {
        try
        {
            var e = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            if (e.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var dev) != 0 || dev == null) return null;
            if (dev.Activate(new Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), 23, IntPtr.Zero, out object o) != 0) return null;
            return use((IAudioEndpointVolume)o);
        }
        catch (Exception) { return null; }
    }

    [ComVisible(true)]
    sealed class VolumeCallback(AudioService owner, bool input) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data) { owner._ui.BeginInvoke(input ? owner.ReadInput : owner.Read); return 0; }
    }

    [ComVisible(true)]
    sealed class Notifications(AudioService owner) : IMMNotificationClient
    {
        // Devices plugged in or out (a headset, a monitor's speakers): the list of outputs changes.
        public int OnDeviceStateChanged(string id, int state) { owner._ui.BeginInvoke(owner.OutputsChanged); return 0; }
        public int OnDeviceAdded(string id) { owner._ui.BeginInvoke(owner.OutputsChanged); return 0; }
        public int OnDeviceRemoved(string id) { owner._ui.BeginInvoke(owner.OutputsChanged); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string? id)
        {
            if (flow == 0 && role == 1) owner._ui.BeginInvoke(owner.Bind);
            if (flow == 1 && role == 1) owner._ui.BeginInvoke(() => { owner.ReadOutputs(); owner.BindInput(); });
            return 0;
        }
        public int OnPropertyValueChanged(string id, PROPERTYKEY key) => 0;
    }

    public void Dispose()
    {
        try { _volume?.UnregisterControlChangeNotify(_callback); } catch (Exception) { }
        try { _inputVolume?.UnregisterControlChangeNotify(_inputCallback); } catch (Exception) { }
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); } catch (Exception) { }
    }
}
