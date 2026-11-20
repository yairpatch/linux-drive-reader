# Takes the screenshots used on the website (site/assets) from demo disk images.
#   1. wsl -u root -- bash tools/make-demo-images.sh <folder>      (builds laptop.img and encrypted-laptop.img)
#   2. powershell -File tools/website-screenshots.ps1 -Exe <built LinuxDriveReader.exe> -Demo <folder> -Out site/assets
# The app runs with LDR_DEMO=1 (this PC's own disks and the administrator notice are hidden).
# The File Explorer screenshot's navigation pane is covered, since it shows this PC's pinned folders.

param([string]$Exe, [string]$Demo, [string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Shot {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int L, T, R, B; }
}
"@
[Shot]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function ById($id) { $script:root.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id))) }
function Press($id) { (ById $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Tree($like) {
  $t = $script:root.FindAll($T::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like $like } | Select-Object -First 1
  if (-not $t) { throw "tree entry '$like' not found" }
  $item = $t; while ($item -and $item.Current.ControlType.ProgrammaticName -ne 'ControlType.TreeItem') { $item = $walker.GetParent($item) }
  $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Snap($hwnd, $name) {
  $r = New-Object Shot+RECT; [Shot]::GetWindowRect($hwnd, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  [Shot]::PrintWindow($hwnd, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); "saved $name"
}
function Start-App($theme, $image, $path) {
  $env:LDR_DEMO = '1'; $env:LDR_THEME = $theme; $env:LDR_DEMO_PATH = $path
  $script:p = Start-Process $Exe -ArgumentList ('"' + (Join-Path $Demo $image) + '"') -PassThru
  for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; $script:p.Refresh(); if ($script:p.HasExited -or $script:p.MainWindowHandle -ne 0) { break } }
  Start-Sleep -Seconds 3; $script:p.Refresh()
  $script:root = $A::FromHandle($script:p.MainWindowHandle)
}
function Stop-App { if (-not $script:p.HasExited) { $script:p.CloseMainWindow() | Out-Null; $script:p.WaitForExit(8000) | Out-Null; if (-not $script:p.HasExited) { $script:p.Kill() } } }

$settingsFile = Join-Path $env:LOCALAPPDATA 'LinuxDriveReader\settings.json'
$backup = if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw } else { $null }
New-Item -ItemType Directory -Force (Split-Path $settingsFile) | Out-Null
'{"Width":1180,"Height":720,"Left":60,"Top":60,"Sidebar":330,"Theme":"System","ShowHiddenFiles":false,"ShowPermissions":true,"ShowWindowsDisk":true,"VerifyIntegrity":true,"KeepDates":true,"OfferOpenFolder":true,"DoubleClickOpens":true,"OpenExplorerAfterMount":false}' | Set-Content $settingsFile
try {
  foreach ($theme in 'Dark', 'Light') {
    Start-App $theme 'laptop.img' '@home/alex'
    Snap $script:p.MainWindowHandle "browse-$($theme.ToLower()).png"
    Stop-App
  }
  # encrypted: the unlock panel, then the volumes inside
  Start-App 'Light' 'encrypted-laptop.img' 'alex/Pictures/2026'
  Tree 'Partition 2*'; Start-Sleep -Seconds 2
  (ById 'UnlockPassword').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('demo')
  Press 'UnlockButton'; Start-Sleep -Seconds 5
  Tree 'demovg/home*'; Start-Sleep -Seconds 3
  Snap $script:p.MainWindowHandle 'encrypted-light.png'
  Stop-App
  # mounted as a drive, plus File Explorer showing it
  Start-App 'Dark' 'laptop.img' '@home/alex'
  $before = (Get-PSDrive -PSProvider FileSystem).Name
  Press 'MountDrive'; Start-Sleep -Seconds 5
  Snap $script:p.MainWindowHandle 'mounted-dark.png'
  $letter = (Get-PSDrive -PSProvider FileSystem).Name | Where-Object { $before -notcontains $_ } | Select-Object -First 1
  if ($letter) {
    $shell = New-Object -ComObject Shell.Application
    $known = @($shell.Windows() | ForEach-Object { $_.HWND })
    Start-Process explorer.exe "$($letter):\@home\alex"
    $win = $null
    for ($i = 0; $i -lt 30 -and -not $win; $i++) { Start-Sleep -Milliseconds 500; $win = $shell.Windows() | Where-Object { $known -notcontains $_.HWND -and $_.LocationURL -like "file:///$($letter):*" } | Select-Object -First 1 }
    if ($win) {
      Start-Sleep -Seconds 3; Snap ([IntPtr]$win.HWND) 'explorer.png'; $win.Quit()
      $path = Join-Path $Out 'explorer.png'; $src = [System.Drawing.Image]::FromFile($path); $bmp = New-Object System.Drawing.Bitmap $src; $src.Dispose()
      $g = [System.Drawing.Graphics]::FromImage($bmp); $s = $bmp.Width / 1424.0
      $g.FillRectangle((New-Object System.Drawing.SolidBrush $bmp.GetPixel([int](100*$s), [int](650*$s))), [int](14*$s), [int](282*$s), [int](172*$s), [int](420*$s))
      $g.Dispose(); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    } else { 'explorer window not found' }
  }
  Press 'EjectDrive'; Start-Sleep -Seconds 3
  Press 'SettingsButton'; Start-Sleep -Seconds 2
  Snap $script:p.MainWindowHandle 'settings-dark.png'
  Stop-App
} finally {
  Get-Process LinuxDriveReader -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force -ErrorAction SilentlyContinue
  if ($backup) { Set-Content $settingsFile $backup -NoNewline } else { Remove-Item $settingsFile -ErrorAction SilentlyContinue }
  cmd /c 'net use' | Select-String '127.0.0.1'
}
