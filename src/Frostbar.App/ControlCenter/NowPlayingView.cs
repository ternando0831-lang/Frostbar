using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.Platform.Sys;

namespace Frostbar.App.ControlCenter;

/// <summary>
/// Now Playing (macOS's menu of the same name, stage 5-2): what plays (the system media controls' current session,
/// as the Control Center's tile shows it) larger: its cover, title, artist and app, with previous, play/pause and next,
/// and a link that opens its app. While nothing plays it says so (the session went while the panel was open).
/// </summary>
public sealed class NowPlayingView : PanelView
{
    const double Cover = 64, ButtonDip = 32;
    readonly ViewKit _k;
    readonly MediaService _media;
    readonly Func<string, string> _appName;
    readonly Border _cover;
    readonly Image _art = new() { Width = Cover, Height = Cover, Stretch = Stretch.UniformToFill };
    readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _artist = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _app = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel _playing = new();
    readonly TextBlock _nothing;
    readonly Border _openLink;
    readonly TextBlock _openText = new();
    string _appId = "";

    /// <summary>The play/pause button, and the link to the app (diagnostics: the specs click them).</summary>
    public Border PlayPauseButton { get; }
    public Border PreviousButton { get; }
    public Border NextButton { get; }
    public Border OpenLink => _openLink;
    public string Title => _title.Text;
    public string Artist => _artist.Text;
    public string AppName => _app.Text;
    /// <summary>What the button shows: "pause" while it plays, "play" while it is paused, null while nothing plays.</summary>
    public string? ButtonShows => _playing.Visibility == Visibility.Visible ? (PlayPauseButton.Child as FrameworkElement)?.Tag as string : null;

    /// <param name="open">Opens the app that plays (its AppsFolder entry; the shell's guarded opener).</param>
    public NowPlayingView(ViewKit k, MediaService media, Func<string, string> appName, Action<string> open, Action close)
    {
        _k = k;
        _media = media;
        _appName = appName;
        Width = 280;
        var (header, _) = k.Header(k.S["bar.item.nowPlaying"], null, null);
        Children.Add(header);

        _cover = new Border { Width = Cover, Height = Cover, CornerRadius = new CornerRadius(8), Background = k.CircleOff, ClipToBounds = true, Margin = new Thickness(0, 0, 12, 0) };
        _artist.Foreground = k.Dim;
        _app.Foreground = k.Dim;
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(_title);
        words.Children.Add(_artist);
        words.Children.Add(_app);
        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 8) };
        DockPanel.SetDock(_cover, System.Windows.Controls.Dock.Left);
        top.Children.Add(_cover);
        top.Children.Add(words);
        _playing.Children.Add(top);

        PreviousButton = MediaButton(Glyphs.Previous, "previous", () => _ = _media.PreviousAsync());
        PlayPauseButton = MediaButton(Glyphs.Play, "play", () => _ = _media.PlayPauseAsync());
        NextButton = MediaButton(Glyphs.Next, "next", () => _ = _media.NextAsync());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
        buttons.Children.Add(PreviousButton);
        buttons.Children.Add(PlayPauseButton);
        buttons.Children.Add(NextButton);
        _playing.Children.Add(buttons);
        Children.Add(_playing);

        _nothing = k.Note(k.S["cc.notPlaying"]);
        Children.Add(_nothing);
        Children.Add(k.Separator());
        _openLink = new Border { Child = _openText, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(GlassShape.PanelRow), Background = Brushes.Transparent };
        _openLink.MouseEnter += (_, _) => _openLink.Background = k.Hover;
        _openLink.MouseLeave += (_, _) => _openLink.Background = Brushes.Transparent;
        ViewKit.OnClick(_openLink, () =>
        {
            if (_appId.Length == 0) return;
            close();
            open(@"shell:AppsFolder\" + _appId);
        });
        Children.Add(_openLink);
        Show();
    }

    Border MediaButton(Geometry glyph, string tag, Action click)
    {
        var b = new Border { Width = ButtonDip, Height = ButtonDip, CornerRadius = new CornerRadius(ButtonDip / 2), Background = Brushes.Transparent, Margin = new Thickness(6, 0, 6, 0) };
        SetGlyph(b, glyph, tag);
        b.MouseEnter += (_, _) => b.Background = _k.CircleOff;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        ViewKit.OnClick(b, () => { if (b.Opacity > 0.5) click(); });
        return b;
    }

    void SetGlyph(Border b, Geometry glyph, string tag)
    {
        var p = Glyphs.Fill(glyph, 16, _k.Text);
        p.Tag = tag;
        b.Child = p;
    }

    protected override void Listen(bool on)
    {
        if (on) _media.Changed += Show;
        else _media.Changed -= Show;
    }

    protected override void Show()
    {
        var now = _media.Current;
        _playing.Visibility = now != null ? Visibility.Visible : Visibility.Collapsed;
        _nothing.Visibility = now != null ? Visibility.Collapsed : Visibility.Visible;
        _openLink.Visibility = now != null && now.AppId.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (now == null) { _appId = ""; return; }
        _appId = now.AppId;
        // A session without a title (some players report none) shows under its app's name, as the Control Center's tile.
        string app = now.AppId.Length > 0 ? _appName(now.AppId) : "";
        _title.Text = now.Title.Length > 0 ? now.Title : app;
        _artist.Text = now.Artist;
        _artist.Visibility = now.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _app.Text = now.Title.Length > 0 ? app : "";
        _app.Visibility = _app.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _art.Source = now.Art;
        _cover.Child = now.Art != null ? _art : Glyphs.Fill(Glyphs.Note, 24, _k.Dim);
        if (_cover.Child is FrameworkElement note && now.Art == null) { note.HorizontalAlignment = HorizontalAlignment.Center; note.VerticalAlignment = VerticalAlignment.Center; }
        SetGlyph(PlayPauseButton, now.Playing ? Glyphs.Pause : Glyphs.Play, now.Playing ? "pause" : "play");
        PlayPauseButton.Opacity = now.CanPlayPause ? 1 : 0.35;
        PreviousButton.Opacity = now.CanPrevious ? 1 : 0.35;
        NextButton.Opacity = now.CanNext ? 1 : 0.35;
        _openText.Text = _k.S.F("np.open", app.Length > 0 ? app : now.AppId);
    }
}
