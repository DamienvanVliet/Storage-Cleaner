param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $scriptRoot "package-portable-exe.ps1") -Configuration $Configuration
& (Join-Path $scriptRoot "package-installer.ps1") -Configuration $Configuration
& (Join-Path $scriptRoot "verify-release.ps1")

Write-Host "All release packages created in artifacts\\releases"
