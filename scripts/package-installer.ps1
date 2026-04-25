param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$publishScript = Join-Path $PSScriptRoot "publish-win-x64.ps1"
& $publishScript -Configuration $Configuration

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

$quickStartPath = Join-Path $installerOutputDir "INSTALL.txt"
@"
Storage Cleaner Installer
=========================

1) Right-click Install-StorageCleaner.ps1 and run with PowerShell.
2) If PowerShell blocks scripts, run:
   powershell -ExecutionPolicy Bypass -File .\Install-StorageCleaner.ps1
3) Use Uninstall-StorageCleaner.ps1 later to remove the app.
"@ | Set-Content -Path $quickStartPath -Encoding UTF8

$releaseDir = Join-Path $repoRoot "artifacts\releases"
New-Item -Path $releaseDir -ItemType Directory -Force | Out-Null
$zipPath = Join-Path $releaseDir "StorageCleaner-win-x64-installer.zip"
if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $installerOutputDir "*") -DestinationPath $zipPath -Force

Write-Host "Installer folder:"
Write-Host "  $installerOutputDir"
Write-Host "Installer zip:"
Write-Host "  $zipPath"
