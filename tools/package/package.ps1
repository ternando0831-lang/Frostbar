# Makes the package that installs Frostbar on another PC: a self-contained Release build (no .NET to install there,
# ReadyToRun for a quick start) with setup, uninstall and the emergency taskbar restore, as one zip:
#   dist\Frostbar-<version>-<commit>-win-x64.zip
#     setup.cmd  uninstall.cmd  restore-taskbar.cmd  README.txt  LICENSE
#     app\                         the program (and THIRD-PARTY-NOTICES.txt)
#     scripts\install\             setup.ps1, uninstall.ps1, outside.ps1
#     scripts\emergency\           restore-taskbar.ps1
# The scripts keep the repository's layout (restore-taskbar.ps1 finds ..\install\outside.ps1). The commit gets
# "-dirty" when src has changes not committed (the build is not that commit's).
# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less files as the ANSI code page.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File package.ps1 [-Output <dir>]

param([string]$Output = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($Output -eq '') { $Output = Join-Path $repo 'dist' }
$project = Join-Path $repo 'src\Frostbar.App\Frostbar.App.csproj'

[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$commit = (& git -C $repo rev-parse --short HEAD).Trim()
if (& git -C $repo status --porcelain -- src) { $commit += '-dirty' }
$name = 'Frostbar-' + $version + '-' + $commit + '-win-x64'

$staging = Join-Path $env:TEMP ('frostbar-package-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $staging $name
$app = Join-Path $root 'app'
try {
  Write-Output ('building ' + $name + ' (self-contained, ReadyToRun)...')
  # ContinuousIntegrationBuild: the source and PDB paths in the DLLs and PDBs as /_/..., not the folder built in
  # (which names the Windows account).
  & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:ContinuousIntegrationBuild=true -o $app --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { throw ('dotnet publish failed: ' + $LASTEXITCODE) }
  if (-not (Test-Path (Join-Path $app 'Frostbar.exe'))) { throw 'the build has no Frostbar.exe' }

  $install = Join-Path $root 'scripts\install'
  $emergency = Join-Path $root 'scripts\emergency'
  New-Item -ItemType Directory -Force -Path $install, $emergency | Out-Null
  Copy-Item (Join-Path $PSScriptRoot 'setup.ps1') $install
  Copy-Item (Join-Path $repo 'tools\install\uninstall.ps1') $install
  Copy-Item (Join-Path $repo 'tools\install\outside.ps1') $install
  Copy-Item (Join-Path $repo 'tools\emergency\restore-taskbar.ps1') $emergency

  # The double-click entries with CRLF line ends (cmd.exe reads a batch file line by line), and the notes.
  foreach ($f in @('setup.cmd', 'uninstall.cmd', 'restore-taskbar.cmd')) {
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $f)) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText((Join-Path $root $f), $text, (New-Object Text.UTF8Encoding($false)))
  }
  Copy-Item (Join-Path $PSScriptRoot 'README.txt') $root
  Copy-Item (Join-Path $repo 'LICENSE') $root

  New-Item -ItemType Directory -Force -Path $Output | Out-Null
  $zip = Join-Path $Output ($name + '.zip')
  if (Test-Path $zip) { Remove-Item $zip }
  # Entry by entry, with "/" between folders as the zip format has it (.NET Framework's CreateFromDirectory wrote "\").
  Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
  $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
  try {
    foreach ($file in Get-ChildItem -Recurse -File $root) {
      $entry = $name + '/' + $file.FullName.Substring($root.Length + 1).Replace('\', '/')
      [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
  }
  finally { $archive.Dispose() }
  $files = @(Get-ChildItem -Recurse -File $root).Count
  $unpacked = [math]::Round(((Get-ChildItem -Recurse -File $root | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
  $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
  Write-Output ('package: ' + $zip)
  Write-Output ('  ' + $size + ' MB (' + $files + ' files, ' + $unpacked + ' MB unpacked)')
  Write-Output ('  SHA256 ' + (Get-FileHash $zip -Algorithm SHA256).Hash)
}
finally {
  if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
}
