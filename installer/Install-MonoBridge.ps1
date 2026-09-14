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

$package = (Resolve-Path $PackageDirectory).Path
$exePath = Join-Path $package $ExeName
if (-not (Test-Path $exePath)) { throw 'Self-contained publish klasörü ve TrMarketplaceHubDesktop.exe bulunamadı.' }

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
