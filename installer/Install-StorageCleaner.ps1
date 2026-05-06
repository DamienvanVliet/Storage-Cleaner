param(
    [string]$InstallRoot,
    [switch]$CreateDesktopShortcut,
    [string]$LogPath
)

$ErrorActionPreference = "Stop"

function Initialize-Log {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $logRoot = Join-Path $env:ProgramData "StorageCleaner\Logs"
        $Path = Join-Path $logRoot "install.log"
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
    Add-Content -LiteralPath $script:InstallerLogPath -Value $line -Encoding UTF8
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-Shortcut {
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [string]$Arguments = "",
        [string]$WorkingDirectory = "",
        [string]$IconPath = ""
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.Arguments = $Arguments
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $shortcut.WorkingDirectory = $WorkingDirectory
    }
    if (-not [string]::IsNullOrWhiteSpace($IconPath)) {
        $shortcut.IconLocation = $IconPath
    }
    $shortcut.Save()
}

function Get-UninstallRegistryPath {
    param([bool]$IsAdminInstall)
    if ($IsAdminInstall) {
        return "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StorageCleaner"
    }

    return "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StorageCleaner"
}

$script:InstallerLogPath = Initialize-Log -Path $LogPath

try {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $payloadRoot = Join-Path $scriptRoot "app"

    if (-not (Test-Path -LiteralPath $payloadRoot)) {
        throw "Installer payload not found: $payloadRoot"
    }

    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        if (Test-IsAdmin) {
            $InstallRoot = Join-Path $env:ProgramFiles "Storage Cleaner"
        }
        else {
            $InstallRoot = Join-Path $env:LOCALAPPDATA "Programs\Storage Cleaner"
        }
    }

    Write-Log "Installing Storage Cleaner to: $InstallRoot"
    New-Item -Path $InstallRoot -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $payloadRoot "*") -Destination $InstallRoot -Recurse -Force

    $uninstallScriptSource = Join-Path $scriptRoot "Uninstall-StorageCleaner.ps1"
    $uninstallScriptTarget = Join-Path $InstallRoot "Uninstall-StorageCleaner.ps1"
    if (Test-Path -LiteralPath $uninstallScriptSource) {
        Copy-Item -LiteralPath $uninstallScriptSource -Destination $uninstallScriptTarget -Force
    }

    $exePath = Join-Path $InstallRoot "StorageCleaner.exe"
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "Installed executable not found: $exePath"
    }

    $isAdmin = Test-IsAdmin
    $programsRoot = if ($isAdmin) { [Environment]::GetFolderPath("CommonPrograms") } else { [Environment]::GetFolderPath("Programs") }
    $shortcutPath = Join-Path $programsRoot "Storage Cleaner.lnk"
    New-Shortcut -ShortcutPath $shortcutPath -TargetPath $exePath -WorkingDirectory $InstallRoot -IconPath $exePath

    if ($CreateDesktopShortcut) {
        $desktopShortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "Storage Cleaner.lnk"
        New-Shortcut -ShortcutPath $desktopShortcutPath -TargetPath $exePath -WorkingDirectory $InstallRoot -IconPath $exePath
    }

    $displayVersion = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($displayVersion)) {
        $displayVersion = "1.0.0"
    }

    $sizeBytes = (Get-ChildItem -LiteralPath $InstallRoot -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sizeBytes) {
        $sizeBytes = 0
    }
    $estimatedSizeKb = [int][Math]::Round($sizeBytes / 1KB)

    $uninstallRegistryPath = Get-UninstallRegistryPath -IsAdminInstall $isAdmin
    $uninstallCommand = "powershell.exe -ExecutionPolicy Bypass -NoProfile -File `"$uninstallScriptTarget`" -InstallRoot `"$InstallRoot`""

    if (-not (Test-Path -LiteralPath $uninstallRegistryPath)) {
        New-Item -Path $uninstallRegistryPath -Force | Out-Null
    }

    Set-ItemProperty -Path $uninstallRegistryPath -Name "DisplayName" -Value "Storage Cleaner"
    Set-ItemProperty -Path $uninstallRegistryPath -Name "DisplayVersion" -Value $displayVersion
    Set-ItemProperty -Path $uninstallRegistryPath -Name "Publisher" -Value "Damien van Vliet"
    Set-ItemProperty -Path $uninstallRegistryPath -Name "InstallLocation" -Value $InstallRoot
    Set-ItemProperty -Path $uninstallRegistryPath -Name "DisplayIcon" -Value "$exePath,0"
    Set-ItemProperty -Path $uninstallRegistryPath -Name "UninstallString" -Value $uninstallCommand
    Set-ItemProperty -Path $uninstallRegistryPath -Name "QuietUninstallString" -Value $uninstallCommand
    Set-ItemProperty -Path $uninstallRegistryPath -Name "URLInfoAbout" -Value "https://github.com/DamienvanVliet/Storage-Cleaner"
    Set-ItemProperty -Path $uninstallRegistryPath -Name "InstallDate" -Value (Get-Date -Format "yyyyMMdd")
    New-ItemProperty -Path $uninstallRegistryPath -Name "NoModify" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name "NoRepair" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $uninstallRegistryPath -Name "EstimatedSize" -Value $estimatedSizeKb -PropertyType DWord -Force | Out-Null

    Write-Log "Install complete."
    Write-Log "Start Menu shortcut: $shortcutPath"
    Write-Log "Uninstall script: $uninstallScriptTarget"
    Write-Log "Installed Apps entry: $uninstallRegistryPath"
    exit 0
}
catch {
    Write-Log $_.Exception.Message "ERROR"
    exit 1
}
