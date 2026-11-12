namespace Frostbar.Core.Search;

/// <summary>A page of Windows Settings: its URI, its names in English and Japanese, and words people search it by.</summary>
public sealed record SettingsPage(string Uri, string En, string Ja, string[] Keywords)
{
    /// <summary>Its names, prepared for matching.</summary>
    public IReadOnlyList<Candidate> Names { get; } = new[] { En, Ja }.Distinct().Select(n => new Candidate(n)).ToArray();
    /// <summary>The words it is found by, prepared for matching.</summary>
    public IReadOnlyList<Candidate> Words { get; } = Keywords.Select(n => new Candidate(n)).ToArray();
}

/// <summary>
/// Windows Settings pages the search offers (ms-settings: URIs of Windows 11), findable by either language whatever
/// the display language is ("display" and "ディスプレイ" both find the Display page).
/// </summary>
public static class SettingsPages
{
    public static readonly IReadOnlyList<SettingsPage> All =
    [
        new("ms-settings:display", "Display", "ディスプレイ", ["screen", "monitor", "resolution", "scale", "brightness", "画面", "解像度", "拡大縮小", "明るさ"]),
        new("ms-settings:nightlight", "Night light", "夜間モード", ["blue light", "ブルーライト"]),
        new("ms-settings:display-advancedgraphics", "Graphics", "グラフィックス", ["gpu", "hdr"]),
        new("ms-settings:sound", "Sound", "サウンド", ["volume", "speaker", "audio", "音量", "スピーカー", "音"]),
        new("ms-settings:sound-devices", "Sound devices", "サウンド デバイス", ["output", "input", "出力", "入力デバイス"]),
        new("ms-settings:apps-volume", "Volume mixer", "音量ミキサー", ["mixer", "ミキサー"]),
        new("ms-settings:notifications", "Notifications", "通知", ["alerts", "do not disturb", "応答不可"]),
        new("ms-settings:quiethours", "Focus", "集中モード", ["focus assist", "フォーカス"]),
        new("ms-settings:powersleep", "Power & battery", "電源とバッテリー", ["power", "sleep", "battery", "電源", "スリープ", "バッテリー"]),
        new("ms-settings:batterysaver", "Energy saver", "省電力", ["battery saver", "バッテリー節約機能"]),
        new("ms-settings:storagesense", "Storage", "ストレージ", ["disk", "space", "ディスク", "容量"]),
        new("ms-settings:multitasking", "Multitasking", "マルチタスク", ["snap", "スナップ", "virtual desktops", "仮想デスクトップ"]),
        new("ms-settings:clipboard", "Clipboard", "クリップボード", []),
        new("ms-settings:about", "About", "バージョン情報", ["system", "pc name", "specs", "システム", "PC 名", "仕様"]),
        new("ms-settings:bluetooth", "Bluetooth & devices", "Bluetooth とデバイス", ["bluetooth", "devices", "デバイス"]),
        new("ms-settings:printers", "Printers & scanners", "プリンターとスキャナー", ["printer", "scanner", "プリンター"]),
        new("ms-settings:mousetouchpad", "Mouse", "マウス", ["pointer", "cursor", "ポインター", "カーソル"]),
        new("ms-settings:devices-touchpad", "Touchpad", "タッチパッド", ["trackpad", "gestures", "ジェスチャ"]),
        new("ms-settings:typing", "Typing", "入力", ["keyboard", "autocorrect", "キーボード"]),
        new("ms-settings:autoplay", "AutoPlay", "自動再生", []),
        new("ms-settings:usb", "USB", "USB", []),
        new("ms-settings:mobile-devices", "Mobile devices", "モバイル デバイス", ["phone", "phone link", "スマートフォン", "スマホ"]),
        new("ms-settings:network-status", "Network & internet", "ネットワークとインターネット", ["network", "internet", "ネットワーク", "インターネット"]),
        new("ms-settings:network-wifi", "Wi-Fi", "Wi-Fi", ["wifi", "wireless", "無線"]),
        new("ms-settings:network-ethernet", "Ethernet", "イーサネット", ["wired", "lan", "有線"]),
        new("ms-settings:network-vpn", "VPN", "VPN", []),
        new("ms-settings:network-airplanemode", "Airplane mode", "機内モード", []),
        new("ms-settings:network-mobilehotspot", "Mobile hotspot", "モバイル ホットスポット", ["hotspot", "tethering", "テザリング"]),
        new("ms-settings:network-proxy", "Proxy", "プロキシ", []),
        new("ms-settings:personalization", "Personalization", "個人用設定", ["appearance", "見た目"]),
        new("ms-settings:personalization-background", "Background", "背景", ["wallpaper", "desktop background", "壁紙", "デスクトップの背景"]),
        new("ms-settings:personalization-colors", "Colors", "色", ["dark mode", "light mode", "accent color", "ダーク モード", "ライト モード", "アクセント カラー"]),
        new("ms-settings:themes", "Themes", "テーマ", []),
        new("ms-settings:lockscreen", "Lock screen", "ロック画面", []),
        new("ms-settings:fonts", "Fonts", "フォント", []),
        new("ms-settings:personalization-start", "Start", "スタート", ["start menu", "スタート メニュー"]),
        new("ms-settings:taskbar", "Taskbar", "タスク バー", ["タスクバー"]),
        new("ms-settings:appsfeatures", "Installed apps", "インストールされているアプリ", ["uninstall", "programs", "apps", "アンインストール", "アプリ"]),
        new("ms-settings:defaultapps", "Default apps", "既定のアプリ", ["default browser", "既定のブラウザー", "関連付け"]),
        new("ms-settings:startupapps", "Startup apps", "スタートアップ アプリ", ["startup", "スタートアップ"]),
        new("ms-settings:optionalfeatures", "Optional features", "オプション機能", []),
        new("ms-settings:yourinfo", "Your info", "ユーザーの情報", ["account", "アカウント"]),
        new("ms-settings:signinoptions", "Sign-in options", "サインイン オプション", ["password", "pin", "windows hello", "パスワード"]),
        new("ms-settings:emailandaccounts", "Email & accounts", "メールとアカウント", ["email", "メール"]),
        new("ms-settings:otherusers", "Other users", "他のユーザー", ["family", "家族"]),
        new("ms-settings:backup", "Windows Backup", "Windows バックアップ", ["backup", "バックアップ"]),
        new("ms-settings:dateandtime", "Date & time", "日付と時刻", ["time zone", "clock", "タイム ゾーン", "時計"]),
        new("ms-settings:regionlanguage", "Language & region", "言語と地域", ["language", "region", "言語", "地域", "日本語"]),
        new("ms-settings:regionformatting", "Regional format", "地域の書式", ["date format", "日付の形式"]),
        new("ms-settings:speech", "Speech", "音声認識", ["voice", "音声"]),
        new("ms-settings:gaming-gamebar", "Game Bar", "Game Bar", ["xbox", "ゲーム バー"]),
        new("ms-settings:gaming-gamemode", "Game Mode", "ゲーム モード", []),
        new("ms-settings:easeofaccess-display", "Accessibility", "アクセシビリティ", ["text size", "ease of access", "文字のサイズ", "簡単操作"]),
        new("ms-settings:privacy", "Privacy & security", "プライバシーとセキュリティ", ["privacy", "permissions", "プライバシー", "アクセス許可"]),
        new("ms-settings:privacy-location", "Location", "位置情報", ["gps"]),
        new("ms-settings:privacy-webcam", "Camera", "カメラ", ["webcam"]),
        new("ms-settings:privacy-microphone", "Microphone", "マイク", ["mic"]),
        new("ms-settings:windowsdefender", "Windows Security", "Windows セキュリティ", ["antivirus", "defender", "ウイルス", "セキュリティ"]),
        new("ms-settings:windowsupdate", "Windows Update", "Windows Update", ["update", "アップデート", "更新"]),
        new("ms-settings:recovery", "Recovery", "回復", ["reset this pc", "PC をリセット"]),
        new("ms-settings:activation", "Activation", "ライセンス認証", ["license", "product key", "プロダクト キー"]),
        new("ms-settings:developers", "For developers", "開発者向け", ["developer mode", "開発者モード"]),
    ];

    /// <summary>
    /// The page's best score for a normalized query (0: no match): by its names, or by a word it is found by, which ranks
    /// just below a name (<see cref="SearchRanker.KeywordPenalty"/>). Frostbar's pages that take the place of some of them:
    /// <see cref="Settings.ShellSettingsPages.Replacing"/>.
    /// </summary>
    public static int Score(SettingsPage page, string query) =>
        Math.Max(page.Names.Max(c => Matcher.Score(query, c)),
                 page.Words.Count == 0 ? 0 : page.Words.Max(c => Math.Max(0, Matcher.Score(query, c) - SearchRanker.KeywordPenalty)));
}
