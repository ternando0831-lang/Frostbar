using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Shell;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Sys;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Overlays;

/// <summary>
/// The volume and brightness display (macOS 27.2's, 2026-11-01: a thin pill with the percent until then, the user's
/// choice C of stage 2-2): a capsule with one large glyph at its left (the speaker with the menu bar's volume item's
/// waves, crossed out when muted; the sun) and the level on a bar of sixteen parts, no number. It never takes the
/// foreground, and the pointer goes through it. Where it stands: Core's HudLayout.
/// </summary>
public sealed class HudWindow : PanelPopup
{
    // From 27.2's pictures, against their 24 pt menu bar item: about 308 × 64 pt, the glyph 28, the bar 214 × 4.5, 22 and 31 at its ends.
    public const double PillWidth = 308, PillHeight = 64;
    const double GlyphSize = 28, GlyphGap = 12, GlyphStroke = 1.2, PartWidth = 12, PartGap = 1.6, PartHeight = 4;

    readonly Grid _glyph = new() { Width = GlyphSize, Height = GlyphSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, GlyphGap, 0) };
    readonly Border[] _fills = new Border[HudLayout.Parts];

    /// <summary>
    /// The level shown, 0–1: a key's step slides there (0.12 s), as macOS's bar does. It jumped (2026-10-17). The first
    /// one is there at once.
    /// </summary>
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(HudWindow),
        new PropertyMetadata(0.0, (d, _) => ((HudWindow)d).Fill()));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    const double SlideMs = Motion.HudSlide;
    bool _set;

    void Fill()
    {
        for (int i = 0; i < _fills.Length; i++) _fills[i].Width = HudLayout.PartFill(Level, i) * PartWidth;
    }

    public HudKind Kind { get; private set; }
    public int Percent { get; private set; }
    public bool Muted { get; private set; }

    /// <summary>The bar's parts filled to the end (diagnostics).</summary>
    public int PartsFull => _fills.Count(f => f.Width >= PartWidth - 0.01);

    public override bool FadesIn => true;

    public HudWindow(ThemeSnapshot t, Material m) : base(t, m)
    {
        Title = "Frostbar.Hud";
        IsHitTestVisible = false;
        Body.Margin = new Thickness(20, 0, 32, 0);
        Body.MinWidth = 0;
        Body.Width = PillWidth - 52;
        var trackBrush = new SolidColorBrush(t.Dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < _fills.Length; i++)
        {
            var part = new Grid { Width = PartWidth, Height = PartHeight, Margin = new Thickness(i == 0 ? 0 : PartGap, 0, 0, 0) };
            part.Children.Add(new Border { CornerRadius = new CornerRadius(1), Background = trackBrush });
            _fills[i] = new Border { CornerRadius = new CornerRadius(1), Background = Text, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            Common.Inks.SetInkFill(_fills[i], true); // the text's colour: dark on glass turned light (item 4)
            part.Children.Add(_fills[i]);
            bar.Children.Add(part);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = PillHeight };
        row.Children.Add(_glyph);
        row.Children.Add(bar);
        Body.Children.Add(row);
        // The pointer goes through to what is under it (it is there for a second and a half, over others' windows).
        SourceInitialized += (_, _) => Native.AddExStyle(Handle, Native.WS_EX_TRANSPARENT);
    }

    /// <summary>What it shows now.</summary>
    public void Set(HudKind kind, int percent, int waves, bool muted)
    {
        Kind = kind;
        Percent = Math.Clamp(percent, 0, 100);
        Muted = muted;
        _glyph.Children.Clear();
        if (kind == HudKind.Brightness) _glyph.Children.Add(Glyphs.Stroke(Glyphs.Sun, GlyphSize, Text, GlyphStroke));
        else
        {
            _glyph.Children.Add(Glyphs.Stroke(Glyphs.Speaker, GlyphSize, Text, GlyphStroke));
            if (waves == 0) _glyph.Children.Add(Glyphs.Stroke(Glyphs.Mute, GlyphSize, Text, GlyphStroke));
            else for (int i = 0; i < waves; i++) _glyph.Children.Add(Glyphs.Stroke(Glyphs.SpeakerWaves[i], GlyphSize, Text, GlyphStroke));
        }
        double level = muted ? 0 : Percent / 100.0;
        if (_set && Animate) Tween.To(this, LevelProperty, level, SlideMs);
        else
        {
            Tween.Stop(this, LevelProperty);
            BeginAnimation(LevelProperty, null);
            Level = level;
        }
        _set = true;
    }
}
