@echo off
rem Installs Frostbar for this user (README.txt). Options go on to setup.ps1 (-Destination, -NoShortcut, -NoStart).
rem Out of the package's folder first: nothing holds it open. One line to the end: a batch file is read as it runs.
cd /d "%TEMP%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install\setup.ps1" %* & echo. & pause & exit /b
