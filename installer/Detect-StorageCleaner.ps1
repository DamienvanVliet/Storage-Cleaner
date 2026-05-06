$ErrorActionPreference = "Stop"

$displayName = "Storage Cleaner"
$executableName = "StorageCleaner.exe"
$registrySubKey = "StorageCleaner"

function Get-UninstallRegistryPaths {
    return @(
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$registrySubKey",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$registrySubKey",
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$registrySubKey"
    )
}

try {
    foreach ($registryPath in Get-UninstallRegistryPaths) {
        if (-not (Test-Path -LiteralPath $registryPath)) {
            continue
        }

        $app = Get-ItemProperty -LiteralPath $registryPath
        if ($app.DisplayName -ne $displayName) {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($app.InstallLocation)) {
            continue
        }

        $exePath = Join-Path $app.InstallLocation $executableName
        if (Test-Path -LiteralPath $exePath) {
            Write-Output "$displayName detected at $exePath"
            exit 0
        }
    }

    exit 1
}
catch {
    exit 1
}
