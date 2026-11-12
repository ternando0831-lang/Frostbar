using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Frostbar.Core.Devices;
using Frostbar.Platform.Infrastructure;
using Windows.Media.Control;

namespace Frostbar.Platform.Sys;

/// <summary>What is playing (the system media controls' current session, as in Windows' quick settings).</summary>
public sealed record NowPlaying(string AppId, string Title, string Artist, bool Playing, bool CanPlayPause, bool CanPrevious, bool CanNext, BitmapSource? Art);

/// <summary>
/// The media that plays now, from any app that reports it to Windows (browsers, music players: the global system
/// media transport controls, M0 S10), with play/pause and previous/next. Events come on WinRT threads; what changed is
/// read there and handed to the UI thread as a frozen snapshot. A session pressed from a panel stays the one shown and
/// acted on while Windows turns to another that already played (Core's MediaFocus).
/// </summary>
public sealed class MediaService : IDisposable
{
    readonly Dispatcher _ui;
    readonly object _gate = new();
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    // The session pressed from a panel stays while Windows turns to another that already played (Core's MediaFocus).
    readonly MediaFocus<GlobalSystemMediaTransportControlsSession> _focus = new(SameApp.Instance);
    int _version;
    bool _disposed;

    public NowPlaying? Current { get; private set; }
    /// <summary>Something else plays, or what plays changed (UI thread).</summary>
    public event Action? Changed;
    /// <summary>How many listen to <see cref="Changed"/> (diagnostics: a closed panel must not stay subscribed).</summary>
    public int Listeners => Changed?.GetInvocationList().Length ?? 0;

    public MediaService(Dispatcher ui)
    {
        _ui = ui;
        _ = StartAsync();
    }

    async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;
            _onCurrent = (_, _) => Attach();
            _onSessions = (_, _) => Attach(); // a session held may be gone
            _manager.CurrentSessionChanged += _onCurrent;
            _manager.SessionsChanged += _onSessions;
            Attach();
        }
        catch (Exception ex) { Log.Info("media controls unavailable: " + ex.Message); }
    }

    void Attach()
    {
        if (_disposed || _manager == null) return;
        GlobalSystemMediaTransportControlsSession? next;
        try { next = _manager.GetCurrentSession(); }
        catch (Exception ex) { Log.Debug("media: " + ex.Message); return; }
        lock (_gate)
        {
            if (_focus.Held != null)
            {
                try
                {
                    var all = _manager.GetSessions();
                    next = _focus.Choose(next, all);
                    Log.Debug($"media: the current session changed with one held ({all.Count} sessions): {_focus.Why}");
                }
                catch (Exception ex) { Log.Debug("media: " + ex.Message); }
            }
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
            }
            _session = next;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
            }
        }
        _ = ReadAsync();
    }

    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = ReadAsync();
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = ReadAsync();

    /// <summary>Reads the session again; an older read that finishes later is dropped.</summary>
    async Task ReadAsync()
    {
        int version = Interlocked.Increment(ref _version);
        var session = _session;
        NowPlaying? now = null;
        try
        {
            if (session != null)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var info = session.GetPlaybackInfo();
                var controls = info?.Controls;
                BitmapSource? art = null;
                if (props?.Thumbnail != null)
                    try { art = await ArtAsync(props.Thumbnail); }
                    catch (Exception ex) { Log.Debug("media: no artwork: " + ex.Message); }
                now = new NowPlaying(session.SourceAppUserModelId ?? "", props?.Title ?? "", props?.Artist ?? "",
                    info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    controls?.IsPlayPauseToggleEnabled ?? false, controls?.IsPreviousEnabled ?? false, controls?.IsNextEnabled ?? false, art);
            }
        }
        catch (Exception ex) { Log.Debug("media: " + ex.Message); }
        if (session == null)
        {
            // No current session: Windows can let one go and make the next one current a moment later (the panel's
            // media section vanished under the pointer as the test target resumed, 2026-10-12). Cleared only if none
            // has come back meanwhile.
            Log.Debug("media: no current session");
            await Task.Delay(600);
        }
        if (version != Volatile.Read(ref _version) || _disposed) return;
        _ = _ui.BeginInvoke(() =>
        {
            if (version != _version) return;
            Current = now;
            Changed?.Invoke();
        });
    }

    /// <summary>The cover, decoded small and frozen.</summary>
    static async Task<BitmapSource?> ArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        using var s = stream.AsStreamForRead();
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 96;
        image.StreamSource = s;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public Task<bool> PlayPauseAsync() => Do(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task<bool> NextAsync() => Do(s => s.TrySkipNextAsync().AsTask());
    public Task<bool> PreviousAsync() => Do(s => s.TrySkipPreviousAsync().AsTask());

    async Task<bool> Do(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        var s = _session;
        if (s == null) return false;
        try
        {
            // Held from now on, with the sessions that play at this moment (before it pauses, Windows may turn to one).
            // Asked off the UI thread: each is a question to the app that plays.
            var manager = _manager;
            var playing = await Task.Run(() => manager?.GetSessions()
                .Where(x => x.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing).ToList() ?? []);
            lock (_gate) _focus.Pressed(s, playing);
            Log.Debug($"media: pressed; {playing.Count} session(s) playing, the pressed one among them: {playing.Any(x => SameApp.Instance.Equals(x, s))}");
            return await action(s);
        }
        catch (Exception ex) { Log.Debug("media: " + ex.Message); return false; }
    }

    /// <summary>The session pressed last from a panel, held while Windows turns to another (diagnostics: its app).</summary>
    public string? HeldApp { get { lock (_gate) return _focus.Held?.SourceAppUserModelId; } }

    /// <summary>
    /// Sessions told apart by their app (AppUserModelId). Windows hands out a new object for the same session each time
    /// it is asked: told apart by the native object, the session just pressed was not among the sessions listed a moment
    /// later, and was let go at once (2026-10-18, cc-media-other). Two sessions of one app (two tabs of a browser) count
    /// as one: between them the panel follows Windows, as before.
    /// </summary>
    sealed class SameApp : IEqualityComparer<GlobalSystemMediaTransportControlsSession>
    {
        public static readonly SameApp Instance = new();

        public bool Equals(GlobalSystemMediaTransportControlsSession? a, GlobalSystemMediaTransportControlsSession? b) =>
            ReferenceEquals(a, b) || a != null && b != null && App(a) == App(b);

        public int GetHashCode(GlobalSystemMediaTransportControlsSession s) => App(s).GetHashCode();

        static string App(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.SourceAppUserModelId ?? ""; }
            catch (Exception) { return ""; } // gone meanwhile
        }
    }

    // Taken off at Dispose: the manager holds them, they hold this service, and this holds the manager (a WinRT object's
    // handlers keep both alive: a service of each menu bar made again stayed, 2026-11-06 review).
    Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs>? _onCurrent;
    Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>? _onSessions;

    public void Dispose()
    {
        _disposed = true;
        if (_manager is { } manager)
        {
            try
            {
                if (_onCurrent != null) manager.CurrentSessionChanged -= _onCurrent;
                if (_onSessions != null) manager.SessionsChanged -= _onSessions;
            }
            catch (Exception ex) { Log.Debug("media: " + ex.Message); }
        }
        lock (_gate)
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session = null;
            }
        }
    }
}
