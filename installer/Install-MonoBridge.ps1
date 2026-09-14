param(
    [string]$PackageDirectory = (Join-Path $PSScriptRoot '..\Windows-M19-Dashboard'),
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\MonoBridgeDesktop'),
    [string]$StartMenuProgramsDirectory = (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'),
    [string]$DesktopDirectory = ([Environment]::GetFolderPath('Desktop')),
    [switch]$CreateDesktopShortcut
)
$ErrorActionPreference = 'Stop'
$ExeName = 'TrMarketplaceHubDesktop.exe'
$CanonicalShortcutName = 'MarketplaceHub Desktop.lnk'
$LegacyShortcutNames = @('MonoBridge Desktop.lnk')
$ManifestName = 'publish-manifest.json'
$SidecarName = 'publish-manifest.sha256'

$package = (Resolve-Path $PackageDirectory).Path

function Assert-PublishManifestIntegrity([string]$packageRoot) {
    $manifestPath = Join-Path $packageRoot $ManifestName
    $sidecarPath = Join-Path $packageRoot $SidecarName
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "publish-manifest.json bulunamadı: $manifestPath" }
    if (-not (Test-Path -LiteralPath $sidecarPath)) { throw "publish-manifest.sha256 bulunamadı: $sidecarPath" }

    # The manifest itself has its own, separate integrity contract -- a sidecar hash that is not one of the
    # manifest's own listed entries -- so a tampered manifest.json is caught even though it is exactly what every
    # per-file hash below trusts.
    $manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
    $actualManifestHash = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($manifestBytes)).Replace('-', '')
    $expectedManifestHash = (Get-Content -LiteralPath $sidecarPath -Raw).Trim()
    if ($actualManifestHash -ne $expectedManifestHash.ToUpperInvariant()) { throw 'publish-manifest.json bütünlüğü bozuk: sha256 sidecar ile eşleşmiyor' }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "publish-manifest.json JSON olarak ayrıştırılamadı: $($_.Exception.Message)"
    }

    if (-not $manifest.Files -or @($manifest.Files).Count -eq 0) { throw 'publish-manifest.json içinde hiç dosya girdisi yok' }

    $canonicalRoot = [System.IO.Path]::GetFullPath($packageRoot).TrimEnd('\', '/')
    $seenNormalized = New-Object 'System.Collections.Generic.HashSet[string]'

    foreach ($entry in @($manifest.Files)) {
        $relative = [string]$entry.Path
        if ([string]::IsNullOrWhiteSpace($relative)) { throw 'manifest girdisinde boş path' }
        if ($relative -match '\.\.' -or [System.IO.Path]::IsPathRooted($relative)) { throw "manifest path güvensiz (traversal/absolute): $relative" }

        $normalized = $relative.Replace('\', '/').ToLowerInvariant()
        if (-not $seenNormalized.Add($normalized)) { throw "manifest içinde duplicate/case-collision path: $relative" }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $packageRoot ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
        if (-not $fullPath.StartsWith($canonicalRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw "manifest path paket kökünün dışına çıkıyor: $relative" }
        if (-not (Test-Path -LiteralPath $fullPath)) { throw "manifest girdisi için dosya eksik: $relative" }

        $actualLength = (Get-Item -LiteralPath $fullPath).Length
        if ($actualLength -ne [int64]$entry.Length) { throw "boyut uyuşmazlığı: $relative (beklenen $($entry.Length), gerçek $actualLength)" }

        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        if ($actualHash -ne ([string]$entry.Sha256).ToUpperInvariant()) { throw "hash uyuşmazlığı: $relative" }
    }

    # Every real file in the package must be declared -- an extra, undeclared file is exactly as dangerous as a
    # tampered declared one, so 1:1 correspondence is required, not just "every listed entry checks out".
    $realFiles = Get-ChildItem -Path $packageRoot -Recurse -File | Where-Object { $_.Name -ne $ManifestName -and $_.Name -ne $SidecarName }
    foreach ($file in $realFiles) {
        $relative = $file.FullName.Substring($canonicalRoot.Length + 1).Replace('\', '/').ToLowerInvariant()
        if (-not $seenNormalized.Contains($relative)) { throw "pakette manifestte olmayan fazladan dosya var: $relative" }
    }
}

# Every entry must be verified, and the package must be proven unmodified, before a single file is copied into the
# install directory -- a fail-closed validation must never leave a partially-applied install behind.
Assert-PublishManifestIntegrity $package

$exePath = Join-Path $package $ExeName
if (-not (Test-Path -LiteralPath $exePath)) { throw 'Self-contained publish klasörü ve TrMarketplaceHubDesktop.exe bulunamadı.' }

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
Copy-Item -Path (Join-Path $package '*') -Destination $InstallDirectory -Recurse -Force
Set-Content -Path (Join-Path $InstallDirectory 'installed-version.txt') -Value '1.0.0' -Encoding UTF8

$installedExe = Join-Path $InstallDirectory $ExeName
$shell = New-Object -ComObject WScript.Shell

function New-CanonicalShortcut([string]$directory) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $path = Join-Path $directory $CanonicalShortcutName
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $installedExe
    $shortcut.WorkingDirectory = $InstallDirectory
    $shortcut.IconLocation = "$installedExe,0"
    $shortcut.Save()

    foreach ($legacyName in $LegacyShortcutNames) {
        $legacyPath = Join-Path $directory $legacyName
        if (Test-Path $legacyPath) {
            Remove-Item -LiteralPath $legacyPath -Force
            Write-Output "legacy-shortcut-removed=$legacyPath"
        }
    }

    Write-Output "shortcut-created=$path|target=$($shortcut.TargetPath)|workingdirectory=$($shortcut.WorkingDirectory)|icon=$($shortcut.IconLocation)"
}

New-CanonicalShortcut $StartMenuProgramsDirectory
if ($CreateDesktopShortcut) { New-CanonicalShortcut $DesktopDirectory }

Write-Output "Kurulum tamamlandı: $InstallDirectory"
Write-Output 'LocalAppData\MonoBridgeDesktop veri klasörü korunur ve silinmez.'
