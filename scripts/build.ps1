param(
    [switch]$RunTests = $true
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "Restoring packages..."
dotnet restore .\StorageCleaner.sln

Write-Host "Building solution..."
dotnet build .\StorageCleaner.sln -c Release

if ($RunTests) {
    Write-Host "Running tests..."
    dotnet test .\StorageCleaner.sln -c Release --no-build
}

Write-Host "Build script finished."
