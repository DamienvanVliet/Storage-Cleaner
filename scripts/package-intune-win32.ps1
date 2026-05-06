param(
    [Parameter(Mandatory = $true)][string]$SourceInstallerPath,
    [string]$AppName = "",
    [string]$Publisher = "",
    [string]$SilentInstallArguments = "",
    [string]$SilentUninstallArguments = "",
    [string]$DetectionDisplayName = "",
    [string]$DetectionExecutableName = "",
    [string]$DetectionInstallLocation = "",
    [string]$DetectionPackageFamilyName = "",
    [string]$IntuneWinAppUtilPath = "",
    [string]$OutputRoot = "",
    [switch]$SkipIntuneWin
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\intune"
}

function ConvertTo-SafeName {
    param([Parameter(Mandatory = $true)][string]$Value)
    $safe = ($Value -replace "[^A-Za-z0-9._-]", "-").Trim("-")
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "Package"
    }

    return $safe
}

function ConvertTo-HashtableLiteral {
    param([hashtable]$Value)

    $lines = @("@{")
    foreach ($key in ($Value.Keys | Sort-Object)) {
        $item = $Value[$key]
        if ($item -is [array]) {
            $quotedItems = @($item | ForEach-Object { "'$($_ -replace "'", "''")'" })
            $lines += "    $key = @($($quotedItems -join ', '))"
        }
        elseif ($item -is [bool]) {
            $lines += "    $key = `$$($item.ToString().ToLowerInvariant())"
        }
        else {
            $lines += "    $key = '$($item -replace "'", "''")'"
        }
    }
    $lines += "}"
    return ($lines -join [Environment]::NewLine)
}

function Read-FileHeader {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytesToRead = [Math]::Min(16, (Get-Item -LiteralPath $Path).Length)
    $buffer = New-Object byte[] $bytesToRead
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        [void]$stream.Read($buffer, 0, $buffer.Length)
    }
    finally {
        $stream.Dispose()
    }

    return $buffer
}

function Get-ZipEntries {
    param([Parameter(Mandatory = $true)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        return @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $zip.Dispose()
    }
}

function Get-AppxManifestXml {
    param([Parameter(Mandatory = $true)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object {
            $_.FullName -eq "AppxManifest.xml" -or
            $_.FullName -eq "AppxMetadata/AppxBundleManifest.xml" -or
            $_.FullName -eq "AppxBundleManifest.xml"
        } | Select-Object -First 1

        if ($null -eq $entry) {
            return $null
        }

        $reader = [System.IO.StreamReader]::new($entry.Open())
        try {
            return [xml]$reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Get-InstallerKind {
    param([Parameter(Mandatory = $true)][string]$Path)

    $extension = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    $kindFromExtension = switch ($extension) {
        ".msi" { "Msi" }
        ".msp" { "Msp" }
        ".exe" { "Exe" }
        ".appx" { "Appx" }
        ".appxbundle" { "Appx" }
        ".msix" { "Appx" }
        ".msixbundle" { "Appx" }
        ".ps1" { "PowerShell" }
        ".cmd" { "Command" }
        ".bat" { "Command" }
        default { $null }
    }

    $buffer = Read-FileHeader -Path $Path
    $kindFromContent = $null
    if ($buffer.Length -ge 2 -and $buffer[0] -eq 0x4D -and $buffer[1] -eq 0x5A) {
        $kindFromContent = "Exe"
    }
    elseif ($buffer.Length -ge 8 -and
        $buffer[0] -eq 0xD0 -and $buffer[1] -eq 0xCF -and $buffer[2] -eq 0x11 -and $buffer[3] -eq 0xE0 -and
        $buffer[4] -eq 0xA1 -and $buffer[5] -eq 0xB1 -and $buffer[6] -eq 0x1A -and $buffer[7] -eq 0xE1) {
        $kindFromContent = "Msi"
    }
    elseif ($buffer.Length -ge 4 -and $buffer[0] -eq 0x50 -and $buffer[1] -eq 0x4B) {
        $entries = Get-ZipEntries -Path $Path
        if ($entries -contains "AppxManifest.xml" -or
            $entries -contains "AppxMetadata/AppxBundleManifest.xml" -or
            $entries -contains "AppxBundleManifest.xml") {
            $kindFromContent = "Appx"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($kindFromContent)) {
        if (-not [string]::IsNullOrWhiteSpace($kindFromExtension) -and $kindFromExtension -ne $kindFromContent) {
            Write-Warning "Installer extension suggests $kindFromExtension, but file content looks like $kindFromContent. Using content-based type."
        }

        return $kindFromContent
    }

    if (-not [string]::IsNullOrWhiteSpace($kindFromExtension)) {
        Write-Warning "Could not verify installer type from file content. Falling back to extension-based type: $kindFromExtension."
        return $kindFromExtension
    }

    throw "Unsupported or unrecognized installer type '$extension'. Supported: .exe, .msi, .msp, .appx, .appxbundle, .msix, .msixbundle, .ps1, .cmd, .bat."
}

function Get-MsiMetadata {
    param([Parameter(Mandatory = $true)][string]$Path)

    $metadata = @{
        ProductCode = ""
        ProductName = ""
        ProductVersion = ""
        Manufacturer = ""
        UpgradeCode = ""
    }

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @($Path, 0))
    try {
        foreach ($propertyName in @("ProductCode", "ProductName", "ProductVersion", "Manufacturer", "UpgradeCode")) {
            $view = $database.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $database, @("SELECT `Value` FROM `Property` WHERE `Property`='$propertyName'"))
            try {
                $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null
                $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
                if ($null -ne $record) {
                    $metadata[$propertyName] = $record.GetType().InvokeMember("StringData", "GetProperty", $null, $record, 1)
                }
            }
            finally {
                if ($null -ne $view) {
                    $view.GetType().InvokeMember("Close", "InvokeMethod", $null, $view, $null) | Out-Null
                }
            }
        }
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($database) | Out-Null
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null
    }

    return $metadata
}

function Get-ExeMetadata {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo]$File)

    $versionInfo = $File.VersionInfo
    $name = @($versionInfo.ProductName, $versionInfo.FileDescription, $File.BaseName) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
    $publisher = @($versionInfo.CompanyName, "") |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
    $frameworkText = (@(
        $versionInfo.ProductName,
        $versionInfo.FileDescription,
        $versionInfo.InternalName,
        $versionInfo.OriginalFilename,
        $versionInfo.Comments
    ) -join " ")

    return @{
        ProductName = $name
        Publisher = $publisher
        Version = $versionInfo.ProductVersion
        FrameworkText = $frameworkText
    }
}

function Get-AppxMetadata {
    param([Parameter(Mandatory = $true)][string]$Path)

    $manifest = Get-AppxManifestXml -Path $Path
    if ($null -eq $manifest) {
        return @{
            ProductName = ""
            Publisher = ""
            Version = ""
            PackageName = ""
            PackageFamilyName = ""
            Executable = ""
        }
    }

    $identity = $manifest.Package.Identity
    $properties = $manifest.Package.Properties
    $application = $manifest.Package.Applications.Application | Select-Object -First 1
    $displayName = $properties.DisplayName
    if ([string]::IsNullOrWhiteSpace($displayName) -or $displayName -like "ms-resource:*") {
        $displayName = $identity.Name
    }

    return @{
        ProductName = $displayName
        Publisher = $identity.Publisher
        Version = $identity.Version
        PackageName = $identity.Name
        PackageFamilyName = $identity.Name
        Executable = $application.Executable
    }
}

function Get-ExeInstallCandidates {
    param([string]$FrameworkText)

    $lower = $FrameworkText.ToLowerInvariant()
    if ($lower -match "squirrel|update\.exe") {
        return @("--silent", "-s")
    }
    if ($lower -match "inno setup|inno") {
        return @("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")
    }
    if ($lower -match "burn|wix|bootstrapper") {
        return @("/quiet /norestart", "/passive /norestart")
    }
    if ($lower -match "installshield") {
        return @("/s /v`"/qn /norestart`"", "/silent")
    }
    if ($lower -match "advanced installer") {
        return @("/quiet /norestart", "/exenoui /qn /norestart")
    }

    return @("/quiet /norestart", "/S", "/silent", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")
}

function Write-Utf8BomFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $utf8Bom = [System.Text.UTF8Encoding]::new($true)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8Bom)
}

function Assert-Utf8Bom {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "Script must be UTF-8 with BOM: $Path"
    }
}

if (-not (Test-Path -LiteralPath $SourceInstallerPath)) {
    throw "Source installer not found: $SourceInstallerPath"
}

$sourceItem = Get-Item -LiteralPath $SourceInstallerPath
if ($sourceItem.PSIsContainer) {
    $supportedExtensions = @(".exe", ".msi", ".msp", ".appx", ".appxbundle", ".msix", ".msixbundle", ".ps1", ".cmd", ".bat")
    $installerFiles = @(Get-ChildItem -LiteralPath $sourceItem.FullName -File |
        Where-Object { $supportedExtensions -contains $_.Extension.ToLowerInvariant() } |
        Sort-Object Name)

    if ($installerFiles.Count -eq 0) {
        throw "No supported installer files found in: $($sourceItem.FullName)"
    }

    foreach ($installerFile in $installerFiles) {
        Write-Host "Processing installer:"
        Write-Host "  $($installerFile.FullName)"

        $childArguments = @(
            "-ExecutionPolicy", "Bypass",
            "-NoProfile",
            "-File", "`"$PSCommandPath`"",
            "-SourceInstallerPath", "`"$($installerFile.FullName)`"",
            "-OutputRoot", "`"$OutputRoot`""
        )
        if (-not [string]::IsNullOrWhiteSpace($IntuneWinAppUtilPath)) {
            $childArguments += @("-IntuneWinAppUtilPath", "`"$IntuneWinAppUtilPath`"")
        }
        if ($SkipIntuneWin) {
            $childArguments += "-SkipIntuneWin"
        }

        $child = Start-Process -FilePath "powershell.exe" -ArgumentList ($childArguments -join " ") -Wait -PassThru -NoNewWindow
        if ($child.ExitCode -ne 0) {
            exit $child.ExitCode
        }
    }

    exit 0
}

$sourceInstaller = $sourceItem
$installerKind = Get-InstallerKind -Path $sourceInstaller.FullName
$metadata = @{}
$installCandidates = @()
$uninstallCandidates = @()
$productCode = ""
$packageName = ""

switch ($installerKind) {
    "Msi" {
        $metadata = Get-MsiMetadata -Path $sourceInstaller.FullName
        $productCode = $metadata.ProductCode
        if ([string]::IsNullOrWhiteSpace($AppName)) { $AppName = $metadata.ProductName }
        if ([string]::IsNullOrWhiteSpace($Publisher)) { $Publisher = $metadata.Manufacturer }
        if ([string]::IsNullOrWhiteSpace($DetectionDisplayName)) { $DetectionDisplayName = $metadata.ProductName }
        $installCandidates = @("/qn /norestart")
        $uninstallCandidates = @("/qn /norestart")
    }
    "Exe" {
        $metadata = Get-ExeMetadata -File $sourceInstaller
        if ([string]::IsNullOrWhiteSpace($AppName)) { $AppName = $metadata.ProductName }
        if ([string]::IsNullOrWhiteSpace($Publisher)) { $Publisher = $metadata.Publisher }
        if ([string]::IsNullOrWhiteSpace($DetectionDisplayName)) { $DetectionDisplayName = $metadata.ProductName }
        $installCandidates = Get-ExeInstallCandidates -FrameworkText $metadata.FrameworkText
        $uninstallCandidates = @("/quiet /norestart", "/S", "/silent", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")
    }
    "Appx" {
        $metadata = Get-AppxMetadata -Path $sourceInstaller.FullName
        $packageName = $metadata.PackageName
        if ([string]::IsNullOrWhiteSpace($AppName)) { $AppName = $metadata.ProductName }
        if ([string]::IsNullOrWhiteSpace($Publisher)) { $Publisher = $metadata.Publisher }
        if ([string]::IsNullOrWhiteSpace($DetectionDisplayName)) { $DetectionDisplayName = $metadata.ProductName }
        if ([string]::IsNullOrWhiteSpace($DetectionPackageFamilyName)) { $DetectionPackageFamilyName = $metadata.PackageFamilyName }
        if ([string]::IsNullOrWhiteSpace($DetectionExecutableName)) { $DetectionExecutableName = $metadata.Executable }
    }
    "PowerShell" {
        if ([string]::IsNullOrWhiteSpace($AppName)) { $AppName = $sourceInstaller.BaseName }
        if ([string]::IsNullOrWhiteSpace($DetectionDisplayName)) { $DetectionDisplayName = $AppName }
    }
    "Command" {
        if ([string]::IsNullOrWhiteSpace($AppName)) { $AppName = $sourceInstaller.BaseName }
        if ([string]::IsNullOrWhiteSpace($DetectionDisplayName)) { $DetectionDisplayName = $AppName }
    }
    default {
        throw "Installer type '$installerKind' is recognized but not implemented."
    }
}

if ([string]::IsNullOrWhiteSpace($AppName)) {
    $AppName = $sourceInstaller.BaseName
}
if ([string]::IsNullOrWhiteSpace($DetectionDisplayName)) {
    $DetectionDisplayName = $AppName
}
if ([string]::IsNullOrWhiteSpace($Publisher)) {
    $Publisher = "Unknown"
}
if (-not [string]::IsNullOrWhiteSpace($SilentInstallArguments)) {
    $installCandidates = @($SilentInstallArguments)
}
if (-not [string]::IsNullOrWhiteSpace($SilentUninstallArguments)) {
    $uninstallCandidates = @($SilentUninstallArguments)
}

$safeAppName = ConvertTo-SafeName -Value $AppName
$packageRoot = Join-Path $OutputRoot $safeAppName
$sourceRoot = Join-Path $packageRoot "source"
$payloadRoot = Join-Path $sourceRoot "payload"
$outputDir = Join-Path $packageRoot "output"

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -Path $payloadRoot -ItemType Directory -Force | Out-Null
New-Item -Path $outputDir -ItemType Directory -Force | Out-Null

$payloadFileName = $sourceInstaller.Name
$payloadPath = Join-Path $payloadRoot $payloadFileName
Copy-Item -LiteralPath $sourceInstaller.FullName -Destination $payloadPath -Force

$manifest = @{
    AppName = $AppName
    Publisher = $Publisher
    InstallerKind = $installerKind
    PayloadFileName = $payloadFileName
    DetectionDisplayName = $DetectionDisplayName
    DetectionExecutableName = $DetectionExecutableName
    DetectionInstallLocation = $DetectionInstallLocation
    DetectionPackageFamilyName = $DetectionPackageFamilyName
    ProductCode = $productCode
    PackageName = $packageName
    InstallCandidates = @($installCandidates)
    UninstallCandidates = @($uninstallCandidates)
}
$manifestLiteral = ConvertTo-HashtableLiteral -Value $manifest

$runtimeScript = @"
param(
    [ValidateSet('Install', 'Uninstall', 'Detect')]
    [string]`$Action = 'Install'
)

`$ErrorActionPreference = 'Stop'
`$Package = $manifestLiteral
`$scriptRoot = Split-Path -Parent `$MyInvocation.MyCommand.Path
`$installerPath = Join-Path `$scriptRoot "payload\$payloadFileName"
`$logRoot = Join-Path `$env:ProgramData "$safeAppName\Logs"
`$logPath = Join-Path `$logRoot "`$(`$Action.ToLowerInvariant()).log"
`$successExitCodes = @(0, 1707, 1641, 3010)
`$missingExitCodes = @(1605, 1614)

function Write-Log {
    param([string]`$Message, [string]`$Level = 'INFO')
    if (-not (Test-Path -LiteralPath `$logRoot)) {
        New-Item -Path `$logRoot -ItemType Directory -Force | Out-Null
    }
    `$line = '[{0}] [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), `$Level, `$Message
    Write-Host `$line
    Add-Content -LiteralPath `$logPath -Value `$line -Encoding UTF8
}

function Split-CommandLine {
    param([Parameter(Mandatory = `$true)][string]`$CommandLine)
    `$pattern = '^\s*"([^"]+)"\s*(.*)$'
    if (`$CommandLine -match `$pattern) {
        return @{ FilePath = `$Matches[1]; Arguments = `$Matches[2] }
    }
    `$parts = `$CommandLine.Trim() -split '\s+', 2
    return @{ FilePath = `$parts[0]; Arguments = if (`$parts.Count -gt 1) { `$parts[1] } else { '' } }
}

function Get-UninstallEntries {
    `$registryRoots = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    foreach (`$root in `$registryRoots) {
        if (-not (Test-Path -LiteralPath `$root)) {
            continue
        }

        foreach (`$key in Get-ChildItem -LiteralPath `$root -ErrorAction SilentlyContinue) {
            `$entry = Get-ItemProperty -LiteralPath `$key.PSPath -ErrorAction SilentlyContinue
            if (`$null -eq `$entry) {
                continue
            }
            `$entry | Add-Member -NotePropertyName RegistryKeyName -NotePropertyValue `$key.PSChildName -Force
            `$entry | Add-Member -NotePropertyName RegistryPath -NotePropertyValue `$key.PSPath -Force
            `$entry
        }
    }
}

function Get-MatchingUninstallEntry {
    `$entries = @(Get-UninstallEntries)
    if (-not [string]::IsNullOrWhiteSpace(`$Package.ProductCode)) {
        `$match = `$entries | Where-Object { `$_.RegistryKeyName -eq `$Package.ProductCode } | Select-Object -First 1
        if (`$null -ne `$match) { return `$match }
    }

    `$displayName = `$Package.DetectionDisplayName
    `$match = `$entries | Where-Object { `$_.DisplayName -eq `$displayName } | Select-Object -First 1
    if (`$null -ne `$match) { return `$match }

    return (`$entries | Where-Object { `$_.DisplayName -like "*`$displayName*" } | Select-Object -First 1)
}

function Test-AppDetected {
    if (-not [string]::IsNullOrWhiteSpace(`$Package.DetectionPackageFamilyName)) {
        `$package = Get-AppxPackage -Name `$Package.DetectionPackageFamilyName -ErrorAction SilentlyContinue
        if (`$null -eq `$package) {
            `$package = Get-AppxPackage | Where-Object {
                `$_.Name -eq `$Package.DetectionPackageFamilyName -or
                `$_.PackageFamilyName -eq `$Package.DetectionPackageFamilyName -or
                `$_.PackageFullName -like "*`$(`$Package.DetectionPackageFamilyName)*"
            } | Select-Object -First 1
        }
        if (`$null -ne `$package) {
            Write-Output "`$(`$Package.AppName) detected as `$(`$package.PackageFullName)"
            return `$true
        }
    }

    `$entry = Get-MatchingUninstallEntry
    if (`$null -eq `$entry) {
        return `$false
    }

    if (-not [string]::IsNullOrWhiteSpace(`$Package.DetectionExecutableName)) {
        `$installLocation = `$entry.InstallLocation
        if ([string]::IsNullOrWhiteSpace(`$installLocation) -and -not [string]::IsNullOrWhiteSpace(`$Package.DetectionInstallLocation)) {
            `$installLocation = [Environment]::ExpandEnvironmentVariables(`$Package.DetectionInstallLocation)
        }
        if (-not [string]::IsNullOrWhiteSpace(`$installLocation)) {
            `$exePath = Join-Path `$installLocation `$Package.DetectionExecutableName
            if (Test-Path -LiteralPath `$exePath) {
                Write-Output "`$(`$Package.AppName) detected at `$exePath"
                return `$true
            }
        }
    }

    Write-Output "`$(`$Package.AppName) detected in `$(`$entry.RegistryKeyName)"
    return `$true
}

function Invoke-ProcessChecked {
    param(
        [Parameter(Mandatory = `$true)][string]`$FilePath,
        [string]`$Arguments = '',
        [int[]]`$AllowedExitCodes = `$successExitCodes
    )
    Write-Log "Running: `$FilePath `$Arguments"
    `$process = Start-Process -FilePath `$FilePath -ArgumentList `$Arguments -Wait -PassThru -WindowStyle Hidden
    Write-Log "Process exit code: `$(`$process.ExitCode)"
    if (`$AllowedExitCodes -notcontains `$process.ExitCode) {
        throw "Process failed with exit code `$(`$process.ExitCode): `$FilePath `$Arguments"
    }
    return `$process.ExitCode
}

function Invoke-Install {
    if (Test-AppDetected) {
        Write-Log "`$(`$Package.AppName) is already installed."
        exit 0
    }
    if (-not (Test-Path -LiteralPath `$installerPath)) {
        throw "Installer payload not found: `$installerPath"
    }

    switch (`$Package.InstallerKind) {
        'Msi' {
            `$args = "/i ```"`$installerPath```" `$(`$Package.InstallCandidates[0])"
            Invoke-ProcessChecked -FilePath 'msiexec.exe' -Arguments `$args | Out-Null
        }
        'Msp' {
            `$args = "/p ```"`$installerPath```" /qn /norestart"
            Invoke-ProcessChecked -FilePath 'msiexec.exe' -Arguments `$args | Out-Null
        }
        'Exe' {
            `$lastError = `$null
            foreach (`$candidate in `$Package.InstallCandidates) {
                try {
                    Invoke-ProcessChecked -FilePath `$installerPath -Arguments `$candidate | Out-Null
                    if (Test-AppDetected) {
                        Write-Log "Install detection succeeded after candidate: `$candidate"
                        exit 0
                    }
                    `$lastError = "Install candidate completed but detection did not match: `$candidate"
                    Write-Log `$lastError 'WARN'
                }
                catch {
                    `$lastError = `$_.Exception.Message
                    Write-Log `$lastError 'WARN'
                }
            }
            throw "All EXE install candidates failed or did not detect the app. Last result: `$lastError"
        }
        'Appx' {
            Add-AppxPackage -Path `$installerPath -ErrorAction Stop
        }
        'PowerShell' {
            Invoke-ProcessChecked -FilePath 'powershell.exe' -Arguments "-ExecutionPolicy Bypass -NoProfile -File ```"`$installerPath```"" | Out-Null
        }
        'Command' {
            Invoke-ProcessChecked -FilePath 'cmd.exe' -Arguments "/d /s /c ```"`$installerPath```"" | Out-Null
        }
        default {
            throw "Unsupported installer kind: `$(`$Package.InstallerKind)"
        }
    }

    if (Test-AppDetected) {
        Write-Log "Install completed and detection succeeded."
        exit 0
    }

    throw "Install completed but detection failed for `$(`$Package.AppName)."
}

function Invoke-Uninstall {
    if (`$Package.InstallerKind -eq 'Appx') {
        `$package = Get-AppxPackage -Name `$Package.DetectionPackageFamilyName -ErrorAction SilentlyContinue
        if (`$null -eq `$package) {
            Write-Log "`$(`$Package.AppName) is already absent."
            exit 0
        }
        Remove-AppxPackage -Package `$package.PackageFullName -ErrorAction Stop
        exit 0
    }

    `$entry = Get-MatchingUninstallEntry
    if (`$null -eq `$entry) {
        Write-Log "`$(`$Package.AppName) is already absent."
        exit 0
    }

    if (`$Package.InstallerKind -eq 'Msi' -or `$entry.RegistryKeyName -match '^\{[0-9A-Fa-f-]{36}\}$') {
        `$productCode = if (-not [string]::IsNullOrWhiteSpace(`$Package.ProductCode)) { `$Package.ProductCode } else { `$entry.RegistryKeyName }
        `$args = "/x `$productCode `$(`$Package.UninstallCandidates[0])"
        Invoke-ProcessChecked -FilePath 'msiexec.exe' -Arguments `$args -AllowedExitCodes (`$successExitCodes + `$missingExitCodes) | Out-Null
        exit 0
    }

    `$command = if (-not [string]::IsNullOrWhiteSpace(`$entry.QuietUninstallString)) { `$entry.QuietUninstallString } else { `$entry.UninstallString }
    if ([string]::IsNullOrWhiteSpace(`$command)) {
        throw "No uninstall command found for `$(`$Package.AppName)."
    }

    foreach (`$candidate in @('') + `$Package.UninstallCandidates) {
        `$candidateCommand = `$command
        if (-not [string]::IsNullOrWhiteSpace(`$candidate) -and `$candidateCommand -notmatch [regex]::Escape(`$candidate)) {
            `$candidateCommand = "`$candidateCommand `$candidate"
        }
        `$split = Split-CommandLine -CommandLine `$candidateCommand
        try {
            Invoke-ProcessChecked -FilePath `$split.FilePath -Arguments `$split.Arguments -AllowedExitCodes (`$successExitCodes + `$missingExitCodes) | Out-Null
            exit 0
        }
        catch {
            Write-Log `$_.Exception.Message 'WARN'
        }
    }

    throw "Uninstall failed for `$(`$Package.AppName)."
}

try {
    switch (`$Action) {
        'Detect' {
            if (Test-AppDetected) { exit 0 }
            exit 1
        }
        'Install' {
            Invoke-Install
        }
        'Uninstall' {
            Invoke-Uninstall
        }
    }
}
catch {
    Write-Log `$_.Exception.Message 'ERROR'
    exit 1
}
"@

$installScript = @"
param()
& `$PSScriptRoot\PackageRuntime.ps1 -Action Install
exit `$LASTEXITCODE
"@

$uninstallScript = @"
param()
& `$PSScriptRoot\PackageRuntime.ps1 -Action Uninstall
exit `$LASTEXITCODE
"@

$detectionScript = @"
param()
& `$PSScriptRoot\PackageRuntime.ps1 -Action Detect
exit `$LASTEXITCODE
"@

$runtimeScriptPath = Join-Path $sourceRoot "PackageRuntime.ps1"
$installScriptPath = Join-Path $sourceRoot "Install-App.ps1"
$uninstallScriptPath = Join-Path $sourceRoot "Uninstall-App.ps1"
$detectionScriptPath = Join-Path $sourceRoot "Detect-App.ps1"

Write-Utf8BomFile -Path $runtimeScriptPath -Content $runtimeScript
Write-Utf8BomFile -Path $installScriptPath -Content $installScript
Write-Utf8BomFile -Path $uninstallScriptPath -Content $uninstallScript
Write-Utf8BomFile -Path $detectionScriptPath -Content $detectionScript

Assert-Utf8Bom -Path $runtimeScriptPath
Assert-Utf8Bom -Path $installScriptPath
Assert-Utf8Bom -Path $uninstallScriptPath
Assert-Utf8Bom -Path $detectionScriptPath

$commandsPath = Join-Path $packageRoot "Intune-commands.txt"
$commands = @"
App name: $AppName
Publisher: $Publisher
Installer type detected: $installerKind
Detection display name: $DetectionDisplayName
Detection package family name: $DetectionPackageFamilyName
Detection executable: $DetectionExecutableName
Detected MSI product code: $productCode

Install command:
powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\Install-App.ps1

Uninstall command:
powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\Uninstall-App.ps1

Detection rule:
Use custom script and upload:
$detectionScriptPath

Return codes:
0 = Success
1707 = Success
3010 = Soft reboot
1641 = Hard reboot
1618 = Retry

Package source folder:
$sourceRoot

IntuneWin output folder:
$outputDir
"@
Set-Content -LiteralPath $commandsPath -Value $commands -Encoding UTF8

if (-not $SkipIntuneWin) {
    if ([string]::IsNullOrWhiteSpace($IntuneWinAppUtilPath)) {
        $IntuneWinAppUtilPath = Join-Path $repoRoot "tools\IntuneWinAppUtil.exe"
    }

    if (-not (Test-Path -LiteralPath $IntuneWinAppUtilPath)) {
        throw "IntuneWinAppUtil.exe not found. Pass -IntuneWinAppUtilPath or use -SkipIntuneWin. Source was prepared at: $sourceRoot"
    }

    $process = Start-Process -FilePath $IntuneWinAppUtilPath -ArgumentList "-c `"$sourceRoot`" -s Install-App.ps1 -o `"$outputDir`" -q" -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "IntuneWinAppUtil failed with exit code $($process.ExitCode)."
    }
}

Write-Host "Intune source prepared:"
Write-Host "  $sourceRoot"
Write-Host "Detection script:"
Write-Host "  $detectionScriptPath"
Write-Host "Commands:"
Write-Host "  $commandsPath"
Write-Host "Output:"
Write-Host "  $outputDir"
