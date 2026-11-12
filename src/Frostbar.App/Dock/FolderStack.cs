using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Dock;
using Frostbar.Core.Localization;
using Frostbar.Platform.Apps;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Dock;

/// <summary>
/// A folder kept in the Dock, opened (macOS's stack shown as a grid): its newest items first, each with its icon and
/// name; a click opens one, the last row opens the folder in the file manager (File Explorer or the app chosen). Like the
/// Dock's menus it never activates, and closes on an outside click or Esc.
/// </summary>
public sealed class FolderStackPopup : PanelPopup
{
    public const int Columns = 4, MaxItems = 60;
    const double Cell = 88, IconDip = 48;

    readonly Func<string, ImageSource?> _icon;
    readonly List<(string Path, Border Cell, Image Picture)> _cells = new();
    readonly Brush _hover;

    public override bool FadesIn => true;
    public override bool RisesFromDock => true;

    /// <summary>The items shown, newest first, with their cells (diagnostics).</summary>
    public IReadOnlyList<(string Path, FrameworkElement Cell)> Cells => _cells.Select(c => (c.Path, (FrameworkElement)c.Cell)).ToList();

    public FolderStackPopup(ThemeSnapshot t, Material m, Strings s, string folder, string name, Func<string, ImageSource?> icon,
        Action<string> open, Action openFolder) : base(t, m)
    {
        _icon = icon;
        _hover = new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0, 0, 0));
        Body.MinWidth = Columns * Cell;
        Body.Children.Add(Heading(name));
        var items = List(folder);
        if (items.Count == 0) Body.Children.Add(new TextBlock { Text = s["dock.stackEmpty"], Foreground = Dim, Margin = new Thickness(0, 4, 0, 4) });
        else
        {
            var grid = new WrapPanel { Width = Columns * Cell };
            foreach (var item in items) grid.Children.Add(CellFor(item, open));
            var scroll = new ScrollViewer
            {
                // 3.5 rows (a row is the cell, Cell + 4, and its margins, 2 + 2): the half row says there is more below.
                Content = grid, MaxHeight = (Cell + 8) * 3.5, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            };
            MacScrollBar.Apply(scroll, () => Animate);
            SmoothScroll.Attach(scroll, () => Animate);
            Body.Children.Add(scroll);
        }
        Body.Children.Add(new Border { Height = 1, Background = Dim, Opacity = 0.35, Margin = new Thickness(0, 8, 0, 0) });
        Body.Children.Add(LinkRow(s.F("dock.openIn", FileManager.NameIn(s)), openFolder)); // "Open in <the chosen app>" when there is one
    }

    /// <summary>The newest items first (macOS sorts a stack by the date added: the creation time here), hidden ones left out.</summary>
    static List<FileSystemInfo> List(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderByDescending(f => f.CreationTimeUtc)
                .Take(MaxItems)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    Border CellFor(FileSystemInfo item, Action<string> open)
    {
        var picture = new Image { Width = IconDip, Height = IconDip, Source = _icon(item.FullName), HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        // The name as File Explorer shows it (known extensions hidden if it hides them; never ".lnk").
        string name = ShellFiles.DisplayName(item.FullName);
        var label = new TextBlock
        {
            Text = StackLabel.TwoLines(name, Measure, LabelWidth), FontSize = LabelSize, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(picture);
        stack.Children.Add(label);
        var cell = new Border
        {
            Child = stack, Width = Cell - 4, Height = Cell + 4, Margin = new Thickness(2), Padding = new Thickness(4, 6, 4, 2),
            CornerRadius = new CornerRadius(GlassShape.StackItem), Background = Brushes.Transparent, ToolTip = name,
        };
        // Opened by a press that began on it (as ViewKit.OnClick): pressed on one file and let go on another, the other opened.
        bool pressed = false;
        cell.MouseEnter += (_, _) => cell.Background = _hover;
        cell.MouseLeave += (_, _) => { cell.Background = Brushes.Transparent; pressed = false; };
        cell.MouseLeftButtonDown += (_, _) => pressed = true;
        cell.MouseLeftButtonUp += (_, _) => { if (!pressed) return; pressed = false; open(item.FullName); };
        KeyNav.SetRun(cell, () => open(item.FullName)); // D2
        GlassBounce.Attach(cell, () => Animate, Frostbar.Core.Shell.PressGive.Picture); // it sinks under a press (2026-10-23)
        _cells.Add((item.FullName, cell, picture));
        return cell;
    }

    const double LabelSize = 11.5, LabelWidth = Cell - 12; // the cell less its margin and padding
    Typeface? _typeface;

    double Measure(string s)
    {
        _typeface ??= new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, LabelSize,
            Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
    }

    /// <summary>How many of the items show their icon (diagnostics).</summary>
    public int IconsShown => _cells.Count(c => c.Picture.Source != null);

    /// <summary>Icons that have arrived since (they are read one by one by the icon helper).</summary>
    public void RefreshIcons()
    {
        foreach (var (path, _, picture) in _cells)
            if (picture.Source == null) picture.Source = _icon(path);
    }
}
