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
$portableDir = Join-Path $repoRoot "artifacts\portable\StorageCleaner-win-x64"
$releaseDir = Join-Path $repoRoot "artifacts\releases"
$portableZip = Join-Path $releaseDir "StorageCleaner-win-x64-portable.zip"
$releaseExe = Join-Path $releaseDir "StorageCleaner.exe"

if (Test-Path $portableDir) {
    Remove-Item -LiteralPath $portableDir -Recurse -Force
}

if (Test-Path $portableZip) {
    Remove-Item -LiteralPath $portableZip -Force
}

New-Item -Path $portableDir -ItemType Directory -Force | Out-Null
New-Item -Path $releaseDir -ItemType Directory -Force | Out-Null

$exePath = Join-Path $publishDir "StorageCleaner.exe"
if (-not (Test-Path $exePath)) {
    throw "Portable EXE not found: $exePath"
}

Copy-Item -LiteralPath $exePath -Destination (Join-Path $portableDir "StorageCleaner.exe") -Force
Copy-Item -LiteralPath $exePath -Destination $releaseExe -Force

$portableReadme = Join-Path $portableDir "README.txt"
@"
Storage Cleaner (Portable EXE)
==============================

How to use:
1) Double-click StorageCleaner.exe
2) If Windows SmartScreen appears, choose "More info" then "Run anyway"

No install required.
User data and logs are saved in:
%LOCALAPPDATA%\StorageCleaner
"@ | Set-Content -Path $portableReadme -Encoding UTF8

Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $portableZip -Force

$checksumPath = Join-Path $releaseDir "StorageCleaner-win-x64-portable.sha256.txt"
$hash = Get-FileHash -Path $portableZip -Algorithm SHA256
"$($hash.Hash)  $(Split-Path $portableZip -Leaf)" | Set-Content -Path $checksumPath -Encoding ASCII

$exeChecksumPath = Join-Path $releaseDir "StorageCleaner.exe.sha256.txt"
$exeHash = Get-FileHash -Path $releaseExe -Algorithm SHA256
"$($exeHash.Hash)  $(Split-Path $releaseExe -Leaf)" | Set-Content -Path $exeChecksumPath -Encoding ASCII

Write-Host "Portable folder:"
Write-Host "  $portableDir"
Write-Host "Portable zip:"
Write-Host "  $portableZip"
Write-Host "Portable EXE:"
Write-Host "  $releaseExe"
Write-Host "Checksum:"
Write-Host "  $checksumPath"
Write-Host "EXE checksum:"
Write-Host "  $exeChecksumPath"
