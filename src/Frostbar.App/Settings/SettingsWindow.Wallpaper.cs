using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Frostbar.App.Common;
using Frostbar.App.Overview;
using Frostbar.Core.Settings;
using Frostbar.Core.Time;
using Frostbar.Platform.Shell;

namespace Frostbar.App.Settings;

/// <summary>The Wallpaper page and the Appearance page's dark mode by itself (stage 4-3).</summary>
public sealed partial class SettingsWindow
{
    // The wallpaper is Windows' own (IDesktopWallpaper, put on every monitor; a spec's shell keeps its own), and the
    // thumbnails are decoded off the UI thread at twice their size. Explorer shows a new wallpaper about 0.3 s after it
    // is set (S20), so the page reads it back a moment later.

    const double ThumbW = 104, ThumbH = 58.5;
    WallpaperPictures? _thumbs;
    static readonly WallpaperFit[] Fits = [WallpaperFit.Fill, WallpaperFit.Fit, WallpaperFit.Stretch, WallpaperFit.Center, WallpaperFit.Tile, WallpaperFit.Span];

    /// <summary>The Appearance page's group: Windows' dark mode from sunset to sunrise at a place, or between two times.</summary>
    void DarkSchedulePart(AppearanceSettings a)
    {
        string[] modes = ["off", "sun", "clock"];
        var mode = new MacControls.PopUp([_s["sched.off"], _s["sched.sun"], _s["sched.clock"]], Math.Max(0, Array.IndexOf(modes, a.DarkSchedule)), _t,
            i => { a.DarkSchedule = modes[i]; _host.Apply(SettingsArea.Schedule); ShowAgain(); });
        Controls["schedule.mode"] = mode;
        var rows = new List<FrameworkElement> { Row(_s["sched.title"], mode, _s["sched.detail"]) };
        if (a.DarkSchedule == "sun")
        {
            var auto = Cities.For("", TimeZoneInfo.Local.Id);
            bool ja = _s.Language == "ja";
            var labels = new List<string> { string.Format(_s["sched.cityAuto"], ja ? auto.NameJa : auto.NameEn) };
            labels.AddRange(Cities.All.Select(c => ja ? c.NameJa : c.NameEn));
            int index = a.City.Length == 0 ? 0 : 1 + Cities.All.ToList().FindIndex(c => c.Key == a.City);
            var city = new MacControls.PopUp(labels, Math.Max(0, index), _t, i => { a.City = i == 0 ? "" : Cities.All[i - 1].Key; _host.Apply(SettingsArea.Schedule); ShowAgain(); });
            Controls["schedule.city"] = city;
            rows.Add(Row(_s["sched.city"], city));
        }
        else if (a.DarkSchedule == "clock")
        {
            // Every half hour of the day.
            var times = Enumerable.Range(0, 48).Select(i => $"{i / 2:00}:{i % 2 * 30:00}").ToArray();
            MacControls.PopUp Time(string key, string value, Action<string> set)
            {
                var p = new MacControls.PopUp(times, Math.Max(0, Array.IndexOf(times, value)), _t, i => { set(times[i]); _host.Apply(SettingsArea.Schedule); ShowAgain(); });
                Controls[key] = p;
                return p;
            }
            rows.Add(Row(_s["sched.from"], Time("schedule.from", a.DarkFrom, v => a.DarkFrom = v)));
            rows.Add(Row(_s["sched.until"], Time("schedule.until", a.DarkUntil, v => a.DarkUntil = v)));
        }
        Add(Group([.. rows]));
        if (a.DarkSchedule is "sun" or "clock")
        {
            // The change the shell will make (its schedule's last look, on its clock), written again when the schedule
            // moves; to the nearest minute, as the almanacs give it (Osaka's sunset on 2026-10-16 is 17:22:45: the NAOJ's 17:23).
            var culture = CultureInfo.GetCultureInfo(_s.Language == "ja" ? "ja-JP" : "en-US");
            var next = Note("");
            Controls["schedule.next"] = next;
            Add(next);
            Action write = () =>
            {
                if (_host.Schedule is not { } state) { next.Text = ""; return; }
                var at = state.Next.ToLocalTime().AddSeconds(30);
                next.Text = string.Format(_s["sched.next"], at.ToString(_s["sched.dateFormat"], culture), state.Dark ? _s["set.light"] : _s["set.dark"]);
            };
            write();
            if (!_indexing)
            {
                _host.ScheduleMoved += write;
                OnLeave(() => _host.ScheduleMoved -= write);
            }
        }
    }

    void WallpaperPage()
    {
        var store = WallpaperStore.Current;
        var w = _settings.Wallpaper;
        _thumbs ??= new WallpaperPictures(Dispatcher, keep: 48);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int px = (int)Math.Ceiling(ThumbW * scale * 2);
        var thumbs = new List<(Border box, string path, bool inGrid)>();
        string? current = store.Current();
        var failure = Failure();

        // ---- the wallpaper now
        var preview = new Border { Width = 288, Height = 162, CornerRadius = new CornerRadius(8), Background = _hover, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 6) };
        var name = new TextBlock { Foreground = _dim, FontSize = 12, Margin = new Thickness(4, 0, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis };
        Controls["wallpaper.preview"] = preview;
        Add(preview);
        Add(name);
        string[] fitLabels = [_s["wp.fill"], _s["wp.fitIn"], _s["wp.stretch"], _s["wp.center"], _s["wp.tile"], _s["wp.span"]];
        var fit = new MacControls.PopUp(fitLabels, Math.Max(0, Array.IndexOf(Fits, store.Fit())), _t, i => Written(() => store.SetFit(Fits[i]), failure));
        Controls["wallpaper.fit"] = fit;
        Add(Group(Row(_s["wp.fit"], fit)));

        // ---- Windows' own
        Add(Caption(_s["wp.windows"]));
        var grid = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (path, i) in WallpaperStore.BuiltIns().Select((p, i) => (p, i)))
        {
            var box = Thumb();
            box.Margin = new Thickness(0, 0, 8, 8);
            Pressed(box, () => Choose(path));
            Controls["wallpaper.builtin." + i] = box;
            thumbs.Add((box, path, true));
            grid.Children.Add(box);
        }
        Add(grid);
        Add(failure);
        var choose = Button(_s["wp.choose"], () => { if (Pick() is { } p) Choose(p); });
        choose.HorizontalAlignment = HorizontalAlignment.Left;
        Controls["wallpaper.choose"] = choose;
        Add(choose);

        // ---- light and dark
        Add(Caption(_s["set.light"] + " / " + _s["set.dark"]));
        var pair = Switch("wallpaper.paired", w.Paired, on =>
        {
            w.Paired = on;
            var (bloomLight, bloomDark) = WallpaperStore.Bloom();
            w.Light ??= bloomLight;
            w.Dark ??= bloomDark;
            _host.Apply(SettingsArea.Wallpaper);
            WhenUp(_shown, () => ShowAgain());
        });
        var pairRows = new List<FrameworkElement> { Row(_s["wp.pair"], pair, _s["wp.pairDetail"]) };
        if (w.Paired)
        {
            var slots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 10) };
            foreach (var (dark, label) in new[] { (false, _s["set.light"]), (true, _s["set.dark"]) })
            {
                string? path = dark ? w.Dark : w.Light;
                var slot = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
                var box = Thumb();
                Pressed(box, () =>
                {
                    if (Pick() is not { } p) return;
                    if (dark) w.Dark = p; else w.Light = p;
                    _host.Apply(SettingsArea.Wallpaper);
                    ShowAgain();
                });
                Controls["wallpaper.pair." + (dark ? "dark" : "light")] = box;
                if (path != null) thumbs.Add((box, path, false));
                slot.Children.Add(box);
                slot.Children.Add(new TextBlock { Text = label, Foreground = _dim, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
                slots.Children.Add(slot);
            }
            pairRows.Add(slots);
        }
        Add(Group([.. pairRows]));
        AddFooter(_s["wp.windowsSettings"], "ms-settings:personalization-background");

        void Show()
        {
            current = store.Current();
            preview.Background = current != null && _thumbs.Get(current, (int)Math.Ceiling(288 * scale)) is { } big
                ? new ImageBrush(big) { Stretch = Stretch.UniformToFill } : _hover;
            name.Text = current != null ? Path.GetFileName(current) : _s["wp.perMonitor"];
            foreach (var (box, path, inGrid) in thumbs)
            {
                if (box.Background is not ImageBrush && _thumbs.Get(path, px) is { } small) box.Background = new ImageBrush(small) { Stretch = Stretch.UniformToFill };
                box.BorderBrush = inGrid && string.Equals(path, current, StringComparison.OrdinalIgnoreCase) ? _accent : Brushes.Transparent;
            }
        }
        void Choose(string path)
        {
            // With the pair on, the picture chosen is the one for how Windows is now (else the pair would put the other back).
            if (w.Paired)
            {
                if (_host.WindowsDark) w.Dark = path; else w.Light = path;
                _host.Apply(SettingsArea.Wallpaper);
            }
            Written(() => store.Set(path), failure);
            int page = _shown;
            Dispatcher.BeginInvoke(async () => { await Task.Delay(500); if (page == _shown) { Show(); if (w.Paired) WhenUp(page, () => ShowAgain()); } });
        }
        Border Thumb() => new()
        {
            Width = ThumbW, Height = ThumbH, CornerRadius = new CornerRadius(5), Background = _hover, BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
        };
        // A thumbnail takes a click without its picture being painted over (Clickable paints the row's background). On the
        // glass window it sinks under a press as a picture does (2026-10-23).
        void Pressed(Border b, Action click)
        {
            b.MouseEnter += (_, _) => b.Opacity = 0.85;
            b.MouseLeave += (_, _) => b.Opacity = 1;
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            GlassBounce.Attach(b, () => Look == "glass" && _t.Animations, Frostbar.Core.Shell.PressGive.Picture);
        }

        if (_indexing) return;
        _thumbs.Loaded += Show;
        OnLeave(() => _thumbs.Loaded -= Show);
        Show();
    }

    /// <summary>The file dialog for a picture; null when nothing was chosen.</summary>
    string? Pick()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"{_s["wp.pictures"]}|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.webp",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
