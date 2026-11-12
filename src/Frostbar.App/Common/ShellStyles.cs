using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Common;

/// <summary>
/// The shell's look for WPF's own small windows (2026-10-19, the user's "C1"): tooltips, and the menu a text field opens
/// on a right click (cut, copy, paste). They were Windows' classic ones, square, grey and in the system's font, under
/// the shell's glass. Here they are macOS's: a tooltip a small rounded plate, the menu the shell's menus' shape and rows
/// (GlassShape.Menu, rows 5 in, the accent under the row pointed at). And the ring around what the keyboard chose
/// (Tab): the accent's, rounded (the buttons had none at all: "D1"). Application-wide implicit styles, their colours
/// resources painted again with the theme.
/// </summary>
public static class ShellStyles
{
    const string Xaml = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="ToolTip">
            <Setter Property="OverridesDefaultStyle" Value="True"/>
            <Setter Property="HasDropShadow" Value="False"/>
            <Setter Property="Foreground" Value="{DynamicResource Frostbar.TipText}"/>
            <Setter Property="FontFamily" Value="{DynamicResource Frostbar.Font}"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ToolTip">
                  <Border Background="{DynamicResource Frostbar.TipBack}" BorderBrush="{DynamicResource Frostbar.Edge}" BorderThickness="0.5"
                          CornerRadius="6" Padding="8,3,8,4" Margin="0,0,6,8" MaxWidth="360">
                    <Border.Effect><DropShadowEffect BlurRadius="8" ShadowDepth="2" Direction="270" Opacity="0.22"/></Border.Effect>
                    <ContentPresenter>
                      <ContentPresenter.Resources>
                        <Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap"/></Style>
                      </ContentPresenter.Resources>
                    </ContentPresenter>
                  </Border>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="ContextMenu">
            <Setter Property="OverridesDefaultStyle" Value="True"/>
            <Setter Property="HasDropShadow" Value="False"/>
            <Setter Property="Foreground" Value="{DynamicResource Frostbar.MenuText}"/>
            <Setter Property="FontFamily" Value="{DynamicResource Frostbar.Font}"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ContextMenu">
                  <Border Background="{DynamicResource Frostbar.MenuBack}" BorderBrush="{DynamicResource Frostbar.Edge}" BorderThickness="0.5"
                          CornerRadius="{DynamicResource Frostbar.MenuRadius}" Padding="5" Margin="0,0,8,10" MinWidth="160">
                    <Border.Effect><DropShadowEffect BlurRadius="14" ShadowDepth="3" Direction="270" Opacity="0.28"/></Border.Effect>
                    <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
                  </Border>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="MenuItem">
            <Setter Property="OverridesDefaultStyle" Value="True"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="MenuItem">
                  <Border x:Name="Row" CornerRadius="{DynamicResource Frostbar.MenuRowRadius}" Padding="10,2,12,3" MinHeight="22" Background="Transparent">
                    <DockPanel>
                      <TextBlock DockPanel.Dock="Right" Text="{TemplateBinding InputGestureText}" Margin="24,0,0,0" Opacity="0.55" VerticalAlignment="Center"/>
                      <ContentPresenter ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                    </DockPanel>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True">
                      <Setter TargetName="Row" Property="Background" Value="{DynamicResource Frostbar.Accent}"/>
                      <Setter Property="Foreground" Value="White"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Opacity" Value="0.4"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style x:Key="{x:Static SystemParameters.FocusVisualStyleKey}">
            <Setter Property="Control.Template">
              <Setter.Value>
                <ControlTemplate>
                  <Border Margin="-3" CornerRadius="9" BorderThickness="3" BorderBrush="{DynamicResource Frostbar.FocusRing}" SnapsToDevicePixels="True"/>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator">
            <Setter Property="OverridesDefaultStyle" Value="True"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Separator">
                  <Border Height="1" Margin="10,4,10,4" Background="{DynamicResource Frostbar.Edge}"/>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """;

    /// <summary>Puts the styles in <paramref name="app"/>'s resources (once) and paints them for <paramref name="t"/>.</summary>
    public static void Install(Application app, ThemeSnapshot t)
    {
        if (!app.Resources.Contains(typeof(ToolTip)))
        {
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Xaml));
            // A text field's own menu: WPF's is a type of its own (the editor's), which the ContextMenu style does not
            // reach (it stayed Windows' square grey one: seen in the pictures, 2026-10-19). Each field gets a plain one.
            EventManager.RegisterClassHandler(typeof(TextBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
            {
                if (s is TextBox box && box.ContextMenu == null) box.ContextMenu = EditMenu(box, cut: true);
            }));
            EventManager.RegisterClassHandler(typeof(PasswordBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
            {
                if (s is PasswordBox box && box.ContextMenu == null) box.ContextMenu = EditMenu(box, cut: false);
            }));
        }
        Paint(app, t);
    }

    /// <summary>Cut, Copy, Paste, Select All for <paramref name="field"/> (a password field: Paste and Select All), named and keyed by WPF's commands.</summary>
    static ContextMenu EditMenu(Control field, bool cut)
    {
        var menu = new ContextMenu();
        MenuItem Item(System.Windows.Input.RoutedUICommand c) => new() { Command = c, CommandTarget = field };
        if (cut)
        {
            menu.Items.Add(Item(System.Windows.Input.ApplicationCommands.Cut));
            menu.Items.Add(Item(System.Windows.Input.ApplicationCommands.Copy));
        }
        menu.Items.Add(Item(System.Windows.Input.ApplicationCommands.Paste));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(System.Windows.Input.ApplicationCommands.SelectAll));
        return menu;
    }

    /// <summary>The styles' colours for <paramref name="t"/> (the theme changed).</summary>
    public static void Paint(Application app, ThemeSnapshot t)
    {
        static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        var r = app.Resources;
        r["Frostbar.Font"] = Materials.UiFont;
        r["Frostbar.TipBack"] = B(t.Dark ? Color.FromArgb(0xF2, 0x2E, 0x2E, 0x30) : Color.FromArgb(0xF7, 0xFB, 0xFB, 0xFB));
        r["Frostbar.TipText"] = B(t.Dark ? Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1D, 0x1D, 0x1F));
        // The settings window's pop-up lists' plate: the same menus.
        r["Frostbar.MenuBack"] = B(t.Dark ? Color.FromRgb(0x32, 0x32, 0x34) : Color.FromRgb(0xF6, 0xF6, 0xF6));
        r["Frostbar.MenuText"] = Materials.Text(t);
        r["Frostbar.Edge"] = B(t.Dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0));
        r["Frostbar.Accent"] = B(Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B));
        // The keyboard's focus ring (macOS's: the accent, a little clear, around what Tab chose: D1).
        r["Frostbar.FocusRing"] = B(Color.FromArgb(0x99, t.Accent.R, t.Accent.G, t.Accent.B));
        r["Frostbar.MenuRadius"] = new CornerRadius(GlassShape.Menu);
        r["Frostbar.MenuRowRadius"] = new CornerRadius(GlassShape.MenuRow);
    }
}
