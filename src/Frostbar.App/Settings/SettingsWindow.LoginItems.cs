using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.Core.Shell;
using Frostbar.Platform.Shell;

namespace Frostbar.App.Settings;

/// <summary>General → Login Items (stage 4-1): Frostbar's own switch, then what Task Manager's "Startup apps" lists.</summary>
public sealed partial class SettingsWindow
{
    // The user's own (the Run key and the Startup folder) turn on and off with Task Manager's mark; every user's and the
    // packaged apps' are listed with their switches greyed (they take an administrator, or Windows' settings). Read off
    // the UI thread as the page opens (file versions and packages: 0.3 s on this PC) and again after each change; the
    // rows keep their order (by title) whatever they are switched to.

    void LoginItemsPage()
    {
        var g = _settings.General;
        bool login = _host.LoginItemAvailable;
        var start = Switch("general.loginItem", login && _host.LoginItemOn, on => { g.LaunchAtLogin = on; _host.SetLoginItem(on); _host.Apply(SettingsArea.Other); });
        if (!login) { start.IsEnabled = false; start.Opacity = 0.4; }
        Add(Group(Row(_s["set.loginItem"], start, login ? null : _s["set.loginItemDev"])));

        Add(Caption(_s["login.openAtLogin"]));
        var list = new StackPanel();
        Add(list);
        var failure = Note("");
        failure.Visibility = Visibility.Collapsed;
        Add(failure);
        Add(Note(_s["login.note"]));

        IReadOnlyList<StartupEntry> last = [];
        var shown = new List<string>(); // the rows' keys: the rows are made again only when these change
        var switches = new Dictionary<string, PillSwitch>();
        int iconRetries = 0;
        bool iconsMissing = false;

        void Show(IReadOnlyList<StartupEntry> all)
        {
            last = StartupItems.Others(all);
            var keys = last.Select(e => e.Key).ToList();
            if (keys.SequenceEqual(shown))
            {
                foreach (var e in last) if (switches.TryGetValue(e.Key, out var sw)) sw.IsOn = e.On;
                return;
            }
            shown = keys;
            Rows();
        }

        void Rows()
        {
            list.Children.Clear();
            switches.Clear();
            ForgetControls("login.item.");
            iconsMissing = false;
            if (last.Count == 0) { list.Children.Add(Note(_s["login.none"])); return; }
            list.Children.Add(Group([.. last.Select(e =>
            {
                PillSwitch sw = null!;
                sw = Switch("login.item." + e.Key, e.On, on => { if (e.CanChange) Change(e, on, sw); });
                if (!e.CanChange) { sw.IsEnabled = false; sw.Opacity = 0.4; }
                switches[e.Key] = sw;
                return ItemRow(Picture(e), e.Title, Detail(e), sw);
            })]));
        }

        FrameworkElement Picture(StartupEntry e)
        {
            var img = e.Program != null ? _host.System?.Icon("login:" + e.Key, e.Program) : null;
            if (img == null && e.Program != null) iconsMissing = true;
            FrameworkElement icon = img != null ? new Image { Source = img, Width = 22, Height = 22 }
                : new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Background = _hover };
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            return new Border { Width = 28, Height = 28, Child = icon };
        }

        // Who made it, its value's name when the title is another (Discord's "Update"), and where it is when that is not
        // the user's Run key.
        string? Detail(StartupEntry e)
        {
            string? where = e.Source switch
            {
                StartupSource.UserFolder => _s["login.folder"],
                StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.MachineFolder => _s["login.allUsers"],
                StartupSource.Package => _s["login.store"],
                _ => null,
            };
            string? name = e.Source is StartupSource.UserRun or StartupSource.MachineRun or StartupSource.MachineRun32
                && !string.Equals(e.Name, e.Title, StringComparison.OrdinalIgnoreCase) ? e.Name : null;
            var parts = new[] { e.Publisher, name, where }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            return parts.Length == 0 ? null : string.Join(" · ", parts);
        }

        void Change(StartupEntry e, bool on, PillSwitch sw)
        {
            failure.Visibility = Visibility.Collapsed;
            int page = _shown;
            Task.Run(() => StartupStore.Set(e, on)).ContinueWith(t =>
            {
                if (page != _shown) return;
                if (t.Result is { } why)
                {
                    sw.IsOn = !on;
                    failure.Text = string.Format(_s["login.failed"], why);
                    failure.Visibility = Visibility.Visible;
                }
                OffUi(StartupStore.List, Show);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        OffUi(StartupStore.List, Show);
        // Icons not loaded yet: the rows are made again a few times as they come (one that cannot be read stays a square).
        int thisPage = _shown;
        Every(800, () => { if (iconsMissing && iconRetries++ < 4) WhenUp(thisPage, Rows); });
        AddFooter(_s["login.windowsSettings"], "ms-settings:startupapps");
    }
}
