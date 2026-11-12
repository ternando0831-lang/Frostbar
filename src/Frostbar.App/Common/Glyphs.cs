using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Frostbar.App.Common;

/// <summary>
/// Our own vector glyphs for the menu bar (no Apple artwork, no SF Symbols). Drawn on a 16×16 grid and scaled.
/// </summary>
public static class Glyphs
{
    static Geometry G(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }

    /// <summary>Four rounded panes: a neutral mark for the system menu.</summary>
    public static readonly Geometry Logo = G(
        "M0,1.5 A1.5,1.5 0 0 1 1.5,0 H5.5 A1.5,1.5 0 0 1 7,1.5 V5.5 A1.5,1.5 0 0 1 5.5,7 H1.5 A1.5,1.5 0 0 1 0,5.5 Z " +
        "M9,1.5 A1.5,1.5 0 0 1 10.5,0 H14.5 A1.5,1.5 0 0 1 16,1.5 V5.5 A1.5,1.5 0 0 1 14.5,7 H10.5 A1.5,1.5 0 0 1 9,5.5 Z " +
        "M0,10.5 A1.5,1.5 0 0 1 1.5,9 H5.5 A1.5,1.5 0 0 1 7,10.5 V14.5 A1.5,1.5 0 0 1 5.5,16 H1.5 A1.5,1.5 0 0 1 0,14.5 Z " +
        "M9,10.5 A1.5,1.5 0 0 1 10.5,9 H14.5 A1.5,1.5 0 0 1 16,10.5 V14.5 A1.5,1.5 0 0 1 14.5,16 H10.5 A1.5,1.5 0 0 1 9,14.5 Z");

    /// <summary>Magnifier (stroke).</summary>
    public static readonly Geometry Search = G("M6.5,1.5 A5,5 0 1 1 6.49,1.5 Z M10.2,10.2 L14.5,14.5");
    /// <summary>The search panel's web row (stroked).</summary>
    public static readonly Geometry Globe = G("M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z M8,1.5 A3,6.5 0 1 1 7.99,1.5 Z M1.5,8 H14.5");
    /// <summary>The search panel's calculation row (stroked).</summary>
    public static readonly Geometry EqualsSign = G("M3.5,6 H12.5 M3.5,10 H12.5");

    /// <summary>Two switches (stroke + fill dots).</summary>
    public static readonly Geometry ControlCenterTracks = G("M3,4.5 H13 M3,11.5 H13");
    public static readonly Geometry ControlCenterKnobs = G("M10.5,4.5 m-2.6,0 a2.6,2.6 0 1 0 5.2,0 a2.6,2.6 0 1 0 -5.2,0 M5.5,11.5 m-2.6,0 a2.6,2.6 0 1 0 5.2,0 a2.6,2.6 0 1 0 -5.2,0");

    /// <summary>Wi-Fi arcs; level 0..3 selects how many arcs are bright.</summary>
    public static readonly Geometry[] WifiArcs =
    [
        G("M8,13.2 m-1.3,0 a1.3,1.3 0 1 0 2.6,0 a1.3,1.3 0 1 0 -2.6,0"),
        G("M5,10.2 A4.3,4.3 0 0 1 11,10.2"),
        G("M2.6,7.6 A7.7,7.7 0 0 1 13.4,7.6"),
        G("M0.3,5 A11,11 0 0 1 15.7,5"),
    ];

    /// <summary>Ethernet: three connected boxes.</summary>
    public static readonly Geometry Ethernet = G("M6,1.5 H10 V5.5 H6 Z M1.5,10.5 H5.5 V14.5 H1.5 Z M10.5,10.5 H14.5 V14.5 H10.5 Z M8,5.5 V8 M3.5,10.5 V8 H12.5 V10.5");

    /// <summary>Speaker body; waves 0..3 by level.</summary>
    public static readonly Geometry Speaker = G("M1.5,6 H4.5 L8.5,2.5 V13.5 L4.5,10 H1.5 Z");
    public static readonly Geometry[] SpeakerWaves =
    [
        G("M10.5,6.2 A2.4,2.4 0 0 1 10.5,9.8"),
        G("M11.8,4.4 A4.6,4.6 0 0 1 11.8,11.6"),
        G("M13.1,2.6 A6.8,6.8 0 0 1 13.1,13.4"),
    ];
    public static readonly Geometry Mute = G("M10.5,5.5 L14.5,10.5 M14.5,5.5 L10.5,10.5");

    /// <summary>Battery outline (24×12 grid) and the bolt shown while charging.</summary>
    public static readonly Geometry BatteryBody = G("M2.5,0.5 H19.5 A2,2 0 0 1 21.5,2.5 V9.5 A2,2 0 0 1 19.5,11.5 H2.5 A2,2 0 0 1 0.5,9.5 V2.5 A2,2 0 0 1 2.5,0.5 Z");
    public static readonly Geometry BatteryTip = G("M23,4 A1,1 0 0 1 23,8 Z");
    public static readonly Geometry Bolt = G("M12,1.5 L7,6.8 H10.5 L9.5,10.5 L14.5,5.2 H11 Z");

    // ---- Control Center (M6) ----
    /// <summary>Bluetooth's rune (stroke).</summary>
    public static readonly Geometry Bluetooth = G("M4.5,4.8 L11.5,11.2 L8,14.5 V1.5 L11.5,4.8 L4.5,11.2");
    /// <summary>A crescent for Focus (fill).</summary>
    public static readonly Geometry Moon = G("M9.6,1.7 A6.5,6.5 0 1 0 14.3,10.6 A5.2,5.2 0 0 1 9.6,1.7 Z");
    /// <summary>Two screens for screen mirroring (stroke).</summary>
    public static readonly Geometry Mirroring = G("M1.5,2.5 H11.5 V9.5 H1.5 Z M4.5,6.5 H14.5 V13.5 H4.5 Z");
    /// <summary>The sun for the display's brightness (stroke).</summary>
    public static readonly Geometry Sun = G(
        "M8,5 A3,3 0 1 1 7.99,5 Z M8,0.8 V2.4 M8,13.6 V15.2 M0.8,8 H2.4 M13.6,8 H15.2 M2.9,2.9 L4,4 M12,12 L13.1,13.1 M2.9,13.1 L4,12 M12,4 L13.1,2.9");
    /// <summary>Dark mode: a circle (stroke) half filled (fill <see cref="ContrastHalf"/>).</summary>
    public static readonly Geometry Contrast = G("M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z");
    public static readonly Geometry ContrastHalf = G("M8,1.5 A6.5,6.5 0 0 1 8,14.5 Z");
    /// <summary>Media controls (fill).</summary>
    public static readonly Geometry Play = G("M4.5,2.5 L13,8 L4.5,13.5 Z");
    public static readonly Geometry Pause = G("M3.5,2.5 H6.5 V13.5 H3.5 Z M9.5,2.5 H12.5 V13.5 H9.5 Z");
    public static readonly Geometry Next = G("M1.5,3.5 L8,8 L1.5,12.5 Z M8,3.5 L14.5,8 L8,12.5 Z");
    public static readonly Geometry Previous = G("M14.5,3.5 L8,8 L14.5,12.5 Z M8,3.5 L1.5,8 L8,12.5 Z");
    /// <summary>A chevron (stroke): more behind this.</summary>
    public static readonly Geometry Chevron = G("M6,3.5 L10.5,8 L6,12.5");
    /// <summary>Two chevrons (stroke): the menu bar's » for the tray icons that do not fit (stage 0-8, the user's choice A).</summary>
    public static readonly Geometry Overflow = G("M3.5,4 L7.5,8 L3.5,12 M8.5,4 L12.5,8 L8.5,12");
    /// <summary>A padlock: a secured network (fill).</summary>
    public static readonly Geometry Lock = G("M4.5,7 H11.5 A1,1 0 0 1 12.5,8 V13.5 A1,1 0 0 1 11.5,14.5 H4.5 A1,1 0 0 1 3.5,13.5 V8 A1,1 0 0 1 4.5,7 Z M5.5,7 V5 A2.5,2.5 0 0 1 10.5,5 V7 H9.2 V5 A1.2,1.2 0 0 0 6.8,5 V7 Z");
    /// <summary>A tick (stroke): the chosen item.</summary>
    public static readonly Geometry Check = G("M3,8.5 L6.5,12 L13,4.5");
    /// <summary>A chevron pointing left (stroke): back.</summary>
    public static readonly Geometry Back = G("M10,3.5 L5.5,8 L10,12.5");
    /// <summary>Device kinds for the Bluetooth list (stroke).</summary>
    public static readonly Geometry Headphones = G("M2.5,10 V8 A5.5,5.5 0 0 1 13.5,8 V10 M2.5,10 H4.5 V14 H2.5 Z M11.5,10 H13.5 V14 H11.5 Z");
    public static readonly Geometry Mouse = G("M8,1.5 A4.5,4.5 0 0 1 12.5,6 V10 A4.5,4.5 0 0 1 3.5,10 V6 A4.5,4.5 0 0 1 8,1.5 Z M8,1.5 V6 M3.5,6 H12.5");
    public static readonly Geometry Keyboard = G("M1.5,4.5 H14.5 V11.5 H1.5 Z M4,7 H4.5 M6.5,7 H7 M9,7 H9.5 M11.5,7 H12 M5,9.2 H11");
    public static readonly Geometry Phone = G("M5,1.5 H11 A1,1 0 0 1 12,2.5 V13.5 A1,1 0 0 1 11,14.5 H5 A1,1 0 0 1 4,13.5 V2.5 A1,1 0 0 1 5,1.5 Z M7,12.5 H9");
    public static readonly Geometry Computer = G("M2,3 H14 V11 H2 Z M5.5,14 H10.5 M8,11 V14");
    public static readonly Geometry Gamepad = G("M4.5,5 H11.5 A3.5,3.5 0 0 1 11.5,12 L10,10.5 H6 L4.5,12 A3.5,3.5 0 0 1 4.5,5 Z M4.5,8.5 H7 M5.75,7.25 V9.75 M10.5,8 H10.6 M11.6,9 H11.7");
    // ---- Settings' sidebar (stroke) ----
    public static readonly Geometry Gear = G(
        "M8,3 A5,5 0 1 1 7.99,3 Z M8,6 A2,2 0 1 1 7.99,6 Z M8,0.8 V3 M8,13 V15.2 M0.8,8 H3 M13,8 H15.2 M2.9,2.9 L4.5,4.5 M11.5,11.5 L13.1,13.1 M2.9,13.1 L4.5,11.5 M11.5,4.5 L13.1,2.9");
    public static readonly Geometry MenuBarPicture = G("M1.5,2.5 H14.5 V13.5 H1.5 Z M1.5,5.5 H14.5");
    public static readonly Geometry DockPicture = G("M1.5,2.5 H14.5 V13.5 H1.5 Z M4.5,10.8 H11.5");
    public static readonly Geometry CornerPicture = G("M1.5,2.5 H14.5 V13.5 H1.5 Z M14.5,8.5 L9.5,13.5 M11.5,13.5 H14.5 V10.5");
    /// <summary>The app grid's page: nine rounded squares (fill).</summary>
    public static readonly Geometry AppGridPicture = Squares();
    /// <summary>A battery on the 16 grid (stroke) and its charge (fill): the Battery page.</summary>
    public static readonly Geometry BatteryPicture = G("M2.5,4.5 H12 A1,1 0 0 1 13,5.5 V10.5 A1,1 0 0 1 12,11.5 H2.5 A1,1 0 0 1 1.5,10.5 V5.5 A1,1 0 0 1 2.5,4.5 Z M14.8,7 V9");
    public static readonly Geometry BatteryPictureLevel = G("M3.5,6.5 H9.5 V9.5 H3.5 Z");
    /// <summary>An "i" in a circle (stroke): About.</summary>
    public static readonly Geometry Info = G("M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z M8,7.2 V11.5 M8,4.6 V4.8");
    /// <summary>A disk drive (stroke): Storage.</summary>
    public static readonly Geometry Drive = G("M2.5,4 H13.5 A1,1 0 0 1 14.5,5 V11 A1,1 0 0 1 13.5,12 H2.5 A1,1 0 0 1 1.5,11 V5 A1,1 0 0 1 2.5,4 Z M11.5,8 H11.6 M4,8 H8");
    /// <summary>A landscape in a frame (stroke) and its sun (fill): the Wallpaper page.</summary>
    public static readonly Geometry WallpaperPicture = G("M1.5,2.5 H14.5 V13.5 H1.5 Z M1.5,11 L5.5,7 L8.5,10 L10.5,8 L14.5,12");
    public static readonly Geometry WallpaperSun = G("M11,5.4 m-1.3,0 a1.3,1.3 0 1 0 2.6,0 a1.3,1.3 0 1 0 -2.6,0");
    /// <summary>A monitor on its stand (stroke): the Displays page, and the page's picture of a display.</summary>
    public static readonly Geometry DisplayPicture = G("M1.5,2.5 H14.5 V11 H1.5 Z M5.5,14 H10.5 M8,11 V14");
    /// <summary>A trackpad with two fingers' dots (stroke): the Trackpad page.</summary>
    public static readonly Geometry TrackpadPicture = G("M2.5,3.5 H13.5 A1,1 0 0 1 14.5,4.5 V11.5 A1,1 0 0 1 13.5,12.5 H2.5 A1,1 0 0 1 1.5,11.5 V4.5 A1,1 0 0 1 2.5,3.5 Z M6.3,8 H6.4 M9.6,8 H9.7");
    /// <summary>A list of three lines with dots (stroke): General → Login Items.</summary>
    public static readonly Geometry ListPicture = G("M2.5,4.5 H3 M5.5,4.5 H13.5 M2.5,8 H3 M5.5,8 H13.5 M2.5,11.5 H3 M5.5,11.5 H13.5");
    /// <summary>A bell (stroke): notifications.</summary>
    public static readonly Geometry Bell = G("M4,11 V7 A4,4 0 0 1 12,7 V11 L13.5,12.5 H2.5 Z M6.5,14 A1.6,1.6 0 0 0 9.5,14");
    /// <summary>A small cross (stroke): dismiss.</summary>
    public static readonly Geometry Cross = G("M4.5,4.5 L11.5,11.5 M11.5,4.5 L4.5,11.5");
    /// <summary>A microphone (stroke): the Sound page's inputs.</summary>
    public static readonly Geometry Microphone = G("M8,1.5 A2.5,2.5 0 0 1 10.5,4 V8 A2.5,2.5 0 0 1 5.5,8 V4 A2.5,2.5 0 0 1 8,1.5 Z M3.5,7.5 A4.5,4.5 0 0 0 12.5,7.5 M8,12 V14.5");
    /// <summary>An hourglass-like timer (stroke): a focus session.</summary>
    public static readonly Geometry Timer = G("M8,3.5 A5.5,5.5 0 1 1 7.99,3.5 Z M8,6 V9 L10,10.5 M6.5,1.5 H9.5");

    static Geometry Squares()
    {
        var g = new GeometryGroup();
        foreach (double y in new[] { 1.8, 6.3, 10.8 })
            foreach (double x in new[] { 1.8, 6.3, 10.8 })
                g.Children.Add(new RectangleGeometry(new Rect(x, y, 3.4, 3.4), 0.9, 0.9));
        g.Freeze();
        return g;
    }

    /// <summary>A note for media without a cover (fill).</summary>
    public static readonly Geometry Note = G("M6,3 L13.5,1.5 V11 A2,2 0 1 1 11.5,9.3 V4.6 L7.5,5.4 V12.5 A2,2 0 1 1 5.5,10.8 Z");
    /// <summary>A video camera (fill): the privacy indicator while the camera is in use (stage 5-2).</summary>
    public static readonly Geometry Camera = G("M3,4 H9.5 A1.5,1.5 0 0 1 11,5.5 V10.5 A1.5,1.5 0 0 1 9.5,12 H3 A1.5,1.5 0 0 1 1.5,10.5 V5.5 A1.5,1.5 0 0 1 3,4 Z M11.5,7 L14.5,5 V11 L11.5,9 Z");
    /// <summary>Play and pause side by side (fill): the menu bar's Now Playing (stage 5-2, macOS's playpause).</summary>
    public static readonly Geometry PlayPause = G("M1.5,3.5 L8,8 L1.5,12.5 Z M9.6,3.5 H11.6 V12.5 H9.6 Z M12.9,3.5 H14.9 V12.5 H12.9 Z");

    // Stage 6-1, the search's actions and filters (stroke).
    /// <summary>The power symbol: shut down.</summary>
    public static readonly Geometry Power = G("M5.1,4.4 A5,5 0 1 0 10.9,4.4 M8,1.8 V7.6");
    /// <summary>A circular arrow: restart.</summary>
    public static readonly Geometry Restart = G("M12.9,8.6 A5,5 0 1 1 11.4,4.2 M11.8,1.6 V4.6 H8.8");
    /// <summary>A bin: empty the trash.</summary>
    public static readonly Geometry Trash = G("M2.8,4.4 H13.2 M6.2,4.4 V2.8 H9.8 V4.4 M4.2,4.4 L5,14 H11 L11.8,4.4 M6.8,6.8 V11.6 M9.2,6.8 V11.6");
    /// <summary>A door and an arrow out of it: log out.</summary>
    public static readonly Geometry Logout = G("M9.5,2.5 H4 A1,1 0 0 0 3,3.5 V12.5 A1,1 0 0 0 4,13.5 H9.5 M7,8 H14 M11.5,5.5 L14,8 L11.5,10.5");
    /// <summary>A page with a folded corner: the Files filter.</summary>
    public static readonly Geometry Document = G("M4,1.5 H9.5 L12.5,4.5 V14.5 H4 Z M9.5,1.5 V4.5 H12.5");
    /// <summary>A window with its title bar: a window answer.</summary>
    public static readonly Geometry WindowPicture = G("M2.5,3 H13.5 A1,1 0 0 1 14.5,4 V12 A1,1 0 0 1 13.5,13 H2.5 A1,1 0 0 1 1.5,12 V4 A1,1 0 0 1 2.5,3 Z M1.5,5.8 H14.5");
    /// <summary>Three windows spread out: the window overview.</summary>
    public static readonly Geometry OverviewPicture = G("M1.5,3.5 H7 V8 H1.5 Z M9,3.5 H14.5 V8 H9 Z M5,10 H11 V14 H5 Z");
    /// <summary>A board with a clip: the Clipboard filter.</summary>
    /// <summary>Accessibility's figure (a stroke): the head, the arms out, the legs apart (the settings' sidebar, 2026-10-22).</summary>
    public static readonly Geometry AccessibilityFigure = G("M8,2.2 A1.3,1.3 0 1 1 7.99,2.2 Z M3,5.8 L8,6.8 L13,5.8 M8,6.8 V10 M8,10 L5.5,14 M8,10 L10.5,14");
    /// <summary>An account's silhouette (filled): the head and the shoulders, for a user with no picture (macOS's grey one).</summary>
    public static readonly Geometry Person = G("M8,2 A3,3 0 1 1 7.99,2 Z M2.5,14.5 A5.5,5 0 0 1 13.5,14.5 Z");
    public static readonly Geometry Clipboard = G("M5.5,2.5 H4 A1,1 0 0 0 3,3.5 V13.5 A1,1 0 0 0 4,14.5 H12 A1,1 0 0 0 13,13.5 V3.5 A1,1 0 0 0 12,2.5 H10.5 M6,1.5 H10 V3.8 H6 Z");

    // Glyph parts share one 16×16 grid (the Wi-Fi dot, the speaker and its waves are separate paths), so a part must
    // never be stretched to its own bounds (2026-10-12: the Wi-Fi dot filled the whole box, the speaker hid its waves).
    // The grid is scaled as a whole to the requested size.
    static Path OnGrid(Path p, double size)
    {
        p.Width = 16; p.Height = 16;
        p.Stretch = Stretch.None;
        p.VerticalAlignment = VerticalAlignment.Center;
        if (Math.Abs(size - 16) > 0.01) p.LayoutTransform = new ScaleTransform(size / 16, size / 16);
        return p;
    }

    public static Path Fill(Geometry g, double size, Brush brush) => OnGrid(new Path { Data = g, Fill = brush }, size);

    public static Path Stroke(Geometry g, double size, Brush brush, double thickness = 1.5) =>
        OnGrid(new Path
        {
            Data = g, Stroke = brush, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        }, size);
}
