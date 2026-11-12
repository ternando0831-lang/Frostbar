# Emergency restore for the Windows taskbar (Frostbar shell / M0 spikes).
# Works without Frostbar.exe: shows Explorer's taskbar windows again, removes the transparent (layered)
# style if it was added, and puts the auto-hide setting back to the value saved before hiding.
# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less files as the ANSI code page.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File restore-taskbar.ps1 [-AutoHide on|off]
# -Outside (internal): already run again outside another app's job.

param([string]$AutoHide = '', [switch]$Outside)

# Run from a Claude session, the saved state read below would be Claude's copy of it (2026-10-19): without -AutoHide,
# run again outside Claude's job (..\install\outside.ps1). If that cannot be done, go on here.
$helper = Join-Path $PSScriptRoot '..\install\outside.ps1'
if ($AutoHide -eq '' -and -not $Outside -and (Test-Path $helper)) {
  try {
    . $helper
    $family = Get-PackageCopy
    if ($family) { Invoke-Outside $PSCommandPath $PSBoundParameters $family; exit $OutsideExitCode }
  } catch { Write-Output ('could not run outside another app''s job (' + $_ + '): going on here; the saved state read may be its copy') }
}

$src = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class EmergencyTaskbar {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint cb; public uint edge; public RECT rc; public IntPtr lParam; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
  [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte a, uint f);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] struct ABD3264 { public int cbSize; public int hWnd; public uint cb; public uint edge; public RECT rc; public long lParam; }
  [StructLayout(LayoutKind.Sequential)] struct MSG3264 { public ABD3264 abd; public uint dwMessage; public uint pad1; public long hShared; public uint pid; public uint pad3; }
  [StructLayout(LayoutKind.Sequential)] struct COPYDATA { public IntPtr dwData; public int cbData; public IntPtr lpData; }
  [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, ref COPYDATA l, uint flags, uint timeout, out IntPtr result);

  // ABM_SETSTATE straight to Explorer's taskbar, as the shell sends it (AppBarMessenger): SHAppBarMessage goes to the
  // first Shell_TrayWnd, which is the shell's own tray window while a shell (perhaps a hung one) is still there.
  static bool SetState(IntPtr tray, int state) {
    var abd = new ABD3264(); abd.cbSize = Marshal.SizeOf(typeof(ABD3264)); abd.hWnd = (int)tray.ToInt64(); abd.lParam = state;
    var m = new MSG3264(); m.abd = abd; m.dwMessage = 10; m.pid = (uint)Process.GetCurrentProcess().Id;
    IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MSG3264)));
    try {
      Marshal.StructureToPtr(m, buf, false);
      var cds = new COPYDATA(); cds.cbData = Marshal.SizeOf(typeof(MSG3264)); cds.lpData = buf;
      IntPtr r;
      return SendMessageTimeout(tray, 0x4A, tray, ref cds, 2, 5000, out r) != IntPtr.Zero;
    } finally { Marshal.FreeHGlobal(buf); }
  }

  public static List<IntPtr> Bars() {
    var list = new List<IntPtr>();
    EnumWindows(delegate (IntPtr h, IntPtr l) {
      var sb = new StringBuilder(64); GetClassName(h, sb, 64);
      string c = sb.ToString();
      if (c != "Shell_TrayWnd" && c != "Shell_SecondaryTrayWnd") return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      try { if (Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant() != "explorer") return true; } catch { return true; }
      list.Add(h); return true;
    }, IntPtr.Zero);
    return list;
  }

  public static string Restore(int abmState) {
    var log = new StringBuilder();
    IntPtr primary = IntPtr.Zero;
    foreach (var h in Bars()) {
      var sb = new StringBuilder(64); GetClassName(h, sb, 64);
      if (sb.ToString() == "Shell_TrayWnd") primary = h;
      long ex = GetWindowLongPtr(h, -20).ToInt64();
      if ((ex & 0x80000) != 0) {
        SetLayeredWindowAttributes(h, 0, 255, 2);
        SetWindowLongPtr(h, -20, new IntPtr(ex & ~0x80000L));
        log.AppendLine(sb + ": removed transparent layer");
      }
      if (!IsWindowVisible(h)) {
        SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x40 | 0x1 | 0x2 | 0x4 | 0x10);
        log.AppendLine(sb + ": shown");
      }
      // Explorer's taskbar is always-on-top (WS_EX_TOPMOST); hiding it via HWND_BOTTOM drops that bit for good.
      if ((GetWindowLongPtr(h, -20).ToInt64() & 0x8) == 0) {
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
        log.AppendLine(sb + ": always-on-top again");
      }
    }
    if (primary != IntPtr.Zero && abmState >= 0) {
      if (SetState(primary, abmState)) log.AppendLine("auto-hide state set to " + abmState);
      else {
        // Explorer did not answer: the old way, through whichever taskbar window is first.
        var d = new APPBARDATA(); d.cbSize = Marshal.SizeOf(d); d.hWnd = primary; d.lParam = new IntPtr(abmState);
        SHAppBarMessage(10, ref d);
        log.AppendLine("auto-hide state set to " + abmState + " (Explorer did not answer directly: sent the old way)");
      }
    }
    if (primary == IntPtr.Zero) log.AppendLine("Explorer taskbar not found (is explorer.exe running?)");
    return log.ToString();
  }
}
'@
Add-Type -TypeDefinition $src

$state = -1
if ($AutoHide -eq 'on') { $state = 1 } elseif ($AutoHide -eq 'off') { $state = 0 }
if ($state -lt 0) {
  $candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Frostbar\state.json'),
    (Join-Path $PSScriptRoot '..\..\spikes\out\taskbar-state.json')
  )
  foreach ($c in $candidates) {
    if (Test-Path $c) {
      try {
        $j = Get-Content -Raw $c | ConvertFrom-Json
        if ($null -ne $j.AbmState) { $state = [int]$j.AbmState; Write-Output ('saved auto-hide state from ' + $c + ': ' + $state); break }
        if ($null -ne $j.taskbar -and $j.taskbar.onlyTopmost -eq $true) { Write-Output ('saved by a run that did not hide the taskbar (' + $c + '): auto-hide left as it is'); break }
        if ($null -ne $j.taskbar -and $null -ne $j.taskbar.abmState) { $state = [int]$j.taskbar.abmState; Write-Output ('saved auto-hide state from ' + $c + ': ' + $state); break }
      } catch { }
    }
  }
}
if ($state -lt 0) { Write-Output 'no saved auto-hide state found: leaving the auto-hide setting as it is' }
Write-Output ([EmergencyTaskbar]::Restore($state))
