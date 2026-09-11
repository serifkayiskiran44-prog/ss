param(
    [string]$Project = (Join-Path $PSScriptRoot '..\TrMarketplaceHubDesktop.csproj'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\Windows-M19-Dashboard'),
    [string]$PackagePath = (Join-Path $PSScriptRoot '..\MonoBridgeDesktop-win-x64.zip')
)
$ErrorActionPreference = 'Stop'
dotnet publish $Project -c Release -r win-x64 --self-contained true -o $OutputDirectory
if (Test-Path $PackagePath) { Remove-Item -LiteralPath $PackagePath -Force }
Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $PackagePath -CompressionLevel Optimal
Write-Output "Self-contained paket hazır: $PackagePath"
