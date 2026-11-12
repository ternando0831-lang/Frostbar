using System.Numerics;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using Windows.UI.Composition;

namespace Frostbar.App.Dock;

/// <summary>
/// The icon that follows the pointer while it is dragged out of its slot (in the Dock, or out of the app grid): a small
/// click-through composition window above everything, because the Dock's own windows clip their pictures at their
/// edges and an icon dragged off the Dock must stay visible (and the grid's are below the Dock). Over the remove zone
/// it turns translucent with a label above it.
/// </summary>
public sealed class DragImage : IDisposable
{
    readonly CompositionWindow _window;
    readonly SpriteVisual _icon, _label;
    CompositionSurfaceBrush? _labelBrush;
    Vector2 _labelSize;
    string? _labelText;

    public DragImage()
    {
        var c = Composition.Compositor;
        _window = new CompositionWindow(new RECT(0, 0, 1, 1), clickThrough: true);
        var root = c.CreateContainerVisual();
        _icon = c.CreateSpriteVisual();
        _label = c.CreateSpriteVisual();
        _label.IsVisible = false;
        root.Children.InsertAtTop(_icon);
        root.Children.InsertAtTop(_label);
        _window.SetRoot(root);
    }

    /// <summary>
    /// Shows <paramref name="icon"/> at <paramref name="sizePx"/> centred on the screen point (<paramref name="x"/>,
    /// <paramref name="y"/>), with <paramref name="label"/> above it when not null (made by <paramref name="makeLabel"/>
    /// the first time it changes). The point may fall between pixels (a picture flying): the icon's own edges are
    /// rounded from it, halves up (its centre rounded first and half an odd size taken off, halves to even, put it a
    /// pixel off by turns as it shrank a pixel a frame, and a pixel off its slot as it landed: 2026-10-23).
    /// </summary>
    public void Show(CompositionBrush? icon, float sizePx, double x, double y, float opacity, string? label = null,
        Func<string, (CompositionSurfaceBrush brush, Vector2 size)>? makeLabel = null)
    {
        if (label != _labelText)
        {
            if (_labelBrush != null) SurfaceFactory.Release(_labelBrush);
            _labelBrush = null;
            _labelText = label;
            if (label != null && makeLabel != null)
            {
                (_labelBrush, _labelSize) = makeLabel(label);
                _label.Brush = _labelBrush;
                _label.Size = _labelSize;
            }
        }
        bool withLabel = _labelBrush != null;
        float gap = 8;
        float w = Math.Max(sizePx, withLabel ? _labelSize.X : 0), h = sizePx + (withLabel ? _labelSize.Y + gap : 0);
        _hideLater?.Stop();
        _icon.Brush = icon;
        int inX = (int)Math.Floor((w - sizePx) / 2), inY = (int)Math.Round(h - sizePx); // on whole pixels of the window
        _icon.Size = new Vector2(sizePx, sizePx);
        _icon.Offset = new Vector3(inX, inY, 0);
        _icon.Opacity = opacity;
        _icon.IsVisible = true;
        _label.IsVisible = withLabel;
        if (withLabel) _label.Offset = new Vector3((w - _labelSize.X) / 2, 0, 0);
        int left = (int)Math.Floor(x - sizePx / 2.0 + 0.5) - inX, top = (int)Math.Floor(y - sizePx / 2.0 + 0.5) - inY;
        _window.Move(new RECT(left, top, left + (int)Math.Ceiling(w), top + (int)Math.Ceiling(h)));
        _window.Show();
    }

    public void Hide() => _window.Hide();

    System.Windows.Threading.DispatcherTimer? _hideLater;

    /// <summary>
    /// Gone in the same composition frame as the changes made with it (its icon taken off the window: the icon that
    /// takes its place in the Dock shows in that frame), the window itself hidden a moment later. Hiding the window at
    /// once can leave a frame with neither.
    /// </summary>
    public void Vanish()
    {
        _icon.IsVisible = false;
        _label.IsVisible = false;
        _hideLater ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(80), System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) => { _hideLater!.Stop(); _window.Hide(); }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _hideLater.Stop();
        _hideLater.Start();
    }

    public void Dispose()
    {
        _hideLater?.Stop();
        if (_labelBrush != null) SurfaceFactory.Release(_labelBrush);
        _window.Dispose();
    }
}
