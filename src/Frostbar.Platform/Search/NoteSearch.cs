using System.Diagnostics;
using Frostbar.Core.Search;
using Frostbar.Platform.Infrastructure;

namespace Frostbar.Platform.Search;

/// <summary>
/// Obsidian's notes for the search (stage 6-2), on a thread of their own: the vaults' Markdown files are read when the
/// search first opens (and again when the vaults change), kept up to date by watching the vaults' folders, and searched
/// there by their names and their bodies, the newest query only (as the files are). The text stays in this process's
/// memory: nothing is written anywhere. Windows Search keeps no contents for .md (S24), so this is the only way to find
/// a body; and the file sources leave out some folders (AppData) or may not index a vault, so the names are found here too.
/// </summary>
public sealed class NoteSearch : IDisposable
{
    /// <summary>A body hit: the note's path, how often (up to <see cref="NoteIndex.CountCap"/>), and its matching line (null: none whole).</summary>
    public sealed record Hit(string Path, int Count, string? Snippet);

    /// <summary>An answer: the query's id, the normalized query, the body hits, the notes named so, how long it took (ms).</summary>
    public sealed record Answer(int Id, string Query, List<Hit> Hits, List<NoteName> Names, long Ms);

    /// <summary>A change seen this long ago is read (Obsidian writes a note in a few steps while it is typed into).</summary>
    const int SettleMs = 400;

    readonly NoteIndex _index = new();
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly object _lock = new();
    readonly Timer _settle;
    readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _gone = new(StringComparer.OrdinalIgnoreCase);
    readonly List<FileSystemWatcher> _watchers = new();
    string[]? _wanted;
    string[] _loaded = [];
    bool _reload;
    (int id, string query, int max, Action<Answer> done)? _next;
    volatile bool _stop;

    /// <summary>Diagnostics: the notes and characters kept, the last reading of them all (ms; -1: not yet), the notes read again.</summary>
    public int Notes { get; private set; }
    public long Characters { get; private set; }
    public long LoadMs { get; private set; } = -1;
    public int Refreshed { get; private set; }

    public NoteSearch()
    {
        _settle = new Timer(_ => _wake.Set());
        _thread = new Thread(Loop) { IsBackground = true, Name = "notes" };
        _thread.Start();
    }

    /// <summary>
    /// The vaults' folders to keep: read when they differ from those kept (the same list does nothing). Written with
    /// '\' between folders, as the watcher and the file sources give paths (obsidian.json may say '/').
    /// </summary>
    public void Load(IEnumerable<string> folders)
    {
        lock (_lock) _wanted = [.. folders.Select(f => f.Replace('/', '\\').TrimEnd('\\'))];
        _wake.Set();
    }

    /// <param name="done">Called on the notes' thread.</param>
    public void Query(int id, string normalizedQuery, int max, Action<Answer> done)
    {
        lock (_lock) _next = (id, normalizedQuery, max, done);
        _wake.Set();
    }

    void Loop()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            if (_stop) break;
            string[]? wanted;
            string[] changed, gone;
            bool reload;
            (int id, string query, int max, Action<Answer> done)? job;
            lock (_lock)
            {
                wanted = _wanted;
                _wanted = null;
                changed = [.. _changed];
                _changed.Clear();
                gone = [.. _gone];
                _gone.Clear();
                reload = _reload;
                _reload = false;
                job = _next;
                _next = null;
            }
            try
            {
                // A folder of notes that went (deleted, or renamed: its notes have new paths): the vaults read again.
                if (!reload && gone.Any(_index.AnyUnder)) reload = true;
                if (wanted != null && !wanted.SequenceEqual(_loaded, StringComparer.OrdinalIgnoreCase) || reload) ReadAll(wanted ?? _loaded);
                else foreach (var path in changed) Refresh(path);
                if (job is { } j)
                {
                    var sw = Stopwatch.StartNew();
                    var hits = _index.Find(j.query, j.max).Select(h => new Hit(h.Path, h.Count, SnippetOf(h.Path, j.query))).ToList();
                    j.done(new Answer(j.id, j.query, hits, _index.FindNames(j.query, j.max), sw.ElapsedMilliseconds));
                }
            }
            catch (Exception ex) { Log.Warn("notes: failed", ex); }
        }
    }

    /// <summary>Every note of the vaults read again, and the folders watched.</summary>
    void ReadAll(string[] vaults)
    {
        var sw = Stopwatch.StartNew();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var old in _loaded) _index.RemoveUnder(old);
        foreach (var vault in vaults.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(vault, "*.md", SearchOption.AllDirectories))
                    if (IsNote(vault, file)) Read(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug($"notes: {vault} partly unread: {ex.Message}"); }
            Watch(vault);
        }
        _loaded = vaults;
        Notes = _index.Count;
        Characters = _index.Characters;
        LoadMs = sw.ElapsedMilliseconds;
        Log.Info($"notes: {Notes} notes ({Characters / 1024} K characters) of {vaults.Length} vault(s) read in {LoadMs} ms");
    }

    /// <summary>A vault's Markdown file that is a note: not in Obsidian's own folders.</summary>
    static bool IsNote(string vault, string file)
    {
        string inside = file.Length > vault.Length + 1 ? file[(vault.Length + 1)..].Replace('\\', '/') : "";
        return file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
               && !inside.StartsWith(".obsidian/", StringComparison.OrdinalIgnoreCase) && !inside.StartsWith(".trash/", StringComparison.OrdinalIgnoreCase);
    }

    void Read(string file)
    {
        try { _index.Put(file, File.ReadAllText(file), File.GetLastWriteTimeUtc(file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug($"notes: {Path.GetFileName(file)} unread: {ex.Message}"); }
    }

    /// <summary>A note that changed, came or went.</summary>
    void Refresh(string path)
    {
        string? vault = _loaded.FirstOrDefault(v => path.StartsWith(v + "\\", StringComparison.OrdinalIgnoreCase));
        if (vault == null || !IsNote(vault, path)) return;
        if (File.Exists(path)) Read(path); else _index.Remove(path);
        Refreshed++;
        Notes = _index.Count;
        Characters = _index.Characters;
    }

    void Watch(string vault)
    {
        try
        {
            var w = new FileSystemWatcher(vault)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            // A note is read again; a folder that went (deleted, or renamed: its notes have new paths) reads the vaults again.
            void Saw(string path, bool gone)
            {
                bool note = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                if (!note && !gone) return; // a folder that came or changed: its notes come one by one
                // Something else that went: a folder of notes only if notes are kept under it (the notes' thread looks).
                // Any file that went read every note again: an attachment deleted, a plugin's save through a temporary
                // file, git's index.lock (2026-11-06 review).
                lock (_lock) { if (note) _changed.Add(path); else _gone.Add(path); }
                _settle.Change(SettleMs, Timeout.Infinite);
            }
            w.Changed += (_, e) => Saw(e.FullPath, gone: false);
            w.Created += (_, e) => Saw(e.FullPath, gone: false);
            w.Deleted += (_, e) => Saw(e.FullPath, gone: true);
            w.Renamed += (_, e) => { Saw(e.OldFullPath, gone: true); Saw(e.FullPath, gone: false); };
            w.Error += (_, e) =>
            {
                Log.Debug("notes: the watch overflowed; reading the vaults again: " + e.GetException().Message);
                lock (_lock) _reload = true;
                _settle.Change(SettleMs, Timeout.Infinite);
            };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { Log.Debug($"notes: {vault} not watched: {ex.Message}"); }
    }

    static string? SnippetOf(string path, string query)
    {
        try { return NoteIndex.Snippet(File.ReadAllText(path), query); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _settle.Dispose();
        foreach (var w in _watchers) w.Dispose();
    }
}
