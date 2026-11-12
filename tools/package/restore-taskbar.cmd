@echo off
rem Emergency: shows the Windows taskbar again and puts its auto-hide setting back, without Frostbar (README.txt).
cd /d "%TEMP%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\emergency\restore-taskbar.ps1" %* & echo. & pause & exit /b
