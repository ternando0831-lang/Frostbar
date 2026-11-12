namespace Frostbar.Core.Shell;

/// <summary>
/// The glass's tint from the appearance's slider (macOS 27's Liquid Glass, from ultra clear to tinted; the user's choice
/// "足す（既定は中）", 2026-10-18): how much of each glass's tint (its alpha) is kept. The middle keeps today's tint, the
/// clear end less than half of it, the tinted end 1.3 times it (the candidates' sheet, <c>Lab mock material</c>).
/// </summary>
public static class GlassTints
{
    public const int Min = 0, Max = 100, StepSize = 5, Default = 50;

    /// <summary>The clear end's and the tinted end's share of today's tint.</summary>
    public const double ClearScale = 0.45, TintedScale = 1.3;

    /// <summary>How much of today's tint the setting keeps: 0 → <see cref="ClearScale"/>, 50 → 1, 100 → <see cref="TintedScale"/>, straight between.</summary>
    public static double Scale(int setting)
    {
        double s = Math.Clamp(setting, Min, Max);
        return s <= Default
            ? ClearScale + (1 - ClearScale) * s / Default
            : 1 + (TintedScale - 1) * (s - Default) / (Max - Default);
    }

    /// <summary>A tint's alpha with the setting (0–255).</summary>
    public static byte Alpha(byte alpha, int setting) => (byte)Math.Clamp(Math.Round(alpha * Scale(setting)), 0, 255);
}
