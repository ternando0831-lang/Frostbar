# Frostbar

English | [日本語](#日本語)

**Windows 11, the macOS way.** Frostbar is a shell that gives Windows 11 a menu bar, a Dock, search, an app grid, Control Center, a notification centre and a window overview in the manner of macOS, running on top of the Windows you already have.

**[Download the latest version](../../releases/latest)** · free and open source (MIT) · Windows 11 (64-bit) · English and Japanese · [Website](https://ternando0831-lang.github.io/Frostbar/)

> Frostbar is an unofficial personal project, not affiliated with or endorsed by Apple Inc. macOS is a trademark of Apple Inc.

## Features

- **Menu bar** — the front app's name and menus (with keyboard shortcuts for common apps), a clock, Wi-Fi, volume and battery, an input menu, now playing, and an indicator while the microphone or camera is in use. It steps aside over full-screen apps and comes back at the top edge.
- **Dock** — magnification, running indicators, folder stacks, Trash, thumbnails of minimized windows, progress bars and badges, drag to rearrange. Bottom, left or right; it can hide itself.
- **Search** (Alt+Space by default) — apps, files (Windows Search, and [Everything](https://www.voidtools.com/) when it runs), settings, calculations and unit conversions, system actions, clipboard history.
- **App grid** — pages, rearranging, folders, hidden apps.
- **Control Center and notification centre** — Wi-Fi, Bluetooth, Focus, brightness, volume, now playing; Windows' notifications grouped by app.
- **Window overview and hot corners** — every window side by side, a strip of virtual desktops, opened from a screen corner.
- **Glass** — translucent glass whose rims catch the light, in light and dark.
- **Settings** — a window much like macOS's System Settings, where almost everything can be changed.

## Requirements

- Windows 11 22H2 (build 22621) or later, x64. The .NET runtime is included in the download.
- The lens effect ("Bend what's behind at the edges") needs Windows 11 24H2 or later.
- Tested by the author on a single PC with Windows 11 26H2 (build 26300).

## Install

1. Download `Frostbar-<version>-<commit>-win-x64.zip` from [Releases](../../releases/latest).
2. Right-click the zip → **Properties** → tick **Unblock** → **OK** (otherwise Windows warns each time a file from it opens).
3. Extract it and double-click `setup.cmd`. If SmartScreen says "Windows protected your PC", choose **More info** → **Run anyway** (the app is not signed).
4. Frostbar is installed to `%LOCALAPPDATA%\Programs\Frostbar`, added to the Start menu and started. A welcome window lets you choose whether to hide the Windows taskbar.

To update, run the new zip's `setup.cmd`; settings are kept.

**If the taskbar does not come back**, double-click `%LOCALAPPDATA%\Programs\Frostbar\restore-taskbar.cmd`. It works without Frostbar running (end a frozen Frostbar in Task Manager first).

**To uninstall**, double-click `%LOCALAPPDATA%\Programs\Frostbar\uninstall.cmd`. It brings the taskbar back and removes the sign-in start, the Start menu entry and the program. Run `uninstall.cmd -RemoveSettings` to also remove `%APPDATA%\Frostbar` and `%LOCALAPPDATA%\Frostbar`.

## Privacy

Frostbar sends nothing over the network (a web search opens in your browser). Notifications, clipboard history, recent files and the microphone and camera records are read on your PC only to be shown, and are neither stored elsewhere nor sent.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) (10.0.401 or later).

```
dotnet build Frostbar.slnx
dotnet test --project tests\Frostbar.Core.Tests\Frostbar.Core.Tests.csproj
```

Run a development build with settings of its own with `src\Frostbar.App\bin\Debug\net10.0-windows10.0.26100.0\Frostbar.exe --profile %TEMP%\frostbar-dev` (it leaves the taskbar alone unless given `--hide-taskbar`). `powershell -ExecutionPolicy Bypass -File tools\package\package.ps1` makes the download zip in `dist\`.

- `src/Frostbar.Core` — decisions that do not depend on Windows, with unit tests
- `src/Frostbar.Platform` — Win32, COM and WinRT calls
- `src/Frostbar.App` — the WPF windows, and the order of start and exit
- `tools/install`, `tools/package`, `tools/emergency` — installing, packaging, the emergency restore

## License

[MIT](LICENSE). Third-party software included (ManagedShell and others): [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

---

## 日本語

**Windows 11 を、macOS のように。** Frostbar は、メニューバー・Dock・検索・アプリ一覧・コントロールセンター・通知センター・ウインドウ一覧を、いつもの Windows の上に重ねて動かすシェルです。

**[最新版をダウンロード](../../releases/latest)** · 無料・オープンソース（MIT）· Windows 11（64 ビット）· 日本語と英語

> Frostbar は個人が作った非公式のソフトウェアで、Apple Inc. とは関係ありません。macOS は Apple Inc. の商標です。

### できること

- **メニューバー** — 前面のアプリの名前とメニュー（主なアプリはキーのショートカット付き）、時計、Wi-Fi・音量・電池、入力メニュー、再生中の曲、マイクとカメラの使用中の印。全画面のアプリの上では隠れ、上端で出てきます
- **Dock** — 拡大、起動中の点、フォルダのスタック、ごみ箱、最小化した窓の縮小図、進捗バーとバッジ、ドラッグでの並べ替え。下・左・右に置け、自動で隠せます
- **検索**（既定は Alt+Space）— アプリ、ファイル（Windows Search。[Everything](https://www.voidtools.com/) が動いていればそれも）、設定、計算と単位換算、PC の操作、クリップボードの履歴
- **アプリ一覧** — ページ送り、並べ替え、フォルダ、非表示
- **コントロールセンター・通知センター** — Wi-Fi・Bluetooth・集中モード・明るさ・音量・再生中、Windows の通知をアプリごとに
- **ウインドウ一覧とホットコーナー** — 全部の窓を並べて選ぶ、仮想デスクトップの帯、画面の角で開く
- **ガラスの見た目** — 縁が光を拾い、後ろが透けるガラス。ライトとダーク
- **設定** — macOS の「システム設定」に似た画面で、ほぼすべてを変えられます

### 必要なもの

- Windows 11 22H2（ビルド 22621）以降、64 ビット（x64）。.NET はダウンロードした zip に入っています
- 「縁で背後を曲げる」レンズは Windows 11 24H2 以降
- 作者が確かめたのは Windows 11 26H2（ビルド 26300）の PC 1 台だけです

### 入れ方

1. [Releases](../../releases/latest) から `Frostbar-<版>-<コミット>-win-x64.zip` をダウンロード
2. zip を右クリック →「プロパティ」→「許可する」に印 →「OK」（しないと、開くたびに Windows の警告が出ます）
3. 「すべて展開」して、中の `setup.cmd` をダブルクリック。「Windows によって PC が保護されました」と出たら「詳細情報」→「実行」（署名のないアプリなので出ます）
4. `%LOCALAPPDATA%\Programs\Frostbar` に入り、スタートメニューに「Frostbar」ができて起動します。初めての起動で案内が出て、Windows のタスクバーを隠すかはそこで選べます

新しい版にするときは、新しい zip の `setup.cmd` をもう一度。設定はそのまま残ります。

**タスクバーが戻らないとき**は `%LOCALAPPDATA%\Programs\Frostbar\restore-taskbar.cmd` をダブルクリック。Frostbar が動いていなくても元に戻します（固まっているときは、先にタスク マネージャーで終了してから）。

**消し方**: `%LOCALAPPDATA%\Programs\Frostbar\uninstall.cmd` をダブルクリック。タスクバーを元に戻し、ログイン時の起動・スタートメニュー・プログラムを消します。設定（`%APPDATA%\Frostbar`・`%LOCALAPPDATA%\Frostbar`）も消すときは `uninstall.cmd -RemoveSettings`。

### プライバシー

Frostbar はネットに何も送りません（Web 検索はブラウザで開くだけです）。通知・クリップボードの履歴・最近使ったファイル・マイクとカメラの使用の記録は、画面に出すために手元で読むだけで、どこにも保存・送信しません。

### ライセンス

[MIT](LICENSE)。同梱している他者のソフトウェア（ManagedShell など）のライセンスは [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
