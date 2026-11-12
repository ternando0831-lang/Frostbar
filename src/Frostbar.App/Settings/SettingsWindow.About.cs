using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Settings;

/// <summary>General → About and Storage (macOS's System Settings pages of those names), in place of Windows' settings.</summary>
public sealed partial class SettingsWindow
{
    void AboutPage()
    {
        // The model at the top, then the rows, as macOS's About page. The rows are there at once (the search reads
        // their labels); their values come in a moment later (WMI is slow).
        var top = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 16) };
        var picture = Glyphs.Stroke(Glyphs.Computer, 64, _text, 1.1);
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        top.Children.Add(picture);
        var model = new TextBlock { Text = Environment.MachineName, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = _text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 2), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var os = new TextBlock { Foreground = _dim, HorizontalAlignment = HorizontalAlignment.Center };
        top.Children.Add(model);
        top.Children.Add(os);
        Add(top);
        Controls["about.model"] = model;

        TextBlock device = Value("…", wrap: true), cpu = Value("…", wrap: true), memory = Value("…", wrap: true), gpu = Value("…", wrap: true),
                  serial = Value("…", wrap: true), windows = Value("…", wrap: true), installed = Value("…", wrap: true);
        var serialRow = Row(_s["about.serial"], serial);
        var installedRow = Row(_s["about.installed"], installed);
        Add(Group(Row(_s["about.device"], device), Row(_s["about.cpu"], cpu), Row(_s["about.memory"], memory), Row(_s["about.gpu"], gpu), serialRow,
                  Row("Windows", windows), installedRow));
        Add(Caption(_s["about.displays"]));
        var displays = new StackPanel();
        Add(displays);
        Add(Caption(_s["about.storage"]));
        var drive = Value("…", wrap: true);
        Add(Group(Row(System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:", drive),
                  NavRow("about.toStorage", Glyphs.Drive, Color.FromRgb(0x8E, 0x8E, 0x93), _s["about.storageSettings"], "storage")));

        string Gb(long bytes) => _s.F("about.gb", Math.Round(bytes / 1_000_000_000.0));
        OffUi(() => (SystemInfo.Read((total, free) => _s.F("about.storageFree", Gb(total), Gb(free)), (version, build) => _s.F("about.osVersion", version, build)), SystemInfo.ReadMore()), r =>
        {
            var (i, more) = r;
            model.Text = string.IsNullOrWhiteSpace(i.Model) ? i.Device : i.Model;
            os.Text = $"{i.Os}  {i.Version}";
            device.Text = i.Device;
            cpu.Text = i.Cpu;
            memory.Text = i.Memory;
            gpu.Text = string.Join("\n", i.Gpus);
            serial.Text = more.Serial;
            serialRow.Visibility = more.Serial.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            windows.Text = $"{i.Os}\n{i.Version}";
            installed.Text = more.Installed is DateTime d ? d.ToString(_s.Language == "ja" ? "yyyy年M月d日" : "MMMM d, yyyy", _s.Culture) : "";
            installedRow.Visibility = more.Installed != null ? Visibility.Visible : Visibility.Collapsed;
            drive.Text = i.Storage;
            displays.Children.Add(Group([.. more.Displays.Select(m => Row(m.Primary ? _s["about.mainDisplay"] : _s["about.display"],
                Value(_s.F("about.displayValue", m.Width, m.Height, m.ScalePercent) + (m.Hz > 1 ? $"  ·  {m.Hz} Hz" : ""))))]));
        });
        // The third-party licenses beside the system report, as macOS's About page keeps its legal links at the bottom.
        string file = Frostbar.Platform.Infrastructure.AppPaths.Notices;
        var notices = Footer(_s["about.notices"], () => CommandRunner.Launch(file));
        notices.IsEnabled = System.IO.File.Exists(file);
        notices.Margin = new Thickness(0, 0, 8, 0);
        var info = Footer(_s["about.systemReport"], () => CommandRunner.Launch("msinfo32.exe"));
        info.Margin = new Thickness(0);
        Controls["about.notices"] = notices;
        Controls["footer"] = info;
        var footers = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 12) };
        footers.Children.Add(notices);
        footers.Children.Add(info);
        Add(footers);
    }

    void StoragePage()
    {
        var area = new StackPanel();
        Add(area);
        // The recycle bin's row is there at once (the search finds 「ごみ箱」); its size comes with the drives'.
        var open = Button(_s["set.open"], FileManager.OpenRecycleBin);
        Controls["storage.trash"] = open;
        var trashDetail = new TextBlock { Text = "…", FontSize = 11.5, Foreground = _dim };
        var trashWords = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        trashWords.Children.Add(new TextBlock { Text = _s["storage.trash"], Foreground = _text });
        trashWords.Children.Add(trashDetail);
        var trash = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 6, 8, 6) };
        var bin = Circle(Glyphs.Stroke(Glyphs.Drive, 14, _text, 1.5), on: false);
        bin.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(bin, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(open, System.Windows.Controls.Dock.Right);
        open.VerticalAlignment = VerticalAlignment.Center;
        trash.Children.Add(bin);
        trash.Children.Add(open);
        trash.Children.Add(trashWords);
        Add(Group(Cell(trash)));

        string Size(long bytes) => bytes >= 1_000_000_000_000 ? _s.F("storage.tb", bytes / 1e12) : bytes >= 1_000_000_000 ? _s.F("storage.gb", bytes / 1e9) : _s.F("storage.mb", Math.Max(0, bytes / 1e6));
        OffUi(() => (SystemInfo.Volumes(), SystemInfo.RecycleBinSize()), r =>
        {
            var (volumes, size) = r;
            foreach (var v in volumes)
            {
                string name = (v.Label.Length > 0 ? v.Label : _s["storage.localDisk"]) + $" ({v.Root.TrimEnd('\\')})";
                long used = v.Total - v.Free;
                var head = new DockPanel { LastChildFill = true };
                var free = new TextBlock { Text = _s.F("storage.freeOf", Size(v.Free), Size(v.Total)), Foreground = _dim, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(free, System.Windows.Controls.Dock.Right);
                head.Children.Add(free);
                head.Children.Add(new TextBlock { Text = name, Foreground = _text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var track = new Grid { Height = 10, Margin = new Thickness(0, 8, 0, 4) };
                track.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(_t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0)) });
                var fill = new Border { CornerRadius = new CornerRadius(5), Background = _accent, HorizontalAlignment = HorizontalAlignment.Left };
                track.Children.Add(fill);
                double share = v.Total > 0 ? (double)used / v.Total : 0;
                track.SizeChanged += (_, _) => fill.Width = Math.Max(10, track.ActualWidth * share);
                var box = new StackPanel { Margin = new Thickness(8, 8, 8, 8) };
                box.Children.Add(head);
                box.Children.Add(track);
                box.Children.Add(new TextBlock { Text = _s.F("storage.used", Size(used)), FontSize = 11.5, Foreground = _dim });
                var cell = Cell(box);
                Controls["storage." + v.Root.TrimEnd('\\')] = cell;
                area.Children.Add(Group(cell));
            }
            if (volumes.Count == 0) area.Children.Add(Note(_s["storage.none"]));
            trashDetail.Text = size is var (bytes, items) ? items == 0 ? _s["storage.trashEmpty"] : _s.F("storage.trashSize", Size(bytes), items) : "";
        });
        AddFooter(_s["storage.windowsSettings"], "ms-settings:storagesense");
    }
}
