param(
    [string]$InstallRoot,
    [switch]$CreateDesktopShortcut
)

$ErrorActionPreference = "Stop"

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

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$payloadRoot = Join-Path $scriptRoot "app"

if (-not (Test-Path $payloadRoot)) {
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

Write-Host "Installing Storage Cleaner to: $InstallRoot"
New-Item -Path $InstallRoot -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $payloadRoot "*") -Destination $InstallRoot -Recurse -Force

$uninstallScriptSource = Join-Path $scriptRoot "Uninstall-StorageCleaner.ps1"
$uninstallScriptTarget = Join-Path $InstallRoot "Uninstall-StorageCleaner.ps1"
if (Test-Path $uninstallScriptSource) {
    Copy-Item -LiteralPath $uninstallScriptSource -Destination $uninstallScriptTarget -Force
}

$exePath = Join-Path $InstallRoot "StorageCleaner.exe"
if (-not (Test-Path $exePath)) {
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

Write-Host "Install complete."
Write-Host "Start Menu shortcut: $shortcutPath"
Write-Host "Uninstall script: $uninstallScriptTarget"
