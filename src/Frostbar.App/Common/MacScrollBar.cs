using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// macOS's thin scroll bar: a rounded grey thumb on no track, without arrows (Windows' classic bar showed up on the
/// settings window's Dock page once it no longer fit, 2026-10-12). Put in a ScrollViewer's resources. On a list (a
/// ScrollViewer) it shows while the list moves, stays while the pointer is over it, and fades away a second after
/// (macOS's scroll bars with a trackpad): it stood there all the time (2026-10-19, the user's "B6"). Out of sight it
/// takes no press.
/// </summary>
public static class MacScrollBar
{
    const string Xaml = """
        <Style TargetType="ScrollBar" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Setter Property="Width" Value="12"/>
          <Setter Property="MinWidth" Value="12"/>
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ScrollBar">
                <Border Background="{TemplateBinding Background}">
                  <Track x:Name="PART_Track" IsDirectionReversed="True">
                    <Track.Thumb>
                      <Thumb>
                        <Thumb.Template>
                          <ControlTemplate TargetType="Thumb">
                            <Border CornerRadius="3" Margin="3,4,3,4" Background="#80888888"/>
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                  </Track>
                </Border>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    static Style? _style;

    /// <summary>The style (made once).</summary>
    public static Style Style => _style ??= (Style)XamlReader.Parse(Xaml);

    /// <summary>
    /// A list's frame with its bars over the content, not beside it (macOS's overlay scroll bars): the vertical bar took a
    /// column of its own, the rows narrower by its width once the list scrolled, and the search panel's list had none at
    /// all (it scrolled with no bar: C8, 2026-10-19).
    /// </summary>
    const string OverlayXaml = """
        <ControlTemplate TargetType="ScrollViewer" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Grid Background="{TemplateBinding Background}">
            <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Margin="{TemplateBinding Padding}" CanContentScroll="{TemplateBinding CanContentScroll}"/>
            <ScrollBar x:Name="PART_VerticalScrollBar" Orientation="Vertical" HorizontalAlignment="Right" Cursor="Arrow"
                       Maximum="{TemplateBinding ScrollableHeight}" ViewportSize="{TemplateBinding ViewportHeight}"
                       Value="{Binding VerticalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}"
                       Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
            <ScrollBar x:Name="PART_HorizontalScrollBar" Orientation="Horizontal" VerticalAlignment="Bottom" Cursor="Arrow"
                       Maximum="{TemplateBinding ScrollableWidth}" ViewportSize="{TemplateBinding ViewportWidth}"
                       Value="{Binding HorizontalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}"
                       Visibility="{TemplateBinding ComputedHorizontalScrollBarVisibility}"/>
          </Grid>
        </ControlTemplate>
        """;

    static ControlTemplate? _overlay;
    static ControlTemplate OverlayTemplate => _overlay ??= (ControlTemplate)XamlReader.Parse(OverlayXaml);

    /// <summary>How long the bar takes to show, how long it stays after the list stops, and how long it takes to go (ms).</summary>
    public const double InMs = Motion.ScrollBarIn, StayMs = Motion.ScrollBarStay, OutMs = Motion.ScrollBarOut;

    /// <summary>Bars shown by a list moving or the pointer, since the start (diagnostics).</summary>
    public static int Shows { get; private set; }

    /// <summary>The appearance's "Show scroll bars: Always" (2026-10-22): a list's bar stands there while it can scroll.</summary>
    public static bool AlwaysShown { get; private set; }

    /// <summary>The appearance's "Click in the scroll bar to: Jump to the spot that's clicked" (else the next page).</summary>
    public static bool JumpToSpot { get; private set; }

    /// <summary>Clicks on a bar's track, since the start (diagnostics): by a page and to the spot.</summary>
    public static (int Pages, int Spots) TrackClicks => (_pages, _spots);
    static int _pages, _spots;

    // The lists' bars made so far, for a change of the settings to reach the open ones.
    static readonly List<WeakReference<Overlay>> Overlays = new();
    static int _trimAt = 128;

    /// <summary>The appearance's scroll bar choices, from the settings (at start and when they change): the open lists follow.</summary>
    public static void Configure(bool always, bool jumpToSpot)
    {
        JumpToSpot = jumpToSpot;
        if (always == AlwaysShown) return;
        AlwaysShown = always;
        Overlays.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Overlays.ToList())
            if (w.TryGetTarget(out var o)) o.Settle();
    }

    /// <summary>
    /// Gives <paramref name="viewer"/>'s scroll bars the thin look; a list's (a ScrollViewer's) vertical bar shows only
    /// while it moves or is pointed at (<paramref name="animate"/>: the shell's animation setting; off, it shows and
    /// goes at once). <paramref name="barTop"/>: the vertical bar begins that far down (DIP), below a toolbar the list
    /// scrolls under (the settings window's: its top under the toolbar took no click, 2026-10-23).
    /// </summary>
    public static void Apply(FrameworkElement viewer, Func<bool>? animate = null, double barTop = 0)
    {
        viewer.Resources[typeof(ScrollBar)] = Style;
        if (viewer is not ScrollViewer sv) return;
        sv.Template = OverlayTemplate;
        new Overlay(sv, animate ?? (() => true), barTop);
    }

    /// <summary>The list's vertical bar's opacity now (diagnostics: 0 at rest, 1 while it moves).</summary>
    public static double Shown(ScrollViewer viewer) => Bar(viewer)?.Opacity ?? -1;

    /// <summary>The list's vertical bar takes a press now (diagnostics: not while out of sight).</summary>
    public static bool Takes(ScrollViewer viewer) => Bar(viewer) is { IsHitTestVisible: true, Visibility: Visibility.Visible };

    static ScrollBar? Bar(ScrollViewer viewer) => viewer.Template?.FindName("PART_VerticalScrollBar", viewer) as ScrollBar;

    /// <summary>The list's vertical bar and its thumb (diagnostics: a spec clicks the track by them), or nulls.</summary>
    public static (FrameworkElement? Bar, FrameworkElement? Thumb) Parts(ScrollViewer viewer) =>
        Bar(viewer) is { } b ? (b, b.Track?.Thumb) : (null, null);

    sealed class Overlay
    {
        readonly ScrollViewer _v;
        readonly Func<bool> _animate;
        readonly double _barTop;
        System.Windows.Threading.DispatcherTimer? _stay;
        ScrollBar? _bar;

        public Overlay(ScrollViewer v, Func<bool> animate, double barTop)
        {
            _v = v;
            _animate = animate;
            _barTop = barTop;
            // The gone ones dropped as it grows (trimmed only when the setting changed, 2026-11-06 review).
            if (Overlays.Count >= _trimAt) { Overlays.RemoveAll(w => !w.TryGetTarget(out _)); _trimAt = Math.Max(128, Overlays.Count * 2); }
            Overlays.Add(new WeakReference<Overlay>(this));
            v.Loaded += (_, _) => Hook();
            if (v.IsLoaded) Hook();
            v.ScrollChanged += (_, e) =>
            {
                // Moved: it shows for a moment. Not for the list's length alone: shown as a notification went, it lay over
                // the next card's right edge, where its × is, and took the press meant for it (2026-10-20, nc-dismiss).
                if (e.VerticalChange != 0) Show();
                // A list grown long enough to scroll (or short again) takes its bar's column, or gives it back (Always).
                if (AlwaysShown && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)) Settle();
            };
        }

        void Hook()
        {
            _v.ApplyTemplate();
            var bar = Bar(_v);
            if (bar == null || ReferenceEquals(bar, _bar)) return;
            _bar = bar;
            bar.Margin = new Thickness(0, _barTop, 0, 0);
            Tween.Stop(bar, UIElement.OpacityProperty);
            bar.Opacity = 0;
            // Out of sight, it lets presses through to the rows under it: lying over the list's right edge, it took the
            // press on a notification's card near its edge, the card never clicked (2026-10-20, nc-dismiss).
            bar.IsHitTestVisible = false;
            bar.MouseEnter += (_, _) => Show(stay: false);
            bar.MouseLeave += (_, _) => Later();
            bar.PreviewMouseLeftButtonDown += (_, e) => Track(bar, e);
            Settle();
        }

        /// <summary>
        /// A press on the track (not the thumb): the next page towards it, or the spot clicked, as the settings say
        /// (macOS's "Click in the scroll bar to"). The track had no buttons, and a press there did nothing.
        /// </summary>
        void Track(ScrollBar bar, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindThumb(d, bar)) return; // the thumb's own drag
            if (bar.Template?.FindName("PART_Track", bar) is not System.Windows.Controls.Primitives.Track track) return;
            e.Handled = true;
            double to;
            if (JumpToSpot)
            {
                _spots++;
                // The thumb's middle at the spot (the track's value for a point puts it there).
                to = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), 0, _v.ScrollableHeight);
            }
            else
            {
                _pages++;
                var thumb = track.Thumb;
                double y = e.GetPosition(track).Y, top = thumb.TranslatePoint(new Point(0, 0), track).Y;
                to = Math.Clamp(_v.VerticalOffset + (y < top ? -1 : 1) * _v.ViewportHeight, 0, _v.ScrollableHeight);
            }
            SmoothScroll.To(_v, to, _animate());
        }

        static bool FindThumb(DependencyObject d, DependencyObject stop)
        {
            for (var x = d; x != null && !ReferenceEquals(x, stop); x = System.Windows.Media.VisualTreeHelper.GetParent(x))
                if (x is Thumb) return true;
            return false;
        }

        /// <summary>
        /// The bar as the settings have it now: standing there (always), or out of sight until the list moves. Always
        /// shown, it takes a column of its own beside the rows while the list can scroll (macOS's legacy scroll bars),
        /// so that it never lies over the rows' right ends (a notification's ×: 2026-10-20, nc-dismiss).
        /// </summary>
        public void Settle()
        {
            if (_bar == null) return;
            if (_v.Template?.FindName("PART_ScrollContentPresenter", _v) is FrameworkElement rows)
            {
                var pad = _v.Padding;
                bool column = AlwaysShown && _v.ScrollableHeight > 0;
                rows.Margin = column ? new Thickness(pad.Left, pad.Top, pad.Right + _bar.Width, pad.Bottom) : pad;
            }
            if (AlwaysShown) { _stay?.Stop(); Fade(1, 0); }
            else if (!_bar.IsMouseOver) Fade(0, 0);
        }

        void Show(bool stay = true)
        {
            if (_bar == null) Hook();
            if (_bar == null) return;
            if (_bar.Opacity < 1 && !Tween.Moving(_bar, UIElement.OpacityProperty)) Shows++;
            Fade(1, InMs);
            if (stay) Later();
            else _stay?.Stop();
        }

        void Later()
        {
            _stay ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(StayMs), System.Windows.Threading.DispatcherPriority.Normal, (_, _) =>
            {
                _stay!.Stop();
                if (AlwaysShown) return; // it stays
                if (_bar is { IsMouseOver: true } || _bar is { IsMouseCaptureWithin: true }) return; // pointed at, or its thumb held
                Fade(0, OutMs);
            }, _v.Dispatcher);
            _stay.Stop();
            _stay.Start();
        }

        void Fade(double to, double ms)
        {
            if (_bar == null) return;
            // Pressed and pointed at only while it shows (from the start of its fade in; no longer as it fades out).
            _bar.IsHitTestVisible = to > 0;
            if (!_animate()) { Tween.Stop(_bar, UIElement.OpacityProperty); _bar.Opacity = to; return; }
            Tween.To(_bar, UIElement.OpacityProperty, to, ms, to > 0 ? Tween.Out : Tween.SineInOut);
        }
    }
}
