# Uninstalls Frostbar: quits it (the taskbar comes back, and is put back even if it was not running), removes its
# sign-in start (only a Task Scheduler task, or a Run value an older version wrote, that starts this copy), its Start
# menu shortcut and its program folder. Settings, state and logs are kept unless -RemoveSettings.
# Run from a Claude session, it runs itself again outside Claude's job (outside.ps1): there, --restore-taskbar would
# read Claude's copy of the state, and the settings removed would be Claude's copy's (2026-10-19).
# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less files as the ANSI code page.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 [-Destination <dir>] [-RemoveSettings] [-NoRestore]
# -NoRestore (tests of a copy in another folder): skip putting the taskbar back from the saved state, which belongs
# to the installed copy. -Outside (internal): already run again outside another app's job.

param(
  [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Frostbar'),
  [switch]$RemoveSettings,
  [switch]$NoRestore,
  [switch]$Outside
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'outside.ps1')
$family = Get-PackageCopy
if ($family -and -not $Outside) { Invoke-Outside $PSCommandPath $PSBoundParameters $family; exit $OutsideExitCode }
if ($family) { Write-Warning ('still in the ' + $family + ' package''s copy of AppData: the state and settings here are its copy') }
$exe = Join-Path $Destination 'Frostbar.exe'
if (-not (Test-Path $exe)) { Write-Output ('no Frostbar in ' + $Destination) }

# 1. Quit it, then put the taskbar back from the saved state (does nothing if it is already back).
$mine = @(Get-CimInstance Win32_Process -Filter "Name = 'Frostbar.exe'" | Where-Object { $_.ExecutablePath -eq $exe })
$shells = @($mine | Where-Object { $_.CommandLine -notmatch '--watchdog' })
if ($shells.Count -gt 0) {
  Write-Output 'quitting Frostbar...'
  Start-Process -FilePath $exe -ArgumentList '--quit' -Wait
  foreach ($s in $shells) {
    $p = Get-Process -Id $s.ProcessId -ErrorAction SilentlyContinue
    if ($p) { [void]$p.WaitForExit(15000) }
  }
}
# Its watchdog can stay up to 3 minutes after it (keeping Explorer's taskbar on top), holding the exe and the folder:
# the folder could not be removed (2026-11-06 review). Stopped once the shell has quit.
foreach ($w in @(Get-CimInstance Win32_Process -Filter "Name = 'Frostbar.exe'" | Where-Object { $_.ExecutablePath -eq $exe -and $_.CommandLine -match '--watchdog' })) {
  $p = Get-Process -Id $w.ProcessId -ErrorAction SilentlyContinue
  if ($p -and -not $p.WaitForExit(5000)) { Stop-Process -Id $w.ProcessId -Force; Write-Output 'stopped the watchdog' }
}
if ((Test-Path $exe) -and -not $NoRestore) { Start-Process -FilePath $exe -ArgumentList '--restore-taskbar' -Wait }

# 2. The sign-in start: the Task Scheduler task (from 2026-10-21), and the Run value older versions wrote with Task
# Manager's mark for it.
$name = 'frostbar'
$scheduler = New-Object -ComObject Schedule.Service
$scheduler.Connect()
$root = $scheduler.GetFolder('\')
$task = $null
try { $task = $root.GetTask($name) } catch { }
if ($task) {
  $starts = @($task.Definition.Actions | Where-Object { $_.Type -eq 0 } | ForEach-Object { $_.Path.Trim().Trim('"') })
  if ($starts -icontains $exe) { $root.DeleteTask($name, 0); Write-Output 'sign-in task removed' }
}
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$item = Get-ItemProperty -Path $run -Name $name -ErrorAction SilentlyContinue
if ($item -and ($item.$name.Trim().Trim('"') -ieq $exe)) {
  Remove-ItemProperty -Path $run -Name $name
  $approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
  if (Get-ItemProperty -Path $approved -Name $name -ErrorAction SilentlyContinue) { Remove-ItemProperty -Path $approved -Name $name }
  Write-Output 'sign-in start removed'
}

# 3. The shortcut (only one that starts this copy) and the program folder.
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Frostbar.lnk'
if (Test-Path $lnk) {
  $target = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath
  if ($target -ieq $exe) { Remove-Item $lnk; Write-Output 'shortcut removed' }
}
if (Test-Path $exe) {
  # Not from inside it: this script can be the copy in the folder removed (the package puts it there), and run
  # again outside another app's job this process began in its own folder, which then could not be removed ("in use").
  Set-Location $env:TEMP
  [Environment]::CurrentDirectory = $env:TEMP
  Remove-Item -Recurse -Force $Destination
  Write-Output ('removed ' + $Destination)
}

# 4. Settings, state and logs, when asked.
if ($RemoveSettings) {
  foreach ($d in @((Join-Path $env:APPDATA 'Frostbar'), (Join-Path $env:LOCALAPPDATA 'Frostbar'))) {
    if (Test-Path $d) { Remove-Item -Recurse -Force $d; Write-Output ('removed ' + $d) }
  }
}
