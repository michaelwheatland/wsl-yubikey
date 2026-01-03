$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$tray = Join-Path $root "tray"
$runtime = "win-x64"

Write-Host "Publishing..."
& dotnet publish $tray -c Release -r $runtime /p:PublishSingleFile=true /p:SelfContained=true

$publishDir = Join-Path $tray "bin/Release/net8.0-windows/$runtime/publish"
$exeSrc = Join-Path $publishDir "WslYubikeyTray.exe"
$exeDst = Join-Path $root "WslYubikeyTray.exe"

Copy-Item $exeSrc $exeDst -Force
Write-Host "Copied to $exeDst"
