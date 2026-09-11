param([string]$PackageDirectory = (Join-Path $PSScriptRoot '..\Windows-M20-Installer'))
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackageDirectory).Path
$exe = Join-Path $package 'TrMarketplaceHubDesktop.exe'
if (-not (Test-Path $exe)) { throw "Self-contained EXE bulunamadı: $exe" }
$version = Join-Path $package 'installed-version.txt'
if (Test-Path $version) { Write-Output "version=$(Get-Content $version -Raw | ForEach-Object Trim)" }
$hash = Get-FileHash $exe -Algorithm SHA256
Write-Output "exe=$($hash.Hash) length=$((Get-Item $exe).Length)"
Write-Output 'package smoke: PASS (EXE ve self-contained dosya seti bulundu)'
