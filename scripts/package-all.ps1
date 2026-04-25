param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $scriptRoot "package-installer.ps1") -Configuration $Configuration
& (Join-Path $scriptRoot "verify-release.ps1")

Write-Host "Installer release package created in artifacts\\releases"
