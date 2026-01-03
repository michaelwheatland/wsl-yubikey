$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$tray = Join-Path $root "tray"
$runtime = "win-x64"
$exeName = "WslYubikeyTray.exe"

Get-Process -Name "WslYubikeyTray" -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Publishing..."
& dotnet publish $tray -c Release -r $runtime /p:PublishSingleFile=true /p:SelfContained=true

$publishDir = Join-Path $tray "bin/Release/net8.0-windows/$runtime/publish"
$exeSrc = Join-Path $publishDir $exeName
$exeDst = Join-Path $root $exeName

Copy-Item $exeSrc $exeDst -Force
Write-Host "Copied to $exeDst"
