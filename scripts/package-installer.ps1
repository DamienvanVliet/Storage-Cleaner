param(
    [string]$Configuration = "Release",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not $SkipPublish) {
    $publishScript = Join-Path $PSScriptRoot "publish-win-x64.ps1"
    & $publishScript -Configuration $Configuration
}

$publishDir = Join-Path $repoRoot "artifacts\publish\win-x64"
$installerTemplateDir = Join-Path $repoRoot "installer"
$installerOutputDir = Join-Path $repoRoot "artifacts\installer\StorageCleaner-win-x64"

if (Test-Path $installerOutputDir) {
    Remove-Item -LiteralPath $installerOutputDir -Recurse -Force
}

New-Item -Path $installerOutputDir -ItemType Directory -Force | Out-Null
New-Item -Path (Join-Path $installerOutputDir "app") -ItemType Directory -Force | Out-Null

Copy-Item -Path (Join-Path $publishDir "*") -Destination (Join-Path $installerOutputDir "app") -Recurse -Force
Copy-Item -Path (Join-Path $installerTemplateDir "Install-StorageCleaner.ps1") -Destination $installerOutputDir -Force
Copy-Item -Path (Join-Path $installerTemplateDir "Uninstall-StorageCleaner.ps1") -Destination $installerOutputDir -Force
Copy-Item -Path (Join-Path $installerTemplateDir "Detect-StorageCleaner.ps1") -Destination $installerOutputDir -Force

$installCmdPath = Join-Path $installerOutputDir "Install-StorageCleaner.cmd"
@"
@echo off
powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0Install-StorageCleaner.ps1"
"@ | Set-Content -Path $installCmdPath -Encoding ASCII

$quickStartPath = Join-Path $installerOutputDir "INSTALL.txt"
@"
Storage Cleaner Installer
=========================

1) Double-click Install-StorageCleaner.cmd
2) This installs Storage Cleaner and adds it to Start Menu + Installed Apps
3) To uninstall later, run Uninstall-StorageCleaner.ps1
4) For Intune script detection, use Detect-StorageCleaner.ps1
"@ | Set-Content -Path $quickStartPath -Encoding UTF8

$releaseDir = Join-Path $repoRoot "artifacts\releases"
New-Item -Path $releaseDir -ItemType Directory -Force | Out-Null

Get-ChildItem -Path $releaseDir -Filter "StorageCleaner*" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$zipPath = Join-Path $releaseDir "StorageCleaner-win-x64-installer.zip"
if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $installerOutputDir "*") -DestinationPath $zipPath -Force

$checksumPath = Join-Path $releaseDir "StorageCleaner-win-x64-installer.sha256.txt"
$hash = Get-FileHash -Path $zipPath -Algorithm SHA256
"$($hash.Hash)  $(Split-Path $zipPath -Leaf)" | Set-Content -Path $checksumPath -Encoding ASCII

Write-Host "Installer folder:"
Write-Host "  $installerOutputDir"
Write-Host "Installer zip:"
Write-Host "  $zipPath"
Write-Host "Installer checksum:"
Write-Host "  $checksumPath"
