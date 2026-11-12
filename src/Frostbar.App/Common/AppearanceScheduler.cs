using System.IO;
using System.Windows.Threading;
using Frostbar.Core.Settings;
using Frostbar.Core.Time;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Shell;

namespace Frostbar.App.Common;

/// <summary>
/// Windows' light and dark by itself (stage 4-3, macOS's automatic appearance): switched once at each sunset and
/// sunrise at the chosen place, or at the two chosen times, with a one-off timer for the next change, worked out again
/// after a sleep and when the settings change. A switch made by hand meanwhile (the Control Center, Windows' settings)
/// stays until the next change. And the light/dark pair of wallpapers: put on as Windows is light or dark, whoever
/// switched it.
/// </summary>
public sealed class AppearanceScheduler : IDisposable
{
    readonly Func<AppSettings> _settings;
    readonly Action<bool> _setDark;
    readonly Func<bool> _isDark;
    readonly DispatcherTimer _timer;
    DateTimeOffset? _switchedFor; // the period (its Since) Windows was switched for
    bool? _wallpaperFor;          // the pair's picture last put on: for dark (true) or light (false)

    /// <summary>What "now" is (a spec sets its own).</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;
    /// <summary>Where the schedule was at the last look (null: off).</summary>
    public AppearanceSchedule.State? Last
    {
        get => _last;
        private set { if (_last == value) return; _last = value; Moved?.Invoke(); }
    }
    AppearanceSchedule.State? _last;
    /// <summary>Raised (on the UI thread) when a look finds the schedule somewhere else.</summary>
    public event Action? Moved;
    public int Switches { get; private set; }
    public int WallpaperChanges { get; private set; }

    /// <param name="setDark">Switches Windows (ThemeService.SetDark, or a spec's own).</param>
    /// <param name="isDark">Whether Windows is dark now.</param>
    public AppearanceScheduler(Dispatcher ui, Func<AppSettings> settings, Action<bool> setDark, Func<bool> isDark)
    {
        _settings = settings;
        _setDark = setDark;
        _isDark = isDark;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui);
        _timer.Tick += (_, _) => { _timer.Stop(); Check(); };
    }

    /// <summary>The schedule's settings changed (on, off, its place or times): Windows follows them at once.</summary>
    public void SettingsChanged()
    {
        _switchedFor = null;
        Check();
    }

    /// <summary>The pair's settings changed: its picture for how Windows is now.</summary>
    public void PairChanged()
    {
        _wallpaperFor = null;
        Pair();
    }

    /// <summary>Works out where the schedule is (at start, after a sleep, at the timer) and switches Windows once for each change.</summary>
    public void Check()
    {
        _timer.Stop();
        var a = _settings().Appearance;
        if (a.DarkSchedule is "sun" or "clock")
        {
            var now = Clock();
            AppearanceSchedule.State state;
            if (a.DarkSchedule == "sun")
            {
                var city = Cities.For(a.City, TimeZoneInfo.Local.Id);
                state = AppearanceSchedule.BySun(now, city.Latitude, city.Longitude);
            }
            else state = AppearanceSchedule.ByClock(now, TimeOnly.ParseExact(a.DarkFrom, "HH:mm"), TimeOnly.ParseExact(a.DarkUntil, "HH:mm"), TimeZoneInfo.Local);
            Last = state;
            if (_switchedFor != state.Since)
            {
                _switchedFor = state.Since;
                if (_isDark() != state.Dark)
                {
                    _setDark(state.Dark);
                    Switches++;
                    Log.Info($"appearance: Windows {(state.Dark ? "dark" : "light")} by the schedule ({a.DarkSchedule}) until {state.Next:yyyy-MM-dd HH:mm}");
                }
            }
            // A day's timer at most (and looked at again then): a sleep stops the timer, and the resume looks again.
            var wait = state.Next - now + TimeSpan.FromSeconds(2);
            _timer.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > TimeSpan.FromHours(12) ? TimeSpan.FromHours(12) : wait;
            _timer.Start();
        }
        else
        {
            Last = null;
            _switchedFor = null;
        }
        Pair();
    }

    /// <summary>The light/dark pair: the picture for how Windows is now, put on once for each change of it.</summary>
    public void Pair()
    {
        var w = _settings().Wallpaper;
        if (!w.Paired) { _wallpaperFor = null; return; }
        bool dark = _isDark();
        if (_wallpaperFor == dark) return;
        _wallpaperFor = dark;
        var (bloomLight, bloomDark) = WallpaperStore.Bloom();
        string? path = dark ? w.Dark ?? bloomDark : w.Light ?? bloomLight;
        if (path == null || !File.Exists(path)) return;
        var store = WallpaperStore.Current;
        WallpaperChanges++;
        Task.Run(() =>
        {
            if (string.Equals(store.Current(), path, StringComparison.OrdinalIgnoreCase)) return;
            if (store.Set(path) is { } why) Log.Warn($"appearance: the {(dark ? "dark" : "light")} wallpaper was not put on: {why}");
        });
    }

    public void Dispose() => _timer.Stop();
}
