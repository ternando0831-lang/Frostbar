using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Frostbar.App.Common;
using Frostbar.Platform.Interop;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Overview;

/// <summary>
/// The close button over the top right corner of the window pointed at in the window overview (stage 3-3; the user's
/// choice 2026-10-16, B): a round dark button with a cross. A small window of its own above the overview, as DWM draws
/// the windows' thumbnails over the overview window's own content. It never takes the foreground: the overview keeps
/// the keys, and does not close as it would for another window coming forward.
/// </summary>
public sealed class OverviewCloseButton : Window
{
    /// <summary>Its size, and how far its centre is in from the thumbnail's corner (DIPs).</summary>
    public const double Diameter = 20, Inset = 12;
    static readonly Brush Normal = new SolidColorBrush(Color.FromArgb(0xF2, 0x3A, 0x3A, 0x3C)), Lit = new SolidColorBrush(Color.FromArgb(0xF2, 0x5A, 0x5A, 0x5E));
    readonly Ellipse _disc = new() { Fill = Normal, Stroke = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 };

    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    /// <summary>What it closes: a window's handle, or a desktop's id for the strip's (null while it is off).</summary>
    public object? For { get; private set; }
    public event Action<object>? Clicked;

    readonly Func<bool> _animate;

    public OverviewCloseButton(Func<bool> animate)
    {
        _animate = animate;
        Title = "Frostbar.OverviewClose";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent; // the corners around the circle let clicks through to the overview
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = Height = Diameter;
        Left = Top = -32000;
        UseLayoutRounding = true;
        var cross = Glyphs.Stroke(Glyphs.Cross, 11, Brushes.White, 1.6);
        cross.HorizontalAlignment = HorizontalAlignment.Center;
        cross.VerticalAlignment = VerticalAlignment.Center;
        cross.IsHitTestVisible = false;
        var grid = new Grid();
        grid.Children.Add(_disc);
        grid.Children.Add(cross);
        Content = grid;
        _disc.MouseEnter += (_, _) => _disc.Fill = Lit;
        _disc.MouseLeave += (_, _) => _disc.Fill = Normal;
        _disc.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (For is { } target) Clicked?.Invoke(target);
        };
        SourceInitialized += (_, _) =>
        {
            Native.AddExStyle(Handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(Handle).AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if ((uint)msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
                return IntPtr.Zero;
            });
        };
    }

    /// <summary>How long it takes to fade in at a window, and out (ms): it came and went at once (2026-10-19, the user's "B11").</summary>
    public const double InMs = Motion.OverviewPointIn, OutMs = Motion.OverviewPointOut;

    // Fading out (still shown, taking no input).
    bool _going;

    /// <summary>Shows it at <paramref name="dip"/> (its top left, DIPs on the screen), above the overview, for <paramref name="target"/>.</summary>
    public void ShowAt(Point dip, object target)
    {
        bool same = IsVisible && !_going && Equals(For, target);
        For = target;
        Left = dip.X;
        Top = dip.Y;
        _disc.Fill = _disc.IsMouseOver ? Lit : Normal;
        if (_going) { _going = false; Fades.Reset(this, null); }
        // Fades in where it goes (another window's corner too: it does not slide across the overview).
        if (!same && _animate()) Fades.FadeIn(this, null, InMs);
        if (!IsVisible) Show();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void Off()
    {
        For = null;
        if (!IsVisible || _going) return;
        if (!_animate()) { Hide(); return; }
        _going = true;
        Fades.FadeOut(this, null, OutMs, () =>
        {
            if (!_going) return;
            _going = false;
            Hide();
            Fades.Reset(this, null); // whole again for the next time it shows
        });
    }

    /// <summary>The pointer is over its circle.</summary>
    public bool Pointed => IsVisible && _disc.IsMouseOver;
}
