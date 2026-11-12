# Installs Frostbar from the package it came in (package.ps1: a self-contained build, no .NET to install) for the user
# who runs it: the program in %LOCALAPPDATA%\Programs\Frostbar with the uninstall and emergency restore scripts next to
# it, a Start menu shortcut, then starts it (the first start shows the welcome, which asks before hiding the
# taskbar). A copy there that is running is quit first (it puts the taskbar back). Settings, state and logs are kept:
# they live in %APPDATA%\Frostbar and %LOCALAPPDATA%\Frostbar.
# Run from a Claude session, it runs itself again outside Claude's job (outside.ps1), as install.ps1 does.
# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less files as the ANSI code page.
#
# Usage:  setup.cmd [options]   or   powershell -NoProfile -ExecutionPolicy Bypass -File setup.ps1 [options]
# Options: -Destination <dir> (another folder: a copy there is a development build, which does not hide the
# taskbar), -NoShortcut, -NoStart. -Outside (internal): already run again outside another app's job.

param(
  [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Frostbar'),
  [switch]$NoShortcut,
  [switch]$NoStart,
  [switch]$Outside
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'outside.ps1')
$family = Get-PackageCopy
if ($family -and -not $Outside) { Invoke-Outside $PSCommandPath $PSBoundParameters $family; exit $OutsideExitCode }
if ($family) { Write-Warning ('still in the ' + $family + ' package''s copy of AppData: the shell started here would use it') }

# The package: scripts\install\setup.ps1 (here), app\ (the program), the .cmd entries and README.txt at its top.
$package = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$app = Join-Path $package 'app'
if (-not (Test-Path (Join-Path $app 'Frostbar.exe'))) { throw ('no app folder with Frostbar.exe in the package: ' + $app) }

# Windows 11 22H2 (build 22621) or later; built for x64 (an ARM64 PC runs it under emulation, not tested).
$build = [Environment]::OSVersion.Version.Build
if ($build -lt 22621) { throw ('Frostbar needs Windows 11 22H2 (build 22621) or later; this PC is build ' + $build) }
$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
if ($arch -eq 'ARM64') { Write-Warning 'this is an ARM64 PC: the x64 build runs under emulation (not tested)' }

$exe = Join-Path $Destination 'Frostbar.exe'
# The folder is mirrored (files not in the package are removed): it must be ours or empty.
if ((Test-Path $Destination) -and -not (Test-Path $exe) -and (Get-ChildItem -Force $Destination | Select-Object -First 1)) {
  throw ('the destination holds other files and no Frostbar.exe: ' + $Destination)
}

# 1. Quit the copy there if it runs (it puts the taskbar back on the way out). Its watchdog (--watchdog) can stay up to
# 3 minutes after it: not the shell, and stopped once the shell has quit.
$mine = @(Get-CimInstance Win32_Process -Filter "Name = 'Frostbar.exe'" | Where-Object { $_.ExecutablePath -eq $exe })
$shells = @($mine | Where-Object { $_.CommandLine -notmatch '--watchdog' })
if ($shells.Count -gt 0) {
  Write-Output 'quitting the installed Frostbar...'
  Start-Process -FilePath $exe -ArgumentList '--quit' -Wait
  foreach ($s in $shells) {
    $p = Get-Process -Id $s.ProcessId -ErrorAction SilentlyContinue
    if ($p -and -not $p.WaitForExit(15000)) { throw 'the installed Frostbar did not quit' }
  }
}
foreach ($w in @(Get-CimInstance Win32_Process -Filter "Name = 'Frostbar.exe'" | Where-Object { $_.ExecutablePath -eq $exe -and $_.CommandLine -match '--watchdog' })) {
  $p = Get-Process -Id $w.ProcessId -ErrorAction SilentlyContinue
  if ($p -and -not $p.WaitForExit(5000)) { Stop-Process -Id $w.ProcessId -Force; Write-Output 'stopped the old watchdog' }
}

# 2. Into place: the program, then the scripts and the notes next to it (uninstall and the emergency restore stay
# there when the package is gone).
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
& robocopy $app $Destination /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw ('copying the program failed: robocopy ' + $LASTEXITCODE) }
& robocopy (Join-Path $package 'scripts') (Join-Path $Destination 'scripts') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw ('copying the scripts failed: robocopy ' + $LASTEXITCODE) }
foreach ($f in @('uninstall.cmd', 'restore-taskbar.cmd', 'README.txt')) { Copy-Item (Join-Path $package $f) $Destination -Force }
# A package downloaded keeps the internet's mark on its files: SmartScreen would ask about Frostbar.exe at each start.
Get-ChildItem -Recurse -File $Destination | Unblock-File
Write-Output ('installed in ' + $Destination)

# 3. A Start menu shortcut (the Dock and the app grid find it there too).
if (-not $NoShortcut) {
  $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Frostbar.lnk'
  $shell = New-Object -ComObject WScript.Shell
  $s = $shell.CreateShortcut($lnk)
  $s.TargetPath = $exe
  $s.WorkingDirectory = $Destination
  $s.Description = 'Frostbar'
  $s.Save()
  Write-Output ('shortcut: ' + $lnk)
}

# 4. Start it.
if (-not $NoStart) {
  Start-Process -FilePath $exe -WorkingDirectory $Destination
  Write-Output 'started'
}
