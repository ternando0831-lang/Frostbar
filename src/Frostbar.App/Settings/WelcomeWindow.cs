using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Frostbar.App.Common;
using Frostbar.Core.Localization;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Sys;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Settings;

/// <summary>What the welcome asked; the shell applies it when the welcome is done.</summary>
public sealed record WelcomeChoices(string Language, bool HideTaskbar, bool TaskbarPins, bool Downloads, bool AutoHide, bool Recent, bool LoginItem);

/// <summary>
/// The first start, as macOS's Setup Assistant in brief: the language, hiding Windows' taskbar, what the Dock starts
/// with, then a few things worth knowing and starting at sign-in. Nothing changes until "Get Started"; closed before
/// that, nothing was agreed: the shell keeps Windows' taskbar and does not ask again (the settings have it all).
/// </summary>
public sealed class WelcomeWindow : Window
{
    public const double WindowWidth = 600, WindowHeight = 520;
    public static readonly string[] Steps = ["welcome", "taskbar", "dock", "ready"];

    readonly ThemeSnapshot _t;
    readonly bool _taskbarAvailable, _loginAvailable;
    readonly Func<string, Strings> _stringsFor;
    readonly Action<WelcomeChoices?> _done;
    Strings _s;
    string _language;
    bool _hide, _pins = true, _downloads = true, _autoHide, _recent, _login;
    bool _finished;
    int _step;
    readonly StackPanel _body = new();
    readonly TextBlock _heading = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 6) };
    readonly TextBlock _intro = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(28, 0, 28, 18) };
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    Brush _text = Brushes.Black, _dim = Brushes.Gray, _card = Brushes.White, _line = Brushes.LightGray, _accent = Brushes.DodgerBlue;

    /// <summary>The step shown (one of <see cref="Steps"/>) and its controls and buttons by key (diagnostics).</summary>
    public string Step => Steps[_step];
    public Dictionary<string, FrameworkElement> Controls { get; } = new();

    /// <param name="stringsFor">The words in a language ("system", "ja", "en"): the window speaks the one chosen at once.</param>
    /// <param name="taskbarAvailable">Hiding the taskbar can be chosen (not in a development build without --hide-taskbar).</param>
    /// <param name="loginAvailable">Starting at sign-in can be chosen (installed builds only).</param>
    /// <param name="done">The choices on "Get Started"; null when the window was closed before.</param>
    public WelcomeWindow(ThemeSnapshot t, string language, Func<string, Strings> stringsFor, bool taskbarAvailable, bool loginAvailable, Action<WelcomeChoices?> done)
    {
        _t = t;
        _language = language;
        _stringsFor = stringsFor;
        _s = stringsFor(language);
        _taskbarAvailable = taskbarAvailable;
        _loginAvailable = loginAvailable;
        _hide = taskbarAvailable;
        _login = loginAvailable;
        _done = done;
        NativeFrame.Apply(this); // Windows' own opening and closing animations
        ResizeMode = ResizeMode.NoResize;
        Width = WindowWidth;
        Height = WindowHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        FontFamily = Materials.UiFont;
        FontSize = 13;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage("ja-JP");
        Colours();

        var root = new DockPanel { Margin = new Thickness(28, 30, 28, 22) };
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom);
        DockPanel.SetDock(_dots, System.Windows.Controls.Dock.Left);
        footer.Children.Add(_dots);
        footer.Children.Add(_buttons);
        root.Children.Add(footer);
        var header = new StackPanel { Background = Brushes.Transparent };
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        // Moved by its header only: a drag started under a control would swallow the control's click (the language
        // choice answers when the button comes up, and the window's move loop took that).
        header.MouseLeftButtonDown += (_, e) => { try { DragMove(); } catch (InvalidOperationException) { } };
        header.Children.Add(Logo());
        header.Children.Add(_heading);
        header.Children.Add(_intro);
        root.Children.Add(header);
        root.Children.Add(_body);
        Content = root;

        KeyDown +=(_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        SourceInitialized += (_, _) => Chrome();
        Closed += (_, _) => { if (!_finished) _done(null); };
        Go(0);
    }

    void Colours()
    {
        _text = Materials.Text(_t);
        var c = ((SolidColorBrush)_text).Color;
        _dim = new SolidColorBrush(Color.FromArgb((byte)(c.A * 0.6), c.R, c.G, c.B));
        _card = new SolidColorBrush(_t.Dark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Colors.White);
        _line = new SolidColorBrush(_t.Dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        _accent = new SolidColorBrush(Color.FromRgb(_t.Accent.R, _t.Accent.G, _t.Accent.B));
        Foreground = _text;
        _intro.Foreground = _dim;
        Background = _t.BlurAllowed ? Brushes.Transparent : new SolidColorBrush(_t.Dark ? Color.FromRgb(0x24, 0x24, 0x24) : Color.FromRgb(0xEC, 0xEC, 0xEC));
    }

    /// <summary>Dark title colours, rounded corners, and Mica behind everything when Windows allows it (as the settings window).</summary>
    void Chrome()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        Native.SetDwm(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, _t.Dark ? 1 : 0);
        NativeFrame.Round(h, _t.Dark);
        if (!_t.BlurAllowed) return;
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(h, ref m);
        HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        Native.SetDwm(h, 38 /*DWMWA_SYSTEMBACKDROP_TYPE*/, 2 /*Mica*/);
    }

    /// <summary>The shell's mark on a rounded square, as an app's icon.</summary>
    static FrameworkElement Logo()
    {
        var square = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(15), HorizontalAlignment = HorizontalAlignment.Center,
            Background = new LinearGradientBrush(Color.FromRgb(0x5E, 0x5C, 0xE6), Color.FromRgb(0x0A, 0x84, 0xFF), 45),
        };
        var mark = Glyphs.Fill(Glyphs.Logo, 34, Brushes.White);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        square.Child = mark;
        return square;
    }

    /// <summary>How far a step's words come in from the side they come from (DIPs), and how long they take (ms).</summary>
    public const double TurnBy = 36, TurnMs = Motion.StepTurn;

    /// <summary>Steps turned with a movement (diagnostics).</summary>
    public int Turns { get; private set; }

    // The heading, the words under it and the step's rows move in together (the logo and the buttons stay).
    readonly TranslateTransform _headingAt = new(), _introAt = new(), _bodyAt = new();

    /// <summary>
    /// Shows step <paramref name="step"/> in the language chosen. A step forward comes in from the right as it fades
    /// in, a step back from the left (macOS's Setup Assistant's pages); it changed at once (2026-10-19, the user's "B13").
    /// </summary>
    void Go(int step)
    {
        int towards = Math.Sign(step - _step);
        _step = step;
        _heading.RenderTransform = _headingAt;
        _intro.RenderTransform = _introAt;
        _body.RenderTransform = _bodyAt;
        if (towards != 0 && _t.Animations && IsVisible)
        {
            Turns++;
            var parts = new List<Tween.Part>();
            double by = ReduceMotion.Calms("welcomeTurn") ? 0 : TurnBy; // reduced motion: the step fades in where it stands
            foreach (var (e, at) in new (UIElement, TranslateTransform)[] { (_heading, _headingAt), (_intro, _introAt), (_body, _bodyAt) })
            {
                at.X = towards * by;
                e.Opacity = 0;
                parts.Add(new(at, TranslateTransform.XProperty, 0.0));
                parts.Add(new(e, OpacityProperty, 1.0, Tween.SineInOut, 0.7));
            }
            Tween.Together(TurnMs, parts);
        }
        Controls.Clear();
        _body.Children.Clear();
        Title = _s["welcome.title"];
        switch (Steps[step])
        {
            case "welcome":
                _heading.Text = _s["welcome.title"];
                _intro.Text = _s["welcome.intro"];
                string[] langs = ["system", "ja", "en"];
                _body.Children.Add(Group(Row(_s["set.language"], Choice("language", [_s["set.followWindows"], "日本語", "English"], Array.IndexOf(langs, _language), i =>
                {
                    _language = langs[i];
                    _s = _stringsFor(_language);
                    Go(0); // in the chosen language at once
                }))));
                break;
            case "taskbar":
                _heading.Text = _s["welcome.taskbarTitle"];
                _intro.Text = _s["welcome.taskbarText"];
                var hide = Switch("hideTaskbar", _hide, on => _hide = on);
                if (!_taskbarAvailable) { hide.IsEnabled = false; hide.Opacity = 0.4; }
                _body.Children.Add(Group(Row(_s["set.hideTaskbar"], hide, _taskbarAvailable ? _s["set.hideTaskbarDetail"] : _s["set.hideTaskbarDev"])));
                break;
            case "dock":
                _heading.Text = _s["welcome.dockTitle"];
                _intro.Text = _s["welcome.dockText"];
                _body.Children.Add(Group(
                    Row(_s["welcome.pins"], Switch("pins", _pins, on => _pins = on)),
                    Row(_s["welcome.downloads"], Switch("downloads", _downloads, on => _downloads = on)),
                    Row(_s["set.autoHide"], Switch("autoHide", _autoHide, on => _autoHide = on)),
                    Row(_s["set.recent"], Switch("recent", _recent, on => _recent = on))));
                break;
            case "ready":
                _heading.Text = _s["welcome.readyTitle"];
                _intro.Text = _s["welcome.readyText"];
                var tips = new List<FrameworkElement> { Tip(Glyphs.Search, _s["welcome.tipSearch"]) };
                // The bottom corners are the taskbar's while it shows (its Start button and "show desktop" are there,
                // so they do nothing): the tip only when it is to be hidden.
                if (_hide && _taskbarAvailable) tips.Add(Tip(Glyphs.CornerPicture, _s["welcome.tipCorners"]));
                tips.Add(Tip(Glyphs.Gear, _s.F("welcome.tipSettings", _s["sys.shellSettings"])));
                _body.Children.Add(Group(tips.ToArray()));
                var login = Switch("loginItem", _login, on => _login = on);
                if (!_loginAvailable) { login.IsEnabled = false; login.Opacity = 0.4; }
                _body.Children.Add(Group(Row(_s["set.loginItem"], login, _loginAvailable ? null : _s["set.loginItemDev"])));
                break;
        }
        Dots();
        Buttons();
    }

    void Dots()
    {
        _dots.Children.Clear();
        var c = ((SolidColorBrush)_text).Color;
        var off = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B));
        for (int i = 0; i < Steps.Length; i++)
            _dots.Children.Add(new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), Fill = i == _step ? _accent : off });
    }

    void Buttons()
    {
        _buttons.Children.Clear();
        if (_step > 0)
        {
            var back = MacControls.Button(_s["welcome.back"], primary: false, _t, cancels: false);
            back.Click += (_, _) => Go(_step - 1);
            Controls["back"] = back;
            _buttons.Children.Add(back);
        }
        bool last = _step == Steps.Length - 1;
        var next = MacControls.Button(last ? _s["welcome.start"] : _s["welcome.next"], primary: true, _t);
        next.Click += (_, _) => { if (last) Finish(); else Go(_step + 1); };
        Controls["next"] = next;
        _buttons.Children.Add(next);
    }

    void Finish()
    {
        _finished = true;
        var choices = new WelcomeChoices(_language, _hide && _taskbarAvailable, _pins, _downloads, _autoHide, _recent, _login && _loginAvailable);
        Close();
        _done(choices);
    }

    // ---- pieces (the settings window's look) -------------------------------------------------------------------

    Border Group(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = _line, Margin = new Thickness(12, 0, 12, 0) });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            Background = _card, CornerRadius = new CornerRadius(10), Child = stack, Margin = new Thickness(0, 0, 0, 12),
            BorderBrush = _line, BorderThickness = new Thickness(_t.Dark ? 0 : 0.5),
        };
    }

    FrameworkElement Row(string label, FrameworkElement control, string? detail = null)
    {
        var grid = new Grid { Margin = new Thickness(12, 9, 12, 9), MinHeight = 22 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = label, Foreground = _text, TextWrapping = TextWrapping.Wrap });
        if (detail != null) words.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = _dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(words);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>A thing worth knowing: a small picture and a line.</summary>
    FrameworkElement Tip(Geometry picture, string text)
    {
        var row = new DockPanel { Margin = new Thickness(12, 9, 12, 9) };
        var glyph = Glyphs.Stroke(picture, 16, _accent, 1.5);
        glyph.Margin = new Thickness(0, 0, 12, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(glyph, System.Windows.Controls.Dock.Left);
        row.Children.Add(glyph);
        row.Children.Add(new TextBlock { Text = text, Foreground = _text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    PillSwitch Switch(string key, bool on, Action<bool> toggled)
    {
        var s = new PillSwitch(_accent, new SolidColorBrush(_t.Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0)), on, _t.Animations);
        s.Toggled += toggled;
        Controls[key] = s;
        return s;
    }

    MacControls.Segmented Choice(string key, IReadOnlyList<string> labels, int selected, Action<int> chosen)
    {
        var c = MacControls.Segments(labels, selected, _t, chosen);
        Controls[key] = c;
        return c;
    }
}
