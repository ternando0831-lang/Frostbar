using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// A list made again (a view's rows as its service tells of a change, the notification centre's cards) moving instead
/// of jumping: a row that came grows into its room as it fades in, a row that went is put back where it was and shrinks
/// out of its room as it fades, and a row that stayed slides from where it was to its new place, the rows under them
/// following as the rooms open and close (macOS's lists). They were made again at once: rows came and went in a frame
/// and the ones under them jumped (2026-10-19, the user's "B2", "B3").
/// <para>
/// Rows are told apart by a key (<see cref="Keyed"/>); unkeyed ones (lines, headings) are laid out where they fall. The
/// owner takes what is shown (<see cref="Take"/>), clears and fills its list as before, then calls <see cref="Show"/>
/// (or <see cref="Run"/> for lists inside lists: a settings page's cards and their rows). All the rows' movements are
/// one movement (Tween.Together): a room opening and another closing a refresh apart would make the rows under them shake.
/// </para>
/// </summary>
public static class ListMotion
{
    /// <summary>How long rooms open and close and rows slide (as a card dismissed: NotificationCenterPanel.LeaveMs).</summary>
    public const double Ms = Motion.Rows;

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(ListMotion), new PropertyMetadata(null));

    static readonly DependencyProperty LeavingProperty =
        DependencyProperty.RegisterAttached("Leaving", typeof(bool), typeof(ListMotion), new PropertyMetadata(false));

    /// <summary><paramref name="e"/> told apart from the list's other rows by <paramref name="key"/>.</summary>
    public static T Keyed<T>(T e, string key) where T : FrameworkElement
    {
        e.SetValue(KeyProperty, key);
        return e;
    }

    public static string? KeyOf(DependencyObject e) => (string?)e.GetValue(KeyProperty);

    /// <summary>A row put back to shrink out of its room (gone from what the owner shows).</summary>
    public static bool IsLeaving(DependencyObject e) => (bool)e.GetValue(LeavingProperty);

    /// <summary>What a list showed before it was made again: its keyed rows in order, each with its top (DIPs in the list).</summary>
    public sealed class Before
    {
        internal readonly List<(string Key, FrameworkElement Row, double Top, double Height)> Rows = new();
        internal bool Shown;

        /// <summary>The row shown with <paramref name="key"/> (a card whose own rows are taken too), or null.</summary>
        public FrameworkElement? Row(string key) => Rows.Find(r => r.Key == key).Row;

        /// <summary>The keys shown, in order.</summary>
        public IEnumerable<string> Keys => Rows.Select(r => r.Key);
    }

    /// <summary>
    /// The list's keyed rows as shown now (where a slide or a room on its way has them). Not shown (not yet on the screen,
    /// or hidden): nothing to move from, and <see cref="Show"/> moves nothing.
    /// </summary>
    public static Before Take(Panel list)
    {
        var b = new Before { Shown = list.IsVisible && list.IsLoaded && PresentationSource.FromVisual(list) != null };
        if (!b.Shown) return b;
        foreach (UIElement c in list.Children)
        {
            // Not what is on its way out already (put back by a change before, or a card dismissed: faded out).
            if (c is not FrameworkElement f || KeyOf(f) is not { } key || IsLeaving(f) || f.ActualHeight <= 0 || f.Opacity < 0.05) continue;
            double top;
            try { top = f.TransformToAncestor(list).Transform(new Point(0, 0)).Y; }
            catch (InvalidOperationException) { continue; }
            b.Rows.Add((key, f, top, f.ActualHeight));
        }
        return b;
    }

    /// <summary>The last change shown moving (diagnostics): rows that came, went and slid.</summary>
    public static (int Came, int Went, int Slid) Last { get; private set; }

    /// <summary>Changes shown moving since the start (diagnostics).</summary>
    public static int Shows { get; private set; }

    /// <summary>Rows moving now (diagnostics: specs wait for them before they read places).</summary>
    public static int Moving
    {
        get
        {
            _moving.RemoveAll(o => !Tween.Moving(o));
            return _moving.Count;
        }
    }

    // What the movements move (rows and their slides): a movement taken over by another, or a list made again, drops out.
    static readonly List<DependencyObject> _moving = new();

    /// <summary>
    /// Moves <paramref name="list"/>, just filled again, from what <paramref name="before"/> took. Rows that came open their
    /// room from nothing, coming down <paramref name="comeFrom"/> DIPs (a notification coming in from above) as they fade
    /// in; rows that went are put back and close theirs; rows that stayed slide from their old place. Returns the rows
    /// put back to go (their owner keeps their glass until <paramref name="gone"/> says they have gone). Nothing moves
    /// when <paramref name="animate"/> is off or the list was not shown.
    /// </summary>
    public static IReadOnlyList<FrameworkElement> Show(Panel list, Before before, bool animate, double comeFrom = 0, Action<IReadOnlyList<FrameworkElement>>? gone = null) =>
        Run([(list, before, comeFrom)], animate, gone);

    /// <summary>
    /// <see cref="Show"/> for several lists as one movement: lists inside others first (a card's rows, then the page's
    /// cards: a card's room follows its rows as they come and go, and the cards under it slide from where they were).
    /// </summary>
    public static IReadOnlyList<FrameworkElement> Run(IReadOnlyList<(Panel List, Before Before, double ComeFrom)> lists, bool animate,
        Action<IReadOnlyList<FrameworkElement>>? gone = null)
    {
        if (!animate) return [];
        var parts = new List<Tween.Part>();
        var finish = new List<Action>();
        var leaving = new List<FrameworkElement>();
        int came = 0, went = 0, slid = 0;
        foreach (var (list, before, comeFrom) in lists)
        {
            if (!before.Shown) continue;
            var (c, w, s) = Prepare(list, before, comeFrom, parts, finish, leaving);
            came += c;
            went += w;
            slid += s;
        }
        if (parts.Count == 0) return [];
        Last = (came, went, slid);
        Shows++;
        // Only what still moves is kept: trimmed here, not only when the diagnostics read it (the installed shell never
        // did, and every row ever moved kept its closed panel's whole tree, 2026-11-06 review).
        _moving.RemoveAll(o => !Tween.Moving(o));
        _moving.AddRange(parts.Select(p => p.Target).Distinct());
        Tween.Together(Ms, parts, () =>
        {
            foreach (var f in finish) f();
            gone?.Invoke(leaving);
        });
        return leaving;
    }

    static (int came, int went, int slid) Prepare(Panel list, Before before, double comeFrom, List<Tween.Part> parts, List<Action> finish, List<FrameworkElement> leaving)
    {
        var now = list.Children.OfType<FrameworkElement>().Where(f => KeyOf(f) != null).ToList();
        var newKeys = now.Select(f => KeyOf(f)!).ToHashSet();
        var oldKeys = before.Rows.Select(r => r.Key).ToHashSet();
        var coming = now.Where(f => !oldKeys.Contains(KeyOf(f)!)).ToList();

        // Rows that went: back where they were, before the next row that stays (after the last one before it when none
        // follows), at the height they had. One still in the card it was in (that card made anew) is taken out of it.
        var going = new List<(FrameworkElement Row, double Height)>();
        for (int i = 0; i < before.Rows.Count; i++)
        {
            var (key, row, _, height) = before.Rows[i];
            if (newKeys.Contains(key)) continue;
            if (row.Parent is Panel old && !ReferenceEquals(old, list)) old.Children.Remove(row);
            if (row.Parent != null) continue;
            int at = -1;
            for (int j = i + 1; j < before.Rows.Count && at < 0; j++)
                if (now.FirstOrDefault(f => KeyOf(f) == before.Rows[j].Key) is { } next) at = list.Children.IndexOf(next);
            for (int j = i - 1; j >= 0 && at < 0; j--)
            {
                var prev = now.FirstOrDefault(f => KeyOf(f) == before.Rows[j].Key) ?? going.Find(g => KeyOf(g.Row) == before.Rows[j].Key).Row;
                if (prev != null && list.Children.IndexOf(prev) is int k && k >= 0) at = k + 1;
            }
            if (at < 0) at = now.Count > 0 ? list.Children.IndexOf(now[0]) : list.Children.Count;
            row.SetValue(LeavingProperty, true);
            row.IsHitTestVisible = false;
            list.Children.Insert(Math.Clamp(at, 0, list.Children.Count), row);
            going.Add((row, height));
        }
        // Their full sizes, then the rooms of the rows that came closed and those of the rows that went open: where
        // the rows that stayed start from.
        if (coming.Count > 0 || going.Count > 0) list.UpdateLayout();
        int before0 = parts.Count;
        foreach (var f in coming)
        {
            double h = f.ActualHeight;
            var m = f.Margin;
            f.ClipToBounds = true;
            f.Height = 0;
            f.Margin = new Thickness(m.Left, 0, m.Right, 0);
            parts.Add(new(f, FrameworkElement.HeightProperty, h, Tween.InOut));
            parts.Add(new(f, FrameworkElement.MarginProperty, m, Tween.InOut));
            // A border is never less tall than its padding: it opens too (NotificationCenterPanel.Leave).
            if (f is Border b)
            {
                var p = b.Padding;
                b.Padding = new Thickness(p.Left, 0, p.Right, 0);
                parts.Add(new(b, Border.PaddingProperty, p, Tween.InOut));
            }
            f.Opacity = 0;
            parts.Add(new(f, UIElement.OpacityProperty, 1.0, Tween.Linear));
            if (comeFrom != 0 && !ReduceMotion.Calms("rowComeFrom") && Slide(f) is { } t) // reduced motion: it grows in where it is
            {
                t.Y = -comeFrom;
                parts.Add(new(t, TranslateTransform.YProperty, 0.0, Tween.Out));
            }
        }
        foreach (var (f, h) in going)
        {
            var m = f.Margin;
            f.ClipToBounds = true;
            f.Height = h;
            parts.Add(new(f, FrameworkElement.HeightProperty, 0.0, Tween.InOut));
            parts.Add(new(f, FrameworkElement.MarginProperty, new Thickness(m.Left, 0, m.Right, 0), Tween.InOut));
            if (f is Border b) parts.Add(new(b, Border.PaddingProperty, new Thickness(b.Padding.Left, 0, b.Padding.Right, 0), Tween.InOut));
            parts.Add(new(f, UIElement.OpacityProperty, 0.0, Tween.Linear, 0.7));
        }
        list.UpdateLayout();
        int slid = 0;
        foreach (var f in now)
        {
            if (coming.Contains(f) || before.Rows.FindIndex(r => r.Key == KeyOf(f)) is var i && i < 0) continue;
            double y;
            try { y = f.TransformToAncestor(list).Transform(new Point(0, 0)).Y; }
            catch (InvalidOperationException) { continue; }
            double dy = before.Rows[i].Top - y;
            if (Math.Abs(dy) < 0.5 || Slide(f) is not { } t) continue;
            t.Y = dy;
            parts.Add(new(t, TranslateTransform.YProperty, 0.0, Tween.InOut));
            slid++;
        }
        if (parts.Count == before0) return (0, 0, 0);
        var gone = going.Select(g => g.Row).ToList();
        leaving.AddRange(gone);
        var slides = parts.Skip(before0).Where(p => p.Target is TranslateTransform).Select(p => (TranslateTransform)p.Target).ToHashSet();
        finish.Add(() =>
        {
            foreach (var f in gone) list.Children.Remove(f);
            // The slides given back: a part that bounces under a press (GlassBounce) puts its own scale there only
            // where nothing else is (a card come in did not bounce).
            foreach (var f in now)
                if (f.RenderTransform is TranslateTransform t && slides.Contains(t)) f.ClearValue(UIElement.RenderTransformProperty);
            foreach (var f in coming)
            {
                // Their own sizes again (a row's text can change later: it grows with it).
                f.ClearValue(FrameworkElement.HeightProperty);
                f.ClipToBounds = false;
            }
        });
        return (coming.Count, going.Count, slid);
    }

    /// <summary>The row's slide (its own translate; null when it has another transform of its own: it keeps that, unslid).</summary>
    static TranslateTransform? Slide(FrameworkElement f)
    {
        if (f.RenderTransform is TranslateTransform t) return t;
        if (f.RenderTransform is null || f.RenderTransform == Transform.Identity)
        {
            var n = new TranslateTransform();
            f.RenderTransform = n;
            return n;
        }
        return null;
    }
}
