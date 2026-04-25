param(
    [string]$InstallRoot
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-UninstallRegistryPath {
    param([bool]$IsAdminInstall)
    if ($IsAdminInstall) {
        return "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StorageCleaner"
    }

    return "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StorageCleaner"
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    if (Test-IsAdmin) {
        $InstallRoot = Join-Path $env:ProgramFiles "Storage Cleaner"
    }
    else {
        $InstallRoot = Join-Path $env:LOCALAPPDATA "Programs\Storage Cleaner"
    }
}

Write-Host "Removing Storage Cleaner from: $InstallRoot"

$programsPaths = @(
    [Environment]::GetFolderPath("Programs"),
    [Environment]::GetFolderPath("CommonPrograms")
)

foreach ($programsPath in $programsPaths) {
    $shortcut = Join-Path $programsPath "Storage Cleaner.lnk"
    if (Test-Path $shortcut) {
        Remove-Item -LiteralPath $shortcut -Force
    }
}

$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "Storage Cleaner.lnk"
if (Test-Path $desktopShortcut) {
    Remove-Item -LiteralPath $desktopShortcut -Force
}

if (Test-Path $InstallRoot) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
}

$isAdmin = Test-IsAdmin
$uninstallPaths = @(
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StorageCleaner",
    "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StorageCleaner",
    (Get-UninstallRegistryPath -IsAdminInstall $isAdmin)
) | Select-Object -Unique

foreach ($registryPath in $uninstallPaths) {
    if (Test-Path $registryPath) {
        Remove-Item -LiteralPath $registryPath -Recurse -Force
    }
}

Write-Host "Uninstall complete."
