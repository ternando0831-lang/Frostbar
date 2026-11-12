using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.MenuBar;
using Frostbar.Platform.Sys;

namespace Frostbar.App.ControlCenter;

/// <summary>
/// The input menu (stage 5-3, macOS's menu under the input source in the menu bar): the keyboard layouts when there
/// are more than one, the Japanese IME's modes (Core's InputModes) with a tick on the one it is in, then "Show Emoji
/// &amp; Symbols" (Windows' Win+.) and the IME's settings (Windows'). Choosing one closes it; the panel never takes the
/// keyboard, so the app in front keeps it and its IME is the one changed.
/// </summary>
public sealed class InputView : PanelView
{
    readonly ViewKit _k;
    readonly ImeMonitor _ime;
    readonly Action _close;
    readonly Action<string> _setMode;
    readonly Action<long> _switchLayout;
    readonly StackPanel _list = new();

    /// <summary>The rows by key ("mode:hiragana", "layout:&lt;hkl&gt;") and the one ticked (diagnostics: the specs click them).</summary>
    public Dictionary<string, Border> Rows { get; } = new();
    public string? Ticked { get; private set; }
    public Border EmojiLink { get; }

    /// <param name="setMode">Puts the app in front's IME in a mode (the shell's guarded command).</param>
    /// <param name="switchLayout">Asks the app in front for a keyboard layout (guarded too).</param>
    /// <param name="emoji">Windows' emoji panel for the app in front (guarded too).</param>
    public InputView(ViewKit k, ImeMonitor ime, Action close, Action<string> setMode, Action<long> switchLayout, Action emoji)
    {
        _k = k;
        _ime = ime;
        _close = close;
        _setMode = setMode;
        _switchLayout = switchLayout;
        Width = 240;
        Children.Add(_list);
        Children.Add(k.Separator());
        EmojiLink = k.Link(k.S["input.emoji"], () => { close(); emoji(); });
        Children.Add(EmojiLink);
        Children.Add(k.Link(k.S["input.settings"], () => { close(); CommandRunner.Launch("ms-settings:regionlanguage"); }));
        Show();
    }

    protected override void Listen(bool on)
    {
        if (on) _ime.Changed += Show;
        else _ime.Changed -= Show;
    }

    protected override void Show()
    {
        _list.Children.Clear();
        Rows.Clear();
        Ticked = null;
        // The layouts, when there is a choice (one Japanese keyboard: nothing to choose).
        var layouts = ImeMonitor.Layouts();
        if (layouts.Count > 1)
        {
            foreach (var (layout, name) in layouts)
                Add($"layout:{layout:X}", name, layout == _ime.Layout, () => _switchLayout(layout));
            if (_ime.Japanese) _list.Children.Add(_k.Separator());
        }
        if (!_ime.Japanese)
        {
            if (layouts.Count <= 1) _list.Children.Add(_k.Note(_k.S["input.noModes"]));
            return;
        }
        string? now = InputModes.Current(_ime.Open, _ime.Conversion);
        foreach (var (key, _, _) in InputModes.All)
            Add("mode:" + key, _k.S["input." + key], key == now, () => _setMode(key));
    }

    /// <summary>A row as a menu's: a tick column, the name; lit under the pointer; choosing it closes the panel.</summary>
    void Add(string key, string title, bool ticked, Action choose)
    {
        var grid = new Grid { Height = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tick = new TextBlock { Text = ticked ? "✓" : "", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var name = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        grid.Children.Add(tick);
        grid.Children.Add(name);
        var row = new Border { Child = grid, Padding = new Thickness(2, 0, 6, 0), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(GlassShape.PanelRow), Background = Brushes.Transparent };
        row.MouseEnter += (_, _) => row.Background = _k.Hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        ViewKit.OnClick(row, () => { choose(); _close(); });
        Rows[key] = row;
        if (ticked) Ticked = key;
        _list.Children.Add(row);
    }
}
