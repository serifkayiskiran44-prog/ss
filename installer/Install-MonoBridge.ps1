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

# Per-file integrity gate: verify every publish-manifest.json entry against the actual
# package bytes before anything is copied into the install directory. Any mismatch,
# missing/extra file, duplicate/case-collision or unsafe path fails closed without
# touching $InstallDirectory. publish-manifest.json/release-manifest.json themselves
# are excluded from the hashed set (they are written after it, by Build-Package.ps1),
# so this is a genuine external integrity check, not a circular self-hash.
$publishManifestPath = Join-Path $package 'publish-manifest.json'
if (-not (Test-Path $publishManifestPath)) { throw 'publish-manifest.json bulunamadı; doğrulanmamış paket kurulmayacak.' }
try { $publishManifest = Get-Content -LiteralPath $publishManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop }
catch { throw 'publish-manifest.json ayrıştırılamadı (bozuk JSON); kurulum durduruldu.' }
if ($publishManifest.product -ne 'MarketplaceHub' -or $publishManifest.rid -ne 'win-x64' -or -not $publishManifest.selfContained) {
    throw 'publish-manifest.json MarketplaceHub win-x64 self-contained paketi doğrulamıyor.'
}
$entries = @($publishManifest.files)
if ($entries.Count -eq 0) { throw 'publish-manifest.json dosya listesi boş; kurulum durduruldu.' }

$seenKeys = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($entry in $entries) {
    $relativePath = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($relativePath)) { throw 'publish-manifest.json içinde boş dosya yolu bulundu; kurulum durduruldu.' }
    $normalized = $relativePath.Replace('\', '/')
    if ($normalized.StartsWith('/') -or $normalized -match '(^|/)\.\.(/|$)' -or $normalized -match '^[A-Za-z]:' -or $normalized.StartsWith('//')) {
        throw "publish-manifest.json içinde güvensiz dosya yolu bulundu: $relativePath"
    }
    $key = $normalized.ToLowerInvariant()
    if (-not $seenKeys.Add($key)) { throw "publish-manifest.json içinde yinelenen veya büyük/küçük harf çakışan yol bulundu: $relativePath" }
}

$actualFiles = Get-ChildItem -LiteralPath $package -File -Recurse |
    Where-Object { $_.Name -ne 'publish-manifest.json' -and $_.Name -ne 'release-manifest.json' }
$actualRelative = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($file in $actualFiles) {
    $rel = [System.IO.Path]::GetRelativePath($package, $file.FullName).Replace('\', '/')
    [void]$actualRelative.Add($rel.ToLowerInvariant())
}
$manifestRelative = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($entry in $entries) { [void]$manifestRelative.Add(([string]$entry.path).Replace('\', '/').ToLowerInvariant()) }

$extra = $actualRelative | Where-Object { -not $manifestRelative.Contains($_) }
if ($extra) { throw "Pakette manifestte olmayan dosya bulundu: $($extra -join ', ')" }

foreach ($entry in $entries) {
    $relativePath = ([string]$entry.path).Replace('\', '/')
    $fullPath = Join-Path $package ($relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
    $resolvedFull = [System.IO.Path]::GetFullPath($fullPath)
    if (-not $resolvedFull.StartsWith(($package.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw "publish-manifest.json yolu paket kökünün dışına çıkıyor: $relativePath"
    }
    if (-not (Test-Path -LiteralPath $resolvedFull -PathType Leaf)) { throw "Manifestte listelenen dosya pakette yok: $relativePath" }
    $info = Get-Item -LiteralPath $resolvedFull
    if ($entry.size -ne $null -and [int64]$info.Length -ne [int64]$entry.size) {
        throw "Dosya boyutu uyuşmuyor: $relativePath"
    }
    $actualHash = (Get-FileHash -LiteralPath $resolvedFull -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = ([string]$entry.sha256).ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Dosya bütünlüğü doğrulanamadı (SHA256 uyuşmazlığı): $relativePath"
    }
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
