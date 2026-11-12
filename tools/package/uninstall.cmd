@echo off
rem Uninstalls Frostbar: puts the taskbar back, removes the sign-in start, the shortcut and the program folder (README.txt).
rem Settings and logs stay; add -RemoveSettings to remove them too.
rem Out of the program folder first (it is removed). This file goes with it: one line to the end, and "(goto)" ends
rem the batch without reading the file again (it said "the path cannot be found" and returned 1).
cd /d "%TEMP%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install\uninstall.ps1" %* & echo. & pause & (goto) 2>nul
