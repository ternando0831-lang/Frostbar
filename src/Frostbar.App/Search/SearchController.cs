using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Localization;
using Frostbar.Core.Search;
using Frostbar.Core.Settings;
using Frostbar.Core.Shell;
using Frostbar.Platform.Icons;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Search;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;
using Frostbar.Platform.Wm;

namespace Frostbar.App.Search;

/// <summary>
/// The search (M4): opened by its shortcut (Alt+Space) or the menu bar's magnifier, it answers as the query is typed —
/// a calculation, the top hit, apps (ranked by how often they were opened), windows, actions, Frostbar's and Windows'
/// settings pages, the recent documents, folders and files (Everything, else Windows Search, on their own thread), and
/// searching the web. Ctrl+1…3 filter the answers to the apps, the files or the actions, and list them with nothing
/// typed (stage 6-1, macOS 26's Spotlight). Esc gives the foreground back to the app that had it; choosing a result
/// opens it.
/// </summary>
public sealed class SearchController : IDisposable
{
    const string SettingsApp = @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    sealed record App(string Name, string ParsingName, Candidate Candidate);

    /// <summary>The filters offered: the clipboard's once its history is kept (stage 6, E3c).</summary>
    /// <summary>The filters offered: the clipboard's with a history to read (stage 6, E3c).</summary>
    SearchFilter[] Offered => ClipboardHistory != null
        ? [SearchFilter.Apps, SearchFilter.Files, SearchFilter.Actions, SearchFilter.Clipboard]
        : [SearchFilter.Apps, SearchFilter.Files, SearchFilter.Actions];

    /// <summary>Windows' clipboard history (or a spec's own): read when the Clipboard filter is turned on, once an opening.</summary>
    public IClipboardHistory? ClipboardHistory { get; set; }
    ClipboardState _clipboardState = ClipboardState.Unavailable;
    IReadOnlyList<ClipboardItem> _clipboardItems = [];
    bool _clipboardRead, _clipboardReading;
    List<SearchResult> _clipboardResults = new();
    /// <summary>Diagnostics: the history's reads.</summary>
    public int ClipboardReads { get; private set; }

    readonly AppSettings _settings;
    readonly ThemeService _theme;
    readonly Func<Strings> _strings;
    readonly Dispatcher _ui;
    readonly FileSearch _files = new();
    /// <summary>The notes' bodies (stage 6-2), made when the search first opens.</summary>
    NoteSearch? _notes;
    readonly UsageStore _usage = new(Path.Combine(AppPaths.Local, "usage.json"));
    IconService _icons;
    readonly DispatcherTimer _iconsLoaded;
    List<App> _apps = new();
    SearchWindow? _window;
    IntPtr _previous;
    bool _closing;
    int _query;
    string _text = "";
    SearchResult? _calculation, _web;
    List<SearchResult> _appResults = new(), _windowResults = new(), _actionResults = new(), _settingResults = new(), _fileResults = new(), _recentResults = new(), _noteNames = new(), _bodyResults = new();
    /// <summary>Nothing typed under a filter: its kinds are listed (all the apps, the recent documents, the actions).</summary>
    bool _listing;
    readonly Stopwatch _since = new();
    ActionIndex? _actions;
    Func<string, bool> _available = _ => true;
    IReadOnlyList<ShellPageWords> _shellPages = [];
    /// <summary>The program of each window answered last (a window's icon, and a spec's guard), by its handle's text.</summary>
    readonly Dictionary<string, (string? Exe, string AppId)> _windowApps = new();

    /// <summary>Open, and not on its way out (closed for everything else as soon as it starts fading).</summary>
    public bool IsOpen => _window?.IsVisible == true && !_closing;
    int _fade; // a closing's fade that ends after the panel was opened again must not hide it
    public SearchWindow? Window => _window;
    /// <summary>Diagnostics: input (the shortcut's press) to the first frame of the open panel, in ms (-1: not measured).</summary>
    public int LastOpenMs { get; private set; } = -1;
    /// <summary>Diagnostics: query to answers on screen, for the apps (synchronous) and the files (from their thread).</summary>
    public long LastAppsMs { get; private set; } = -1;
    public long LastFilesMs { get; private set; } = -1;
    public string LastFileSource { get; private set; } = "";
    public string LastFilesFor { get; private set; } = "";
    /// <summary>Diagnostics: the query (its id) the files and the notes' bodies last answered, and the body hits' query (normalized).</summary>
    public int LastFilesId { get; private set; } = -1;
    public int LastNotesId { get; private set; } = -1;
    public string LastNotesFor { get; private set; } = "";
    public string Text => _text;
    /// <summary>Diagnostics: what the last choice did (or why it did nothing).</summary>
    public string LastChoice { get; private set; } = "";
    /// <summary>Diagnostics: how long reading Frostbar's settings pages for the search took (ms; -1: not read yet).</summary>
    public long ShellPagesMs { get; set; } = -1;

    /// <summary>Opens a page of Frostbar's settings; set by the shell.</summary>
    public Action<string>? OpenSettings { get; set; }
    /// <summary>The documents Windows lists as used lately (the system menu's Recent Items' source, stage 5-2).</summary>
    public RecentDocuments? Recent { get; set; }
    /// <summary>The app windows (the Dock's window model), top to bottom.</summary>
    public Func<IReadOnlyList<TrackedWindow>>? Windows { get; set; }
    /// <summary>Brings a window to the front, going to its desktop (the Dock's way).</summary>
    public Action<IntPtr>? ActivateWindow { get; set; }
    /// <summary>Does an action (<see cref="SearchActions"/>): the shell's, which guards its own under a spec.</summary>
    public Action<string>? RunAction { get; set; }
    /// <summary>Which actions can be done now (a switch's other way, an empty bin's emptying cannot): asked at each opening.</summary>
    public Func<Func<string, bool>>? ActionsAvailable { get; set; }
    /// <summary>
    /// Under an E2E spec only: the one vault the search knows (a folder of the spec's own), in place of Obsidian's
    /// (obsidian.json): a spec's shell neither reads nor keeps the user's notes.
    /// </summary>
    public string? SpecVault { get; set; }
    /// <summary>
    /// Under an E2E spec only (<see cref="SpecGuard"/>): the folder of the programs the search may open. Documents,
    /// folders, notes, the web and Windows' settings open in the user's apps and are not opened at all. Null: every app.
    /// </summary>
    public string? OnlyAppsIn { get; set; }

    public SearchController(AppSettings settings, ThemeService theme, Func<Strings> strings, Dispatcher ui, double scale)
    {
        _settings = settings;
        _theme = theme;
        _strings = strings;
        _ui = ui;
        // Icons arrive one by one: show them together, 60 ms after the last.
        _iconsLoaded = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => { _iconsLoaded!.Stop(); Render(); }, ui);
        _icons = Icons(scale);
    }

    /// <summary>The rows' icons, read at the display's scale (22 DIP).</summary>
    IconService Icons(double scale)
    {
        var icons = new IconService(_ui, (int)Math.Ceiling(22 * scale));
        icons.Loaded += _ => { if (IsOpen) { _iconsLoaded.Stop(); _iconsLoaded.Start(); } };
        return icons;
    }

    /// <summary>The Start menu's apps (names and AppsFolder parsing names), once they are known.</summary>
    public void SetApps(IEnumerable<(string Name, string ParsingName)> apps) =>
        _apps = apps.Where(a => !string.IsNullOrWhiteSpace(a.Name)).Select(a => new App(a.Name, a.ParsingName, new Candidate(a.Name))).ToList();

    /// <summary>Frostbar's settings pages as the search finds them (read once, after start-up: <see cref="Settings.SettingsWindow.SearchIndex"/>).</summary>
    public void SetShellPages(IReadOnlyList<ShellPageWords> pages) => _shellPages = pages;

    /// <summary>The actions prepared for matching in the display language and the other (made at the first query).</summary>
    ActionIndex Actions
    {
        get
        {
            if (_actions != null) return _actions;
            var s = _strings();
            return _actions = new ActionIndex(s, Strings.For(s.Language == "ja" ? "en" : "ja", CultureInfo.InvariantCulture));
        }
    }

    string FilterName(SearchFilter f) => _strings()[f switch
    {
        SearchFilter.Apps => "search.filterApps",
        SearchFilter.Files => "search.filterFiles",
        SearchFilter.Actions => "search.filterActions",
        _ => "search.filterClipboard",
    }];

    SearchWindow Create()
    {
        var t = _theme.Current;
        var w = new SearchWindow(t, Material(t), _strings()["search.placeholder"], FilterName, Offered, _strings()["search.dictation"]);
        w.QueryChanged += OnQuery;
        w.FilterChanged += OnQuery; // the same text answered again under the filter
        w.Chosen += Choose;
        w.Dismissed += () => Close(restore: true);
        w.Deactivated += (_, _) => { if (!_closing) Close(restore: false); }; // clicked elsewhere: that app keeps it
        return w;
    }

    Platform.Dwm.Material Material(ThemeSnapshot t) =>
        Materials.Menu(t, _settings.Appearance, (float)(GlassShape.Search * Monitors.Primary().Scale));

    public void ApplyTheme(ThemeSnapshot t) => _window?.SetMaterial(t, Material(t));

    /// <summary>
    /// The display's scale changed: the panel is made again at the next opening (a tool window gets no WM_DPICHANGED on
    /// Windows 11; the bars, the Dock, the app grid and the overview were made again, the search panel was not).
    /// </summary>
    public void Rescaled(double scale)
    {
        if (IsOpen) Close(restore: false);
        _window?.Close();
        _window = null;
        // The icons too: read at the start's scale, they were blurred at a larger one (and larger than needed at a smaller).
        _icons.Dispose();
        _icons = Icons(scale);
        _fileIcons.Clear();
    }

    /// <summary>Makes the panel's window once after start-up, so the first real opening does not pay for it.</summary>
    public void Warm()
    {
        _window ??= Create();
        new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
    }

    public void Toggle(int pressTime = 0)
    {
        if (IsOpen) Close(restore: true); else Open(pressTime);
    }

    /// <param name="pressTime">When the shortcut was pressed (TickCount ms), to time the opening; 0: not timed.</param>
    public void Open(int pressTime = 0)
    {
        var sw = Stopwatch.StartNew();
        int queued = pressTime != 0 ? Math.Max(0, Environment.TickCount - pressTime) : 0;
        if (_closing) { _fade++; _closing = false; } // asked again while fading out: it comes back
        _clipboardRead = false; // copied since: read again when the Clipboard filter is turned on
        ReadVaults();
        // The notes' bodies: read on their own thread at the first opening, and when the vaults change.
        (_notes ??= new NoteSearch()).Load(_vaults.Select(v => v.Path));
        _available = ActionsAvailable?.Invoke() ?? (_ => true);
        var w = _window ??= Create();
        w.ResetFade();
        var fg = Native.GetForegroundWindow();
        if (fg != w.Handle) _previous = fg;
        var mon = Monitors.Of(_previous) ?? Monitors.Primary();
        double s = mon.Scale;
        var wa = mon.Work;
        w.Left = Math.Round((wa.Left / s + (wa.Width / s - SearchWindow.PanelWidth) / 2));
        w.Top = Math.Round(wa.Top / s + wa.Height / s * 0.1); // Apple's capsule stands just under the bar (0.2 of the way down before)
        w.SetFilter(SearchFilter.None); // each opening starts with every kind (the field starts empty too)
        w.Field.Clear();
        Show([]);
        w.FadeIn(_theme.Current.Animations ? Fades.PanelInMs : 0); // before it shows (Fades.FadeIn)
        w.Show();
        w.Activate();
        if (Native.GetForegroundWindow() != w.Handle) WindowActions.ForceForeground(w.Handle);
        w.Field.Focus();
        Keyboard.Focus(w.Field);
        LastOpenMs = -1;
        if (pressTime != 0)
        {
            EventHandler? frame = null;
            frame = (_, _) =>
            {
                CompositionTarget.Rendering -= frame;
                LastOpenMs = queued + (int)sw.ElapsedMilliseconds;
            };
            CompositionTarget.Rendering += frame;
        }
        Log.Debug($"search opened in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Fades the panel out and hides it; with <paramref name="restore"/> the app that was in front gets the foreground
    /// back at once (typing goes there while the panel fades).
    /// </summary>
    public void Close(bool restore)
    {
        if (_window == null || !_window.IsVisible || _closing) return;
        _closing = true;
        _query++; // answers still on their way are for a closed panel
        if (restore && _previous != IntPtr.Zero && Native.IsWindow(_previous)) WindowActions.ForceForeground(_previous);
        int fade = ++_fade;
        var w = _window;
        w.FadeOut(_theme.Current.Animations ? Fades.PanelOutMs : 0, () =>
        {
            if (fade != _fade) return; // opened again meanwhile
            w.Hide();
            w.ResetFade();
            _closing = false;
        });
    }

    /// <summary>Diagnostics: a filter on or off, as Ctrl+1…4 does.</summary>
    public void SetFilter(SearchFilter f) => _window?.SetFilter(f);

    // ---- answers -----------------------------------------------------------------------------------------------

    SearchFilter Filter => _window?.Filter ?? SearchFilter.None;

    void OnQuery()
    {
        if (_window == null) return;
        _since.Restart();
        _text = _window.Field.Text;
        int id = ++_query;
        var s = _strings();
        string q = TextNormalizer.Normalize(_text);
        var filter = Filter;
        bool all = filter == SearchFilter.None, apps = filter is SearchFilter.None or SearchFilter.Apps, files = filter is SearchFilter.None or SearchFilter.Files;
        _fileResults = new List<SearchResult>();
        _noteNames = new List<SearchResult>();
        _bodyResults = new List<SearchResult>();
        _listing = q.Length == 0 && !all;
        // The clipboard's history (stage 6, E3c): read at its first filtering of an opening, then searched as typed.
        if (filter == SearchFilter.Clipboard && !_clipboardRead) { _clipboardResults = new(); _ = ReadClipboard(); }
        else _clipboardResults = filter == SearchFilter.Clipboard ? ClipboardSearch.Rows(_clipboardState, _clipboardItems, q, DateTimeOffset.Now, s) : new();
        if (q.Length == 0)
        {
            _calculation = _web = null;
            _settingResults = new();
            _windowResults = new();
            _appResults = _listing && filter == SearchFilter.Apps ? AppListing() : new();
            _actionResults = _listing && filter == SearchFilter.Actions ? [.. Actions.Everything(_available)] : new();
            _recentResults = _listing && filter == SearchFilter.Files ? RecentListing() : new();
            Render();
            return;
        }
        _calculation = !all ? null
            : Calculator.TryEvaluate(_text, out double v)
                ? new SearchResult(SearchKind.Calculation, Calculator.Format(v), _text.Trim(), Calculator.Format(v), Matcher.Exact)
                : UnitConverter.TryConvert(_text, out var c, s.Language)
                    ? new SearchResult(SearchKind.Conversion, c.Result, c.Expression, c.Number, Matcher.Exact)
                    : null;
        _appResults = !apps ? new() : _apps
            .Select(a => (a, score: Matcher.Score(q, a.Candidate)))
            .Where(x => x.score > 0)
            .Select(x => new SearchResult(SearchKind.App, x.a.Name, null, x.a.ParsingName, SearchRanker.WithUse(x.score, _usage.Count(x.a.ParsingName))))
            .ToList();
        // Windows and actions from two characters, unless filtered to them: one letter is in nearly every name.
        _windowResults = apps && q.Length >= 2 ? WindowsFor(q) : new();
        _actionResults = filter == SearchFilter.Actions || all && q.Length >= 2 ? [.. Actions.Find(q, _available)] : new();
        _settingResults = all ? ShellSettingsSearch.Answers(q, s.Language == "ja", _shellPages) : new();
        _recentResults = files ? RecentFor(q) : new();
        _web = all ? new SearchResult(SearchKind.Web, s.F("search.web", _text.Trim()), null, _text.Trim(), 0) : null;
        Render();
        LastAppsMs = _since.ElapsedMilliseconds;
        // A source whose every kind the settings leave out is not asked (Spotlight's "Search results"); a filter asks.
        var hidden = all ? Hidden : [];
        if (files && _text.Trim().Length >= 2 && !SearchCategories.AllHidden(hidden, SearchKind.Folder, SearchKind.File, SearchKind.Note))
            _files.Query(id, _text, _settings.Search.FileSource, 60, a => _ui.BeginInvoke(DispatcherPriority.Normal, () => OnFiles(a, q)));
        // The notes whose name or body has it (stage 6-2), from two characters as the files.
        if (files && q.Length >= 2 && _notes != null && !hidden.Contains(SearchKind.Note))
            _notes.Query(id, q, filter == SearchFilter.Files ? SearchRanker.MaxFiltered : SearchRanker.MaxNotes * 2,
                a => _ui.BeginInvoke(DispatcherPriority.Normal, () => OnNoteHits(a)));
    }

    /// <summary>Diagnostics: query to the notes' body hits on screen (ms; -1: none yet).</summary>
    public long LastNotesMs { get; private set; } = -1;

    async Task ReadClipboard()
    {
        if (_clipboardReading || ClipboardHistory is not { } history) return;
        _clipboardReading = true;
        try
        {
            var (state, items) = await history.ReadAsync();
            _clipboardState = state;
            _clipboardItems = items;
            _clipboardRead = true;
            ClipboardReads++;
            Log.Debug($"search: the clipboard history {state}, {items.Count} item(s)");
        }
        finally { _clipboardReading = false; }
        if (_window != null && IsOpen && Filter == SearchFilter.Clipboard) OnQuery();
    }

    void OnNoteHits(NoteSearch.Answer a)
    {
        if (a.Id != _query || _window == null) return; // a newer query, or closed
        LastNotesMs = _since.ElapsedMilliseconds;
        LastNotesId = a.Id;
        LastNotesFor = a.Query;
        _noteNames = a.Names.Select(NameResult).OfType<SearchResult>().ToList();
        _bodyResults = a.Hits.Select(BodyResult).OfType<SearchResult>().ToList();
        Render();
    }

    /// <summary>
    /// A note found by its name in the vaults' list: as a file source's note shows (never below a plain "somewhere
    /// inside", so above any body hit). The same note from a file source is the same answer (merged by its path).
    /// </summary>
    SearchResult? NameResult(NoteName n) =>
        ObsidianVaults.NoteOf(_vaults, n.Path) is { } note
            ? new SearchResult(SearchKind.Note, Path.GetFileNameWithoutExtension(n.Path), WhereOf(note), n.Path, Math.Max(n.Score, Matcher.Inside - 100))
            : null;

    /// <summary>
    /// A note found by its body: below a note found by its name, the more often the higher; its line with the words
    /// on the right (where its vault and folder are when no line has them whole).
    /// </summary>
    SearchResult? BodyResult(NoteSearch.Hit h) =>
        ObsidianVaults.NoteOf(_vaults, h.Path) is { } note
            ? new SearchResult(SearchKind.Note, Path.GetFileNameWithoutExtension(h.Path), h.Snippet ?? WhereOf(note), h.Path, SearchRanker.BodyScore(h.Count))
            : null;

    /// <summary>Where a note is: its vault, and the folders in it ("Obsidian › Coding › Logs").</summary>
    static string WhereOf((ObsidianVault Vault, string Note) note)
    {
        string folder = ObsidianVaults.FolderOf(note.Note);
        return folder.Length > 0 ? note.Vault.Name + " › " + folder.Replace("/", " › ") : note.Vault.Name;
    }

    void OnFiles(FileSearch.Answer a, string q)
    {
        if (a.Id != _query || _window == null) return; // a newer query, or closed
        LastFilesMs = _since.ElapsedMilliseconds;
        LastFileSource = a.Source;
        LastFilesFor = a.Text;
        LastFilesId = a.Id;
        _fileResults = (a.Items ?? new List<FoundItem>()).Select(i => FileResult(i.Path, i.IsFolder, q)).ToList();
        Render();
    }

    /// <summary>
    /// A found path as an answer: an Obsidian vault's Markdown file is a note (its name without ".md", its folder in
    /// the vault); the source matched it its own way, so never below a plain "somewhere inside".
    /// </summary>
    SearchResult FileResult(string path, bool isFolder, string q)
    {
        string name = Path.GetFileName(path.TrimEnd('\\'));
        if (name.Length == 0) name = path;
        if (!isFolder && ObsidianVaults.NoteOf(_vaults, path) is { } note)
        {
            string title = Path.GetFileNameWithoutExtension(name);
            int noteScore = Math.Max(Matcher.Score(q, new Candidate(title)), Matcher.Inside - 100);
            return new SearchResult(SearchKind.Note, title, WhereOf(note), path, noteScore);
        }
        int score = Math.Max(Matcher.Score(q, new Candidate(name)), Matcher.Inside - 100);
        return new SearchResult(isFolder ? SearchKind.Folder : SearchKind.File, name, Path.GetDirectoryName(path), path, score);
    }

    /// <summary>The recent documents whose name answers, above a file the source finds as well (they are known at once).</summary>
    List<SearchResult> RecentFor(string q) =>
        [.. (Recent?.Items ?? []).Where(d => d.Target != null)
             .Select(d => (d, score: Matcher.Score(q, new Candidate(Path.GetFileName(d.Target!)))))
             .Where(x => x.score > 0)
             .Select(x => FileResult(x.d.Target!, isFolder: false, q) with { Score = x.score + SearchRanker.RecentBonus })];

    /// <summary>The Files filter with nothing typed: the recent documents, the most recent first.</summary>
    List<SearchResult> RecentListing()
    {
        var list = new List<SearchResult>();
        foreach (var d in (Recent?.Items ?? []).Where(d => d.Target != null).OrderByDescending(d => d.Used).Take(SearchRanker.MaxFiltered))
            list.Add(FileResult(d.Target!, isFolder: false, "") with { Score = 100_000 - list.Count });
        return list;
    }

    /// <summary>The Apps filter with nothing typed: the apps, the most used first, then by name.</summary>
    List<SearchResult> AppListing() =>
        [.. _apps.OrderByDescending(a => _usage.Count(a.ParsingName)).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                 .Select((a, i) => new SearchResult(SearchKind.App, a.Name, null, a.ParsingName, 100_000 - i))];

    /// <summary>The app windows whose title answers (any desktop's: choosing one goes there).</summary>
    List<SearchResult> WindowsFor(string q)
    {
        var list = new List<SearchResult>();
        _windowApps.Clear();
        foreach (var w in Windows?.Invoke() ?? [])
        {
            string title = Native.Title(w.Hwnd);
            if (title.Length == 0) continue;
            int score = Matcher.Score(q, new Candidate(title));
            if (score <= 0) continue;
            string key = w.Hwnd.ToInt64().ToString(CultureInfo.InvariantCulture);
            _windowApps[key] = (w.ExePath, w.AppId);
            list.Add(new SearchResult(SearchKind.Window, title, w.AppName, key, score));
        }
        return list;
    }

    // ---- Obsidian's notes ------------------------------------------------------------------------------------------

    static readonly string ObsidianJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");
    IReadOnlyList<ObsidianVault> _vaults = [];
    DateTime _vaultsWritten;

    /// <summary>Obsidian's vaults, read again when its obsidian.json changed (a small file: at each opening).</summary>
    void ReadVaults()
    {
        if (SpecVault != null)
        {
            if (_vaults.Count != 1 || _vaults[0].Path != SpecVault) _vaults = [new ObsidianVault("spec", SpecVault)];
            return;
        }
        try
        {
            var written = File.Exists(ObsidianJson) ? File.GetLastWriteTimeUtc(ObsidianJson) : default;
            if (written == _vaultsWritten) return;
            _vaultsWritten = written;
            _vaults = written == default ? [] : ObsidianVaults.Parse(File.ReadAllText(ObsidianJson));
            Log.Debug($"search: {_vaults.Count} Obsidian vault(s)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug("search: Obsidian's vaults unread: " + ex.Message); }
    }

    /// <summary>Obsidian's app in the Start menu (its icon goes on the notes), or null.</summary>
    string? ObsidianApp => _apps.FirstOrDefault(a => a.Name.Equals("Obsidian", StringComparison.OrdinalIgnoreCase))?.ParsingName;

    IReadOnlyList<SearchResult> _shown = [];

    /// <summary>The query the list was last shown for: answers that come later for it (files, icons) keep the choice.</summary>
    int _renderedQuery = -1;

    void Render()
    {
        if (_window == null) return;
        bool again = _renderedQuery == _query;
        _renderedQuery = _query;
        var answers = new SearchAnswers(_calculation, _appResults, _windowResults, _actionResults, _settingResults,
            SearchRanker.MergeFiles(_fileResults, [.. _recentResults, .. _noteNames, .. _bodyResults]), _web, _listing, _clipboardResults);
        Show(SearchRanker.Arrange(answers, Filter, Hidden), keepChoice: again);
    }

    /// <summary>The kinds the settings leave out of the answer (Spotlight's "Search results", 2026-10-22), read as the search is used.</summary>
    HashSet<SearchKind> Hidden => SearchCategories.Hidden(_settings.Search.Hidden);

    void Show(IReadOnlyList<SearchResult> results, bool keepChoice = false)
    {
        _shown = results;
        var top = Filter == SearchFilter.None ? results.FirstOrDefault(r => r.Kind is SearchKind.App or SearchKind.Setting or SearchKind.Action) : null;
        var s = _strings();
        _window!.Show(results,
            r => ReferenceEquals(r, top) ? s["search.topHit"]
                : _listing && r.Kind is SearchKind.Note or SearchKind.Folder or SearchKind.File ? s["sys.recent"]
                : r.Kind switch
                {
                    SearchKind.Calculation => s["search.calculator"],
                    SearchKind.Conversion => s["search.conversion"],
                    SearchKind.App => s["search.apps"],
                    SearchKind.Window => s["search.windows"],
                    SearchKind.Action => s["search.actions"],
                    SearchKind.Setting => s["search.settings"],
                    SearchKind.Note => s["search.notes"],
                    SearchKind.Folder => s["search.folders"],
                    SearchKind.File => s["search.files"],
                    SearchKind.Clipboard => s["search.filterClipboard"],
                    _ => s["search.webSection"],
                },
            r => r.Kind switch
            {
                SearchKind.Calculation or SearchKind.Conversion => r.Detail, // what was worked out ("1+2×3", "10 km")
                SearchKind.App => s["search.kindApp"],
                // Frostbar's page (a row found on it: its page too) or Windows'.
                SearchKind.Setting => ShellSettingsSearch.PageOf(r.Target) == null ? s["search.kindSetting"]
                    : r.Detail is { } page ? s["search.kindShellSetting"] + " › " + page : s["search.kindShellSetting"],
                SearchKind.Window => r.Detail, // its app
                SearchKind.Action => s["search.kindAction"],
                SearchKind.Note => r.Detail, // the vault and the folders in it
                SearchKind.Folder or SearchKind.File => ShortPath(r.Detail),
                SearchKind.Clipboard => r.Detail, // what it holds and when it was copied
                _ => null,
            },
            Icon, Glyph, keepChoice);
    }

    /// <summary>A folder as people say it: the user's folder as "~".</summary>
    static string? ShortPath(string? dir)
    {
        if (dir == null) return null;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return dir.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + dir[home.Length..] : dir;
    }

    ImageSource? Icon(SearchResult r)
    {
        string? source = r.Kind switch
        {
            SearchKind.App => @"shell:AppsFolder\" + r.Target,
            // Frostbar's own icon on the pages it opens itself.
            SearchKind.Setting => ShellSettingsSearch.PageOf(r.Target) != null ? Environment.ProcessPath ?? SettingsApp : SettingsApp,
            SearchKind.Folder or SearchKind.File => r.Target,
            SearchKind.Note => ObsidianApp is { } obsidian ? @"shell:AppsFolder\" + obsidian : r.Target, // Obsidian's, as Spotlight shows Notes'
            // The window's app (its program, else its AppsFolder entry).
            SearchKind.Window => _windowApps.TryGetValue(r.Target, out var app) ? app.Exe ?? @"shell:AppsFolder\" + app.AppId : null,
            _ => null,
        };
        if (source == null) return null;
        string key = "search:" + source;
        if (r.Kind is SearchKind.Folder or SearchKind.File) KeepFileIcon(key);
        return _icons.Get(key, source);
    }

    // The icons of the files and folders shown last (the most recent last); older ones are forgotten, read again from the
    // disk cache if shown again, as the Dock's stacks do. Every path ever shown in an answer stayed (up to 50 more with
    // each look through Files; 2026-11-06 review). Apps and settings are few and stay.
    const int FileIconsKept = 300;
    readonly LinkedList<string> _fileIcons = new();

    void KeepFileIcon(string key)
    {
        _fileIcons.Remove(key);
        _fileIcons.AddLast(key);
        while (_fileIcons.Count > FileIconsKept)
        {
            _icons.Forget(_fileIcons.First!.Value);
            _fileIcons.RemoveFirst();
        }
    }

    /// <summary>The picture of an answer without an icon: each action's (a window's while its app's icon is unknown).</summary>
    static Geometry? Glyph(SearchResult r) => r.Kind switch
    {
        SearchKind.Action => r.Target switch
        {
            SearchActions.Lock => Glyphs.Lock,
            SearchActions.Sleep or SearchActions.DndOn or SearchActions.DndOff => Glyphs.Moon,
            SearchActions.Restart => Glyphs.Restart,
            SearchActions.Shutdown => Glyphs.Power,
            SearchActions.Logout => Glyphs.Logout,
            SearchActions.EmptyTrash => Glyphs.Trash,
            SearchActions.ForceQuit => Glyphs.Cross,
            SearchActions.DarkOn or SearchActions.DarkOff => Glyphs.Contrast,
            SearchActions.Desktop => Glyphs.DisplayPicture,
            SearchActions.Overview => Glyphs.OverviewPicture,
            SearchActions.AppGrid => Glyphs.AppGridPicture,
            SearchActions.Notifications => Glyphs.Bell,
            SearchActions.ControlCenter => Glyphs.ControlCenterTracks,
            SearchActions.AboutPc => Glyphs.Info,
            _ => Glyphs.Bolt,
        },
        SearchKind.Window => Glyphs.WindowPicture,
        SearchKind.Clipboard => Glyphs.Clipboard,
        _ => null,
    };

    /// <summary>
    /// Under a spec (<see cref="OnlyAppsIn"/>): an answer the search may not open. The test target's apps and windows,
    /// Frostbar's own pages, the actions (the shell guards each) and a calculation are opened; documents, folders, notes,
    /// the web and Windows' settings would open in the user's apps.
    /// </summary>
    bool Refused(SearchResult r)
    {
        if (OnlyAppsIn is not { } folder) return false;
        bool allowed = r.Kind switch
        {
            SearchKind.Calculation or SearchKind.Conversion or SearchKind.Action => true,
            // A spec's own history's items (the user's clipboard is not touched); not Windows' clipboard settings.
            SearchKind.Clipboard => r.Target != ClipboardSearch.OffTarget,
            SearchKind.Setting => ShellSettingsSearch.PageOf(r.Target) != null,
            SearchKind.App => SpecGuard.Allows(folder, ShellApi.LinkTarget(@"shell:AppsFolder\" + r.Target), isDesktop: false),
            SearchKind.Window => _windowApps.TryGetValue(r.Target, out var app) && SpecGuard.Allows(folder, app.Exe, isDesktop: false),
            _ => false,
        };
        if (allowed) return false;
        LastChoice = $"guarded: a {r.Kind} that is not the test target's";
        Log.Warn($"search: {r.Kind} '{r.Title}' → {LastChoice}: nothing done");
        return true;
    }

    void Choose(SearchResult r, bool reveal)
    {
        Log.Debug($"search: open {r.Kind}{(reveal ? " (show in File Explorer)" : "")}");
        if (Refused(r)) { Close(restore: true); return; }
        LastChoice = r.Kind + " " + r.Target;
        switch (r.Kind)
        {
            case SearchKind.Calculation or SearchKind.Conversion:
                // The answer goes to the clipboard and the app that was in front gets the foreground back, to paste it.
                try { Clipboard.SetText(r.Target); } catch (Exception ex) { Log.Warn("search: could not copy the result", ex); }
                Close(restore: true);
                return;
            case SearchKind.App:
                Close(restore: false);
                _usage.Add(r.Target);
                // One that runs comes forward instead (macOS), and the Dock bounces one that starts (2026-10-23).
                if (CommandRunner.Opening?.Invoke(@"shell:AppsFolder\" + r.Target, "search") != true) CommandRunner.Launch(@"shell:AppsFolder\" + r.Target);
                return;
            case SearchKind.Setting:
                Close(restore: false);
                // Frostbar's own page where it has one (Wi-Fi, Sound, the Dock…), Windows' otherwise.
                if (ShellSettingsSearch.PageOf(r.Target) is { } page) OpenSettings?.Invoke(page);
                else CommandRunner.Launch(r.Target);
                return;
            case SearchKind.Window:
                Close(restore: false);
                if (long.TryParse(r.Target, NumberStyles.Integer, CultureInfo.InvariantCulture, out long h)) ActivateWindow?.Invoke(new IntPtr(h));
                return;
            case SearchKind.Action:
                // The foreground goes back first: the action's question, panel or window comes after it (in front).
                Close(restore: true);
                string id = r.Target;
                _ui.BeginInvoke(DispatcherPriority.Background, () => RunAction?.Invoke(id));
                return;
            case SearchKind.Folder or SearchKind.File:
                Close(restore: false);
                // Folders in the file manager of the settings, as the Dock's do.
                if (reveal) FileManager.Reveal(r.Target);
                else if (r.Kind == SearchKind.Folder) FileManager.OpenFolder(r.Target);
                else CommandRunner.Launch(r.Target);
                return;
            case SearchKind.Note:
                Close(restore: false);
                // In Obsidian (in the vault it belongs to); Ctrl+Enter shows the file, as for the files.
                if (reveal) FileManager.Reveal(r.Target);
                else if (ObsidianVaults.NoteOf(_vaults, r.Target) is { } note) CommandRunner.Launch(ObsidianVaults.OpenUri(note.Vault, note.Note));
                else CommandRunner.Launch(r.Target);
                return;
            case SearchKind.Web:
                Close(restore: false);
                CommandRunner.Launch(_settings.Search.WebSearchUrl.Replace("{q}", Uri.EscapeDataString(r.Target), StringComparison.Ordinal));
                return;
            case SearchKind.Clipboard:
                if (r.Target == ClipboardSearch.EmptyTarget) return; // nothing to put back
                if (r.Target == ClipboardSearch.OffTarget)
                {
                    Close(restore: false);
                    CommandRunner.Launch("ms-settings:clipboard"); // where the history is turned on
                    return;
                }
                // Back on the clipboard, and the app that was in front gets the foreground back to paste it.
                bool put = ClipboardHistory?.PutBack(r.Target) == true;
                LastChoice = (put ? "clipboard put back " : "clipboard not put back ") + r.Target;
                if (!put) Log.Warn("search: the clipboard item was not put back");
                Close(restore: true);
                return;
        }
    }

    /// <summary>An element's rectangle on the screen in pixels (null while it is not shown).</summary>
    static object? ScreenRect(FrameworkElement e)
    {
        if (!e.IsVisible || PresentationSource.FromVisual(e) == null) return null;
        var a = e.PointToScreen(new Point(0, 0));
        var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new { x = (int)Math.Round(a.X), y = (int)Math.Round(a.Y), w = (int)Math.Round(b.X - a.X), h = (int)Math.Round(b.Y - a.Y) };
    }

    /// <summary>Diagnostics: the panel as the E2E specs read it.</summary>
    public object State() => new
    {
        open = IsOpen,
        hwnd = _window?.Handle.ToInt64() ?? 0,
        size = _window == null ? null : $"Width {_window.Width} Actual {_window.ActualWidth}x{_window.ActualHeight} Content {(_window.Content as FrameworkElement)?.ActualWidth} MinWidth {_window.MinWidth} MaxWidth {_window.MaxWidth}",
        foreground = _window != null && Native.GetForegroundWindow() == _window.Handle,
        focused = _window?.Field.IsKeyboardFocused ?? false,
        composing = _window?.Composing ?? false,
        text = _text,
        query = _query,
        selection = _window?.Selection ?? -1,
        bar = _window == null ? null : new { opacity = Math.Round(_window.ListBar.Bar, 2), scrolls = _window.ListBar.Scrolls }, // C8
        glides = _window?.Glides ?? 0, height = _window == null ? 0 : Math.Round(_window.ActualHeight, 1), // E1
        filter = Filter.ToString(),
        listing = _listing,
        filters = _window?.FilterButtons.ToDictionary(kv => kv.Key.ToString(), kv => ScreenRect(kv.Value)),
        buttonsShown = _window?.ButtonsShown ?? false, dictations = _window?.Dictations ?? 0, // 2026-11-08: the capsule's buttons, its microphone
        lastChoice = LastChoice,
        shellPages = _shellPages.Count,
        shellPagesMs = ShellPagesMs,
        lastOpenMs = LastOpenMs,
        lastAppsMs = LastAppsMs,
        lastFilesMs = LastFilesMs,
        lastNotesMs = LastNotesMs,
        notes = _notes == null ? null : new { count = _notes.Notes, characters = _notes.Characters, loadMs = _notes.LoadMs, refreshed = _notes.Refreshed },
        clipboard = ClipboardHistory == null ? null : new { state = _clipboardState.ToString(), items = _clipboardItems.Count, read = _clipboardRead, reads = ClipboardReads },
        fileSource = LastFileSource,
        filesFor = LastFilesFor,
        filesId = LastFilesId,
        notesId = LastNotesId,
        notesFor = LastNotesFor,
        results = _shown.Select(r => new { kind = r.Kind.ToString(), r.Title, r.Detail, r.Target, r.Score }),
        rows = _window?.RowElements.Select(ScreenRect),
        choices = _window?.Choices,
    };

    /// <summary>Diagnostics: the list shown again for the same text, as when files or icons come in later.</summary>
    public void RenderAgain() => Render();

    /// <summary>Diagnostics: types <paramref name="text"/> into the field as if it had been typed (no IME).</summary>
    public void SetText(string text)
    {
        if (_window == null) return;
        _window.Field.Text = text;
        _window.Field.CaretIndex = text.Length;
    }

    public void Dispose()
    {
        _iconsLoaded.Stop();
        _files.Dispose();
        _notes?.Dispose();
        _icons.Dispose();
        _usage.Save();
        _window?.Close();
    }
}

/// <summary>How often each app was opened from the search (by AppsFolder parsing name): it ranks them higher.</summary>
public sealed class UsageStore
{
    readonly string _file;
    readonly Dictionary<string, int> _counts;
    readonly DispatcherTimer _save;

    public UsageStore(string file)
    {
        _file = file;
        try { _counts = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(file)) ?? new() : new(); }
        catch (Exception ex) { Log.Warn("search: usage counts could not be read; starting again", ex); _counts = new(); }
        _counts = new Dictionary<string, int>(_counts, StringComparer.OrdinalIgnoreCase);
        _save = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { _save!.Stop(); Save(); }, Dispatcher.CurrentDispatcher);
    }

    public int Count(string key) => _counts.GetValueOrDefault(key);

    public void Add(string key)
    {
        _counts[key] = Count(key) + 1;
        _save.Stop();
        _save.Start();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_counts));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) { Log.Warn("search: usage counts could not be saved", ex); }
    }
}
