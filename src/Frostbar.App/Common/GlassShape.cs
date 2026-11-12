namespace Frostbar.App.Common;

/// <summary>
/// Liquid Glass's shapes (the user's choice "C 強め" of three pictures, 2026-10-18; `Lab mock shape`): rounder corners
/// than before, each inner one concentric with its plate (the plate's radius less the inset of what is inside it), the
/// Dock's ends half circles, and a deep soft shadow under every glass plate. DIPs. macOS 27 took 26's corners a little
/// back (the user's "作業 5 は両方戻す", 2026-10-19): the menus, the small panels, the search panel, the pop-ups, a
/// stack and a banner tighter than "C" (14, 22, 26, 14, 24, 24 then); the Control Center's and the Notification
/// Center's pieces keep 27's own.
/// </summary>
public static class GlassShape
{
    /// <summary>Menus (the bar's, the Dock's, the submenus): 8 before "C", 14 with it. Their rows sit 5 in (MenuPopup.Pad).</summary>
    public const double Menu = 10, MenuRow = Menu - 5;

    /// <summary>The small panels (Wi-Fi, sound, battery, focus, now playing, input, the tray's »): 10 before "C", 22 with it. Rows sit 8 in.</summary>
    public const double Panel = 16, PanelRow = Panel - 8;

    /// <summary>The tray's » grid: its cells sit 6 in from the panel (they were rounded by 4: C5, 2026-10-19).</summary>
    public const double TrayCell = Panel - 6;

    /// <summary>The Control Center and the Notification Center: 14 before; their tiles and cards (12 before) sit 10 in.</summary>
    public const double LargePanel = 30, Tile = LargePanel - 10;

    /// <summary>A Dock folder's stack: 12 before "C", 24 with it; its items (6 before) sit about 10 in.</summary>
    public const double Stack = 18, StackItem = Stack - 10;

    /// <summary>
    /// The search panel: 16 before "C", 26 with it, 20 after 27's going back; half its field (56) since 2026-11-08, a
    /// capsule as macOS 27's "Search or Ask" while it shows no answers. Its rows (32 high, 7 before) sit 8 in, rounded at
    /// most to half their height.
    /// </summary>
    public const double Search = 28, SearchRow = 16;

    /// <summary>A tray balloon's banner: 12 before "C", 24 with it (the Notification Center's cards are 20).</summary>
    public const double Banner = 18;

    /// <summary>The settings window's pop-up lists: 8 before "C", 14 with it; their rows (4 before) sit 4 in.</summary>
    public const double Popup = 10, PopupRow = Popup - 4;

    /// <summary>
    /// The Dock's plate: rounded by this share of its thickness. Apple's picture of macOS 27 has 23 px on a 72-px plate
    /// (measured 2026-11-08; the user's "A"); its ends were half circles (from "C", 2026-10-18).
    /// </summary>
    public const double DockRoundness = 0.32;

    /// <summary>
    /// The shadow under every glass plate: blurred by this much, this far down, black this dark. Faint and wide (the
    /// user's "B4", 2026-10-18: Apple's Control Center, enlarged, shows nearly none; it was 36, 14, 0.38 and its dark
    /// band under each plate made the glass look like raised buttons).
    /// </summary>
    public const double ShadowBlur = 40, ShadowDrop = 6, ShadowAlpha = 0.12;

    /// <summary>
    /// macOS 27's Control Center (the user's "A", 2026-10-18; `Lab mock cc`): no panel, each control its own glass on a
    /// grid of 60-DIP cells 12 apart: circles and capsules (radius half a cell), wide modules 82 high and square ones
    /// rounded by 26, the Notification Center's cards by 20 and its calendar by 24.
    /// </summary>
    public const double CcCell = 60, CcGap = 12, Module = 26, ModuleHeight = 82, Card = 20, Widget = 24;

    /// <summary>
    /// A view opened in the Control Center (Wi-Fi, Bluetooth, sound, focus) stands 10 DIPs inside a piece of
    /// <see cref="LargePanel"/>, its rows reaching 6 past that: they sit 4 in, concentric with the piece (a row's
    /// rounding held to half its height). They had a small panel's rows' 8 (C5, 2026-10-19).
    /// </summary>
    public const double CcViewInset = 10, CcViewRow = LargePanel - (CcViewInset - 6);

    /// <summary>A piece's shadow: lighter than a plate's (a piece is thinner glass); nearly none (the user's "B4", 2026-10-18; it was 18, 6, 0.22).</summary>
    public const double PieceShadowBlur = 20, PieceShadowDrop = 2, PieceShadowAlpha = 0.06;

    /// <summary>
    /// A light piece's shadow: a little more, so that over a white window the piece shows where it is (it vanished into
    /// it, 2026-10-19).
    /// </summary>
    public const double LightPieceShadowBlur = 24, LightPieceShadowDrop = 3, LightPieceShadowAlpha = 0.10;
}
