param(
    [string]$PackageDirectory = (Join-Path $PSScriptRoot '..\Windows-Current'),
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\MarketplaceHub'),
    [switch]$SkipShortcut
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackageDirectory).Path
$exeSource = Join-Path $package 'TrMarketplaceHubDesktop.exe'
if (-not (Test-Path $exeSource)) { throw 'Self-contained publish klasörü ve TrMarketplaceHubDesktop.exe bulunamadı.' }

$releaseManifestPath = Join-Path $package 'release-manifest.json'
if (-not (Test-Path $releaseManifestPath)) { throw 'release-manifest.json bulunamadı; doğrulanmamış paket kurulmayacak.' }
$releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
if ($releaseManifest.product -ne 'MarketplaceHub' -or $releaseManifest.rid -ne 'win-x64' -or -not $releaseManifest.selfContained) {
    throw 'Release manifest MarketplaceHub win-x64 self-contained paketi doğrulamıyor.'
}

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
Copy-Item -Path (Join-Path $package '*') -Destination $InstallDirectory -Recurse -Force

$installedExe = Join-Path $InstallDirectory 'TrMarketplaceHubDesktop.exe'
if (-not (Test-Path $installedExe)) { throw 'Kurulum sonrası EXE bulunamadı.' }

$commit = if ($releaseManifest.commit) { [string]$releaseManifest.commit } else { 'unknown' }
Set-Content -Path (Join-Path $InstallDirectory 'installed-version.txt') -Value $commit -Encoding UTF8

if (-not $SkipShortcut) {
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\MarketplaceHub.lnk'
    $shortcutDirectory = Split-Path -Parent $shortcutPath
    New-Item -ItemType Directory -Force -Path $shortcutDirectory | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $installedExe
    $shortcut.WorkingDirectory = $InstallDirectory
    $shortcut.Save()
}

Write-Output "Kurulum tamamlandı: $InstallDirectory"
Write-Output 'LocalAppData\MonoBridgeDesktop kullanıcı veri klasörü uygulama verisi olarak korunur ve kurulum tarafından silinmez.'
