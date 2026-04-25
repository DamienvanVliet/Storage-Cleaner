param(
    [string]$ReleaseDir = "",
    [string]$ExePath = "",
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

$installerZipPath = Join-Path $ReleaseDir "StorageCleaner-win-x64-installer.zip"
$installerChecksumPath = Join-Path $ReleaseDir "StorageCleaner-win-x64-installer.sha256.txt"

Assert-ChecksumMatches -FilePath $installerZipPath -ChecksumFilePath $installerChecksumPath

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $defaultExePath = Join-Path (Join-Path $repoRoot "artifacts\publish\win-x64") "StorageCleaner.exe"
    if (Test-Path $defaultExePath) {
        $ExePath = $defaultExePath
    }
}

if (-not [string]::IsNullOrWhiteSpace($ExePath) -and (Test-Path $ExePath)) {
    $signature = Get-AuthenticodeSignature -FilePath $ExePath
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
}
elseif ($RequireValidSignature) {
    throw "RequireValidSignature was set, but no executable was found for signature validation."
}

Write-Host "Release verification completed."
