using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Shell;
using Microsoft.Win32;
using Windows.System.Power;
using Windows.UI.ViewManagement;

namespace Frostbar.Platform.Sys;

/// <param name="Dark">
/// Draw dark: the light or dark mode chosen (Windows' or the shell's), and always in high contrast, where the shell's
/// text is white (light surfaces put it white on white: the settings, the welcome, the dialogs).
/// </param>
/// <param name="ChosenDark">The light or dark mode itself, high contrast or not (the Control Center's tile shows it).</param>
public sealed record ThemeSnapshot(bool Dark, bool AppsDark, Windows.UI.Color Accent, bool Transparency, bool HighContrast, bool EnergySaver, bool Animations,
    bool ChosenDark)
{
    /// <summary>Blur needs transparency effects on, no high contrast and no energy saver (ADR-003).</summary>
    public bool BlurAllowed => Transparency && !HighContrast && !EnergySaver;
}

/// <summary>
/// Windows appearance, live. The shell chrome follows SystemUsesLightTheme (like the taskbar); windows with app content
/// (Settings) follow AppsUseLightTheme. Change notifications arrive on several threads and are marshalled to the UI.
/// Lives as long as the app (the WinRT handlers are never removed).
/// </summary>
public sealed class ThemeService
{
    readonly Dispatcher _ui;
    readonly UISettings _settings = new();

    public ThemeSnapshot Current { get; private set; }
    public event Action<ThemeSnapshot>? Changed;

    /// <summary>The settings' light or dark for the shell (null: Windows' mode). <see cref="Refresh"/> applies it.</summary>
    public bool? DarkOverride { get; set; }
    /// <summary>The settings' animation choice (null: Windows' "Animation effects"). <see cref="Refresh"/> applies it.</summary>
    public bool? AnimationsOverride { get; set; }
    /// <summary>
    /// The settings' accent colour (<see cref="Core.Shell.AccentColors"/>: "system" is Windows'). The shell's only; not in
    /// high contrast, where the user's highlight stays. <see cref="Refresh"/> applies it.
    /// </summary>
    public string Accent { get; set; } = Core.Shell.AccentColors.System;

    /// <summary>Start-up warm-up, any thread: loads what the first <see cref="UISettings"/> needs (one made and dropped).</summary>
    public static void WarmUp()
    {
        try { _ = new UISettings().GetColorValue(UIColorType.Accent); _ = PowerManager.EnergySaverStatus; }
        catch (Exception) { }
    }

    public ThemeService(Dispatcher ui, MessageWindow messages)
    {
        _ui = ui;
        Current = Read();
        messages.SettingChanged += area => { if (area is null or "ImmersiveColorSet" or "WindowsThemeElement" or "Policy") Refresh(); };
        _settings.ColorValuesChanged += (_, _) => _ui.BeginInvoke(Refresh);
        _settings.AdvancedEffectsEnabledChanged += (_, _) => _ui.BeginInvoke(Refresh);
        _settings.AnimationsEnabledChanged += (_, _) => _ui.BeginInvoke(Refresh);
        PowerManager.EnergySaverStatusChanged += (_, _) => _ui.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        Stats.Inc("theme.refresh");
        var next = Read();
        if (next == Current) return;
        Log.Info($"theme: dark={next.Dark} accent=#{next.Accent.R:X2}{next.Accent.G:X2}{next.Accent.B:X2} transparency={next.Transparency} highContrast={next.HighContrast} energySaver={next.EnergySaver} animations={next.Animations}");
        Current = next;
        Changed?.Invoke(next);
    }

    /// <summary>
    /// Switches Windows to dark or light (both the system's and the apps' setting, as the quick settings' Personalization
    /// page does) and tells every window (WM_SETTINGCHANGE "ImmersiveColorSet"); <see cref="Changed"/> follows.
    /// </summary>
    public static void SetDark(bool dark)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        }
        Task.Run(() => Interop.Native.BroadcastSettingChange("ImmersiveColorSet"));
        Log.Info($"theme: switched Windows to {(dark ? "dark" : "light")}");
    }

    /// <summary>Whether Windows itself is dark now (its system setting, whatever the shell's own choice).</summary>
    public static bool WindowsDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return !(k?.GetValue("SystemUsesLightTheme") is int s && s != 0);
    }

    ThemeSnapshot Read()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool systemLight = k?.GetValue("SystemUsesLightTheme") is int s && s != 0;
        bool appsLight = k?.GetValue("AppsUseLightTheme") is int a && a != 0;
        bool saver = false;
        try { saver = PowerManager.EnergySaverStatus == EnergySaverStatus.On; } catch (Exception) { }
        bool dark = DarkOverride ?? !systemLight, highContrast = global::System.Windows.SystemParameters.HighContrast;
        // In high contrast, the user's own colours (D5, 2026-10-19: always dark, black and white, whatever theme they chose;
        // Desert's is light): dark when the theme's window background is, the accent its highlight.
        var back = global::System.Windows.SystemColors.WindowColor;
        var highlight = global::System.Windows.SystemColors.HighlightColor;
        bool contrastDark = back.R * 0.2126 + back.G * 0.7152 + back.B * 0.0722 < 128;
        var chosen = Core.Shell.AccentColors.Of(Accent, dark);
        return new ThemeSnapshot(
            Dark: highContrast ? contrastDark : dark,
            AppsDark: !appsLight,
            Accent: highContrast ? global::Windows.UI.Color.FromArgb(255, highlight.R, highlight.G, highlight.B)
                : chosen is { } c ? global::Windows.UI.Color.FromArgb(255, c.R, c.G, c.B) : _settings.GetColorValue(UIColorType.Accent),
            Transparency: _settings.AdvancedEffectsEnabled,
            HighContrast: highContrast,
            EnergySaver: saver,
            Animations: AnimationsOverride ?? _settings.AnimationsEnabled,
            ChosenDark: dark);
    }
}
