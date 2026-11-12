using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Sys;

namespace Frostbar.App.MenuBar;

/// <summary>
/// The tray icons the bar has no room for, or that the user put here: a grid four across under the » button (stage
/// 0-8, the user's choice A of three, 2026-10-15), each cell as the bar's (16 DIPs in 28). A press and a release go to
/// the icon's app as on the bar; the panel closes once the button comes up (the app opens its own menu or window, as
/// Windows' flyout of hidden icons closes). While it is open the apps are told where the cells are (<see cref="IconRects"/>).
/// </summary>
public sealed class TrayOverflowPanel : PanelPopup
{
    public const int Across = 4;
    readonly List<(string Key, Border Cell)> _cells = new();

    public TrayOverflowPanel(ThemeSnapshot t, Material m, IReadOnlyList<TrayIconView> icons, Action<string, MouseButton, bool> mouse, Action close) : base(t, m)
    {
        Body.Margin = new Thickness(6);
        Body.MinWidth = 0;
        // Pointing: the views' rows' shade; pressed: the bar's.
        Brush over = new SolidColorBrush(t.Dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        Brush pressed = new SolidColorBrush(t.Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0));
        var grid = new UniformGrid { Columns = Math.Clamp(icons.Count, 1, Across) };
        foreach (var icon in icons)
        {
            var img = new Image { Source = icon.Icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var cell = new Border { Child = img, Width = 28, Height = 28, CornerRadius = new CornerRadius(GlassShape.TrayCell), Background = Brushes.Transparent };
            cell.ToolTip = string.IsNullOrWhiteSpace(icon.Tooltip) ? null : icon.Tooltip;
            AutomationProperties.SetName(cell, icon.Tooltip);
            string key = icon.Key;
            bool down = false;
            cell.MouseEnter += (_, _) => cell.Background = over;
            cell.MouseLeave += (_, _) => { cell.Background = Brushes.Transparent; down = false; };
            cell.MouseDown += (_, e) => { e.Handled = true; down = true; cell.Background = pressed; mouse(key, e.ChangedButton, true); };
            cell.MouseUp += (_, e) =>
            {
                e.Handled = true;
                if (!down) return; // pressed elsewhere (the » button) and let go here
                down = false;
                cell.Background = over;
                mouse(key, e.ChangedButton, false);
                close();
            };
            // The keys (D2): a click on the icon, as its owner hears one (down and up), and the panel closes.
            KeyNav.SetRun(cell, () => { mouse(key, MouseButton.Left, true); mouse(key, MouseButton.Left, false); close(); });
            grid.Children.Add(cell);
            _cells.Add((key, cell));
        }
        Body.Children.Add(grid);
    }

    /// <summary>The icons' keys, in the panel's order (diagnostics).</summary>
    public IReadOnlyList<string> Keys => [.. _cells.Select(c => c.Key)];

    /// <summary>Screen rectangles (pixels) of the cells by key, once the panel is shown.</summary>
    public Dictionary<string, RECT> IconRects()
    {
        var map = new Dictionary<string, RECT>();
        if (!IsVisible) return map;
        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var (key, cell) in _cells)
        {
            var p = cell.PointToScreen(new Point(0, 0)); // device pixels
            map[key] = new RECT((int)Math.Round(p.X), (int)Math.Round(p.Y),
                (int)Math.Round(p.X + cell.ActualWidth * dpi.DpiScaleX), (int)Math.Round(p.Y + cell.ActualHeight * dpi.DpiScaleY));
        }
        return map;
    }
}
