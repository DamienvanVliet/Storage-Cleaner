param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$project = ".\src\StorageCleaner.App\StorageCleaner.App.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\win-x64"

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

Write-Host "Publishing Storage Cleaner (win-x64, self-contained)..."
dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -o $publishDir

Write-Host "Publish output:"
Write-Host "  $publishDir"
