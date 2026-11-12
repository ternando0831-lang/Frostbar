# Starts the installed Frostbar again (after the specs, which need it quit), unless a Frostbar runs already: a second start
# hands "activate" to the running shell, which opens the settings window on the user's screen.
# Run from a Claude session, it runs itself again outside Claude's job (outside.ps1): a shell started from there would
# use Claude's copy of AppData and be closed by Claude's updates (2026-10-19).
# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less files as the ANSI code page.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File start.ps1 [-Destination <dir>]
# -Outside (internal): already run again outside another app's job.

param(
  [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Frostbar'),
  [switch]$Outside
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'outside.ps1')
$family = Get-PackageCopy
if ($family -and -not $Outside) { Invoke-Outside $PSCommandPath $PSBoundParameters $family; exit $OutsideExitCode }
if ($family) { Write-Warning ('still in the ' + $family + ' package''s copy of AppData: the shell started here would use it') }

$exe = Join-Path $Destination 'Frostbar.exe'
if (-not (Test-Path $exe)) { throw ('no Frostbar in ' + $Destination) }
# A watchdog (--watchdog) left a few minutes after its shell quit is not a shell running (it kept the start from
# happening; 2026-11-06 review).
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'Frostbar.exe'" | Where-Object { $_.CommandLine -notmatch '--watchdog' })
if ($running.Count -gt 0) {
  Write-Output ('a Frostbar runs already (pid ' + (($running | ForEach-Object { $_.ProcessId }) -join ', ') + '): not started')
  exit 0
}
$p = Start-Process -FilePath $exe -WorkingDirectory $Destination -PassThru
Write-Output ('started: pid ' + $p.Id)
