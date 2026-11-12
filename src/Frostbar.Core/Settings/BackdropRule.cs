namespace Frostbar.Core.Settings;

public enum Backdrop { Blur, Solid, Clear }

/// <summary>
/// What the Appearance page's 背景 (material) means for each surface. "auto" (and the older "translucent"): blur where
/// Windows allows it, else solid. "solid": solid everywhere. "transparent": the menu bar only is clear (macOS 26), the
/// rest as "auto" — and with Windows' transparency effects off everything is solid, the menu bar too, as the page says.
/// 2026-10-12: "transparent" made every menu, panel, the app grid and the overview solid instead, and left the bar clear
/// with transparency off.
/// </summary>
public static class BackdropRule
{
    public static Backdrop For(string material, bool blurAllowed, bool menuBar)
    {
        if (material == "solid" || !blurAllowed) return Backdrop.Solid;
        return material == "transparent" && menuBar ? Backdrop.Clear : Backdrop.Blur;
    }
}
