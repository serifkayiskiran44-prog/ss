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

# Real process launch: file presence alone does not prove the app actually renders -
# a missing dependency, XAML load error or unhandled startup exception would still
# pass every check above while the EXE crashes or never shows a window. Run it against
# an isolated temp data directory (MARKETPLACEHUB_DATA_DIR) so this never touches real
# user data, and require MainWindow to actually become visible within a bounded time.
$smokeDataDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("marketplacehub-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $smokeDataDirectory | Out-Null
$process = $null
try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.WorkingDirectory = $package
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['MARKETPLACEHUB_DATA_DIR'] = $smokeDataDirectory
    $process = [System.Diagnostics.Process]::Start($psi)

    $deadline = (Get-Date).AddSeconds(30)
    $title = $null
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "EXE MainWindow açılmadan önce kapandı (exit code $($process.ExitCode))." }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $title = $process.MainWindowTitle; break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $title -and $process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'MainWindow zaman aşımına uğradı (30s); pencere görünür olmadı.' }
    Write-Output "MainWindow smoke: PASS ('$title' penceresi görünür oldu)"
}
finally {
    if ($process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
    }
    if (Test-Path -LiteralPath $smokeDataDirectory) { Remove-Item -LiteralPath $smokeDataDirectory -Recurse -Force -ErrorAction SilentlyContinue }
}
