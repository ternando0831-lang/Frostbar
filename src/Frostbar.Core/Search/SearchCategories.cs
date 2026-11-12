namespace Frostbar.Core.Search;

/// <summary>
/// Spotlight's "Search results" switches (System Settings › Spotlight, 2026-10-22): the kinds of results the search
/// page lets the user leave out of the answer, in the page's order, each with the kinds it stands for. The one list the
/// page, the validator and the ranker use. The clipboard is a filter of its own (Ctrl+4), not a kind of the answer.
/// </summary>
public static class SearchCategories
{
    public static readonly string[] Names = ["apps", "windows", "actions", "settings", "notes", "folders", "files", "calculator", "conversions", "web"];

    /// <summary>The kind <paramref name="name"/> stands for (null: not one of <see cref="Names"/>).</summary>
    public static SearchKind? Kind(string name) => name switch
    {
        "apps" => SearchKind.App,
        "windows" => SearchKind.Window,
        "actions" => SearchKind.Action,
        "settings" => SearchKind.Setting,
        "notes" => SearchKind.Note,
        "folders" => SearchKind.Folder,
        "files" => SearchKind.File,
        "calculator" => SearchKind.Calculation,
        "conversions" => SearchKind.Conversion,
        "web" => SearchKind.Web,
        _ => null,
    };

    /// <summary>The kinds left out by the settings' list of names (unknown names are none).</summary>
    public static HashSet<SearchKind> Hidden(IEnumerable<string>? names) =>
        [.. (names ?? []).Select(Kind).Where(k => k.HasValue).Select(k => k!.Value)];

    /// <summary>Whether a source need not be asked at all: every kind it answers with is left out.</summary>
    public static bool AllHidden(ISet<SearchKind> hidden, params SearchKind[] kinds) => kinds.All(hidden.Contains);
}
