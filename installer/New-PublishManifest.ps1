param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'
$ManifestName = 'publish-manifest.json'
$SidecarName = 'publish-manifest.sha256'

$package = (Resolve-Path $PackageDirectory).Path

# The manifest never lists itself or its own integrity sidecar -- hashing a file that hashes itself is a circular
# contract that proves nothing, so both are always excluded from the file list they describe.
$files = Get-ChildItem -Path $package -Recurse -File |
    Where-Object { $_.Name -ne $ManifestName -and $_.Name -ne $SidecarName } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($package.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        [PSCustomObject]@{ Path = $relative; Length = $_.Length; Sha256 = $hash }
    }

$manifest = [PSCustomObject]@{
    ApplicationVersion = '1.0.0'
    Runtime            = 'win-x64/self-contained'
    CreatedUtc         = (Get-Date).ToUniversalTime().ToString('o')
    Files              = @($files)
}

$manifestPath = Join-Path $package $ManifestName
$json = $manifest | ConvertTo-Json -Depth 6
Set-Content -LiteralPath $manifestPath -Value $json -Encoding UTF8 -NoNewline

# A separate top-level integrity contract for the manifest itself: this sidecar is not one of the manifest's own
# entries, so a tampered manifest.json can be detected even though it is exactly what the per-file hashes trust.
$manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
$manifestHash = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($manifestBytes)).Replace('-', '')
Set-Content -LiteralPath (Join-Path $package $SidecarName) -Value $manifestHash -Encoding UTF8 -NoNewline

Write-Output "manifest-written=$manifestPath|files=$($files.Count)"
Write-Output "manifest-sha256=$manifestHash"
