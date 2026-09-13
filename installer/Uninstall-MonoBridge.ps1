param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\MarketplaceHub'),
    [switch]$SkipShortcut
)

$ErrorActionPreference = 'Stop'
if (-not $SkipShortcut) {
    $shortcut = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MarketplaceHub.lnk'
    if (Test-Path $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
}
if (Test-Path $InstallDirectory) { Remove-Item -LiteralPath $InstallDirectory -Recurse -Force }
Write-Output 'MarketplaceHub uygulama dosyaları kaldırıldı. LocalAppData\MonoBridgeDesktop kullanıcı verileri korunmuştur.'
