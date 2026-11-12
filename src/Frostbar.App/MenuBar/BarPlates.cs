using System.Numerics;
using Frostbar.Platform.Dwm;
using Windows.UI.Composition;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.MenuBar;

/// <summary>
/// The plate behind the menu bar's item pressed or open, with Liquid Glass (2026-10-23, the user's "A 沈む＋隣へ光る"):
/// it comes in over <see cref="Motion.BarPressIn"/> from the press (nothing showed until the menu opened) with the
/// press's light spreading inside it (PressLight's numbers), and goes over <see cref="Motion.BarPressOut"/>. Drawn by
/// composition in the bar's backdrop window, under its text: the WPF bar is not drawn again while a menu opens (a WPF
/// fade there slowed the next menu's opening, 2026-10-12). Without Liquid Glass the bar keeps its plain plate.
/// </summary>
sealed class BarPlates
{
    readonly Compositor _c = Composition.Compositor;
    readonly ShapeVisual _visual;
    readonly CompositionRoundedRectangleGeometry _geometry;
    readonly CompositionSpriteShape _plate, _light;
    readonly CompositionColorBrush _fill;
    readonly CompositionRadialGradientBrush _glow;

    // PressLight's light, in DIPs: 40 → 210 over its spread, alpha 0.6 → 0.12, then gone over its fade.
    const float FromRadius = 40, ToRadius = 210, FromAlpha = 0.6f, ToAlpha = 0.12f;

    public BarPlates(ContainerVisual layer)
    {
        _visual = _c.CreateShapeVisual();
        _geometry = _c.CreateRoundedRectangleGeometry();
        _fill = _c.CreateColorBrush(Composition.Color(0, 0, 0, 0));
        _plate = _c.CreateSpriteShape(_geometry);
        _plate.FillBrush = _fill;
        _glow = _c.CreateRadialGradientBrush();
        _glow.MappingMode = CompositionMappingMode.Absolute;
        _glow.ColorStops.Add(_c.CreateColorGradientStop(0, Composition.Color((byte)(FromAlpha * 255), 255, 255, 255)));
        _glow.ColorStops.Add(_c.CreateColorGradientStop(1, Composition.Color(0, 255, 255, 255)));
        _light = _c.CreateSpriteShape(_geometry);
        _light.FillBrush = _glow;
        _visual.Shapes.Add(_plate);
        _visual.Shapes.Add(_light);
        _visual.Opacity = 0;
        _visual.IsVisible = false;
        layer.Children.InsertAtTop(_visual);
    }

    /// <summary>The item it is behind now (diagnostics), and whether it is shown (asked: DWM fades it).</summary>
    public object? Item { get; private set; }
    public bool Shown { get; private set; }

    /// <summary>Presses it has shown (diagnostics).</summary>
    public int Presses { get; private set; }

    /// <summary>
    /// Behind <paramref name="item"/> at <paramref name="px"/> (the window's pixels), its corners <paramref name="radius"/>
    /// pixels, of <paramref name="color"/>. <paramref name="press"/>: where it was pressed (the window's pixels; its light
    /// spreads from there and it comes in), or null (opened without a press: at once). <paramref name="animate"/> false:
    /// at once, no light.
    /// </summary>
    public void Show(object item, (float x, float y, float w, float h) px, float radius, Windows.UI.Color color, Vector2? press, bool animate, float scale)
    {
        bool same = ReferenceEquals(item, Item) && Shown;
        Item = item;
        Shown = true;
        _visual.Size = new Vector2(px.x + px.w + 1, px.y + px.h + 1);
        _geometry.Offset = new Vector2(px.x, px.y);
        _geometry.Size = new Vector2(px.w, px.h);
        _geometry.CornerRadius = new Vector2(radius, radius);
        _fill.Color = color;
        _visual.IsVisible = true;
        if (press is { } at && animate)
        {
            Presses++;
            Fade(same ? null : 0f, 1, Motion.BarPressIn);
            Light(at, scale);
            return;
        }
        if (same) return; // pressed a moment ago and opened now: as it is
        _visual.StopAnimation("Opacity");
        _visual.Opacity = 1;
        _light.FillBrush = null;
    }

    /// <summary>Goes (over <see cref="Motion.BarPressOut"/>, or at once).</summary>
    public void Hide(bool animate)
    {
        if (!Shown) return;
        Shown = false;
        Item = null;
        if (!animate) { _visual.StopAnimation("Opacity"); _visual.Opacity = 0; _visual.IsVisible = false; return; }
        Fade(null, 0, Motion.BarPressOut, smooth: true);
    }

    void Fade(float? from, float to, double ms, bool smooth = false)
    {
        var a = _c.CreateScalarKeyFrameAnimation();
        if (from is { } f) a.InsertKeyFrame(0f, f);
        a.InsertKeyFrame(1f, to, smooth
            ? _c.CreateCubicBezierEasingFunction(new Vector2(1 / 3f, 0f), new Vector2(2 / 3f, 1f)) // smoothstep
            : _c.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.68f, 1f))); // cubic out
        a.Duration = TimeSpan.FromMilliseconds(ms);
        _visual.StartAnimation("Opacity", a);
    }

    /// <summary>The press's light from <paramref name="at"/>: wider and fainter over its spread, then gone.</summary>
    void Light(Vector2 at, float scale)
    {
        _light.FillBrush = _glow;
        _glow.EllipseCenter = at;
        _glow.GradientOriginOffset = Vector2.Zero;
        var radius = _c.CreateVector2KeyFrameAnimation();
        radius.InsertKeyFrame(0f, new Vector2(FromRadius * scale, FromRadius * scale));
        radius.InsertKeyFrame(1f, new Vector2(ToRadius * scale, ToRadius * scale), _c.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.68f, 1f)));
        radius.Duration = TimeSpan.FromMilliseconds(Motion.PressSpread);
        _glow.StartAnimation("EllipseRadius", radius);
        // A shape has no opacity of its own: the stop's colour carries the light's strength, 0.6 down to 0.12 as it
        // spreads, then out.
        var linear = _c.CreateLinearEasingFunction();
        double all = Motion.PressSpread + Motion.PressFade;
        var stop = (CompositionColorGradientStop)_glow.ColorStops[0];
        var color = _c.CreateColorKeyFrameAnimation();
        color.InsertKeyFrame(0f, Composition.Color((byte)(FromAlpha * 255), 255, 255, 255));
        color.InsertKeyFrame((float)(Motion.PressSpread / all), Composition.Color((byte)(ToAlpha * 255), 255, 255, 255), linear);
        color.InsertKeyFrame(1f, Composition.Color(0, 255, 255, 255), linear);
        color.Duration = TimeSpan.FromMilliseconds(all);
        stop.StartAnimation("Color", color);
    }
}
