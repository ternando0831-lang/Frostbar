using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Localization;
using Frostbar.Platform.Sys;
using Windows.Devices.WiFi;

namespace Frostbar.App.ControlCenter;

/// <summary>
/// "Enter the password for …" (macOS asks the same way when a network is joined for the first time): a small window
/// that takes the keyboard. Join tries the password; a wrong one keeps the window open with a note, as macOS does.
/// The password goes to Windows only (a credential for the connection); nothing keeps it here.
/// </summary>
public sealed class WifiPasswordDialog : DialogWindow
{
    public WifiPasswordDialog(ThemeSnapshot t, Strings s, string ssid, Func<string, Task<WiFiConnectionStatus>> join) : base(s["cc.wifi"], t)
    {
        var accent = new SolidColorBrush(Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B));
        var picture = new Grid { Width = 16, Height = 16, LayoutTransform = new ScaleTransform(3, 3), HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < Glyphs.WifiArcs.Length; i++)
            picture.Children.Add(i == 0 ? Glyphs.Fill(Glyphs.WifiArcs[i], 16, accent) : Glyphs.Stroke(Glyphs.WifiArcs[i], 16, accent, 1.6));
        Body.Children.Add(picture);
        Body.Children.Add(Line(s.F("wifi.passwordFor", ssid), 14, bold: true));
        var box = MacControls.Field(new PasswordBox { Width = 280, Margin = new Thickness(0, 14, 0, 4) }, t);
        Body.Children.Add(box);
        var note = Line("", 12, dim: true);
        note.Visibility = Visibility.Collapsed;
        Body.Children.Add(note);
        var cancel = MacButton(s["dlg.cancel"], primary: false);
        var ok = MacButton(s["wifi.join"], primary: true);
        ok.IsEnabled = false;
        box.PasswordChanged += (_, _) => ok.IsEnabled = box.Password.Length > 0;
        cancel.Click += (_, _) => Close();
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = cancel.IsEnabled = box.IsEnabled = false;
            note.Text = s["wifi.joining"];
            note.Visibility = Visibility.Visible;
            var result = await join(box.Password);
            if (result == WiFiConnectionStatus.Success) { Close(); return; }
            note.Text = result == WiFiConnectionStatus.InvalidCredential ? s["wifi.wrongPassword"] : s.F("wifi.failed", ssid);
            if (result == WiFiConnectionStatus.InvalidCredential) Fades.Shake(this, t.Animations); // macOS's "no"
            cancel.IsEnabled = box.IsEnabled = true;
            ok.IsEnabled = box.Password.Length > 0;
            box.SelectAll();
            box.Focus();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Body.Children.Add(buttons);
        Loaded += (_, _) => box.Focus();
    }
}

/// <summary>A menu bar item's panel holding one of the views (Wi-Fi, Sound).</summary>
public sealed class ViewPanel : PanelPopup
{
    public PanelView View { get; }

    public ViewPanel(ThemeSnapshot t, Platform.Dwm.Material m, PanelView view) : base(t, m)
    {
        View = view;
        Body.Margin = new Thickness(12, 10, 12, 8);
        Body.MinWidth = 0;
        Body.Children.Add(view);
    }

    protected override void OnClosed(EventArgs e)
    {
        View.Detach();
        base.OnClosed(e);
    }
}
