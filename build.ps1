$ErrorActionPreference = "Stop"

param(
  [switch]$SelfContained
)

$root = $PSScriptRoot
$tray = Join-Path $root "tray"
$runtime = "win-x64"
$exeName = "WslYubikeyTray.exe"

Get-Process -Name "WslYubikeyTray" -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Publishing..."
$self = $SelfContained.IsPresent

if ($self) {
  & dotnet publish $tray -c Release -r $runtime /p:PublishSingleFile=true /p:SelfContained=true /p:PublishReadyToRun=false /p:EnableCompressionInSingleFile=true
} else {
  & dotnet publish $tray -c Release -r $runtime /p:PublishSingleFile=true /p:SelfContained=false
}

$publishDir = Join-Path $tray "bin/Release/net8.0-windows/$runtime/publish"
$exeSrc = Join-Path $publishDir $exeName
$exeDst = Join-Path $root $exeName
$svgSrc = Join-Path $tray "key.svg"
$svgDst = Join-Path $root "key.svg"

Copy-Item $exeSrc $exeDst -Force
if (Test-Path $svgSrc) { Copy-Item $svgSrc $svgDst -Force }
Write-Host "Copied to $exeDst"
