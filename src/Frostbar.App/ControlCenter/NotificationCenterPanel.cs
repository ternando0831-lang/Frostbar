using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Localization;
using Frostbar.Core.Shell;
using Frostbar.Core.Time;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Sys;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.ControlCenter;

/// <summary>
/// What the notification centre reads and does (Windows' notification centre underneath, and the tray apps' balloons).
/// <see cref="Open"/> is a card's click: its app opens, or a balloon's app hears the click. <see cref="Clear"/> ends once
/// they are gone from Windows (off the UI thread).
/// </summary>
public sealed record NotificationCenterServices(
    Func<Task<IReadOnlyList<Notice>>> Read, Func<uint, bool> Remove, Func<Task<bool>> Clear, Func<string> Access,
    Func<bool?> DoNotDisturb, Func<string, string, ImageSource?> Icon, Action<Notice> Open);

/// <summary>
/// The notification centre (macOS's, opened from the clock): Windows' notifications grouped by app, newest first, each
/// dismissable, a click opening its app; then a calendar of the month with Japan's holidays. Like the Control Center
/// it never takes the foreground and closes on an outside click or Esc. Windows tells an app without a package
/// nothing when notifications come and go, so the list is read again every 2 seconds while the panel is open.
/// </summary>
public sealed class NotificationCenterPanel : PanelPopup
{
    public const double PanelWidth = 356;
    const int ShownPerApp = 3;

    readonly ThemeSnapshot _t;
    readonly Strings _s;
    readonly NotificationCenterServices _sv;
    readonly Action _close;
    readonly Brush _tile, _hover, _red, _accent;
    readonly StackPanel _list = new();
    readonly ScrollViewer _scroll;
    readonly Border _clearAll;
    readonly StackPanel _calendar = new();
    readonly DispatcherTimer _poll;
    readonly HashSet<string> _expanded = new();
    IReadOnlyList<Notice> _items = [];
    /// <summary>The notifications the list shows now (null: none shown yet, or the note about access).</summary>
    uint[]? _shownIds;
    DateOnly _month;
    bool _read, _waiting, _closed;
    int _reads;

    /// <summary>The notifications shown, their cards by id, and the dismiss buttons (diagnostics: the specs click them).</summary>
    public IReadOnlyList<Notice> Shown => _items;
    /// <summary>The ids the list shows now (diagnostics; null before the first answer is shown). <see cref="Shown"/> is what was read.</summary>
    public IReadOnlyList<uint>? ListedIds => _shownIds;
    public Dictionary<uint, Border> Cards { get; } = new();
    public Dictionary<uint, Border> DismissButtons { get; } = new();
    public FrameworkElement ClearAllButton => _clearAll;

    /// <summary>How many notifications the list showed as the panel was made (-1: none read yet; diagnostics, "A3").</summary>
    public int ListedAtOpen { get; }

    /// <summary>The apps' "N more" capsules by app (diagnostics: a spec opens one).</summary>
    public Dictionary<string, Border> MoreButtons { get; } = new();
    public DateOnly Month => _month;
    public string? AccessProblem { get; private set; }

    /// <param name="pieces">The glass of each card and part (macOS 27: no panel under them; Materials.Piece).</param>
    public NotificationCenterPanel(ThemeSnapshot t, Material m, Material pieces, Strings s, NotificationCenterServices services, Action close) : base(t, m)
    {
        _t = t;
        _s = s;
        _sv = services;
        _close = close;
        _tile = new SolidColorBrush(t.Dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        _hover = new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        _red = new SolidColorBrush(SystemTint.Red(t.Dark)); // the system red of the appearance (C7)
        _accent = new SolidColorBrush(Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B));
        Body.Margin = new Thickness(10);
        Body.MinWidth = 0;
        Body.Width = PanelWidth - 20;

        // macOS 27's form (the user's "A", 2026-10-18; `Lab mock cc`): no panel; "Clear All" a small capsule of glass at
        // the right, each card, the calendar and the settings' link their own glass, the apps' names on the wallpaper.
        _clearAll = new Border
        {
            Child = new TextBlock { Text = s["nc.clearAll"], FontSize = 12 }, Padding = new Thickness(14, 4, 14, 5), CornerRadius = new CornerRadius(13),
            Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed,
        };
        _clearAll.MouseLeftButtonUp += (_, e) => { e.Handled = true; ClearAll(); };
        KeyNav.SetRun(_clearAll, ClearAll); // D2
        GlassBounce.Attach(_clearAll, () => Animate); // macOS 27's glass gives under a press (the user's "A")
        _fixed.Add((_clearAll, 13, false));
        Body.Children.Add(_clearAll);

        _scroll = new ScrollViewer
        {
            Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height - 420), // the calendar and the margins below
        };
        MacScrollBar.Apply(_scroll, () => Animate);
        SmoothScroll.Attach(_scroll, () => Animate);
        Body.Children.Add(_scroll);

        // The calendar
        _month = DateOnly.FromDateTime(DateTime.Today);
        var calendarTile = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(GlassShape.Widget), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 10, 0, 0), Child = _calendar };
        PressLight.SetOff(calendarTile, true); // its arrows light, the tile itself does nothing pressed (C4)
        _fixed.Add((calendarTile, GlassShape.Widget, true));
        Body.Children.Add(calendarTile);
        ShowCalendar();

        var settings = new Border
        {
            Child = new TextBlock { Text = s["nc.settings"], FontSize = 12 }, Padding = new Thickness(16, 5, 16, 6), CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 2),
        };
        settings.MouseLeftButtonUp += (_, e) => { e.Handled = true; _close(); CommandRunner.Launch("ms-settings:notifications"); };
        KeyNav.SetRun(settings, () => { _close(); CommandRunner.Launch("ms-settings:notifications"); }); // D2
        GlassBounce.Attach(settings, () => Animate);
        _fixed.Add((settings, 14, false));
        Body.Children.Add(settings);
        UsePieces(Pieces, pieces);

        // What the last panel read, at once (the read below brings what changed since, moving).
        if (_lastRead != null) { _items = _lastRead; _read = true; }
        ShowList();
        ListedAtOpen = _shownIds?.Length ?? -1;
        _ = ReadAsync();
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = ReadAsync(), Dispatcher);
        _poll.Start();
    }

    public override bool FadesIn => true;

    /// <summary>Reads Windows' notifications again (the panel's timer, and after a dismissal).</summary>
    public async Task ReadAsync()
    {
        // None while they are being cleared: a read before they are gone brings them all back (the clear reads after).
        if (_clearing) return;
        // The latest read wins: an earlier one that ends after it would bring back a notification just dismissed.
        int read = ++_reads;
        string access = _sv.Access();
        if (access != "Allowed")
        {
            bool changed = AccessProblem != access || !_read;
            AccessProblem = access;
            _read = true;
            _lastRead = null;
            if (changed) ShowWhenUp();
            return;
        }
        AccessProblem = null;
        var items = await _sv.Read();
        if (read != _reads || _closed) return;
        _items = _lastRead = items;
        _read = true;
        // Cards on their way out: the list is made again (with this answer) once they have gone. Made again now, they
        // went in a frame (2026-10-17: the reads after a dismissal made it again for an app's icon still loading).
        if (_leaving.Count > 0) return;
        // Made again only when something came or went against what is shown (a card under the pointer stays, and
        // keeps its click), or for the apps' icons that were still loading (a few times: an icon that cannot be read
        // stays the bell).
        bool same = _shownIds != null && items.Select(i => i.Id).SequenceEqual(_shownIds);
        bool icons = _missingIcons && _iconRetries < 3;
        if (same && !icons) return;
        if (same) _iconRetries++;
        ShowWhenUp();
    }

    /// <summary>
    /// Shows the list now, or once the mouse button is up: cards made again under a pressed button lose its click. The
    /// clock opens the panel on the press, so the first answer often came while the button was still down, and was
    /// then never shown: the reads after it found nothing new against what had been read (2026-10-14).
    /// </summary>
    void ShowWhenUp()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) { ShowList(); return; }
        if (_waiting) return;
        _waiting = true;
        Dispatcher.BeginInvoke(async () =>
        {
            while (Mouse.LeftButton == MouseButtonState.Pressed && !_closed) await Task.Delay(100);
            _waiting = false;
            if (!_closed) ShowList();
        });
    }

    bool _missingIcons;
    int _iconRetries;

    /// <summary>
    /// What the last panel read (shown at once by the next one: it opened empty and the cards came a read later, about
    /// 0.25 s, pushing the calendar down, 2026-10-19). Null: nothing read yet, or no access then.
    /// </summary>
    static IReadOnlyList<Notice>? _lastRead;

    /// <summary>
    /// Makes the list again, moving (ListMotion): a notification that came grows in from above, pushing the others
    /// down; one that went closes its room; an app's cards opened ("N more") grow in, closed ("Show Less") close up.
    /// They were made again at once (2026-10-19, the user's "B3"). The glass of what goes stays with it while it shrinks.
    /// </summary>
    void ShowList()
    {
        var before = ListMotion.Take(_list);
        var oldParts = _parts.ToList();
        Fill();
        var going = ListMotion.Show(_list, before, Animate, ComeFrom, gone => _parts.RemoveAll(p => gone.Contains(p.e)));
        foreach (var p in oldParts) if (going.Contains(p.e)) _parts.Add(p);
    }

    /// <summary>How far a card that came slides down as it grows in (DIPs).</summary>
    public const double ComeFrom = 10;

    void Fill()
    {
        _missingIcons = false;
        _list.Children.Clear();
        _parts.Clear();
        Cards.Clear();
        DismissButtons.Clear();
        MoreButtons.Clear();
        _shownIds = AccessProblem == null && _read ? _items.Select(i => i.Id).ToArray() : null;
        if (_sv.DoNotDisturb() == true)
        {
            var dnd = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 8), Tag = DndLine };
            dnd.Children.Add(Glyphs.Fill(Glyphs.Moon, 13, _accent));
            dnd.Children.Add(OnWallpaper(new TextBlock { Text = _s["nc.dndOn"], FontSize = 12, Foreground = Dim, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }));
            _list.Children.Add(ListMotion.Keyed(dnd, "dnd"));
        }
        if (AccessProblem != null)
        {
            _list.Children.Add(ListMotion.Keyed(Note(Words(_s["nc.noAccess"])), "note:access"));
            var access = LinkRow(_s["nc.accessSettings"], () => { _close(); CommandRunner.Launch("ms-settings:privacy-notifications"); });
            _parts.Add((access, GlassShape.PanelRow, false)); // its own glass, on the wallpaper
            _list.Children.Add(ListMotion.Keyed(access, "link:access"));
            _clearAll.Visibility = Visibility.Collapsed;
            return;
        }
        _clearAll.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count == 0)
        {
            if (_read) _list.Children.Add(ListMotion.Keyed(Note(new TextBlock { Text = _s["nc.none"], Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) }), "note:none"));
            return;
        }
        var now = DateTimeOffset.Now;
        foreach (var g in NotificationGroups.Group(_items))
        {
            string key = g.AppId.Length > 0 ? g.AppId : g.App;
            bool open = _expanded.Contains(key);
            // The app's line: its icon and name, "Show Less" when opened, × for the whole group (the line goes with its cards).
            var groupHead = new DockPanel { LastChildFill = true, Margin = new Thickness(4, 4, 2, 4) };
            var groupParts = new List<FrameworkElement>();
            var clear = SmallButton(Glyphs.Cross, () => Dismiss(g.Items.Select(i => i.Id).ToList(), groupParts));
            clear.Background = Brushes.Transparent; // its own glass, on the wallpaper
            GlassBounce.Attach(clear, () => Animate); // it gave nothing under a press (C4)
            _parts.Add((clear, 10, false));
            DockPanel.SetDock(clear, System.Windows.Controls.Dock.Right);
            groupHead.Children.Add(clear);
            if (open)
            {
                var less = OnWallpaper(new TextBlock { Text = _s["nc.showLess"], FontSize = 11.5, Foreground = Dim, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                less.MouseLeftButtonUp += (_, e) => { e.Handled = true; _expanded.Remove(key); ShowList(); };
                KeyNav.SetRun(less, () => { _expanded.Remove(key); ShowList(); }); // D2
                GlassBounce.Attach(less, () => Animate);
                DockPanel.SetDock(less, System.Windows.Controls.Dock.Right);
                groupHead.Children.Add(less);
            }
            groupHead.Children.Add(OnWallpaper(new TextBlock { Text = g.App.Length > 0 ? g.App : _s["nc.unknownApp"], FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }));
            _list.Children.Add(ListMotion.Keyed(groupHead, "app:" + key));
            groupParts.Add(groupHead);
            var shown = open ? g.Items : g.Items.Take(ShownPerApp).ToList();
            foreach (var n in shown) _list.Children.Add(ListMotion.Keyed(Card(n, now), "card:" + n.Id));
            if (!open && g.Items.Count > ShownPerApp)
            {
                var more = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 4, 14, 5), Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new TextBlock { Text = _s.F("nc.more", g.Items.Count - ShownPerApp), FontSize = 12, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center } };
                more.MouseEnter += (_, _) => more.Background = _hover;
                more.MouseLeave += (_, _) => more.Background = Brushes.Transparent;
                more.MouseLeftButtonUp += (_, e) => { e.Handled = true; _expanded.Add(key); ShowList(); };
                KeyNav.SetRun(more, () => { _expanded.Add(key); ShowList(); }); // D2
                GlassBounce.Attach(more, () => Animate);
                _list.Children.Add(ListMotion.Keyed(more, "more:" + key));
                MoreButtons[g.App] = more;
                _parts.Add((more, 12, false));
                groupParts.Add(more);
            }
        }
    }

    TextBlock Words(string text) => new() { Text = text, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 4) };

    // ---- macOS 27's pieces of glass ----------------------------------------------------------------------------------

    // The parts that are glass: the fixed ones (Clear All, the calendar, the settings' link) and the list's (cards, the
    // apps' ×, "more", the notes), made again with the list.
    readonly List<(FrameworkElement e, double radius, bool shadow)> _fixed = new(), _parts = new();

    IEnumerable<Piece> Pieces()
    {
        foreach (var (e, r, s) in _parts) yield return new Piece(e, r, s);
        foreach (var (e, r, s) in _fixed) yield return new Piece(e, r, s);
    }

    /// <summary>Words on the wallpaper itself (an app's name): a soft shadow of the other tone under them, to read on any wallpaper.</summary>
    T OnWallpaper<T>(T e) where T : UIElement
    {
        e.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = _t.Dark ? Colors.Black : Colors.White, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.7 };
        return e;
    }

    /// <summary>A note in the list (none, no access): a card of glass holding it.</summary>
    Border Note(UIElement child)
    {
        var b = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(GlassShape.Card), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Child = child };
        PressLight.SetOff(b, true); // words only: a press does nothing, so nothing lights (C4)
        _parts.Add((b, GlassShape.Card, true));
        return b;
    }

    /// <summary>A notification: the app's icon, its title and text, when; × under the pointer; a click opens the app.</summary>
    Border Card(Notice n, DateTimeOffset now)
    {
        var img = _sv.Icon(n.AppId, n.App);
        if (img == null && n.AppId.Length > 0) _missingIcons = true;
        var icon = img != null ? (FrameworkElement)new Image { Source = img, Width = 28, Height = 28 }
            : new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = _hover, Child = Glyphs.Stroke(Glyphs.Bell, 15, Text, 1.4) };
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 10, 0);
        var when = new TextBlock { Text = NotificationGroups.When(n.Created, now, _s), FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 1, 0, 0) };
        var dismiss = SmallButton(Glyphs.Cross, () => Dismiss([n.Id]));
        dismiss.VerticalAlignment = VerticalAlignment.Top;
        dismiss.Visibility = Visibility.Hidden;
        var right = new Grid { VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(when);
        right.Children.Add(dismiss);
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = n.Title.Length > 0 ? n.Title : n.App, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (n.Body.Length > 0) words.Children.Add(new TextBlock { Text = n.Body, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 50, Margin = new Thickness(0, 1, 0, 0) });
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(right, System.Windows.Controls.Dock.Right);
        dock.Children.Add(icon);
        dock.Children.Add(right);
        dock.Children.Add(words);
        var card = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(GlassShape.Card), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = dock };
        _parts.Add((card, GlassShape.Card, true));
        GlassBounce.Attach(card, () => Animate);
        card.MouseEnter += (_, _) => { dismiss.Visibility = Visibility.Visible; when.Visibility = Visibility.Hidden; };
        card.MouseLeave += (_, _) => { dismiss.Visibility = Visibility.Hidden; when.Visibility = Visibility.Visible; };
        // A tray balloon is answered by its icon's number, its app's path not needed (one whose path could not be read
        // never heard its click).
        bool opens = n.AppId.Length > 0 || Frostbar.Core.Shell.BalloonList.IsBalloon(n.Id);
        OnClick(card, () =>
        {
            Frostbar.Platform.Infrastructure.Log.Debug($"notification centre: card 0x{n.Id:X8} clicked{(opens ? "" : " (no app)")}");
            if (!opens) return;
            _close();
            _sv.Open(n);
        });
        KeyNav.SetRun(card, () => { if (!opens) return; _close(); _sv.Open(n); }); // D2
        Cards[n.Id] = card;
        DismissButtons[n.Id] = dismiss;
        return card;
    }

    Border SmallButton(Geometry glyph, Action click)
    {
        var b = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = _hover, Child = Glyphs.Stroke(glyph, 10, Text, 1.6) };
        OnClick(b, click);
        KeyNav.SetRun(b, click); // D2
        return b;
    }

    /// <summary>
    /// A click on <paramref name="target"/>: a press that began on it, let go on it (as ViewKit.OnClick). A release only,
    /// after a press elsewhere (or with no give, under reduced motion), opened the app or dismissed the card.
    /// </summary>
    static void OnClick(UIElement target, Action click)
    {
        bool pressed = false;
        target.MouseLeftButtonDown += (_, _) => pressed = true;
        target.MouseLeave += (_, _) => pressed = false;
        target.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!pressed) return;
            pressed = false;
            click();
        };
    }

    /// <param name="alsoGoing">The rest of what goes with them (a whole app's line and its "more").</param>
    void Dismiss(IReadOnlyList<uint> ids, IReadOnlyList<FrameworkElement>? alsoGoing = null)
    {
        foreach (var id in ids) _sv.Remove(id);
        _items = _lastRead = _items.Where(i => !ids.Contains(i.Id)).ToList();
        var going = ids.Where(Cards.ContainsKey).ToDictionary(id => id, id => Cards[id]);
        if (!Animate || going.Count == 0) { ShowList(); _ = ReadAsync(); return; }
        // The list shows what is left already: a read that agrees makes nothing again while they go.
        _shownIds = _shownIds?.Where(id => !ids.Contains(id)).ToArray();
        foreach (var (id, card) in going) _leaving[id] = card;
        Leave([.. going.Values, .. alsoGoing ?? []]);
        _ = ReadAsync();
    }

    /// <summary>
    /// They all go at once, and the list is read again once Windows has dismissed them (one by one, off the UI thread).
    /// Read at once, it had them all still, and they came back as the cards finished going (2026-10-21).
    /// </summary>
    async void ClearAll()
    {
        if (_clearing) return;
        _clearing = true;
        _reads++; // a read on its way has them all
        var going = Cards.ToDictionary(kv => kv.Key, kv => kv.Value);
        _items = _lastRead = [];
        if (!Animate || going.Count == 0) ShowList();
        else
        {
            _shownIds = [];
            foreach (var (id, card) in going) _leaving[id] = card;
            Leave([.. _list.Children.OfType<FrameworkElement>().Where(e => !Equals(e.Tag, DndLine))]); // "Do Not Disturb is on" stays
        }
        try { await _sv.Clear(); }
        finally { _clearing = false; }
        if (!_closed) _ = ReadAsync();
    }

    bool _clearing;

    const string DndLine = "dnd";

    // ---- going away ------------------------------------------------------------------------------------------------
    // A card dismissed (or a whole app's, or all) slides a little to the right as it fades, and its room closes up, the
    // cards under it moving up into it (macOS's): 0.22 s, then the list is made again. It was made again at once, the
    // card gone in a frame and the ones under it jumping up (2026-10-17).

    public const double LeaveMs = Motion.CardLeave, LeaveSlide = 24;
    readonly Dictionary<uint, Border> _leaving = new();
    int _leaves;

    /// <summary>The cards on their way out (diagnostics: they shrink and fade before the list is made again).</summary>
    public IReadOnlyDictionary<uint, Border> Leaving => _leaving;

    void Leave(IReadOnlyList<FrameworkElement> going)
    {
        int leave = ++_leaves;
        var ease = Tween.InOut;
        // All of it as one movement (Tween.Together): each card's slide, fade, margins and height, and every card going.
        // Each its own, the height and the margins could round a refresh apart, and one card's room close a frame
        // before another's.
        var parts = new List<Tween.Part>();
        bool slides = !ReduceMotion.Calms("cardLeave"); // reduced motion: it fades as its room closes, without sliding off
        foreach (var e in going)
        {
            e.IsHitTestVisible = false;
            e.ClipToBounds = true;
            e.Height = e.ActualHeight;
            var slide = new TranslateTransform();
            e.RenderTransform = slide;
            var m = e.Margin;
            if (slides) parts.Add(new(slide, TranslateTransform.XProperty, LeaveSlide, ease));
            parts.Add(new(e, OpacityProperty, 0.0, Tween.Linear, 0.7));
            parts.Add(new(e, MarginProperty, new Thickness(m.Left, 0, m.Right, 0), ease));
            // A card is never less tall than its padding: that goes too (it stopped at 22 px, 2026-10-17).
            if (e is Border b)
            {
                var p = b.Padding;
                parts.Add(new(b, Border.PaddingProperty, new Thickness(p.Left, 0, p.Right, 0), ease));
            }
            parts.Add(new(e, HeightProperty, 0.0, ease));
        }
        Tween.Together(LeaveMs, parts, () =>
        {
            if (leave != _leaves || _closed) return; // a later one makes the list again
            _leaving.Clear();
            ShowList();
        });
    }

    // ---- the calendar ---------------------------------------------------------------------------------------------

    /// <summary>Shows the month <paramref name="by"/> months from the one shown (0: today's month).</summary>
    public void TurnMonth(int by)
    {
        var first = new DateOnly(_month.Year, _month.Month, 1);
        _month = by == 0 ? DateOnly.FromDateTime(DateTime.Today) : first.AddMonths(by);
        ShowCalendar();
    }

    void ShowCalendar()
    {
        _calendar.Children.Clear();
        var culture = _s.Culture;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var first = new DateOnly(_month.Year, _month.Month, 1);
        bool japanese = _s.Language == "ja";
        var start = first.AddDays(-(int)first.DayOfWeek);
        int weeks = (int)Math.Ceiling(((int)first.DayOfWeek + DateTime.DaysInMonth(first.Year, first.Month)) / 7.0);
        // The grid's days of the months around too (January 1st at the end of December's grid is red).
        var holidays = new Dictionary<DateOnly, string>();
        if (japanese)
            foreach (int year in new[] { start.Year, first.Year, start.AddDays(weeks * 7 - 1).Year }.Distinct())
                foreach (var (d, name) in JapaneseHolidays.Of(year)) holidays[d] = name;

        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        var arrows = new StackPanel { Orientation = Orientation.Horizontal };
        arrows.Children.Add(Arrow(Glyphs.Back, () => TurnMonth(-1)));
        var todayButton = new TextBlock { Text = _s["nc.today"], FontSize = 11.5, Foreground = Dim, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        todayButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; TurnMonth(0); };
        KeyNav.SetRun(todayButton, () => TurnMonth(0)); // D2
        arrows.Children.Add(todayButton);
        arrows.Children.Add(Arrow(Glyphs.Chevron, () => TurnMonth(1)));
        DockPanel.SetDock(arrows, System.Windows.Controls.Dock.Right);
        head.Children.Add(arrows);
        head.Children.Add(new TextBlock { Text = first.ToString(japanese ? "yyyy年 M月" : "MMMM yyyy", culture), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _calendar.Children.Add(head);

        var grid = new UniformGrid { Columns = 7 };
        // The week starts on Sunday (as Japanese and US calendars have it).
        for (int i = 0; i < 7; i++)
        {
            var name = culture.DateTimeFormat.GetShortestDayName((DayOfWeek)i);
            grid.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = i == 0 ? _red : Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
        }
        for (int i = 0; i < weeks * 7; i++)
        {
            var d = start.AddDays(i);
            bool inMonth = d.Month == first.Month;
            bool red = d.DayOfWeek == DayOfWeek.Sunday || holidays.ContainsKey(d);
            var number = new TextBlock
            {
                Text = d.Day.ToString(), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = d == today ? Brushes.White : red ? _red : Text,
            };
            var cell = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Child = number, Background = d == today ? _red : Brushes.Transparent, Opacity = inMonth ? 1 : 0.35 };
            PressLight.SetOff(cell, true); // a day does nothing pressed (C4)
            if (holidays.TryGetValue(d, out var holiday)) cell.ToolTip = holiday;
            grid.Children.Add(cell);
        }
        _calendar.Children.Add(grid);

        // This month's holidays under the grid.
        foreach (var (d, name) in holidays.Where(h => h.Key.Year == first.Year && h.Key.Month == first.Month))
            _calendar.Children.Add(new TextBlock { Text = $"{d.ToString("M/d (ddd)", culture)}  {name}", FontSize = 11.5, Foreground = Dim, Margin = new Thickness(2, 4, 0, 0) });
    }

    Border Arrow(Geometry glyph, Action click)
    {
        var b = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brushes.Transparent, Child = Glyphs.Stroke(glyph, 12, Text, 1.8) };
        b.MouseEnter += (_, _) => b.Background = _hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        KeyNav.SetRun(b, click); // D2
        return b;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _poll.Stop();
        base.OnClosed(e);
    }
}
