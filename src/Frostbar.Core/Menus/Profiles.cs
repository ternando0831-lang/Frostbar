using Frostbar.Core.Localization;
using static Frostbar.Core.Menus.MenuBuilder;

namespace Frostbar.Core.Menus;

/// <summary>Menus for one family of apps, chosen by executable name.</summary>
/// <param name="Help">The keys of the app's help (the Help menu's item); null: no Help menu (F1 is something else there).</param>
/// <param name="Settings">The keys that open the app's settings (the app menu's "Settings…"); null: no such item.</param>
public sealed record AppProfile(string Id, IReadOnlyList<string> Exes, Func<Strings, IReadOnlyList<Menu>> Menus, string? Help = "F1", string? Settings = null);

/// <summary>
/// Built-in profiles. Each lists only shortcuts the app really has; anything else falls back to the generic menus.
/// Keys are stored as virtual keys, so "Ctrl+=" means the VK_OEM_PLUS key (";" on a Japanese keyboard), which is what
/// these apps bind zoom-in to.
/// </summary>
public static class Profiles
{
    static Menu M(string id, string title, params MenuItem[] items) => new(id, title, Tidy(items));
    static MenuItem Sep => MenuItem.Separator;

    /// <summary>A View menu with zoom and full screen (for the apps that bind those keys), after <paramref name="before"/>.</summary>
    static Menu Zoom(Strings s, MenuItem[] before, string reset = "Ctrl+0") => M("view", s["menu.view"],
        [.. before, .. (before.Length > 0 ? new[] { Sep } : []),
         Keys("zoom-in", s["view.zoomIn"], "Ctrl+="), Keys("zoom-out", s["view.zoomOut"], "Ctrl+-"), Keys("zoom-reset", s["view.actualSize"], reset),
         Sep, Keys("fullscreen", s["view.fullScreen"], "F11")]);

    static Menu StandardEdit(Strings s, params MenuItem[] extra) => M("edit", s["menu.edit"],
        [Keys("undo", s["edit.undo"], "Ctrl+Z"), Keys("redo", s["edit.redo"], "Ctrl+Y"), Sep,
         Keys("cut", s["edit.cut"], "Ctrl+X"), Keys("copy", s["edit.copy"], "Ctrl+C"), Keys("paste", s["edit.paste"], "Ctrl+V"),
         Keys("select-all", s["edit.selectAll"], "Ctrl+A"), Sep, .. extra, Sep, Keys("emoji", s["edit.emoji"], "Win+.")]);

    /// <summary>
    /// Apps without a profile: only keys that mean the same in any Windows app. Not zoom, full screen, Close (Ctrl+W) or
    /// Save As (Ctrl+Shift+S): in Excel Ctrl+- deletes cells, Ctrl+0 hides columns and F11 inserts a chart sheet, in Word
    /// Ctrl+Shift+S applies styles, in Paint Ctrl+W resizes (2026-10-12).
    /// </summary>
    public static readonly AppProfile Generic = new("generic", [], s =>
    [
        M("file", s["menu.file"], Keys("new", s["file.new"], "Ctrl+N"), Keys("open", s["file.open"], "Ctrl+O"), Sep,
            Keys("save", s["file.save"], "Ctrl+S"), Sep, Keys("print", s["file.print"], "Ctrl+P")),
        StandardEdit(s, Keys("find", s["edit.find"], "Ctrl+F")),
    ]);

    public static readonly AppProfile Chromium = new("chromium", ["brave.exe", "chrome.exe", "msedge.exe", "vivaldi.exe", "opera.exe"], s =>
    [
        M("file", s["menu.file"], Keys("new-tab", s["file.newTab"], "Ctrl+T"), Keys("new-window", s["file.newWindow"], "Ctrl+N"),
            Keys("new-private", s["file.newPrivate"], "Ctrl+Shift+N"), Keys("reopen-tab", s["file.reopenTab"], "Ctrl+Shift+T"),
            Keys("open-location", s["file.openLocation"], "Ctrl+L"), Sep, Keys("close-tab", s["file.closeTab"], "Ctrl+W"),
            Keys("close-window", s["file.closeWindow"], "Ctrl+Shift+W"), Sep, Keys("save-page", s["file.savePage"], "Ctrl+S"), Sep,
            Keys("print", s["file.print"], "Ctrl+P")),
        StandardEdit(s, Keys("find", s["edit.find"], "Ctrl+F")),
        Zoom(s, [Keys("reload", s["view.reload"], "Ctrl+R"), Keys("hard-reload", s["view.hardReload"], "Ctrl+Shift+R"),
            Keys("dev-tools", s["view.devTools"], "F12")]),
        M("history", s["menu.history"], Keys("back", s["go.back"], "Alt+Left"), Keys("forward", s["go.forward"], "Alt+Right"), Sep,
            Keys("history", s["go.history"], "Ctrl+H"), Keys("downloads", s["go.downloads"], "Ctrl+J")),
        M("bookmarks", s["menu.bookmarks"], Keys("bookmark", s["bm.add"], "Ctrl+D"), Keys("bookmark-manager", s["bm.manager"], "Ctrl+Shift+O"),
            Keys("bookmark-bar", s["bm.bar"], "Ctrl+Shift+B")),
        M("tab", s["menu.tab"], Keys("next-tab", s["tab.next"], "Ctrl+Tab"), Keys("prev-tab", s["tab.prev"], "Ctrl+Shift+Tab")),
    ]);

    public static readonly AppProfile Explorer = new("explorer", ["explorer.exe"], s =>
    [
        M("file", s["menu.file"], Keys("new-window", s["file.newWindow"], "Ctrl+N"), Keys("new-tab", s["file.newTab"], "Ctrl+T"),
            Keys("new-folder", s["file.newFolder"], "Ctrl+Shift+N"), Sep, Keys("close-tab", s["file.closeTab"], "Ctrl+W"), Sep,
            Keys("properties", s["file.properties"], "Alt+Enter")),
        StandardEdit(s, Keys("rename", s["edit.rename"], "F2"), Keys("delete", s["edit.delete"], "Delete")),
        M("view", s["menu.view"], Keys("refresh", s["view.refresh"], "F5"), Keys("address", s["view.address"], "Alt+D"),
            Keys("search", s["view.search"], "Ctrl+E")),
        M("go", s["menu.go"], Keys("back", s["go.back"], "Alt+Left"), Keys("forward", s["go.forward"], "Alt+Right"), Keys("up", s["go.up"], "Alt+Up")),
    ]);

    /// <summary>
    /// The desktop: File Explorer's, but with no folder window there are no tabs, address bar, search or going back
    /// (the Explorer profile's items did nothing on it). New Window opens the file manager.
    /// </summary>
    public static readonly AppProfile Desktop = new("desktop", [], s =>
    [
        M("file", s["menu.file"], Keys("new-window", s["file.newWindow"], "Ctrl+N"), Keys("new-folder", s["file.newFolder"], "Ctrl+Shift+N"), Sep,
            Keys("properties", s["file.properties"], "Alt+Enter")),
        StandardEdit(s, Keys("rename", s["edit.rename"], "F2"), Keys("delete", s["edit.delete"], "Delete")),
        M("view", s["menu.view"], Keys("refresh", s["view.refresh"], "F5")),
    ]);

    public static readonly AppProfile Notepad = new("notepad", ["notepad.exe"], s =>
    [
        M("file", s["menu.file"], Keys("new-tab", s["file.newTab"], "Ctrl+N"), Keys("new-window", s["file.newWindow"], "Ctrl+Shift+N"),
            Keys("open", s["file.open"], "Ctrl+O"), Sep, Keys("save", s["file.save"], "Ctrl+S"), Keys("save-as", s["file.saveAs"], "Ctrl+Shift+S"),
            Keys("save-all", s["file.saveAll"], "Ctrl+Alt+S"), Sep, Keys("print", s["file.print"], "Ctrl+P"), Sep, Keys("close-tab", s["file.closeTab"], "Ctrl+W")),
        StandardEdit(s, Keys("find", s["edit.find"], "Ctrl+F"), Keys("find-next", s["edit.findNext"], "F3"), Keys("replace", s["edit.replace"], "Ctrl+H"),
            Keys("go-to-line", s["edit.goToLine"], "Ctrl+G"), Keys("time-date", s["edit.timeDate"], "F5")),
        M("view", s["menu.view"], Keys("zoom-in", s["view.zoomIn"], "Ctrl+="), Keys("zoom-out", s["view.zoomOut"], "Ctrl+-"),
            Keys("zoom-reset", s["view.actualSize"], "Ctrl+0")),
    ]);

    /// <summary>Windows Terminal: Ctrl+Shift+W is its closePane (a tab of one pane closes with it); F1 is not bound and would reach the shell.</summary>
    public static readonly AppProfile Terminal = new("terminal", ["windowsterminal.exe"], s =>
    [
        M("shell", s["menu.shell"], Keys("new-tab", s["file.newTab"], "Ctrl+Shift+T"), Keys("new-window", s["file.newWindow"], "Ctrl+Shift+N"), Sep,
            Keys("close-pane", s["file.closePane"], "Ctrl+Shift+W")),
        M("edit", s["menu.edit"], Keys("copy", s["edit.copy"], "Ctrl+Shift+C"), Keys("paste", s["edit.paste"], "Ctrl+Shift+V"), Sep,
            Keys("find", s["edit.find"], "Ctrl+Shift+F")),
        Zoom(s, []),
    ], Help: null, Settings: "Ctrl+,");

    /// <summary>VS Code and the editors built on it, which keep its keys: zoom resets with Ctrl+NumPad0 (Ctrl+0 focuses the side bar), F1 is the Command Palette.</summary>
    public static readonly AppProfile VsCode = new("vscode", ["code.exe", "code - insiders.exe", "cursor.exe", "windsurf.exe", "antigravity.exe"], s =>
    [
        M("file", s["menu.file"], Keys("new-file", s["file.newFile"], "Ctrl+N"), Keys("new-window", s["file.newWindow"], "Ctrl+Shift+N"),
            Keys("open-file", s["file.openFile"], "Ctrl+O"), Sep, Keys("save", s["file.save"], "Ctrl+S"), Keys("save-as", s["file.saveAs"], "Ctrl+Shift+S"), Sep,
            Keys("close-editor", s["file.closeEditor"], "Ctrl+W")),
        StandardEdit(s, Keys("find", s["edit.find"], "Ctrl+F"), Keys("replace", s["edit.replace"], "Ctrl+H"),
            Keys("find-in-files", s["edit.findInFiles"], "Ctrl+Shift+F")),
        Zoom(s, [Keys("command-palette", s["view.commandPalette"], "Ctrl+Shift+P"), Keys("explorer", s["view.explorer"], "Ctrl+Shift+E"),
            Keys("terminal", s["view.terminal"], "Ctrl+`")], reset: "Ctrl+NumPad0"),
        M("go", s["menu.go"], Keys("go-to-file", s["go.toFile"], "Ctrl+P"), Keys("go-to-line", s["edit.goToLine"], "Ctrl+G"), Sep,
            Keys("back", s["go.back"], "Alt+Left"), Keys("forward", s["go.forward"], "Alt+Right")),
    ], Help: null, Settings: "Ctrl+,");

    /// <summary>The user's own file manager (shortcuts from its CLAUDE.md).</summary>
    public static readonly AppProfile Bipane = new("bipane", ["bipane.exe"], s =>
    [
        M("file", s["menu.file"], Keys("same-folder-tab", s["tab.sameFolder"], "Ctrl+N"), Sep, Keys("close-tab", s["file.closeTab"], "Ctrl+W")),
        StandardEdit(s, Keys("rename", s["edit.rename"], "F2"), Keys("delete", s["edit.delete"], "Delete")),
        M("view", s["menu.view"], Keys("refresh", s["view.refresh"], "F5"), Keys("address", s["view.address"], "Alt+D"), Keys("search", s["view.search"], "Ctrl+E"), Sep,
            Keys("view-xl", s["view.extraLarge"], "Ctrl+Shift+1"), Keys("view-l", s["view.large"], "Ctrl+Shift+2"), Keys("view-m", s["view.medium"], "Ctrl+Shift+3"),
            Keys("view-list", s["view.list"], "Ctrl+Shift+4"), Keys("view-details", s["view.details"], "Ctrl+Shift+6"), Keys("view-tiles", s["view.tiles"], "Ctrl+Shift+8")),
        M("go", s["menu.go"], Keys("back", s["go.back"], "Alt+Left"), Keys("forward", s["go.forward"], "Alt+Right"), Keys("up", s["go.up"], "Alt+Up")),
    ], Settings: "Ctrl+,"); // F1 shows its keyboard shortcuts

    /// <summary>Obsidian's default hotkeys (its Settings → Hotkeys).</summary>
    public static readonly AppProfile Obsidian = new("obsidian", ["obsidian.exe"], s =>
    [
        M("file", s["menu.file"], Keys("new-note", s["file.newNote"], "Ctrl+N"), Keys("new-tab", s["file.newTab"], "Ctrl+T"), Sep,
            Keys("quick-switcher", s["file.quickSwitcher"], "Ctrl+O"), Sep, Keys("reopen-tab", s["file.reopenTab"], "Ctrl+Shift+T"),
            Keys("close-tab", s["file.closeTab"], "Ctrl+W")),
        StandardEdit(s, Keys("find", s["edit.find"], "Ctrl+F"), Keys("replace", s["edit.replace"], "Ctrl+H"), Keys("search-all", s["edit.searchAll"], "Ctrl+Shift+F")),
        M("format", s["menu.format"], Keys("bold", s["format.bold"], "Ctrl+B"), Keys("italic", s["format.italic"], "Ctrl+I"), Keys("link", s["format.link"], "Ctrl+K")),
        M("view", s["menu.view"], Keys("command-palette", s["view.commandPalette"], "Ctrl+P"), Keys("reading-view", s["view.readingView"], "Ctrl+E"),
            Keys("graph", s["view.graph"], "Ctrl+G"), Sep,
            Keys("zoom-in", s["view.zoomIn"], "Ctrl+="), Keys("zoom-out", s["view.zoomOut"], "Ctrl+-"), Keys("zoom-reset", s["view.actualSize"], "Ctrl+0")),
        M("go", s["menu.go"], Keys("back", s["go.back"], "Ctrl+Alt+Left"), Keys("forward", s["go.forward"], "Ctrl+Alt+Right")),
    ], Settings: "Ctrl+,");

    /// <summary>Discord's keyboard shortcuts (its Ctrl+/ list).</summary>
    public static readonly AppProfile Discord = new("discord", ["discord.exe", "discordptb.exe", "discordcanary.exe"], s =>
    [
        StandardEdit(s, Keys("find", s["edit.find"], "Ctrl+F"), Keys("upload", s["edit.upload"], "Ctrl+Shift+U"),
            Keys("emoji-picker", s["edit.emojiPicker"], "Ctrl+E"), Keys("gif-picker", s["edit.gifPicker"], "Ctrl+G")),
        M("view", s["menu.view"], Keys("pins", s["view.pins"], "Ctrl+P"), Keys("inbox", s["view.inbox"], "Ctrl+I"), Keys("members", s["view.members"], "Ctrl+U"), Sep,
            Keys("zoom-in", s["view.zoomIn"], "Ctrl+="), Keys("zoom-out", s["view.zoomOut"], "Ctrl+-"), Keys("zoom-reset", s["view.actualSize"], "Ctrl+0"), Sep,
            Keys("shortcuts", s["view.shortcuts"], "Ctrl+/")),
        M("go", s["menu.go"], Keys("quick-switcher", s["file.quickSwitcher"], "Ctrl+K"), Sep,
            Keys("prev-server", s["go.prevServer"], "Ctrl+Alt+Up"), Keys("next-server", s["go.nextServer"], "Ctrl+Alt+Down"),
            Keys("prev-channel", s["go.prevChannel"], "Alt+Up"), Keys("next-channel", s["go.nextChannel"], "Alt+Down"),
            Keys("prev-unread", s["go.prevUnread"], "Alt+Shift+Up"), Keys("next-unread", s["go.nextUnread"], "Alt+Shift+Down"), Sep,
            Keys("mark-read", s["go.markRead"], "Esc"), Keys("mark-server-read", s["go.markServerRead"], "Shift+Esc")),
        M("call", s["menu.call"], Keys("mute", s["call.mute"], "Ctrl+Shift+M"), Keys("deafen", s["call.deafen"], "Ctrl+Shift+D"), Sep,
            Keys("answer", s["call.answer"], "Ctrl+Enter"), Keys("decline", s["call.decline"], "Esc")),
    ], Help: "Ctrl+Shift+H", Settings: "Ctrl+,");

    public static readonly IReadOnlyList<AppProfile> All = [Chromium, Explorer, Notepad, Terminal, VsCode, Bipane, Obsidian, Discord];

    /// <summary>What each Chromium browser calls its private window (Ctrl+Shift+N): Chrome Incognito, Edge InPrivate, the others Private.</summary>
    public static string PrivateWindowKey(string exeName) => exeName.ToLowerInvariant() switch
    {
        "chrome.exe" => "file.newIncognito",
        "msedge.exe" => "file.newInPrivate",
        _ => "file.newPrivate",
    };

    /// <summary>The desktop counts as Explorer (macOS shows Finder there).</summary>
    public static AppProfile Find(string exeName, bool isDesktop = false)
    {
        if (isDesktop) return Desktop;
        var exe = exeName.ToLowerInvariant();
        return All.FirstOrDefault(p => p.Exes.Contains(exe)) ?? Generic;
    }
}
