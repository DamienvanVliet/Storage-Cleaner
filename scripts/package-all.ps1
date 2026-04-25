param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $scriptRoot "package-portable-exe.ps1") -Configuration $Configuration
& (Join-Path $scriptRoot "package-installer.ps1") -Configuration $Configuration

Write-Host "All release packages created in artifacts\\releases"
