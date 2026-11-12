using System.Text.Json;

namespace Frostbar.Core.Settings;

/// <param name="Unreadable">The file is there but could not be read (held by another program): the defaults are in use, and nothing is saved over it.</param>
public sealed record SettingsLoad(AppSettings Settings, IReadOnlyList<string> Fixes, string? CorruptCopy, bool Unreadable = false);

/// <summary>Reads and writes settings.json. Saving goes through a temporary file and keeps the previous file as .bak.</summary>
public static class SettingsStore
{
    /// <summary>Files that could not be read this run: saving the defaults would replace the user's settings (2026-10-12).</summary>
    static readonly HashSet<string> Unreadable = new(StringComparer.OrdinalIgnoreCase);

    public static SettingsLoad Load(string path)
    {
        if (!File.Exists(path)) return Fresh();
        string? text = null;
        for (int attempt = 0; text == null; attempt++)
        {
            try { text = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    lock (Unreadable) Unreadable.Add(Path.GetFullPath(path));
                    return Fresh() with { Unreadable = true };
                }
                Thread.Sleep(150); // held for a moment (a backup, a virus scan)
            }
        }
        AppSettings? s;
        try { s = JsonSerializer.Deserialize(text, SettingsJson.Default.AppSettings); }
        catch (JsonException)
        {
            // Keep the broken file for the user; start from the last good settings (.bak, the file before the last save)
            // or else the defaults. Its first save had put the broken file in place of that .bak (File.Replace), so the
            // broken one goes once copied (2026-11-06 review: a power cut after a write left the user with defaults).
            var copy = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            try { File.Copy(path, copy, overwrite: true); } catch (IOException) { copy = null; }
            var backup = FromBackup(path);
            if (copy != null && backup != null) try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return (backup ?? Fresh()) with { CorruptCopy = copy };
        }
        s ??= new AppSettings();
        var fixes = SettingsValidator.Normalize(s);
        return new SettingsLoad(s, fixes, null);
    }

    /// <summary>The settings of the .bak beside <paramref name="path"/>, or null (none, or not readable either).</summary>
    static SettingsLoad? FromBackup(string path)
    {
        string bak = path + ".bak";
        try
        {
            if (!File.Exists(bak)) return null;
            var s = JsonSerializer.Deserialize(File.ReadAllText(bak), SettingsJson.Default.AppSettings);
            if (s == null) return null;
            var fixes = new List<string> { "settings.json was broken: read from settings.json.bak" };
            fixes.AddRange(SettingsValidator.Normalize(s));
            return new SettingsLoad(s, fixes, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static SettingsLoad Fresh()
    {
        var s = new AppSettings();
        SettingsValidator.Normalize(s);
        return new SettingsLoad(s, [], null);
    }

    /// <summary>Writes the settings; false (nothing written) for a file that could not be read this run.</summary>
    public static bool Save(string path, AppSettings s) => Write(path, Text(s));

    /// <summary>The settings as they are written (serialised on the thread that changes them; see <see cref="SettingsSaver"/>).</summary>
    public static string Text(AppSettings s) => JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings);

    /// <summary>Writes settings serialised by <see cref="Text"/>; false (nothing written) for a file that could not be read this run.</summary>
    public static bool Write(string path, string text)
    {
        lock (Unreadable) if (Unreadable.Contains(Path.GetFullPath(path))) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        // On the disk before it takes the file's place: a power cut a moment after could leave an empty file.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            w.Write(text);
            w.Flush();
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
        return true;
    }
}
