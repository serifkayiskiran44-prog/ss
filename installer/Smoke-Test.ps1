param([string]$PackageDirectory = (Join-Path $PSScriptRoot '..\Windows-Current'))
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackageDirectory).Path
$exe = Join-Path $package 'TrMarketplaceHubDesktop.exe'
if (-not (Test-Path $exe)) { throw "Self-contained EXE bulunamadı: $exe" }
$runtime = Join-Path $package 'TrMarketplaceHubDesktop.runtimeconfig.json'
$deps = Join-Path $package 'TrMarketplaceHubDesktop.deps.json'
if (-not (Test-Path $runtime) -or -not (Test-Path $deps)) { throw "Self-contained runtime metadata eksik: runtimeconfig/deps" }
$templateDirectory = Join-Path $package 'templates'
if (-not (Test-Path $templateDirectory)) { throw "templates klasörü publish çıktısında bulunamadı: $templateDirectory" }
if (-not (Get-ChildItem -LiteralPath $templateDirectory -File -Filter '*.json' | Select-Object -First 1)) { throw 'Publish edilen template JSON bulunamadı.' }
$publishManifest = Join-Path $package 'publish-manifest.json'
$releaseManifest = Join-Path $package 'release-manifest.json'
if (-not (Test-Path $publishManifest) -or -not (Test-Path $releaseManifest)) { throw 'Release manifest dosyaları eksik.' }
$hash = Get-FileHash $exe -Algorithm SHA256
Write-Output "exe=$($hash.Hash) length=$((Get-Item $exe).Length)"
Write-Output 'package smoke: PASS (EXE, runtime metadata, templates ve manifestler bulundu)'
