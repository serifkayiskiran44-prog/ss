param(
    [string]$PackageDirectory = (Join-Path $PSScriptRoot '..\Windows-M20-Installer'),
    [string]$StartMenuShortcutPath = ''
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackageDirectory).Path
$exe = Join-Path $package 'TrMarketplaceHubDesktop.exe'
if (-not (Test-Path $exe)) { throw "Self-contained EXE bulunamadı: $exe" }
$runtime = Join-Path $package 'TrMarketplaceHubDesktop.runtimeconfig.json'
$deps = Join-Path $package 'TrMarketplaceHubDesktop.deps.json'
if (-not (Test-Path $runtime) -or -not (Test-Path $deps)) { throw "Self-contained runtime metadata eksik: runtimeconfig/deps" }
$version = Join-Path $package 'installed-version.txt'
if (Test-Path $version) { Write-Output "version=$(Get-Content $version -Raw | ForEach-Object Trim)" }
$hash = Get-FileHash $exe -Algorithm SHA256
Write-Output "exe=$($hash.Hash) length=$((Get-Item $exe).Length)"

if ($StartMenuShortcutPath -ne '') {
    # A shortcut file existing on disk is not proof the app works: resolve it and verify its real target survives.
    if (-not (Test-Path $StartMenuShortcutPath)) { throw "Start Menu kısayolu bulunamadı: $StartMenuShortcutPath" }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($StartMenuShortcutPath)
    $target = $shortcut.TargetPath
    if ([string]::IsNullOrWhiteSpace($target) -or -not (Test-Path $target)) { throw "Kısayol hedefi bozuk veya eksik: $target" }
    if ((Get-Item $target).Length -eq 0) { throw "Kısayol hedefi sıfır byte: $target" }
    Write-Output "shortcut-target=$target"
}

Write-Output 'package smoke: PASS (EXE, runtime metadata ve self-contained dosya seti bulundu)'
