param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\MonoBridgeDesktop'),
    [string]$StartMenuProgramsDirectory = (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'),
    [string]$DesktopDirectory = ([Environment]::GetFolderPath('Desktop'))
)
$ErrorActionPreference = 'Stop'
$ShortcutNames = @('MarketplaceHub Desktop.lnk', 'MonoBridge Desktop.lnk')

foreach ($name in $ShortcutNames) {
    foreach ($directory in @($StartMenuProgramsDirectory, $DesktopDirectory)) {
        $path = Join-Path $directory $name
        if (Test-Path $path) {
            Remove-Item -LiteralPath $path -Force
            Write-Output "shortcut-removed=$path"
        }
    }
}

if (Test-Path $InstallDirectory) { Remove-Item -LiteralPath $InstallDirectory -Recurse -Force }
Write-Output 'Uygulama dosyaları kaldırıldı. LocalAppData\MonoBridgeDesktop kullanıcı verileri korunmuştur.'
