# Frostbar

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
