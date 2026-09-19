param(
    [string]$Project = (Join-Path $PSScriptRoot '..\TrMarketplaceHubDesktop.csproj'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\Windows-Current'),
    [string]$PackagePath = (Join-Path $PSScriptRoot '..\MarketplaceHub-win-x64.zip')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
$packageFull = [System.IO.Path]::GetFullPath($PackagePath)

if (Test-Path $outputFull) { Remove-Item -LiteralPath $outputFull -Recurse -Force }
New-Item -ItemType Directory -Path $outputFull -Force | Out-Null

dotnet publish $Project -c Release -r win-x64 --self-contained true -o $outputFull
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$files = Get-ChildItem -LiteralPath $outputFull -File -Recurse | Sort-Object FullName
if (-not ($files | Where-Object Name -eq 'TrMarketplaceHubDesktop.exe')) {
    throw 'TrMarketplaceHubDesktop.exe publish çıktısında bulunamadı.'
}
$outputPrefix = $outputFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

$manifestEntries = foreach ($file in $files) {
    $fileFull = [System.IO.Path]::GetFullPath($file.FullName)
    if (-not $fileFull.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Yayın dosyası hedef klasör dışında: $fileFull"
    }
    [pscustomobject]@{
        path = $fileFull.Substring($outputPrefix.Length).Replace('\\','/')
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$publishManifest = [pscustomobject]@{
    product = 'MarketplaceHub'
    rid = 'win-x64'
    selfContained = $true
    files = @($manifestEntries)
}
$publishManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputFull 'publish-manifest.json') -Encoding utf8

$commitSha = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { 'local' }
$releaseManifest = [pscustomobject]@{
    product = 'MarketplaceHub'
    commit = $commitSha
    rid = 'win-x64'
    selfContained = $true
    package = [System.IO.Path]::GetFileName($packageFull)
}
$releaseManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputFull 'release-manifest.json') -Encoding utf8

& (Join-Path $PSScriptRoot 'Smoke-Test.ps1') -PackageDirectory $outputFull
if ($LASTEXITCODE -ne 0) { throw "package smoke failed with exit code $LASTEXITCODE" }

if (Test-Path $packageFull) { Remove-Item -LiteralPath $packageFull -Force }
Compress-Archive -Path (Join-Path $outputFull '*') -DestinationPath $packageFull -CompressionLevel Optimal

$packageHash = (Get-FileHash -LiteralPath $packageFull -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "PUBLISH_PATH=$outputFull"
Write-Output "INSTALLER_ARTIFACT=$packageFull"
Write-Output "PACKAGE_SHA256=$packageHash"
