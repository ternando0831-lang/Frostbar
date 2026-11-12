# Installs Frostbar for daily use: a Release build in %LOCALAPPDATA%\Programs\Frostbar (apart from the development
# build, so a running copy never locks the build output), a Start menu shortcut, then starts it (the first start
# shows the welcome, which asks before hiding the taskbar). An installed copy that is running is quit first (it puts
# the taskbar back). Settings, state and logs are kept: they live in %APPDATA%\Frostbar and %LOCALAPPDATA%\Frostbar.
# Run from a Claude session, it runs itself again outside Claude's job (outside.ps1): a shell started from there would
# use Claude's copy of AppData and be closed by Claude's updates (2026-10-19).
# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less files as the ANSI code page.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 [-Destination <dir>] [-NoShortcut] [-NoStart]
# -Outside (internal): already run again outside another app's job.

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
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'src\Frostbar.App\Frostbar.App.csproj'
$exe = Join-Path $Destination 'Frostbar.exe'

# The folder is mirrored (files not in the build are removed): it must be ours or empty.
if ((Test-Path $Destination) -and -not (Test-Path $exe) -and (Get-ChildItem -Force $Destination | Select-Object -First 1)) {
  throw ('the destination holds other files and no Frostbar.exe: ' + $Destination)
}

# 1. Quit the installed copy if it runs (it restores the taskbar on the way out). Its watchdog (--watchdog) can stay
# up to 3 minutes after it, asking Explorer to keep its taskbar on top: not the shell, and stopped once the shell
# has quit (counted as the shell, it failed the install with 'did not quit').
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

# 2. Build: framework-dependent (the .NET 10 Desktop Runtime), ReadyToRun for a quick start.
$staging = Join-Path $env:TEMP ('frostbar-publish-' + [guid]::NewGuid().ToString('N'))
Write-Output 'building...'
& dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $staging --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw ('dotnet publish failed: ' + $LASTEXITCODE) }

# 3. Into place.
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
& robocopy $staging $Destination /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw ('copying failed: robocopy ' + $LASTEXITCODE) }
Remove-Item -Recurse -Force $staging
Write-Output ('installed in ' + $Destination)

# 4. A Start menu shortcut (the Dock and the app grid find it there too).
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

# 5. Start it.
if (-not $NoStart) {
  Start-Process -FilePath $exe
  Write-Output 'started'
}
