using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Common;

/// <summary>
/// Where a pop-up button's list and a "…" menu show (2026-10-19, the user's "C3"): on the shell's menus' glass, a window
/// of its own under its button (macOS's pop-up menus are its menus), where they were WPF's popup, opaque and grey, a
/// look of their own beside the glass menus. Like WPF's popup it closes on a click anywhere else (the mouse captured
/// while it shows) and keeps the keyboard where it was (it never takes the foreground). With no glass from the shell
/// (<see cref="Material"/> unset: a window made before the shell set it), WPF's popup as before.
/// </summary>
public sealed class GlassList
{
    /// <summary>The menus' glass for theme <paramref>t</paramref> (the shell sets it as it starts).</summary>
    public static Func<ThemeSnapshot, Material>? Material { get; set; }

    /// <summary>Lists shown on glass since the start (diagnostics).</summary>
    public static int Shown { get; private set; }

    readonly FrameworkElement _anchor;
    readonly Border _list;
    readonly ThemeSnapshot _t;
    readonly Popup _popup;
    Host? _window;

    public GlassList(FrameworkElement anchor, Border list, ThemeSnapshot t, Popup popup)
    {
        _anchor = anchor;
        _list = list;
        _t = t;
        _popup = popup;
        anchor.Unloaded += (_, _) => Close(); // its page made again while it showed
    }

    // The window of a list chosen from, while it fades out.
    Host? _fading;

    /// <summary>It shows (on glass or in WPF's popup), fading out after a row was chosen included (as WPF's popup stays open through its fade).</summary>
    public bool IsOpen => _window != null || _fading != null || _popup.IsOpen;

    /// <summary>On glass now (diagnostics).</summary>
    public bool OnGlass => _window != null;

    public void Open()
    {
        if (Material == null || PresentationSource.FromVisual(_anchor) is not { } source) { _popup.IsOpen = true; return; }
        Close();
        _popup.Child = null;
        // Still on the window of a list fading out (opened again at once): taken off it.
        if (_list.Parent is Panel was) was.Children.Remove(_list);
        // The list as it sits on glass: no fill, edge or shadow of its own (the glass has them), the menus' inset.
        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Effect = null;
        _list.Margin = new Thickness(0);
        _list.Padding = new Thickness(Menus.MenuPopup.Pad);
        var w = _window = new Host(_t, Material(_t), _list);
        var toDip = source.CompositionTarget.TransformFromDevice;
        var under = toDip.Transform(_anchor.PointToScreen(new Point(0, _anchor.ActualHeight)));
        var over = toDip.Transform(_anchor.PointToScreen(new Point(0, 0)));
        w.Left = under.X - Menus.MenuPopup.Pad;
        w.Top = under.Y + 2;
        w.Show();
        w.UpdateLayout();
        // No room under it: above it (the bottom of the screen), as the menus do.
        var work = SystemParameters.WorkArea;
        if (w.Top + w.ActualHeight > work.Bottom) w.Top = Math.Max(work.Top, over.Y - 2 - w.ActualHeight);
        Shown++;
        Mouse.Capture(w, CaptureMode.SubTree);
        w.PreviewMouseDown += (_, e) =>
        {
            // A press outside it (the capture brings it here): it closes, as WPF's popup does.
            var at = e.GetPosition(w);
            if (at.X < 0 || at.Y < 0 || at.X >= w.ActualWidth || at.Y >= w.ActualHeight) { e.Handled = true; Close(); }
        };
        w.LostMouseCapture += (_, _) => { if (ReferenceEquals(_window, w)) Close(); };
    }

    /// <summary>Gone at once (Esc, a click elsewhere, its page made again).</summary>
    public void Close()
    {
        _popup.IsOpen = false;
        if (_fading is { } f)
        {
            // Fading out after a choice: gone now.
            _fading = null;
            Tween.Stop(_list, UIElement.OpacityProperty);
            f.Release(_list);
            f.Close();
        }
        if (_window is not { } w) return;
        _window = null;
        if (Mouse.Captured is DependencyObject c && (ReferenceEquals(c, w) || w.IsAncestorOf(c))) Mouse.Capture(null);
        w.Release(_list);
        w.Close();
    }

    /// <summary>A row chosen: it fades out (the content and its glass), then is gone (<paramref name="done"/> on WPF's popup).</summary>
    public void FadeOut(bool animate, Func<bool> current, Action reset)
    {
        if (_window is not { } w)
        {
            if (!animate) { _popup.IsOpen = false; reset(); return; }
            Tween.To(_list, UIElement.OpacityProperty, 0, Fades.MenuOutMs, Tween.SineInOut, () => { if (current()) { _popup.IsOpen = false; reset(); } });
            return;
        }
        // Out of _window before the mouse is let go: losing the capture closes the list that is still _window.
        _fading = w;
        _window = null;
        if (Mouse.Captured is DependencyObject c && (ReferenceEquals(c, w) || w.IsAncestorOf(c))) Mouse.Capture(null);
        if (!animate) { _fading = null; w.Release(_list); w.Close(); reset(); return; }
        Tween.To(_list, UIElement.OpacityProperty, 0, Fades.MenuOutMs, Tween.SineInOut, () =>
        {
            if (ReferenceEquals(_fading, w)) _fading = null;
            w.Release(_list);
            reset();
        });
        w.CloseFading();
    }

    /// <summary>The window of glass the list shows on.</summary>
    sealed class Host : Menus.PanelPopup
    {
        public Host(ThemeSnapshot t, Material m, Border list) : base(t, m)
        {
            Title = "Frostbar.List";
            Body.Margin = new Thickness(0);
            Body.MinWidth = 0;
            Body.Children.Add(list);
        }

        /// <summary>The list taken out (it is shown again on the next window).</summary>
        public void Release(Border list) => Body.Children.Remove(list);
    }
}
