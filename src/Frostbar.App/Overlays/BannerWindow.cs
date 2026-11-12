using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Localization;
using Frostbar.Core.Shell;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Overlays;

/// <summary>
/// A banner (stage 2-1, the user's choice B): a notice in the shape of the notification centre's cards, on a glass of
/// its own under the menu bar at the right. It never takes the foreground; under the pointer its × comes in place of
/// the time; a click is the notice's (a balloon's app hears it).
/// </summary>
public sealed class BannerWindow : PanelPopup
{
    /// <summary>As wide as the notification centre's cards (its panel 356 less its 10 + 10 margins).</summary>
    public const double BannerWidth = 336;

    readonly Border _dismiss;
    readonly TextBlock _when;
    readonly ContentControl _icon;
    readonly Brush _hover;

    public Notice Notice { get; }
    public FrameworkElement DismissButton => _dismiss;
    public bool PointerOver { get; private set; }
    /// <summary>The banner was clicked (not its ×).</summary>
    public event Action<BannerWindow>? Clicked;
    public event Action<BannerWindow>? Dismissed;
    /// <summary>The pointer came over it (true) or left (false).</summary>
    public event Action<BannerWindow, bool>? Hovered;

    public override bool FadesIn => true;

    public BannerWindow(ThemeSnapshot t, Material m, Strings s, Notice n, ImageSource? icon) : base(t, m)
    {
        Title = "Frostbar.Banner";
        Notice = n;
        _hover = new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        Body.Margin = new Thickness(10, 9, 10, 9);
        Body.MinWidth = 0;
        Body.Width = BannerWidth - 20;

        _icon = new ContentControl { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0), Focusable = false };
        SetIcon(icon);
        _when = new TextBlock { Text = NotificationGroups.When(n.Created, DateTimeOffset.Now, s), FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 1, 0, 0) };
        _dismiss = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = _hover, Child = Glyphs.Stroke(Glyphs.Cross, 10, Text, 1.6),
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden };
        _dismiss.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dismissed?.Invoke(this); };
        var right = new Grid { VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(_when);
        right.Children.Add(_dismiss);
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = n.Title.Length > 0 ? n.Title : n.App, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (n.Body.Length > 0) words.Children.Add(new TextBlock { Text = n.Body, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 50, Margin = new Thickness(0, 1, 0, 0) });
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_icon, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(right, System.Windows.Controls.Dock.Right);
        dock.Children.Add(_icon);
        dock.Children.Add(right);
        dock.Children.Add(words);
        Body.Children.Add(dock);

        MouseEnter += (_, _) => { PointerOver = true; _dismiss.Visibility = Visibility.Visible; _when.Visibility = Visibility.Hidden; Hovered?.Invoke(this, true); };
        MouseLeave += (_, _) => { PointerOver = false; _dismiss.Visibility = Visibility.Hidden; _when.Visibility = Visibility.Visible; Hovered?.Invoke(this, false); };
        MouseLeftButtonUp += (_, e) => { if (!e.Handled) Clicked?.Invoke(this); };
    }

    /// <summary>The app's icon (the bell while it has none: it may still be loading).</summary>
    public void SetIcon(ImageSource? icon) =>
        _icon.Content = icon != null ? new Image { Source = icon, Width = 28, Height = 28 }
            : new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = _hover, Child = Glyphs.Stroke(Glyphs.Bell, 15, Text, 1.4) };

    public bool HasIcon => _icon.Content is Image;

    /// <summary>Its height before it is shown (DIPs, as laid out at its width).</summary>
    public double MeasuredHeight()
    {
        if (IsLoaded && ActualHeight > 0) return ActualHeight;
        Body.Measure(new Size(BannerWidth, double.PositiveInfinity));
        return Body.DesiredSize.Height;
    }
}
