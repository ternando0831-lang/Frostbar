using System.Text.Json;
using Frostbar.Core.Shell;

namespace Frostbar.Platform.Infrastructure;

/// <summary>
/// state.json, shared with the watchdog process and the emergency script. Writes go to a temporary file that is then
/// renamed over the old one, so a reader never sees half a file; the temporary file reaches the disk before the
/// rename (a power cut a moment after a write could leave an empty file, read as no state: the taskbar's snapshot lost).
/// </summary>
public sealed class StateStore(string path)
{
    readonly object _gate = new();
    public string Path => path;

    public ShellState Load()
    {
        lock (_gate) return TryLoad(out var s) ? s : new ShellState();
    }

    /// <summary>
    /// The state, or false when the file is there and could not be read (held by another process all the while): a
    /// change must then not be written over it. Written over, an empty state took the taskbar's snapshot and its
    /// "hidden by us" away, and the taskbar stayed hidden at the quit (2026-11-06 review). A file that is not JSON (cut
    /// short) is kept beside it as state.json.bad, and the state starts fresh.
    /// </summary>
    bool TryLoad(out ShellState state)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(path)) { state = new ShellState(); return true; }
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                state = JsonSerializer.Deserialize(fs, ShellStateJson.Default.ShellState) ?? new ShellState();
                return true;
            }
            catch (IOException) { Thread.Sleep(20); }
            catch (JsonException ex)
            {
                Log.Warn("state.json unreadable, starting fresh (kept as state.json.bad)", ex);
                try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
                state = new ShellState();
                return true;
            }
        }
        state = new ShellState();
        return false;
    }

    public void Save(ShellState s)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, s, ShellStateJson.Default.ShellState);
                fs.Flush(flushToDisk: true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); return; }
                catch (IOException) when (attempt < 10) { Thread.Sleep(20); }
                catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(20); }
            }
        }
    }

    /// <summary>Changes the state and writes it; when it could not be read, nothing is written (the change is lost, not the state).</summary>
    public ShellState Update(Action<ShellState> change)
    {
        lock (_gate)
        {
            if (!TryLoad(out var s))
            {
                Log.Warn("state.json could not be read (held by another process): a change not written");
                change(s);
                return s;
            }
            change(s);
            Save(s);
            return s;
        }
    }
}
