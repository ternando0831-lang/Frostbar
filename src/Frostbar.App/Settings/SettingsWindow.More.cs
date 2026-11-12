using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Menus;
using Frostbar.Core.Search;
using Frostbar.Core.Shell;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;
using Frostbar.Platform.Wm;

namespace Frostbar.App.Settings;

/// <summary>
/// What macOS's System Settings has and Frostbar's lacked (2026-10-22): Accessibility's display and motion, the accent
/// colour and the scroll bars, the menu bar's battery percentage, flashing separators and recent items' count,
/// Spotlight's kinds of results, Desktop &amp; Dock's windows, and Sound's effects; and the big header a page of macOS's
/// begins with (General, Accessibility).
/// </summary>
public sealed partial class SettingsWindow
{
    // ---- the account at the sidebar's top -----------------------------------------------------------------------

    static (string Path, ImageSource Picture)? _accountPicture;

    /// <summary>
    /// macOS's account row at the top of System Settings' sidebar: the user's picture in a circle (a grey silhouette
    /// without one), their name, and the kind of account under it; a click opens Windows' "Your info".
    /// </summary>
    FrameworkElement AccountRow()
    {
        var account = _host.Account;
        FrameworkElement face;
        if (PictureOf(account.Picture) is { } picture)
            face = new Ellipse { Width = 34, Height = 34, Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill } };
        else
        {
            var grey = new Grid { Width = 34, Height = 34 };
            grey.Children.Add(new Ellipse { Fill = new LinearGradientBrush(Color.FromRgb(0xA8, 0xA8, 0xAE), Color.FromRgb(0x86, 0x86, 0x8C), 90) });
            var silhouette = Glyphs.Fill(Glyphs.Person, 20, Brushes.White);
            silhouette.HorizontalAlignment = HorizontalAlignment.Center;
            silhouette.VerticalAlignment = VerticalAlignment.Bottom;
            silhouette.Margin = new Thickness(0, 0, 0, 4);
            grey.Children.Add(silhouette);
            face = grey;
        }
        face.Margin = new Thickness(0, 0, 9, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = account.Name, FontWeight = FontWeights.SemiBold, Foreground = _text, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock { Text = _s[account.Microsoft ? "account.microsoft" : "account.local"], FontSize = 11, Foreground = _dim, TextTrimming = TextTrimming.CharacterEllipsis });
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(face, System.Windows.Controls.Dock.Left);
        dock.Children.Add(face);
        dock.Children.Add(words);
        var row = new Border { Child = dock, Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Arrow };
        row.MouseEnter += (_, _) => row.Background = _hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a drag of the window
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; CommandRunner.Launch("ms-settings:yourinfo"); };
        row.ToolTip = _s["account.tip"];
        AutomationProperties.SetName(row, account.Name);
        AccountItem = row; // not among the page's controls: those go each time a page is shown
        return row;
    }

    /// <summary>The account row at the sidebar's top (diagnostics).</summary>
    public FrameworkElement? AccountItem { get; private set; }

    /// <summary>The account's picture, read once (96 px) and kept while the shell runs; null for none or one that cannot be read.</summary>
    static ImageSource? PictureOf(string? path)
    {
        if (path == null) return null;
        if (_accountPicture is { } kept && kept.Path == path) return kept.Picture;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 96;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            _accountPicture = (path, image);
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            Frostbar.Platform.Infrastructure.Log.Debug("settings: the account picture cannot be read: " + ex.Message);
            return null;
        }
    }

    // ---- Accessibility ------------------------------------------------------------------------------------------

    static readonly Color AccessibilityBlue = Color.FromRgb(0x0A, 0x84, 0xFF);

    /// <summary>
    /// macOS's Accessibility for the shell's own windows: Display (reduce transparency, increase contrast) and Motion
    /// (reduce motion, and the animations themselves), then Windows' own settings for the rest (text size, filters).
    /// </summary>
    void AccessibilityPage()
    {
        var x = _settings.Accessibility;
        var a = _settings.Appearance;
        Add(PageHeader(Glyphs.AccessibilityFigure, stroke: true, AccessibilityBlue, _s["set.accessibility"], _s["acc.header"]));

        Add(Caption(_s["acc.display"]));
        // Reduce transparency is the appearance's opaque background (one setting shown on both pages).
        var transparency = Switch("accessibility.reduceTransparency", a.Material == "solid", on =>
        {
            a.Material = on ? "solid" : Frostbar.Core.Settings.AppearanceSettings.DefaultMaterial;
            _host.Apply(SettingsArea.Appearance);
        });
        var contrast = Switch("accessibility.increaseContrast", x.IncreaseContrast, on =>
        {
            x.IncreaseContrast = on;
            _host.Apply(SettingsArea.Appearance); // the glass and this window drawn again with their borders
        });
        Add(Group(Row(_s["acc.reduceTransparency"], transparency, _s["acc.reduceTransparencyDetail"]),
            Row(_s["acc.increaseContrast"], contrast, _s["acc.increaseContrastDetail"])));

        Add(Caption(_s["acc.motion"]));
        var motion = Switch("accessibility.reduceMotion", x.ReduceMotion, on => { x.ReduceMotion = on; _host.Apply(SettingsArea.Motion); });
        // The animations (the appearance's choice, here too: macOS keeps its motion settings on this page).
        string[] modes = ["system", "off", "on"];
        var animations = Choice("accessibility.animations", [_s["set.followWindows"], _s["set.on"], _s["set.off"]], Array.IndexOf(modes, a.ReduceMotion),
            i => { a.ReduceMotion = modes[i]; _host.Apply(SettingsArea.Appearance); });
        Add(Group(Row(_s["acc.reduceMotion"], motion, _s["acc.reduceMotionDetail"]), Row(_s["acc.animations"], animations, _s["set.animationsDetail"])));

        var text = Footer(_s["acc.textSize"], () => CommandRunner.Launch("ms-settings:easeofaccess-display"));
        text.Margin = new Thickness(0, 0, 8, 12);
        var windows = Footer(_s["acc.windowsSettings"], () => CommandRunner.Launch("ms-settings:easeofaccess"));
        Controls["accessibility.textSize"] = text;
        Controls["footer"] = windows;
        var both = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        both.Children.Add(text);
        both.Children.Add(windows);
        Add(both);
    }

    /// <summary>
    /// The big header macOS's General and Accessibility pages begin with: the page's coloured square, larger, its name
    /// and a line of what it is for, in the middle of a card.
    /// </summary>
    Border PageHeader(Geometry glyph, bool stroke, Color color, string title, string text)
    {
        var picture = stroke ? Glyphs.Stroke(glyph, 34, Brushes.White, 2.6) : Glyphs.Fill(glyph, 34, Brushes.White);
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        picture.VerticalAlignment = VerticalAlignment.Center;
        var square = new Border
        {
            Width = 60, Height = 60, CornerRadius = new CornerRadius(14), Child = picture, HorizontalAlignment = HorizontalAlignment.Center,
            Background = new LinearGradientBrush(Lighter(color, 0.12), color, 90),
        };
        var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        stack.Children.Add(square);
        stack.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = _text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 4) });
        stack.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = _dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        var (edge, width) = CardEdge();
        var card = new Border
        {
            Background = _card, CornerRadius = new CornerRadius(10), Child = stack, Margin = new Thickness(0, 0, 0, 12),
            BorderBrush = edge, BorderThickness = new Thickness(width),
        };
        Controls["page.header"] = card;
        return card;
    }

    static Color Lighter(Color c, double by) => Color.FromRgb((byte)(c.R + (255 - c.R) * by), (byte)(c.G + (255 - c.G) * by), (byte)(c.B + (255 - c.B) * by));

    // ---- Appearance: the accent colour and the scroll bars ------------------------------------------------------

    /// <summary>
    /// macOS's accent colours as a row of circles, the chosen one with a white dot in it and its name under the row
    /// (Windows' own first, drawn as the colours of a wheel: macOS's "Multicolour"). ← → move the choice.
    /// </summary>
    FrameworkElement AccentRow()
    {
        var a = _settings.Appearance;
        var circles = new StackPanel { Orientation = Orientation.Horizontal };
        var name = new TextBlock { FontSize = 11, Foreground = _dim, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 2, 0) };
        var holder = new StackPanel { Focusable = true };
        holder.Children.Add(circles);
        holder.Children.Add(name);
        var dots = new Dictionary<string, Border>();
        void Mark()
        {
            foreach (var (n, d) in dots)
            {
                bool on = n == a.Accent;
                ((Grid)d.Child).Children[1].Visibility = on ? Visibility.Visible : Visibility.Collapsed; // the white dot
                d.BorderBrush = on ? new SolidColorBrush(_t.Dark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x59, 0, 0, 0)) : Brushes.Transparent;
            }
            name.Text = _s["accent." + a.Accent];
        }
        void Choose(string n)
        {
            if (n == a.Accent) return;
            a.Accent = n;
            Mark();
            _host.Apply(SettingsArea.Appearance); // the theme's accent: this window is made again in it
        }
        foreach (var n in AccentColors.Names)
        {
            Brush fill = AccentColors.Of(n, _t.Dark) is { } c ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B))
                : new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromRgb(0xFF, 0x45, 0x3A), 0), new(Color.FromRgb(0xFF, 0x9F, 0x0A), 0.25), new(Color.FromRgb(0x30, 0xD1, 0x58), 0.5),
                    new(Color.FromRgb(0x0A, 0x84, 0xFF), 0.75), new(Color.FromRgb(0xBF, 0x5A, 0xF2), 1),
                }, 45);
            // The colour, the chosen one's white dot over it, and a ring round the chosen one (the border).
            var inner = new Grid();
            inner.Children.Add(new Ellipse { Fill = fill });
            inner.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            var dot = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5), Padding = new Thickness(1.5),
                Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Arrow, Background = Brushes.Transparent, Child = inner,
            };
            dot.ToolTip = _s["accent." + n];
            AutomationProperties.SetName(dot, _s["accent." + n]);
            dot.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a drag of the window
            string chosen = n;
            dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; Choose(chosen); };
            dots[n] = dot;
            Controls["appearance.accent." + n] = dot;
            circles.Children.Add(dot);
        }
        holder.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            e.Handled = true;
            int at = Array.IndexOf(AccentColors.Names, a.Accent) + (e.Key == Key.Right ? 1 : -1);
            if (at >= 0 && at < AccentColors.Names.Length) Choose(AccentColors.Names[at]);
        };
        AutomationProperties.SetName(holder, _s["set.accent"]);
        Controls["appearance.accent"] = holder;
        Mark();
        return Row(_s["set.accent"], holder, _s["set.accentDetail"]);
    }

    /// <summary>macOS's "Show scroll bars" and "Click in the scroll bar to", for the shell's own lists.</summary>
    Border ScrollBarsGroup()
    {
        var a = _settings.Appearance;
        var shows = Choice("appearance.scrollBars", [_s["set.scrollWhen"], _s["set.scrollAlways"]], Array.IndexOf(ScrollBarChoices.Shows, a.ScrollBars),
            i => { a.ScrollBars = ScrollBarChoices.Shows[i]; _host.Apply(SettingsArea.ScrollBars); });
        var click = new MacControls.PopUp([_s["set.scrollPage"], _s["set.scrollSpot"]], Math.Max(0, Array.IndexOf(ScrollBarChoices.Clicks, a.ScrollBarClick)), _t,
            i => { a.ScrollBarClick = ScrollBarChoices.Clicks[i]; _host.Apply(SettingsArea.ScrollBars); });
        Controls["appearance.scrollBarClick"] = click;
        return Group(Row(_s["set.scrollBars"], shows), Row(_s["set.scrollClick"], click));
    }

    // ---- Menu bar: the battery's percentage, flashing separators, recent items ----------------------------------

    /// <summary>"Show battery percentage" (under the items: the battery's own option, as macOS's menu bar page has it).</summary>
    FrameworkElement BatteryPercentRow()
    {
        var m = _settings.MenuBar;
        return Row(_s["bar.batteryPercent"], Switch("menuBar.batteryPercent", m.BatteryPercent, on => { m.BatteryPercent = on; _host.Apply(SettingsArea.Battery); }));
    }

    /// <summary>macOS's "Recent documents, applications and servers": none, 5 … 50 of each.</summary>
    Border RecentCountGroup()
    {
        var m = _settings.MenuBar;
        string[] labels = [.. RecentItems.Counts.Select(n => n == 0 ? _s["set.none"] : n.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        var count = new MacControls.PopUp(labels, Math.Max(0, Array.IndexOf(RecentItems.Counts, m.RecentCount)), _t,
            i => { m.RecentCount = RecentItems.Counts[i]; _host.Apply(SettingsArea.RecentItems); });
        Controls["menuBar.recentCount"] = count;
        return Group(Row(_s["bar.recentCount"], count, _s["bar.recentCountDetail"]));
    }

    // ---- Search: the kinds of results ---------------------------------------------------------------------------

    /// <summary>Spotlight's "Search results": a switch for each kind (on: in the answer). The settings keep those turned off.</summary>
    void SearchResultsPart()
    {
        var q = _settings.Search;
        Add(Caption(_s["search.results"]));
        Add(Group([.. SearchCategories.Names.Select(name => Row(_s["searchCat." + name], Switch("search.kind." + name, !q.Hidden.Contains(name), on =>
        {
            q.Hidden = on ? [.. q.Hidden.Where(h => h != name)] : [.. q.Hidden.Where(h => h != name), name];
            _host.Apply(SettingsArea.Other); // read as the search is used
        })))]));
        Add(Note(_s["search.resultsNote"]));
    }

    // ---- Dock: the windows ---------------------------------------------------------------------------------------

    /// <summary>
    /// Desktop &amp; Dock's default web browser (its name, and Windows' default apps to change it: Windows keeps that
    /// choice to itself) and Desktop's "Show items on desktop" (Windows' desktop icons, its own setting).
    /// </summary>
    void DesktopPart()
    {
        var browser = new TextBlock { Text = "…", Foreground = _dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
        Controls["dock.browserName"] = browser;
        OffUi(DefaultBrowser.Name, name => browser.Text = name ?? _s["dock.browserUnknown"]);
        var change = Button(_s["dock.browserChange"], () => CommandRunner.Launch("ms-settings:defaultapps"));
        Controls["dock.browserChange"] = change;
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(browser);
        right.Children.Add(change);
        Add(Group(Row(_s["dock.browser"], right)));

        Add(Caption(_s["dock.desktop"]));
        // Windows' own: written off the UI thread (Explorer is asked and waited for), the switch put back if it failed.
        PillSwitch? icons = null;
        icons = Switch("desktop.items", DesktopItems.Shown(), on => OffUi(() => DesktopItems.Set(on), error =>
        {
            if (error == null || icons == null) return;
            Frostbar.Platform.Infrastructure.Log.Warn("settings: desktop items: " + error);
            icons.IsOn = DesktopItems.Shown();
        }));
        Add(Group(Row(_s["dock.desktopItems"], icons, _s["dock.desktopItemsDetail"])));
    }

    /// <summary>Desktop &amp; Dock's Windows: margins round tiled windows (the shell's), and Windows' Snap (its own setting).</summary>
    void WindowsPart()
    {
        var w = _settings.Window;
        Add(Caption(_s["dock.windows"]));
        var margins = Switch("window.tileMargins", w.TileMargins, on => { w.TileMargins = on; _host.Apply(SettingsArea.Other); });
        // Windows' setting: written off the UI thread (it tells every window); not taken, the switch is put back to
        // what Windows has (as the desktop items' just above: it went on showing the new state).
        PillSwitch? snap = null;
        snap = Switch("window.snap", WindowSnap.On(), on => OffUi(() => WindowSnap.Set(on), error =>
        {
            if (error == null || snap == null) return;
            snap.IsOn = WindowSnap.On();
        }));
        Add(Group(Row(_s["dock.tileMargins"], margins, _s["dock.tileMarginsDetail"]), Row(_s["dock.snap"], snap, _s["dock.snapDetail"])));
    }

    // ---- Sound: the effects --------------------------------------------------------------------------------------

    /// <summary>Sound's "Sound effects" (at the top of macOS's page): the shell's interface sounds, and the volume's feedback.</summary>
    void SoundEffectsPart()
    {
        var x = _settings.Sound;
        Add(Caption(_s["sound.effects"]));
        Add(Group(
            Row(_s["sound.uiSounds"], Switch("sound.uiSounds", x.InterfaceSounds, on => { x.InterfaceSounds = on; _host.Apply(SettingsArea.Other); }), _s["sound.uiSoundsDetail"]),
            Row(_s["sound.volumeFeedback"], Switch("sound.volumeFeedback", x.VolumeFeedback, on => { x.VolumeFeedback = on; _host.Apply(SettingsArea.Other); }), _s["sound.volumeFeedbackDetail"])));
    }
}
