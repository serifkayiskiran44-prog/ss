param([string]$PackageDirectory = (Join-Path $PSScriptRoot '..\Windows-M20-Installer'))
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
Write-Output 'package smoke: PASS (EXE, runtime metadata ve self-contained dosya seti bulundu)'
