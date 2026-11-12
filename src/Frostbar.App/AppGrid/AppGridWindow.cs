using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Frostbar.App.Common;
using Frostbar.Core.AppGrid;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using ContainerVisual = Windows.UI.Composition.ContainerVisual;

namespace Frostbar.App.AppGrid;

/// <summary>
/// The app grid's window (M5): over the monitor below the menu bar (not the whole monitor, or Explorer would take it for
/// a fullscreen app), activated so its search field takes typing. It draws only an almost invisible fill (it takes
/// every click) and the field; the icons, names and page dots are composition visuals above its blurred plate
/// (<see cref="AppGridScene"/>), so turning a page is an animation DWM runs (as the Dock, ADR-007).
/// </summary>
public sealed class AppGridWindow : Window
{
    readonly WindowBackdrop _backdrop;
    readonly Canvas _canvas = new();
    readonly Border _fieldBox, _nameFrame;
    readonly TextBlock _placeholder;

    public TextBox Field { get; }
    /// <summary>An open folder's name being edited, over its title (stage 6 E1).</summary>
    public TextBox NameBox { get; }
    public bool EditingName => _nameFrame.Visibility == Visibility.Visible;
    /// <summary>The name's box lost the keyboard while it was being edited (a click elsewhere: the name is kept).</summary>
    public event Action? NameLeft;
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public IntPtr BackdropHandle => _backdrop.BackdropHandle;
    public ContainerVisual Overlay => _backdrop.Overlay;
    /// <summary>The IME is composing: the text is not final yet (the filter waits).</summary>
    public bool Composing { get; private set; }

    /// <summary>The field's text changed, once it is final (not while the IME composes).</summary>
    public event Action? QueryChanged;
    /// <summary>A horizontal wheel or touchpad scroll (WM_MOUSEHWHEEL; positive: to the right).</summary>
    public event Action<int>? HorizontalWheel;

    public AppGridWindow(Material m, string placeholder)
    {
        Title = "Frostbar.AppGrid";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = Materials.UiFont;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage("ja-JP");
        Shrink(); // until it opens
        var white = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
        var dim = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        Field = new TextBox
        {
            FontSize = 14, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = white, CaretBrush = white,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
        };
        _placeholder = new TextBlock { Text = placeholder, FontSize = 14, Foreground = dim, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var glass = Glyphs.Stroke(Glyphs.Search, 14, dim, 1.4);
        glass.Margin = new Thickness(0, 0, 8, 0);
        glass.VerticalAlignment = VerticalAlignment.Center;
        var inner = new Grid();
        inner.Children.Add(_placeholder);
        inner.Children.Add(Field);
        var row = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };
        DockPanel.SetDock(glass, System.Windows.Controls.Dock.Left);
        row.Children.Add(glass);
        row.Children.Add(inner);
        _fieldBox = new Border
        {
            Child = row, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Width = GridGeometry.FieldWidth, Height = GridGeometry.FieldHeight,
        };
        _canvas.Children.Add(_fieldBox);
        // The folder's name, edited where it shows (macOS): its size and weight, on a faint plate.
        NameBox = new TextBox
        {
            FontSize = FolderGeometry.TitleFontSize, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = white, CaretBrush = white, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0), SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), MaxLength = 80,
        };
        _nameFrame = new Border
        {
            Child = NameBox, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
        };
        NameBox.LostKeyboardFocus += (_, _) => { if (EditingName) NameLeft?.Invoke(); };
        _canvas.Children.Add(_nameFrame);
        Content = _canvas;
        Field.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = Field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!Composing) QueryChanged?.Invoke();
        };
        // As in the search panel: the IME's text is not the query yet.
        TextCompositionManager.AddPreviewTextInputStartHandler(Field, (_, _) => Composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Field, (_, e) => Composing = e.TextComposition.CompositionText.Length > 0);
        TextCompositionManager.AddPreviewTextInputHandler(Field, (_, _) => { Composing = false; Dispatcher.BeginInvoke(() => QueryChanged?.Invoke()); });
        // A click around the field (its frame, the magnifier) puts the caret in it.
        _fieldBox.MouseLeftButtonDown += (_, e) => { Field.Focus(); e.Handled = true; };
        _backdrop = new WindowBackdrop(this, m) { TakesSpill = false }; // the ground under the grid, not a thing on it: no press's light
        SourceInitialized += (_, _) =>
        {
            Native.AddExStyle(Handle, Native.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(Handle).AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == 0x020E /*WM_MOUSEHWHEEL*/)
                {
                    HorizontalWheel?.Invoke((short)((wp.ToInt64() >> 16) & 0xFFFF));
                    handled = true;
                }
                return IntPtr.Zero;
            });
        };
    }

    /// <summary>The field's box in the window (DIPs).</summary>
    public Rect FieldRect => new(Canvas.GetLeft(_fieldBox), Canvas.GetTop(_fieldBox), _fieldBox.Width, _fieldBox.Height);

    /// <summary>The name's edit box in the window (DIPs; empty while not editing).</summary>
    public Rect NameRect => EditingName ? new(Canvas.GetLeft(_nameFrame), Canvas.GetTop(_nameFrame), _nameFrame.Width, _nameFrame.Height) : Rect.Empty;

    /// <summary>Edits <paramref name="name"/> in a box centred on <paramref name="title"/> (DIPs), all of it chosen.</summary>
    public void EditName(Rect title, string name)
    {
        double width = Math.Min(title.Width, 480);
        _nameFrame.Width = width;
        _nameFrame.Height = title.Height + 4;
        Canvas.SetLeft(_nameFrame, Math.Round(title.Left + (title.Width - width) / 2));
        Canvas.SetTop(_nameFrame, Math.Round(title.Top - 2));
        NameBox.Text = name;
        _nameFrame.Visibility = Visibility.Visible;
        NameBox.Focus();
        Keyboard.Focus(NameBox);
        NameBox.SelectAll();
    }

    /// <summary>The name is no longer edited; the search field takes the keyboard again.</summary>
    public void EndEditName()
    {
        if (!EditingName) return;
        _nameFrame.Visibility = Visibility.Collapsed;
        Field.Focus();
        Keyboard.Focus(Field);
    }

    /// <summary>The search field dims while a folder is open (the grid behind dims too).</summary>
    public void DimField(bool dim) => _fieldBox.Opacity = dim ? 0.35 : 1;

    public void SetMaterial(Material m) => _backdrop.SetMaterial(m);

    /// <summary>The blur and the field fade in with the pictures (the opening; at once for 0 ms).</summary>
    // The field's layer moves by the display's frames (Tween), from where the pictures' begin: it ran on WPF's clock,
    // which comes out of its rest unevenly, beside the blur's and the pictures' (2026-10-19, the user's "A5").
    public void FadeIn(double ms)
    {
        _backdrop.FadePlate(ms > 0 ? 0 : 1, 0);
        Tween.Stop(_canvas, OpacityProperty);
        _canvas.BeginAnimation(OpacityProperty, null);
        _canvas.Opacity = ms > 0 ? 0 : 1;
        if (ms <= 0) return;
        _backdrop.FadePlate(1, ms);
        Tween.To(_canvas, OpacityProperty, 1.0, ms, Tween.Out);
    }

    /// <summary>The blur and the field fade out (the closing; the pictures fade in the scene).</summary>
    public void FadeOut(double ms)
    {
        // Slow at both ends, with the pictures (the scene's closing curve).
        _backdrop.FadePlate(0, ms, smooth: true);
        Tween.To(_canvas, OpacityProperty, 0.0, ms, Tween.SineInOut);
    }

    /// <summary>The field's layer's opacity now, and whether it is on its way (diagnostics).</summary>
    public (double Opacity, bool Moving) FieldLayer => (_canvas.Opacity, Tween.Moving(_canvas, OpacityProperty));

    /// <summary>Covers <paramref name="area"/> (DIPs) and puts the field where the layout wants it.</summary>
    public void Place(Rect area, GridLayout layout)
    {
        Left = area.Left;
        Top = area.Top;
        Width = MinWidth = MaxWidth = area.Width;   // pinned, like the search panel (WPF took 2 px there)
        Height = MinHeight = MaxHeight = area.Height;
        Canvas.SetLeft(_fieldBox, layout.Field.X);
        Canvas.SetTop(_fieldBox, layout.Field.Y);
    }

    /// <summary>
    /// One pixel while closed: a layered WPF window keeps a bitmap the size of the window, 16 MB over the whole screen
    /// (2026-10-12). Opening places it again (<see cref="Place"/>).
    /// </summary>
    public void Shrink()
    {
        MinWidth = MinHeight = 0;
        Width = MaxWidth = 1;
        Height = MaxHeight = 1;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _backdrop.Dispose();
    }
}
