param(
    [string]$PfxPath = "",
    [string]$PfxPassword = "",
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PfxPath)) {
    $PfxPath = $env:STORAGE_CLEANER_PFX_PATH
}

if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
    $PfxPassword = $env:STORAGE_CLEANER_PFX_PASSWORD
}

if ([string]::IsNullOrWhiteSpace($PfxPath) -or -not (Test-Path $PfxPath)) {
    throw "Code-sign certificate (.pfx) not found. Pass -PfxPath or set STORAGE_CLEANER_PFX_PATH."
}

if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
    throw "Certificate password is missing. Pass -PfxPassword or set STORAGE_CLEANER_PFX_PASSWORD."
}

function Resolve-SignToolPath {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        $candidate = Get-ChildItem -Path $kitsRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like "*\\x64\\signtool.exe" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    throw "signtool.exe not found. Install Windows SDK (Signing Tools)."
}

function Invoke-Sign {
    param(
        [string]$SignToolPath,
        [string]$TargetPath
    )

    if (-not (Test-Path $TargetPath)) {
        throw "File not found for signing: $TargetPath"
    }

    & $SignToolPath sign /fd SHA256 /td SHA256 /tr $TimestampServer /f $PfxPath /p $PfxPassword $TargetPath
    if ($LASTEXITCODE -ne 0) {
        throw "Signing failed for: $TargetPath"
    }

    $signature = Get-AuthenticodeSignature -FilePath $TargetPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Signature verification failed for $TargetPath (Status: $($signature.Status))."
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$publishScript = Join-Path $PSScriptRoot "publish-win-x64.ps1"
& $publishScript -Configuration $Configuration

$publishExe = Join-Path $repoRoot "artifacts\publish\win-x64\StorageCleaner.exe"
$signToolPath = Resolve-SignToolPath

Write-Host "Using SignTool:"
Write-Host "  $signToolPath"
Write-Host "Signing:"
Write-Host "  $publishExe"

Invoke-Sign -SignToolPath $signToolPath -TargetPath $publishExe

& (Join-Path $PSScriptRoot "package-installer.ps1") -Configuration $Configuration -SkipPublish
& (Join-Path $PSScriptRoot "verify-release.ps1") -ExePath $publishExe -RequireValidSignature

Write-Host "Release signing and verification complete."
