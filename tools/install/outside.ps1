# Dot-sourced by install.ps1, uninstall.ps1, start.ps1 and tools\emergency\restore-taskbar.ps1.
# The Claude desktop app (an MSIX package) keeps the processes its sessions start in its job, with no package of their
# own: their new files in %APPDATA% and %LOCALAPPDATA% go to its copy (%LOCALAPPDATA%\Packages\Claude_...\LocalCache),
# and its updates close them. A Frostbar started from there reads and writes that copy and is closed by Claude's update
# (2026-10-19: the taskbar was left hidden). These scripts run themselves again outside it: a process that WMI starts
# (Win32_Process.Create) has WmiPrvSE for a parent, is not in the caller's job, runs on the user's desktop and gets the
# user's own environment. Breaking away from the job (CREATE_BREAKAWAY_FROM_JOB) is refused there.
# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less files as the ANSI code page.

if (-not ('FrostbarPackageCopy' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class FrostbarPackageCopy {
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder sb, uint size, uint flags);
  // Where a new file in the folder really goes: the final path of a hidden file made and deleted at once (null if unknown).
  public static string WhereNewFilesGo(string folder) {
    string path = System.IO.Path.Combine(folder, ".where-" + Guid.NewGuid().ToString("N"));
    // GENERIC_WRITE | DELETE, share all, CREATE_NEW, FILE_FLAG_DELETE_ON_CLOSE | FILE_ATTRIBUTE_HIDDEN
    using (var h = CreateFileW(path, 0x40000000 | 0x00010000, 7, IntPtr.Zero, 1, 0x04000000 | 0x2, IntPtr.Zero)) {
      if (h.IsInvalid) return null;
      var sb = new StringBuilder(1024);
      uint n = GetFinalPathNameByHandleW(h, sb, 1024, 0);
      return (n == 0 || n >= 1024) ? null : sb.ToString();
    }
  }
}
'@
}

# The package whose copy of AppData this process writes to (from a Claude session: Claude_...), or $null.
function Get-PackageCopy {
  $went = [FrostbarPackageCopy]::WhereNewFilesGo($env:LOCALAPPDATA)
  if ($went -and $went -match '\\Packages\\([^\\]+)\\LocalCache(\\|$)') { return $Matches[1] }
  return $null
}

# Runs $Script again outside the job with its bound parameters and -Outside, and passes its output on (through a file in
# %TEMP%, which is not part of the copy). Its exit code is left in $OutsideExitCode (what a function returns is its output).
# Use:  $family = Get-PackageCopy
#       if ($family -and -not $Outside) { Invoke-Outside $PSCommandPath $PSBoundParameters $family; exit $OutsideExitCode }
function Invoke-Outside([string]$Script, $Bound, [string]$Family) {
  $script:OutsideExitCode = 1
  $log = Join-Path $env:TEMP ('frostbar-outside-' + [guid]::NewGuid().ToString('N') + '.log')
  $line = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $Script + '" -Outside'
  foreach ($k in $Bound.Keys) {
    $v = $Bound[$k]
    if ($v -is [System.Management.Automation.SwitchParameter]) { if ($v.IsPresent) { $line += ' -' + $k } }
    else {
      if (([string]$v).IndexOfAny([char[]]'"%') -ge 0) { throw ('cannot pass -' + $k + ' outside (it holds a quote or a percent sign): ' + $v) }
      # Backslashes before the closing quote are doubled, or they would take the quote ("C:\dir\" read as C:\dir").
      $line += ' -' + $k + ' "' + (([string]$v) -replace '(\\+)$', '$1$1') + '"'
    }
  }
  # cmd /s strips the outer quotes and keeps the rest as it is; it writes the output (and the errors) to the file, in
  # UTF-8 (chcp: the hidden console's code page, which PowerShell and dotnet write in; Japanese errors came out garbled
  # when read with this process's code page).
  $command = 'cmd.exe /d /s /c "chcp 65001 >nul & ' + $line + ' > "' + $log + '" 2>&1"'
  $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
  # Started in %TEMP%, not in the script's folder: the uninstaller removes that folder (the package's copy of it sits
  # in the program folder), and the cmd.exe here held it as its working folder until the end ("in use", 2026-10-20).
  # The scripts find their files from their own place ($PSScriptRoot), not the working folder.
  $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $command; CurrentDirectory = $env:TEMP; ProcessStartupInformation = $startup }
  if ($r.ReturnValue -ne 0) { throw ('could not start outside ' + $Family + ': Win32_Process.Create returned ' + $r.ReturnValue) }
  # Opened at once (the handle keeps its exit code).
  $p = $null
  try { $p = [System.Diagnostics.Process]::GetProcessById([int]$r.ProcessId); $null = $p.Handle } catch { $p = $null }
  Write-Output ('this runs in the ' + $Family + ' package''s job and copy of AppData: running again outside it (pid ' + $r.ProcessId + ')')

  # Pass the output on as it comes, whole lines at a time.
  $enc = New-Object System.Text.UTF8Encoding($false)
  $pos = 0
  do {
    $done = ($null -eq $p) -or $p.WaitForExit(300)
    if (Test-Path $log) {
      $fs = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
      try {
        $fs.Position = $pos
        $buf = New-Object byte[] ([int]($fs.Length - $pos))
        $n = $fs.Read($buf, 0, $buf.Length)
      } finally { $fs.Dispose() }
      $end = $n
      if (-not $done -and $n -gt 0) { $end = [Array]::LastIndexOf($buf, [byte]10, $n - 1) + 1 }
      if ($end -gt 0) {
        $text = $enc.GetString($buf, 0, $end).TrimEnd("`r", "`n")
        foreach ($l in ($text -split "`r?`n")) { Write-Output $l }
        $pos += $end
      }
    }
  } until ($done)
  Remove-Item $log -ErrorAction SilentlyContinue
  if ($null -eq $p) { Write-Output 'it finished before its exit code could be read'; return }
  $script:OutsideExitCode = $p.ExitCode
}
