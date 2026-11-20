# Builds the files to hand out: one self-contained exe (no .NET needed on the target PC), under two names.
#   dist\LinuxDriveReaderSetup.exe   run it to install (Start menu entry, "Installed apps" entry, uninstaller)
#   dist\LinuxDriveReader.exe        the same program, runs without installing
# Scripted install:   LinuxDriveReaderSetup.exe --install [--peruser] [--desktop] --quiet
# Scripted uninstall: "<install folder>\LinuxDriveReader.exe" --uninstall --quiet
#   -Version 1.2.3 overrides the version written in the project file (the GitHub workflow passes the tag).
param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
$options = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none')
if ($Version) { $options += "-p:Version=$Version" }
& dotnet publish (Join-Path $root 'src\LinuxDriveReader') -c Release -r win-x64 --self-contained true -o $dist -nologo -v q @options
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Copy-Item (Join-Path $dist 'LinuxDriveReader.exe') (Join-Path $dist 'LinuxDriveReaderSetup.exe')
Get-ChildItem $dist | ForEach-Object { '{0,-32} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
