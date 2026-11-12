using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.Core.Localization;
using Frostbar.Platform.Identity;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Sys;
using Frostbar.Platform.Wm;
using Path = System.IO.Path;

namespace Frostbar.App.Menus;

/// <summary>
/// A small window that takes focus (it needs keyboard input), macOS look: no title bar, a red close button at the top
/// left, rounded corners, acrylic while active (DWM backdrop: fine here because the window is active), dragged anywhere.
/// </summary>
public class DialogWindow : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(28, 34, 28, 22) };
    protected readonly Brush Text, Dim;
    protected readonly ThemeSnapshot Theme;
    /// <summary>The red close button at the top left (diagnostics: the specs click it).</summary>
    public FrameworkElement CloseDot { get; }
    bool _moved;

    public DialogWindow(string title, ThemeSnapshot t)
    {
        Theme = t;
        Title = title;
        NativeFrame.Apply(this); // a real frame: Windows animates opening and closing
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Materials.UiFont;
        FontSize = 13;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage("ja-JP");
        Text = Materials.Text(t);
        var c = ((SolidColorBrush)Text).Color;
        Dim = new SolidColorBrush(Color.FromArgb((byte)(c.A * 0.6), c.R, c.G, c.B));
        Foreground = Text;
        bool blur = t.BlurAllowed;
        Background = blur ? Brushes.Transparent : new SolidColorBrush(t.Dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xF2, 0xF2, 0xF2));

        var close = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57)), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 14, 0, 0) };
        // The press stays here: the window's drag below would start Windows' move loop, which takes the button-up, and the
        // dot never closed anything (2026-10-12; the welcome window's language choice was lost the same way).
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, _) => Close();
        CloseDot = close;
        var root = new Grid();
        root.Children.Add(Body);
        root.Children.Add(close);
        Content = root;

        MouseLeftButtonDown += (_, e) => { if (e.Handled || e.OriginalSource is Button) return; _moved = true; try { DragMove(); } catch (InvalidOperationException) { } };
        // Liquid Glass: a button pressed lights where it is pressed, the light spilling onto the buttons beside it (2026-10-23).
        PressLight.Attach(this, () => blur && t.Animations && GlassKnob.Enabled());
        // Content that arrives later (About This PC's details) grows the window from its top left: it stays centred where
        // it opened until it is moved.
        SizeChanged += (_, e) =>
        {
            if (_moved || e.PreviousSize.Width <= 0) return;
            Left -= (e.NewSize.Width - e.PreviousSize.Width) / 2;
            Top -= (e.NewSize.Height - e.PreviousSize.Height) / 2;
        };
        // Esc, and Alt+F4 (the window has no system menu to take it: below).
        KeyDown += (_, e) => { if (e.Key == Key.Escape || e.Key == Key.System && e.SystemKey == Key.F4) { e.Handled = true; Close(); } };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            // No system menu: over a frame extended into a see-through window, DWM drew Windows' close button (an "×" at
            // the top right) beside the red dot (2026-10-16: on the display question and the Wi-Fi password window alike,
            // since the dialogs got a real frame on 2026-10-12).
            Native.SetWindowLongPtr(h, Native.GWL_STYLE, new IntPtr(Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64() & ~Native.WS_SYSMENU));
            Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            Native.SetDwm(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, t.Dark ? 1 : 0);
            NativeFrame.Round(h, t.Dark);
            if (blur)
            {
                var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(h, ref m);
                HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.SetDwm(h, 38 /*DWMWA_SYSTEMBACKDROP_TYPE*/, 3 /*acrylic*/);
            }
        };
    }

    protected TextBlock Line(string text, double size = 13, bool bold = false, bool dim = false) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = dim ? Dim : Text,
        TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 420,
    };

    protected Button MacButton(string text, bool primary) => MacControls.Button(text, primary, Theme);

    /// <summary>Opens in front and keyboard-ready (a dialog shown by a never-active shell must claim the foreground).</summary>
    public void ShowInFront()
    {
        Show();
        WindowActions.ForceForeground(new WindowInteropHelper(this).Handle);
        Activate();
    }
}

/// <summary>The dialogs the menus open.</summary>
public sealed class Dialogs(Func<ThemeSnapshot> theme, Func<Strings> strings, AppIdentity identity)
{
    /// <summary>Opens a page of Frostbar's settings (About This PC's "More Info…" opens General → About); null: Windows' About.</summary>
    public Action<string>? OpenSettings { get; set; }

    public void AboutPc()
    {
        var s = strings();
        var w = new DialogWindow(s["sys.aboutPc"], theme());
        var body = (StackPanel)((Grid)w.Content).Children[0];
        var t = theme();
        body.Children.Add(Glyphs.Fill(Glyphs.Logo, 64, Materials.Text(t)));
        var model = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2), HorizontalAlignment = HorizontalAlignment.Center, Text = Environment.MachineName };
        var os = new TextBlock { Foreground = Dim(t), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        var rows = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(model);
        body.Children.Add(os);
        body.Children.Add(rows);
        var more = MacControls.Button(s["about.more"], primary: false, t, cancels: false);
        more.Margin = new Thickness(0, 18, 0, 0);
        more.HorizontalAlignment = HorizontalAlignment.Center;
        more.Click += (_, _) => { if (OpenSettings != null) { w.Close(); OpenSettings("about"); } else CommandRunner.Launch("ms-settings:about"); };
        body.Children.Add(more);
        w.ShowInFront();
        Task.Run(() => SystemInfo.Read((total, free) => s.F("about.storageFree", Gb(total), Gb(free)), (version, build) => s.F("about.osVersion", version, build))).ContinueWith(task =>
        {
            if (task.Exception != null) { Log.Warn("about: system info failed", task.Exception); return; }
            var i = task.Result;
            model.Text = string.IsNullOrWhiteSpace(i.Model) ? i.Device : i.Model;
            os.Text = $"{i.Os}  {i.Version}";
            void Row(string k, string v)
            {
                int r = rows.RowDefinitions.Count;
                rows.RowDefinitions.Add(new RowDefinition());
                var kt = new TextBlock { Text = k, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 12, 2), HorizontalAlignment = HorizontalAlignment.Right };
                var vt = new TextBlock { Text = v, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
                Grid.SetRow(kt, r); Grid.SetRow(vt, r); Grid.SetColumn(vt, 1);
                rows.Children.Add(kt); rows.Children.Add(vt);
            }
            Row(s["about.device"], i.Device);
            Row(s["about.cpu"], i.Cpu);
            Row(s["about.memory"], i.Memory);
            Row(s["about.gpu"], string.Join("\n", i.Gpus));
            Row(s["about.display"], i.Display);
            Row(s["about.storage"], i.Storage);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static string Gb(long bytes) => $"{bytes / 1_000_000_000.0:0} GB";

    public void AboutApp(FrontWindow app)
    {
        var s = strings();
        var w = new DialogWindow(s.F("app.about", app.DisplayName), theme());
        var body = (StackPanel)((Grid)w.Content).Children[0];
        string version = "", company = "";
        if (app.ExePath != null && File.Exists(app.ExePath))
        {
            try { var v = FileVersionInfo.GetVersionInfo(app.ExePath); version = v.ProductVersion ?? v.FileVersion ?? ""; company = v.CompanyName ?? ""; }
            catch (Exception) { }
        }
        body.Children.Add(new TextBlock { Text = app.DisplayName, FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        if (version.Length > 0) body.Children.Add(new TextBlock { Text = $"{s["about.version"]} {version}", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        if (company.Length > 0) body.Children.Add(new TextBlock { Text = company, Foreground = Dim(theme()), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        if (app.ExePath != null) body.Children.Add(new TextBlock { Text = app.ExePath, Foreground = Dim(theme()), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 460, TextWrapping = TextWrapping.Wrap });
        w.ShowInFront();
    }

    // One of each at a time: asked again (Ctrl+Alt+Esc twice, Restart… from the menu and from the search), another window
    // opened exactly over the first, and a countdown left behind the one cancelled restarted the PC at 0.
    DialogWindow? _forceQuit, _power;

    public void ForceQuit()
    {
        if (_forceQuit is { } open) { open.Activate(); return; }
        var s = strings();
        var w = new DialogWindow(s["dlg.forceQuitTitle"], theme()) { Width = 440, SizeToContent = SizeToContent.Height };
        var body = (StackPanel)((Grid)w.Content).Children[0];
        var t = theme();
        body.Children.Add(new TextBlock { Text = s["dlg.forceQuitTitle"], FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(new TextBlock { Text = s["dlg.forceQuitHint"], TextWrapping = TextWrapping.Wrap, Foreground = Dim(t), Margin = new Thickness(0, 0, 0, 10) });
        var list = new ListBox
        {
            Height = 240, Foreground = Materials.Text(t), BorderThickness = new Thickness(1), Padding = new Thickness(2),
            Background = new SolidColorBrush(t.Dark ? Color.FromArgb(0x33, 0, 0, 0) : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(t.Dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0)),
        };
        MacScrollBar.Apply(list); // the thin bar of the other windows, not Windows' grey one with arrows
        body.Children.Add(list);
        // What could not be ended (an app running as administrator refuses us): said here, not only in the log.
        var failed = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Dim(t), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        body.Children.Add(failed);
        var button = MacControls.Button(s["dlg.forceQuit"], primary: true, t);
        button.IsEnabled = false;
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(button);
        var apps = new List<(string name, HashSet<uint> pids)>();
        void Fill()
        {
            apps.Clear();
            list.Items.Clear();
            foreach (var g in WindowList.AppWindows().GroupBy(h => identity.AppIdForWindow(h)))
            {
                // The app's own processes: a packaged app's window belongs to ApplicationFrameHost, shared by all of them.
                var pids = g.Select(Native.AppProcessId).Where(p => p != 0).ToHashSet();
                if (pids.Count == 0) continue; // a packaged app suspended in its frame: nothing of its own to end
                var exe = Native.ProcessPath(pids.First());
                if (exe != null && Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue; // would take the desktop with it
                string name = identity.DisplayName(g.Key, exe);
                bool hung = g.Any(Native.IsHungAppWindow);
                apps.Add((name, pids));
                list.Items.Add(name + (hung ? s["dlg.notResponding"] : ""));
            }
        }
        Fill();
        list.SelectionChanged += (_, _) => button.IsEnabled = list.SelectedIndex >= 0;
        button.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i < 0) return;
            var (name, pids) = apps[i];
            // Asked in the shell's own alert (it was Windows' message box: light and square in a dark shell, its
            // buttons in Windows' language and "OK" for the action).
            Confirm(t, s.F("dlg.forceQuitQuestion", name), s["dlg.forceQuitDetail"], s["dlg.cancel"], s["dlg.forceQuit"], () =>
            {
                bool ok = true;
                foreach (var pid in pids)
                {
                    try { Process.GetProcessById((int)pid).Kill(); Log.Info($"force quit pid {pid}"); }
                    catch (ArgumentException) { } // gone meanwhile
                    catch (Exception ex) { ok = false; Log.Warn($"force quit pid {pid} failed", ex); }
                }
                failed.Text = ok ? "" : s.F("dlg.forceQuitFailed", name);
                failed.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
                Fill();
                w.Activate();
            });
        };
        _forceQuit = w;
        w.Closed += (_, _) => _forceQuit = null;
        w.ShowInFront();
    }

    /// <summary>
    /// An alert's width. Apple's picture of one is about 260 pt; Japanese sentences, with no spaces to break at, read
    /// better a little wider.
    /// </summary>
    public const double AlertWidth = 320;
    const double AlertInset = 16, AlertTextInset = 4, AlertButtonGap = 8;

    /// <summary>
    /// A window for an alert, in the form macOS has had since 26 (Apple's HIG pictures, 2026-11-01; its icon, words and
    /// buttons stood centred, Big Sur's form, until then): the icon, the title and <paramref name="words"/> at the left, and
    /// the buttons filling the width as capsules. No red dot: an alert has none (Esc cancels). Where the icon stands is not
    /// in those pictures (they draw none): at the left, with the words.
    /// </summary>
    static DialogWindow Alert(ThemeSnapshot t, string title, FrameworkElement icon, TextBlock? words, Button yes, Button? no = null)
    {
        var w = new DialogWindow(title, t) { Width = AlertWidth, SizeToContent = SizeToContent.Height };
        w.CloseDot.Visibility = Visibility.Collapsed;
        var body = (StackPanel)((Grid)w.Content).Children[0];
        body.Margin = new Thickness(AlertInset, 20, AlertInset, AlertInset);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(AlertTextInset, 0, AlertTextInset, 10);
        body.Children.Add(icon);
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(AlertTextInset, 0, AlertTextInset, 4) });
        if (words != null)
        {
            // Stronger than the other windows' secondary text (60%): over the see-through glass, in light, it read faintly.
            var ink = ((SolidColorBrush)Materials.Text(t)).Color;
            words.Foreground = new SolidColorBrush(Color.FromArgb((byte)(ink.A * 0.8), ink.R, ink.G, ink.B));
            words.TextWrapping = TextWrapping.Wrap;
            words.Margin = new Thickness(AlertTextInset, 0, AlertTextInset, 0);
            body.Children.Add(words);
        }
        body.Children.Add(AlertButtons(yes, no));
        return w;
    }

    /// <summary>
    /// An alert's buttons (Apple's two pictures): side by side in equal parts with the action at the right; when a word
    /// does not fit its part, one above the other with the action on top.
    /// </summary>
    static UIElement AlertButtons(Button yes, Button? no)
    {
        var margin = new Thickness(0, 14, 0, 0);
        if (no == null) { yes.Margin = margin; return yes; }
        double part = (AlertWidth - 2 * AlertInset - AlertButtonGap) / 2;
        bool Fits(Button b) { b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return b.DesiredSize.Width <= part; }
        if (Fits(yes) && Fits(no))
        {
            var row = new Grid { Margin = margin };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition());
            no.Margin = new Thickness(0, 0, AlertButtonGap / 2, 0);
            yes.Margin = new Thickness(AlertButtonGap / 2, 0, 0, 0);
            Grid.SetColumn(yes, 1);
            row.Children.Add(no);
            row.Children.Add(yes);
            return row;
        }
        var stack = new StackPanel { Margin = margin };
        no.Margin = new Thickness(0, AlertButtonGap - 2, 0, 0);
        stack.Children.Add(yes);
        stack.Children.Add(no);
        return stack;
    }

    /// <summary>Something the user should know (macOS's informational alert): the logo, the title, what follows, and OK (Enter or Esc).</summary>
    public static DialogWindow Notice(ThemeSnapshot t, string title, string detail, string ok)
    {
        var button = MacControls.AlertButton(ok, primary: true, t);
        var w = Alert(t, title, Glyphs.Fill(Glyphs.Logo, 48, Materials.Text(t)), new TextBlock { Text = detail }, button);
        button.Click += (_, _) => w.Close();
        w.ShowInFront();
        return w;
    }

    /// <summary>The theme's secondary text (the text colour at 60%).</summary>
    static Brush Dim(ThemeSnapshot t)
    {
        var c = ((SolidColorBrush)Materials.Text(t)).Color;
        return new SolidColorBrush(Color.FromArgb((byte)(c.A * 0.6), c.R, c.G, c.B));
    }

    /// <summary>
    /// A question about something that cannot be taken back (macOS's alert): the logo, the question, what follows, and
    /// Cancel (Esc) beside the action (Enter). <paramref name="yes"/> runs once the window has gone.
    /// </summary>
    public static DialogWindow Confirm(ThemeSnapshot t, string question, string detail, string cancel, string action, Action yes)
    {
        var no = MacControls.AlertButton(cancel, primary: false, t);
        var ok = MacControls.AlertButton(action, primary: true, t);
        var w = Alert(t, question, Glyphs.Fill(Glyphs.Logo, 48, Materials.Text(t)), detail.Length > 0 ? new TextBlock { Text = detail } : null, ok, no);
        bool chosen = false;
        no.Click += (_, _) => w.Close();
        ok.Click += (_, _) => { chosen = true; w.Close(); };
        w.Closed += (_, _) => { if (chosen) yes(); };
        w.ShowInFront();
        return w;
    }

    /// <summary>
    /// A development build only logs power actions: says so, or the item seemed to do nothing (sleep, lock) or the
    /// confirmation to have failed (restart, shut down, log out).
    /// </summary>
    public void Skipped(string action)
    {
        var s = strings();
        Notice(theme(), action.TrimEnd('…'), s["dryRun"], s["dlg.ok"]);
    }

    /// <summary>
    /// "Keep these display settings?" after a display change (Windows' and macOS's question): put back by itself after
    /// <paramref name="seconds"/> unless kept; Revert, Esc and the close button put it back at once. Exactly one of
    /// <paramref name="putBack"/> and <paramref name="kept"/> runs, once (Core's RevertCountdown).
    /// </summary>
    public static DialogWindow KeepDisplay(ThemeSnapshot t, Strings s, int seconds, Action putBack, Action kept)
    {
        var revert = MacControls.AlertButton(s["disp.revert"], primary: false, t);
        var keep = MacControls.AlertButton(s["disp.keep"], primary: true, t);
        var count = new TextBlock();
        var w = Alert(t, s["disp.keepTitle"], Glyphs.Stroke(Glyphs.DisplayPicture, 48, Materials.Text(t), 1.2), count, keep, revert);
        var countdown = new Frostbar.Core.Devices.RevertCountdown(Environment.TickCount64, seconds);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        void Update() => count.Text = s.F("disp.keepCount", countdown.Left(Environment.TickCount64));
        void Done() { timer.Stop(); if (w.IsVisible) w.Close(); }
        timer.Tick += (_, _) =>
        {
            if (countdown.Tick(Environment.TickCount64)) { Done(); putBack(); }
            else Update();
        };
        revert.Click += (_, _) => { if (countdown.PutBack()) { Done(); putBack(); } };
        keep.Click += (_, _) => { if (countdown.Keep()) { Done(); kept(); } };
        // Closed another way (Esc, the red dot): put back, as Windows does.
        w.Closed += (_, _) => { timer.Stop(); if (countdown.PutBack()) putBack(); };
        Update();
        timer.Start();
        w.ShowInFront();
        return w;
    }

    /// <summary>macOS-style confirmation with a 60-second countdown that then does it anyway.</summary>
    public void ConfirmPower(string title, string action, Func<string> doIt)
    {
        _power?.Close(); // the question asked last is the one shown (its countdown stops as it closes)
        var s = strings();
        var cancel = MacControls.AlertButton(s["dlg.cancel"], primary: false, theme());
        var ok = MacControls.AlertButton(action, primary: true, theme());
        var count = new TextBlock();
        var w = Alert(theme(), title, Glyphs.Fill(Glyphs.Logo, 48, Materials.Text(theme())), count, ok, cancel);
        int left = 60;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Update() => count.Text = s.F("dlg.countdown", left);
        bool done = false;
        void Go()
        {
            if (done) return;
            done = true;
            timer.Stop();
            w.Close();
            string r = doIt();
            Log.Info($"{action}: {r}");
            if (r == PowerActions.Skipped) Skipped(action);
        }
        timer.Tick += (_, _) => { left--; Update(); if (left <= 0) Go(); };
        cancel.Click += (_, _) => { timer.Stop(); w.Close(); };
        ok.Click += (_, _) => Go();
        w.Closed += (_, _) => { timer.Stop(); if (_power == w) _power = null; };
        _power = w;
        Update();
        timer.Start();
        w.ShowInFront();
    }
}
