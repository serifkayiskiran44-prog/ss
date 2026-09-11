param([string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\MonoBridgeDesktop'))
$ErrorActionPreference = 'Stop'
$shortcut = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MonoBridge Desktop.lnk'
if (Test-Path $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
if (Test-Path $InstallDirectory) { Remove-Item -LiteralPath $InstallDirectory -Recurse -Force }
Write-Output 'Uygulama dosyaları kaldırıldı. LocalAppData\MonoBridgeDesktop kullanıcı verileri korunmuştur.'
