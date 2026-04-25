param(
    [string]$ReleaseDir = "",
    [switch]$RequireValidSignature
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $ReleaseDir = Join-Path $repoRoot "artifacts\releases"
}

function Assert-Exists {
    param([string]$PathToCheck)
    if (-not (Test-Path $PathToCheck)) {
        throw "Missing required release file: $PathToCheck"
    }
}

function Assert-ChecksumMatches {
    param(
        [string]$FilePath,
        [string]$ChecksumFilePath
    )

    Assert-Exists -PathToCheck $FilePath
    Assert-Exists -PathToCheck $ChecksumFilePath

    $line = (Get-Content -LiteralPath $ChecksumFilePath | Select-Object -First 1).Trim()
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "Checksum file is empty: $ChecksumFilePath"
    }

    $expectedHash = ($line -split "\s+")[0].ToUpperInvariant()
    $actualHash = (Get-FileHash -Path $FilePath -Algorithm SHA256).Hash.ToUpperInvariant()

    if ($expectedHash -ne $actualHash) {
        throw "Checksum mismatch for $(Split-Path $FilePath -Leaf). Expected $expectedHash, got $actualHash"
    }

    Write-Host "Checksum OK: $(Split-Path $FilePath -Leaf)"
}

$exePath = Join-Path $ReleaseDir "StorageCleaner.exe"
$exeChecksumPath = Join-Path $ReleaseDir "StorageCleaner.exe.sha256.txt"
$portableZipPath = Join-Path $ReleaseDir "StorageCleaner-win-x64-portable.zip"
$portableChecksumPath = Join-Path $ReleaseDir "StorageCleaner-win-x64-portable.sha256.txt"
$installerZipPath = Join-Path $ReleaseDir "StorageCleaner-win-x64-installer.zip"
$installerChecksumPath = Join-Path $ReleaseDir "StorageCleaner-win-x64-installer.sha256.txt"

Assert-ChecksumMatches -FilePath $exePath -ChecksumFilePath $exeChecksumPath
Assert-ChecksumMatches -FilePath $portableZipPath -ChecksumFilePath $portableChecksumPath
Assert-ChecksumMatches -FilePath $installerZipPath -ChecksumFilePath $installerChecksumPath

$signature = Get-AuthenticodeSignature -FilePath $exePath
Write-Host "Digital signature status: $($signature.Status)"
if ($signature.SignerCertificate -isnot [System.Security.Cryptography.X509Certificates.X509Certificate2]) {
    Write-Host "Signer certificate: (none)"
}
else {
    Write-Host "Signer certificate: $($signature.SignerCertificate.Subject)"
}

if ($RequireValidSignature -and $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Executable signature is not valid. Status: $($signature.Status)"
}

Write-Host "Release verification completed."
