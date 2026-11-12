using System.Text.Json;
using System.Text.Json.Serialization;

namespace Frostbar.Core.Dock;

/// <summary>
/// The AppIDs of the Dock's kept shortcuts and programs (the taskbar's own, from Explorer's resolver: ADR-005), kept
/// between starts with each file's last write time: asking the resolver took about 6.5 ms a pin, 113–122 ms for the 18
/// of a Dock at start-up (2026-10-16, a Release build). A file written since is asked again; the shell also asks again
/// for every pin a while after start-up and puts right what differs (the resolver may answer otherwise without the
/// file changing, after an update of Windows). Not thread-safe: the UI thread owns it.
/// </summary>
public sealed class ShortcutIds
{
    /// <summary>A file's AppID as the resolver gave it, and the file's last write time then (UTC ticks).</summary>
    public sealed record Entry(long Written, string AppId);

    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Something was set or dropped since it was read or last saved.</summary>
    public bool Changed { get; private set; }

    public int Count => _entries.Count;

    /// <summary>The AppID kept for <paramref name="path"/> written at <paramref name="written"/>, or null (none, or written since).</summary>
    public string? Get(string path, long written) => _entries.TryGetValue(path, out var e) && e.Written == written ? e.AppId : null;

    /// <summary>Keeps <paramref name="path"/>'s AppID (a change only when it differs).</summary>
    public void Set(string path, long written, string appId)
    {
        if (Get(path, written) == appId) return;
        _entries[path] = new Entry(written, appId);
        Changed = true;
    }

    /// <summary>Forgets the files no longer kept in the Dock (nor among its recent apps).</summary>
    public void KeepOnly(IEnumerable<string> paths)
    {
        var keep = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in _entries.Keys.Where(p => !keep.Contains(p)).ToList())
        {
            _entries.Remove(path);
            Changed = true;
        }
    }

    /// <summary>The kept entries as JSON; the change is then saved.</summary>
    public string ToJson()
    {
        Changed = false;
        return JsonSerializer.Serialize(new Dictionary<string, Entry>(_entries, StringComparer.OrdinalIgnoreCase), ShortcutIdsJson.Default.DictionaryStringEntry);
    }

    /// <summary>Entries read back (none when the text cannot be read: they are asked again).</summary>
    public static ShortcutIds Parse(string? json)
    {
        var ids = new ShortcutIds();
        if (string.IsNullOrWhiteSpace(json)) return ids;
        try
        {
            if (JsonSerializer.Deserialize(json, ShortcutIdsJson.Default.DictionaryStringEntry) is { } read)
                foreach (var (path, e) in read)
                    if (e is { AppId.Length: > 0 }) ids._entries[path] = e;
        }
        catch (JsonException) { }
        return ids;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, ShortcutIds.Entry>))]
public sealed partial class ShortcutIdsJson : JsonSerializerContext;
