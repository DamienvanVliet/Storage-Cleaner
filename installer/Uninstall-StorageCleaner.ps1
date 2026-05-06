param(
    [string]$InstallRoot,
    [string]$LogPath
)

$ErrorActionPreference = "Stop"

function Initialize-Log {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $logRoot = Join-Path $env:ProgramData "StorageCleaner\Logs"
        $Path = Join-Path $logRoot "uninstall.log"
    }

    $logDirectory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $logDirectory)) {
        New-Item -Path $logDirectory -ItemType Directory -Force | Out-Null
    }

    return $Path
}

function Write-Log {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR")][string]$Level = "INFO"
    )

    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    Write-Host $line
    Add-Content -LiteralPath $script:UninstallerLogPath -Value $line -Encoding UTF8
}

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

$script:UninstallerLogPath = Initialize-Log -Path $LogPath

try {
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        if (Test-IsAdmin) {
            $InstallRoot = Join-Path $env:ProgramFiles "Storage Cleaner"
        }
        else {
            $InstallRoot = Join-Path $env:LOCALAPPDATA "Programs\Storage Cleaner"
        }
    }

    Write-Log "Removing Storage Cleaner from: $InstallRoot"

    $programsPaths = @(
        [Environment]::GetFolderPath("Programs"),
        [Environment]::GetFolderPath("CommonPrograms")
    )

    foreach ($programsPath in $programsPaths) {
        $shortcut = Join-Path $programsPath "Storage Cleaner.lnk"
        if (Test-Path -LiteralPath $shortcut) {
            Remove-Item -LiteralPath $shortcut -Force
            Write-Log "Removed shortcut: $shortcut"
        }
    }

    $desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "Storage Cleaner.lnk"
    if (Test-Path -LiteralPath $desktopShortcut) {
        Remove-Item -LiteralPath $desktopShortcut -Force
        Write-Log "Removed shortcut: $desktopShortcut"
    }

    if (Test-Path -LiteralPath $InstallRoot) {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
        Write-Log "Removed install directory: $InstallRoot"
    }

    $isAdmin = Test-IsAdmin
    $uninstallPaths = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StorageCleaner",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StorageCleaner",
        (Get-UninstallRegistryPath -IsAdminInstall $isAdmin)
    ) | Select-Object -Unique

    foreach ($registryPath in $uninstallPaths) {
        if (Test-Path -LiteralPath $registryPath) {
            Remove-Item -LiteralPath $registryPath -Recurse -Force
            Write-Log "Removed registry key: $registryPath"
        }
    }

    Write-Log "Uninstall complete."
    exit 0
}
catch {
    Write-Log $_.Exception.Message "ERROR"
    exit 1
}
