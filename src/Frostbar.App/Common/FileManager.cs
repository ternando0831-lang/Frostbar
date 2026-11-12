using System.Diagnostics;
using System.IO;
using Frostbar.App.Menus;
using Frostbar.Core.Localization;

namespace Frostbar.App.Common;

/// <summary>
/// The app that shows folders (macOS's Finder): File Explorer, or a program the user chose in the settings
/// (General.FileManager "custom" and FileManagerPath), which is started with the folder's path on its command line.
/// A file is shown by opening the folder it is in (only File Explorer is known to select it). The Recycle Bin, a
/// virtual folder other apps may not take, always opens in File Explorer.
/// </summary>
public static class FileManager
{
    public const string Explorer = "explorer", Custom = "custom";
    public const string RecycleBinPath = "shell:RecycleBinFolder";

    /// <summary>The settings' choice (General.FileManager), kept up to date by the shell.</summary>
    public static string Choice { get; set; } = Explorer;
    /// <summary>The settings' program for <see cref="Custom"/> (General.FileManagerPath), kept up to date by the shell.</summary>
    public static string Path { get; set; } = "";

    /// <summary>The app used now: the chosen program while it is there, else File Explorer.</summary>
    public static string Current => Choice == Custom && File.Exists(Path) ? Custom : Explorer;

    /// <summary>The name the menus show for the app used now ("Show in …").</summary>
    public static string NameIn(Strings s) => Current == Custom ? AppName(Path) : s["fm.explorer"];

    /// <summary>A program's name as Windows shows it: its file description, else its file name.</summary>
    public static string AppName(string exe)
    {
        try
        {
            string? described = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(described)) return described;
        }
        catch (Exception) { }
        return System.IO.Path.GetFileNameWithoutExtension(exe);
    }

    public static void OpenFolder(string folder)
    {
        if (Current == Custom) CommandRunner.Launch(Path, Quote(folder));
        else CommandRunner.Launch("explorer.exe", Quote(folder));
    }

    /// <summary>A file or folder, shown in the folder it is in (selected, in File Explorer).</summary>
    public static void Reveal(string path)
    {
        if (Current == Custom)
        {
            string? folder = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
            if (folder != null) CommandRunner.Launch(Path, Quote(folder));
        }
        else CommandRunner.Launch("explorer.exe", "/select," + Quote(path));
    }

    /// <summary>A new window (Finder's New Window, from the desktop's menus).</summary>
    public static void NewWindow()
    {
        if (Current == Custom) CommandRunner.Launch(Path);
        else CommandRunner.Launch("explorer.exe");
    }

    /// <summary>The Recycle Bin, in File Explorer.</summary>
    public static void OpenRecycleBin() => CommandRunner.Launch("explorer.exe", RecycleBinPath);

    static string Quote(string path) => "\"" + path + "\"";
}
