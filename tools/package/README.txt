Frostbar — Windows 11 を macOS 風にするシェル（メニューバー・Dock・検索・アプリ一覧・コントロールセンター・ウインドウ一覧）

■ 必要なもの
・Windows 11 22H2（ビルド 22621）以降、64 ビット（x64）
・.NET はこのパッケージに入っているので、別に入れなくてよい
・Liquid Glass のレンズ（設定の外観「縁で背後を曲げる」）は Windows 11 24H2 以降
・確かめたのは Windows 11 26H2（ビルド 26300）の PC 1 台だけ

■ 入れ方
1. ネットからダウンロードした zip なら、先に zip を右クリック →「プロパティ」→ 下の「許可する」に印 →「OK」
   （しないと、展開したファイルを開くたびに Windows の警告が出る）
2. zip を右クリック →「すべて展開」で、好きな場所に展開する
3. 展開したフォルダの setup.cmd をダブルクリック
   ・「Windows によって PC が保護されました」と出たら「詳細情報」→「実行」
4. %LOCALAPPDATA%\Programs\Frostbar に入り、スタートメニューに「Frostbar」ができて、起動する
5. 初めての起動で案内が出る。Windows のタスクバーを隠すのは、案内で選んでから
   ・ログイン時に起動するかは、案内の最後か、設定の「一般 › ログイン項目」で選べる

・新しい版にするときは、新しい zip の setup.cmd をもう一度。設定はそのまま残る
・展開したフォルダは、入れた後は消してよい（消すための uninstall.cmd と非常用の restore-taskbar.cmd は
  %LOCALAPPDATA%\Programs\Frostbar にも入っている）

■ 消し方
%LOCALAPPDATA%\Programs\Frostbar\uninstall.cmd をダブルクリック（展開したフォルダの uninstall.cmd でも同じ）。
タスクバーを元に戻し、ログイン時の起動・スタートメニューの「Frostbar」・プログラムのフォルダを消す。
設定とログ（%APPDATA%\Frostbar と %LOCALAPPDATA%\Frostbar）は残る。これも消すときは、コマンド プロンプトで
  "%LOCALAPPDATA%\Programs\Frostbar\uninstall.cmd" -RemoveSettings

■ タスクバーが戻らないとき（非常用）
%LOCALAPPDATA%\Programs\Frostbar\restore-taskbar.cmd をダブルクリック（展開したフォルダにもある）。
Frostbar が動いていなくても、Windows のタスクバーを表示し、「タスクバーを自動的に隠す」を Frostbar が使う前の値に戻す。
Frostbar が固まっているときは、先にタスク マネージャーで Frostbar を終了してから。

■ ライセンス
Frostbar は MIT ライセンス（LICENSE）。同梱している他者のソフトウェアのライセンスは app\THIRD-PARTY-NOTICES.txt
（入れた後は %LOCALAPPDATA%\Programs\Frostbar\THIRD-PARTY-NOTICES.txt、設定の「一般 › 情報」からも開ける）。
Frostbar は個人が作った非公式のソフトウェアで、Apple Inc. とは関係ありません。macOS は Apple Inc. の商標です。
https://github.com/ternando0831-lang/Frostbar


==============================================================================
Frostbar - a shell that gives Windows 11 a macOS-style menu bar, Dock, search,
app grid, Control Center and window overview
==============================================================================

Requirements: Windows 11 22H2 (build 22621) or later, x64. .NET is included.
The lens effect needs Windows 11 24H2 or later. Tested on one PC with 26H2.

Install
1. If the zip came from the internet: right-click it > Properties > tick
   Unblock > OK (otherwise Windows warns each time a file from it opens).
2. Right-click the zip > Extract All.
3. Double-click setup.cmd. If "Windows protected your PC" appears, choose
   More info > Run anyway (the app is not signed).
4. Frostbar goes to %LOCALAPPDATA%\Programs\Frostbar, gets a Start menu entry
   and starts. A welcome window asks before the Windows taskbar is hidden.
To update, run the new zip's setup.cmd; settings are kept.

Uninstall
Double-click %LOCALAPPDATA%\Programs\Frostbar\uninstall.cmd. Settings and logs
(%APPDATA%\Frostbar, %LOCALAPPDATA%\Frostbar) stay; to remove them too, run
  "%LOCALAPPDATA%\Programs\Frostbar\uninstall.cmd" -RemoveSettings

If the taskbar does not come back
Double-click %LOCALAPPDATA%\Programs\Frostbar\restore-taskbar.cmd. It works
without Frostbar running; end a frozen Frostbar in Task Manager first.

License: MIT (LICENSE). Third-party software: app\THIRD-PARTY-NOTICES.txt.
Frostbar is an unofficial project, not affiliated with Apple Inc. macOS is a
trademark of Apple Inc.
