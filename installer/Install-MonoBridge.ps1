param(
    [string]$PackageDirectory = (Join-Path $PSScriptRoot '..\Windows-M19-Dashboard'),
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\MonoBridgeDesktop')
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackageDirectory).Path
if (-not (Test-Path (Join-Path $package 'TrMarketplaceHubDesktop.exe'))) { throw 'Self-contained publish klasörü ve TrMarketplaceHubDesktop.exe bulunamadı.' }
New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
Copy-Item -Path (Join-Path $package '*') -Destination $InstallDirectory -Recurse -Force
Set-Content -Path (Join-Path $InstallDirectory 'installed-version.txt') -Value '1.0.0' -Encoding UTF8
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MonoBridge Desktop.lnk'))
$shortcut.TargetPath = Join-Path $InstallDirectory 'TrMarketplaceHubDesktop.exe'
$shortcut.WorkingDirectory = $InstallDirectory
$shortcut.Save()
Write-Output "Kurulum tamamlandı: $InstallDirectory"
Write-Output 'LocalAppData\MonoBridgeDesktop veri klasörü korunur ve silinmez.'
