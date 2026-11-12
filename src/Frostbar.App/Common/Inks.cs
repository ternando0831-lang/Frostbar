using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Frostbar.App.Common;

/// <summary>
/// What is on a part of glass in the other tone (item 4, the user's "A", 2026-10-19: the glass under a part turned light
/// over a white window, its text dark with it). The part was built in the appearance's colours; turned, its neutral
/// colours are mapped to the other tone's (white text to black, a faint white fill to a faint black one: the light and
/// dark themes' own pairs, about 0.92 of the alpha for text and 0.62 for faint fills), and put back as they were when it
/// turns back. What stands on an opaque fill (a white or accent circle and its glyph, a slider's fill) keeps its colour:
/// that fill, not the glass, is behind it. Colours are mapped from what the part's code set last: a colour it sets
/// afterwards (a switch painted off) is mapped by the next <see cref="Apply"/>, which its owner calls after layout.
/// </summary>
public static class Inks
{
    /// <summary>A control that draws itself and holds its colours: it draws them through <paramref name="map"/> (null: as set).</summary>
    public interface ITinted
    {
        void Tone(Mapping? map);
    }

    /// <summary>A part's colours in the other tone: ink (text, glyphs, lines) and fills.</summary>
    public sealed class Mapping(bool toLight)
    {
        readonly Dictionary<Color, SolidColorBrush> _cache = new();

        /// <summary>The glass turned light (dark ink on it), or dark.</summary>
        public bool ToLight { get; } = toLight;

        /// <summary>A text's, a glyph's or a line's brush in the other tone (itself when it is not the appearance's neutral).</summary>
        public Brush Ink(Brush b) => Mapped(b, fill: false) ?? b;

        /// <summary>A fill's: faint neutral fills mapped, an opaque one kept (what stands on it keeps its colours).</summary>
        public Brush Fill(Brush b) => Mapped(b, fill: true) ?? b;

        internal SolidColorBrush? Mapped(Brush b, bool fill)
        {
            // A colour on its way (a switch painted on or off) is its code's: it paints in the part's tone (ToneOf).
            if (b is not SolidColorBrush s || !s.IsFrozen && Tween.Moving(s)) return null;
            var c = s.Color;
            if (c.A == 0 || fill && c.A >= OpaqueFill) return null;
            var to = Map(c, ToLight);
            if (to == null) return null;
            if (!_cache.TryGetValue(to.Value, out var brush))
            {
                brush = new SolidColorBrush(to.Value);
                brush.Freeze();
                _cache[to.Value] = brush;
            }
            return brush;
        }
    }

    /// <summary>A fill at least this opaque is a surface of its own (a circle switched on), not a tint of the glass.</summary>
    public const byte OpaqueFill = 0x99;

    /// <summary>
    /// An element filled with the text's colour (a level bar: the volume display's): its fill is ink, mapped however
    /// opaque it is (white on light glass vanished).
    /// </summary>
    public static readonly DependencyProperty InkFillProperty =
        DependencyProperty.RegisterAttached("InkFill", typeof(bool), typeof(Inks), new PropertyMetadata(false));

    public static void SetInkFill(DependencyObject o, bool on) => o.SetValue(InkFillProperty, on);
    public static bool GetInkFill(DependencyObject o) => (bool)o.GetValue(InkFillProperty);

    static readonly Mapping ToLightMap = new(toLight: true), ToDarkMap = new(toLight: false);

    /// <summary>The mapping for glass of tone <paramref name="dark"/> in a panel built for <paramref name="appearanceDark"/> (null: the same).</summary>
    public static Mapping? For(bool dark, bool appearanceDark) => dark == appearanceDark ? null : dark ? ToDarkMap : ToLightMap;

    /// <summary>
    /// <paramref name="c"/> in the other tone: on glass turned light, a light neutral colour becomes black (text 0.92 of
    /// its alpha, faint fills 0.62: <c>#EBFFFFFF</c> to <c>#D8000000</c>, <c>#26FFFFFF</c> to <c>#18000000</c>); on glass
    /// turned dark, a dark neutral becomes white the other way. Null: kept (a colour, a grey, a neutral of the other tone).
    /// </summary>
    public static Color? Map(Color c, bool toLight)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min > 16) return null; // a colour (an accent, an app's picture)
        if (toLight)
        {
            if (min < 0xC0) return null;
            return Color.FromArgb((byte)Math.Round(c.A * (c.A >= 0x60 ? 0.92 : 0.62)), 0, 0, 0);
        }
        if (max > 0x40) return null;
        return Color.FromArgb((byte)Math.Min(255, Math.Round(c.A / (c.A >= 0x58 ? 0.92 : 0.62))), 0xFF, 0xFF, 0xFF);
    }

    // What each element had before it was mapped (its local value, or unset), and the brush put in its place: a value the
    // code set since is not ours, and is the element's own again.
    sealed class Kept
    {
        public readonly Dictionary<DependencyProperty, (object original, Brush ours)> Values = new();
    }

    static readonly ConditionalWeakTable<DependencyObject, Kept> _kept = new();

    /// <summary>Elements mapped (diagnostics: a part turned light shows its text dark).</summary>
    public static int Mapped { get; private set; }

    /// <summary>
    /// Colours what is on <paramref name="root"/> (a part of glass, or a whole panel) for glass of tone
    /// <paramref name="dark"/>, the panel built for <paramref name="appearanceDark"/>: mapped when they differ, put back
    /// when they are the same. Cheap when nothing changed (it runs after each layout of a panel with a part turned).
    /// </summary>
    public static void Apply(FrameworkElement root, bool dark, bool appearanceDark)
    {
        Walk(root, root, For(dark, appearanceDark), onFill: false);
    }

    /// <summary>
    /// Diagnostics: what is drawn on <paramref name="root"/> in the wrong tone for glass of tone <paramref name="dark"/>
    /// ("Type.Property #AARRGGBB"): turned, the appearance's neutrals that <see cref="Apply"/> would map now (a colour its
    /// code set since without a layout); not turned, colours still mapped. Shown elements only; self-drawn controls
    /// (<see cref="ITinted"/>) are not looked into.
    /// </summary>
    public static List<string> Strays(FrameworkElement root, bool dark, bool appearanceDark)
    {
        var list = new List<string>();
        Stray(root, For(dark, appearanceDark), onFill: false, list);
        return list;
    }

    static void Stray(DependencyObject o, Mapping? map, bool onFill, List<string> list)
    {
        // Not drawn: hidden, or of no size (a #FFF0F0F0 rectangle of a template, in the panels whose list scrolls: taken for
        // the corner between a scroll viewer's bars, not checked).
        if (o is UIElement { IsVisible: false } || o is FrameworkElement { ActualWidth: <= 0 } or FrameworkElement { ActualHeight: <= 0 }) return;
        void See(DependencyProperty dp, bool fill, bool onIt)
        {
            if (map == null)
            {
                if (_kept.TryGetValue(o, out var kept) && kept.Values.TryGetValue(dp, out var was) && ReferenceEquals(o.ReadLocalValue(dp), was.ours))
                    list.Add($"{o.GetType().Name}.{dp.Name} {(was.ours as SolidColorBrush)?.Color} (still mapped)");
                return;
            }
            if (!onIt && o.GetValue(dp) is SolidColorBrush b && map.Mapped(b, fill) != null) list.Add($"{o.GetType().Name}.{dp.Name} {b.Color}");
        }
        static bool Surface(Brush? b) => b switch { SolidColorBrush s => s.Color.A >= OpaqueFill, null => false, _ => true };
        bool fillHere = false;
        switch (o)
        {
            case Shape s:
                See(Shape.FillProperty, false, onFill);
                See(Shape.StrokeProperty, false, onFill);
                break;
            case Border b when GetInkFill(b):
                See(Border.BackgroundProperty, false, onFill);
                break;
            case Border b:
                See(Border.BackgroundProperty, true, false);
                fillHere = Surface(b.Background);
                See(Border.BorderBrushProperty, false, onFill || fillHere);
                break;
            case Panel p:
                See(Panel.BackgroundProperty, true, false);
                fillHere = Surface(p.Background);
                break;
            case Control c:
                See(Control.BackgroundProperty, true, false);
                fillHere = Surface(c.Background);
                See(Control.BorderBrushProperty, false, onFill || fillHere);
                break;
        }
        // Text: what draws it (inherited values are the mapped root's); mapped values still set are looked for everywhere.
        if (o is TextBlock or Control || map == null && o is FrameworkElement) See(TextElement.ForegroundProperty, false, onFill || fillHere);
        int n = VisualTreeHelper.GetChildrenCount(o);
        for (int i = 0; i < n; i++) Stray(VisualTreeHelper.GetChild(o, i), map, onFill || fillHere, list);
    }

    static void Walk(DependencyObject o, DependencyObject root, Mapping? map, bool onFill)
    {
        if (o is ITinted tinted) tinted.Tone(map);
        bool fillHere = false;
        switch (o)
        {
            case Shape s:
                Ink(s, Shape.FillProperty, map, onFill);
                Ink(s, Shape.StrokeProperty, map, onFill);
                break;
            case Border b when GetInkFill(b):
                Ink(b, Border.BackgroundProperty, map, onFill);
                break;
            case Border b:
                fillHere = Fill(b, Border.BackgroundProperty, map);
                Ink(b, Border.BorderBrushProperty, map, onFill || fillHere);
                break;
            case Panel p:
                fillHere = Fill(p, Panel.BackgroundProperty, map);
                break;
            case Control c:
                fillHere = Fill(c, Control.BackgroundProperty, map);
                Ink(c, Control.BorderBrushProperty, map, onFill || fillHere);
                break;
        }
        // Text: every element's foreground (TextBlock's and Control's are TextElement's); the part's own inherited one set on it.
        if (o is FrameworkElement)
            Ink(o, TextElement.ForegroundProperty, map, onFill || fillHere, inherited: ReferenceEquals(o, root));
        int n = VisualTreeHelper.GetChildrenCount(o);
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i), root, map, onFill || fillHere);
    }

    /// <summary>A fill mapped or put back; true when it is an opaque surface of its own (what is on it keeps its colours).</summary>
    static bool Fill(DependencyObject o, DependencyProperty dp, Mapping? map)
    {
        var value = Set(o, dp, map, fill: true, inherited: false);
        return value switch
        {
            SolidColorBrush s => s.Color.A >= OpaqueFill,
            null => false,
            _ => true, // a picture, a gradient: a surface
        };
    }

    static void Ink(DependencyObject o, DependencyProperty dp, Mapping? map, bool onFill, bool inherited = false) =>
        Set(o, dp, onFill ? null : map, fill: false, inherited);

    /// <summary>Maps or puts back one value; returns the element's own (unmapped) brush.</summary>
    static Brush? Set(DependencyObject o, DependencyProperty dp, Mapping? map, bool fill, bool inherited)
    {
        var local = o.ReadLocalValue(dp);
        _kept.TryGetValue(o, out var kept);
        if (kept != null && kept.Values.TryGetValue(dp, out var was))
        {
            if (ReferenceEquals(local, was.ours))
            {
                if (map != null) return was.original as Brush;
                // Turned back: what it had.
                kept.Values.Remove(dp);
                if (was.original == DependencyProperty.UnsetValue) o.ClearValue(dp);
                else o.SetValue(dp, was.original);
                return was.original as Brush;
            }
            kept.Values.Remove(dp); // set again by its code since: its own now
        }
        Brush? own = local as Brush;
        if (map == null) return own;
        Brush? source = own;
        if (local == DependencyProperty.UnsetValue)
        {
            if (!inherited) return null;
            source = o.GetValue(dp) as Brush; // the panel's text colour, inherited
        }
        if (source == null || map.Mapped(source, fill) is not { } mapped) return own;
        o.SetValue(dp, mapped);
        (kept ?? _kept.GetOrCreateValue(o)).Values[dp] = (local, mapped);
        Mapped++;
        return own;
    }
}
