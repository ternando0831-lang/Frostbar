namespace Frostbar.Core.Settings;

/// <summary>
/// Frostbar's settings window as a list, one for the window and the search (stage 6-1): the sidebar's sections in its
/// order, the pages opened from a row of another, each page's title, words a page is found by without showing them,
/// and the Windows Settings pages Frostbar's own take the place of (ADR-013). The search kept its own hand-written list of
/// the last (2026-10-12), which knew none of the pages stage 4 added.
/// </summary>
public static class ShellSettingsPages
{
    /// <summary>The sidebar's sections, in its order (macOS's: the radios and the battery first, sound after the shell's own).</summary>
    public static readonly string[] Sidebar = ["wifi", "bluetooth", "battery", "general", "accessibility", "appearance", "menuBar", "dock", "appGrid", "hotCorners", "display", "wallpaper", "search", "sound", "keyboard", "mouse", "trackpad", "advanced"];

    /// <summary>Pages opened from a row of another (General → About, Storage, Login Items): the sidebar marks the parent, the header has a back arrow.</summary>
    public static readonly IReadOnlyDictionary<string, string> SubPages = new Dictionary<string, string> { ["about"] = "general", ["storage"] = "general", ["loginItems"] = "general" };

    /// <summary>Every page: the sidebar's sections in its order, then the sub-pages.</summary>
    public static IEnumerable<string> All => Sidebar.Concat(SubPages.Keys);

    /// <summary>The string a page's title is (the sidebar's and the header's): the radios' and the sound's are the Control Center's names.</summary>
    public static string TitleKey(string page) => page is "wifi" or "bluetooth" or "sound" ? "cc." + page : "set." + page;

    /// <summary>
    /// Other names a page goes by, found as its title is (not as a word): macOS's English 「Displays」 is Windows'
    /// 「Display」, and a search for "display" found Windows' page before Frostbar's (2026-10-16).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> AlsoNamed = new Dictionary<string, string[]> { ["display"] = ["Display"] };

    /// <summary>A page's names besides its title in the display language: its title in the other language, and <see cref="AlsoNamed"/>.</summary>
    public static IReadOnlyList<string> OtherNames(string page, Func<string, string> otherLanguage) =>
        [otherLanguage(TitleKey(page)), .. AlsoNamed.GetValueOrDefault(page, [])];

    /// <summary>Words a page is found by without showing them: the pages say 「Dock」 (as macOS's Japanese does), and 「ドック」 is typed too.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AlsoFoundBy = new Dictionary<string, string[]>
    {
        ["dock"] = ["ドック"], ["battery"] = ["電源", "スリープ", "power"], ["about"] = ["このPCについて", "バージョン情報"],
        ["keyboard"] = ["ショートカット", "shortcut", "タイル", "tile", "スナップ", "snap", "リピート", "repeat"],
        ["loginItems"] = ["スタートアップ", "startup", "自動起動", "サインイン", "sign-in"],
        ["trackpad"] = ["タッチパッド", "touchpad", "ジェスチャ", "gesture", "タップ", "tap"],
        ["mouse"] = ["ポインター", "pointer", "ホイール", "wheel", "カーソル", "cursor"],
        ["wallpaper"] = ["背景", "background", "デスクトップ", "desktop picture"],
        // The Displays page's rows are read off the UI thread, so its words are not on the page when it is indexed.
        ["display"] = ["解像度", "resolution", "拡大率", "スケール", "scale", "拡大縮小", "リフレッシュ レート", "refresh rate", "Hz", "明るさ", "brightness", "HDR", "モニター", "monitor"],
        ["appearance"] = ["日の入り", "日の出", "sunset", "sunrise", "スケジュール", "schedule", "アクセント", "accent", "強調色", "スクロールバー", "scroll bar"],
        ["accessibility"] = ["ユーザー補助", "簡単操作", "視差効果", "動きを減らす", "reduce motion", "透明度", "transparency", "コントラスト", "contrast"],
        ["sound"] = ["効果音", "サウンドエフェクト", "sound effects", "フィードバック", "feedback"],
        ["menuBar"] = ["最近使った項目", "recent items", "バッテリーの割合", "percentage", "区切り", "separators"],
    };

    /// <summary>
    /// The page of Frostbar's own that takes the place of a Windows Settings page (Wi-Fi, Bluetooth, Sound and its devices
    /// and mixer, Power &amp; battery, About, Storage: ADR-013), or null where Windows' page stays.
    /// </summary>
    public static string? Replacing(string uri) => uri switch
    {
        "ms-settings:network-wifi" => "wifi",
        "ms-settings:bluetooth" => "bluetooth",
        "ms-settings:sound" or "ms-settings:sound-devices" or "ms-settings:apps-volume" => "sound",
        "ms-settings:powersleep" or "ms-settings:batterysaver" => "battery",
        "ms-settings:about" => "about",
        "ms-settings:storagesense" => "storage",
        _ => null,
    };
}
