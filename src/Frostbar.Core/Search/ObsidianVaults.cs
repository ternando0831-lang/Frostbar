using System.Text.Json;

namespace Frostbar.Core.Search;

/// <summary>An Obsidian vault: its id (the key in obsidian.json) and its folder.</summary>
public sealed record ObsidianVault(string Id, string Path)
{
    /// <summary>The vault's name as Obsidian shows it: its folder's name.</summary>
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
}

/// <summary>
/// Obsidian's vaults (its obsidian.json, in %APPDATA%\obsidian) and the notes in them: the search shows a vault's
/// Markdown files as notes and opens them in Obsidian (obsidian://open), as Spotlight shows notes apart from files.
/// </summary>
public static class ObsidianVaults
{
    /// <summary>The vaults listed in obsidian.json ("vaults": id → { "path": … }); none when it cannot be read.</summary>
    public static IReadOnlyList<ObsidianVault> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("vaults", out var vaults)
                || vaults.ValueKind != JsonValueKind.Object) return [];
            var list = new List<ObsidianVault>();
            foreach (var v in vaults.EnumerateObject())
                if (v.Value.ValueKind == JsonValueKind.Object && v.Value.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                    && p.GetString() is { Length: > 0 } path)
                    list.Add(new ObsidianVault(v.Name, path));
            return list;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// The vault holding <paramref name="file"/> and the note's path in it ('/' between folders, without ".md"), when it
    /// is a Markdown file of a vault and not in Obsidian's own folders (.obsidian, .trash); null otherwise.
    /// </summary>
    public static (ObsidianVault Vault, string Note)? NoteOf(IReadOnlyList<ObsidianVault> vaults, string file)
    {
        if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
        string path = file.Replace('/', '\\');
        foreach (var v in vaults.OrderByDescending(v => v.Path.Length)) // the innermost of vaults inside others
        {
            string root = v.Path.Replace('/', '\\').TrimEnd('\\') + "\\";
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            string inside = path[root.Length..].Replace('\\', '/');
            if (inside.StartsWith(".obsidian/", StringComparison.OrdinalIgnoreCase) || inside.StartsWith(".trash/", StringComparison.OrdinalIgnoreCase)) return null;
            return (v, inside[..^3]);
        }
        return null;
    }

    /// <summary>The folder a note is in, inside its vault ('/' between folders), or "" at the vault's top.</summary>
    public static string FolderOf(string note)
    {
        int slash = note.LastIndexOf('/');
        return slash < 0 ? "" : note[..slash];
    }

    /// <summary>The URI that opens a note in Obsidian (the vault named by its id: two vaults may share a folder name).</summary>
    public static string OpenUri(ObsidianVault vault, string note) =>
        $"obsidian://open?vault={Uri.EscapeDataString(vault.Id)}&file={Uri.EscapeDataString(note)}";
}
