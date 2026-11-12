using System.Management;
using System.Runtime.InteropServices;
using Frostbar.Platform.Shell;
using Microsoft.Win32;

namespace Frostbar.Platform.Sys;

/// <summary>"About This PC". Reads WMI, so call it off the UI thread.</summary>
public static class SystemInfo
{
    public sealed record Info(string Model, string Os, string Version, string Device, string Cpu, string Memory, IReadOnlyList<string> Gpus, string Display, string Storage);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public int dwLength; public int dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    /// <param name="versionText">The version and the build in the UI's words ("25H2（ビルド 26300.1）"); "build" in English without it.</param>
    public static Info Read(Func<long, long, string> storageText, Func<string, string, string>? versionText = null)
    {
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = nt?.GetValue("ProductName") as string ?? "Windows";
        string build = nt?.GetValue("CurrentBuild") as string ?? "";
        // ProductName still says "Windows 10" on Windows 11 (builds 22000+).
        if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        string release = $"{nt?.GetValue("DisplayVersion")}", fullBuild = $"{build}.{nt?.GetValue("UBR")}";
        string version = versionText?.Invoke(release, fullBuild) ?? $"{release} (build {fullBuild})";

        using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        string cpu = (cpuKey?.GetValue("ProcessorNameString") as string ?? "").Trim();

        var mem = new MEMORYSTATUSEX { dwLength = Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        string memory = $"{Math.Round(mem.ullTotalPhys / 1073741824.0)} GB";

        string model = "", gpusText = "";
        var gpus = new List<string>();
        try
        {
            using (var q = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in q.Get()) model = $"{o["Manufacturer"]} {o["Model"]}".Trim();
            using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                foreach (ManagementObject o in q.Get()) if (o["Name"] is string n) gpus.Add(n);
        }
        catch (ManagementException) { }
        _ = gpusText;

        var mon = Monitors.Primary();
        string display = $"{mon.Bounds.Width} × {mon.Bounds.Height} ({mon.Dpi * 100 / 96}%)";

        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
        string storage = storageText(drive.TotalSize, drive.AvailableFreeSpace);

        return new Info(model, product, version, Environment.MachineName, cpu, memory, gpus, display, storage);
    }

    /// <summary>What the About page adds to the dialog's: the serial number, when Windows was installed, each display.</summary>
    public sealed record More(string Serial, DateTime? Installed, IReadOnlyList<DisplayInfo> Displays, string Edition);

    /// <summary>A display: its device name, pixels, scale (percent) and refresh rate (Hz).</summary>
    public sealed record DisplayInfo(string Name, int Width, int Height, int ScalePercent, int Hz, bool Primary);

    /// <summary>Reads WMI and the registry: call off the UI thread.</summary>
    public static More ReadMore()
    {
        string serial = "";
        try
        {
            using var q = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            foreach (ManagementObject o in q.Get()) serial = (o["SerialNumber"] as string ?? "").Trim();
        }
        catch (ManagementException) { }
        // Makers' placeholders.
        if (serial.Length == 0 || serial.Contains("To be filled", StringComparison.OrdinalIgnoreCase) || serial.Equals("Default string", StringComparison.OrdinalIgnoreCase)) serial = "";

        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        DateTime? installed = nt?.GetValue("InstallDate") is int seconds && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((uint)seconds).LocalDateTime : null;
        string edition = nt?.GetValue("EditionID") as string ?? "";

        var displays = new List<DisplayInfo>();
        foreach (var m in Monitors.All())
        {
            int hz = DisplayModes.Current(m.Device)?.Hz ?? 0;
            displays.Add(new DisplayInfo(m.Device, m.Bounds.Width, m.Bounds.Height, (int)(m.Dpi * 100 / 96), hz, m.Primary));
        }
        return new More(serial, installed, displays, edition);
    }

    /// <summary>A fixed drive: its letter, label, size and free space in bytes.</summary>
    public sealed record Volume(string Root, string Label, long Total, long Free, bool System);

    /// <summary>The fixed drives that are ready (the Storage page's bars).</summary>
    public static IReadOnlyList<Volume> Volumes()
    {
        string system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var list = new List<Volume>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new Volume(d.RootDirectory.FullName, d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace, string.Equals(d.RootDirectory.FullName, system, StringComparison.OrdinalIgnoreCase)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(v => v.System).ThenBy(v => v.Root).ToList();
    }

    [StructLayout(LayoutKind.Sequential)] struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);

    /// <summary>The recycle bin's size in bytes and how many items it holds (every drive's bin), or null.</summary>
    public static (long bytes, long items)? RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : null;
    }
}
