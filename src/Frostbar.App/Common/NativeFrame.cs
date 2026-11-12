using System.Windows;
using System.Windows.Shell;

namespace Frostbar.App.Common;

/// <summary>
/// A real window frame under the shell's own chrome (the settings window, dialogs): WPF's WindowChrome gives the
/// frame's room to the content, and the window keeps a normal window's styles, so Windows animates it like any app's
/// window: it zooms in as it opens, fades as it closes, and minimizes into its Dock icon (2026-10-12: as a
/// WindowStyle.None popup it appeared and went at once).
/// </summary>
public static class NativeFrame
{
    /// <param name="resizable">Its edges take it larger or smaller (the settings window: E4); a dialog keeps its size.</param>
    public static void Apply(Window w, bool resizable = false)
    {
        w.WindowStyle = WindowStyle.SingleBorderWindow;
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(resizable ? 6 : 0),
            GlassFrameThickness = new Thickness(-1),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
        });
    }

    /// <summary>
    /// Windows' rounded corners, and the 1-px line Windows draws along the window's edge in a colour of the shell's
    /// (macOS's windows have a faint light rim in dark, a faint dark one in light). Windows cuts the corners without
    /// smoothing them; that line, drawn smooth along the curve, is what hides the steps. None showed on the settings
    /// window, its corners stepping a pixel at a time (2026-11-08, the user's "ギザギザを直す"; seen enlarged on its
    /// picture).
    /// </summary>
    public static void Round(IntPtr h, bool dark)
    {
        Frostbar.Platform.Interop.Native.SetDwm(h, Frostbar.Platform.Interop.Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2 /*ROUND*/);
        // COLORREF (0x00BBGGRR): a grey darker than the window, between it and what is behind (light: #C8C6C6 was too near
        // the window's own light grey to show, and the corner still stepped on the picture).
        Frostbar.Platform.Interop.Native.SetDwm(h, Frostbar.Platform.Interop.Native.DWMWA_BORDER_COLOR, dark ? 0x00605C5C : 0x00B4B0B0);
    }
}
