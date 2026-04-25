# Storage Cleaner

Storage Cleaner is a Windows desktop app that helps you see what is using your disk space and clean files safely.

## 1. What the app is

Storage Cleaner is a real WPF desktop application for Windows 10/11.

It scans your drives and folders, shows exact folder sizes, and lets you clean data with safety checks.

## 2. What the app does

- Scans drives or folders recursively.
- Shows folder size, file count, folder count, and percent of scanned space.
- Lets you sort and search results.
- Lets you clean files/folders from inside the app.
- Uses Recycle Bin by default.
- Supports Safe Cleanup, Duplicate Lab, Photo Cleanup, App Uninstaller, Snapshot Diff, Restore Center, and Storage Map.

## 3. How to install it

### Easy install (recommended)

1. Build installer package:
   ```powershell
   .\scripts\package-installer.ps1
   ```
2. Open:
   `artifacts\installer\StorageCleaner-win-x64`
3. Run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-StorageCleaner.ps1
   ```
4. Start the app from the Start Menu: `Storage Cleaner`.

### Uninstall

Run:
```powershell
powershell -ExecutionPolicy Bypass -File .\Uninstall-StorageCleaner.ps1
```

You can run it from the installer folder or from the installed app folder.

## 4. How to run it

From source:
```powershell
dotnet run --project .\src\StorageCleaner.App\StorageCleaner.App.csproj
```

After install:
- Open `Storage Cleaner` from Start Menu.

## 5. How to build it

Quick build + tests:
```powershell
.\scripts\build.ps1
```

Manual:
```powershell
dotnet restore .\StorageCleaner.sln
dotnet build .\StorageCleaner.sln -c Release
dotnet test .\StorageCleaner.sln -c Release --no-build
```

Publish portable release:
```powershell
.\scripts\publish-win-x64.ps1
```

Output:
`artifacts\publish\win-x64`

## 6. How each tab works

- `Home`: Tile launcher for all features.
- `Scan Drive`: Starts scans, shows progress, warnings, and scan mode.
- `Folder Explorer`: Browse scanned folders/files, sort by size, open in Explorer, clean selected items.
- `Safe Cleanup`: Preview safe cleanup categories, filter/search results, clean with safety checks.
- `Photo Cleanup`: Finds screenshots, similar photos, blurry photos, and large videos. Review first, then clean.
- `App Uninstaller`: Lists installed apps, scans leftovers, preview uninstall/cleanup.
- `Advanced Tools`: Duplicate Lab, Waste Analysis, Treemap analytics, Snapshot Diff, Restore Center.
- `Cleanup History`: Full audit log of cleanup actions with filters/search.
- `Settings`: Theme, safety defaults, exclusions, protected roots, automation rules.

## 7. Safety explanation

- The app always shows a preview before deleting.
- The app creates restore backups before real cleanup actions.
- Risky actions require typed confirmation.
- System/protected paths are blocked by safety logic.
- You can restore cleaned items in Restore Center when backups exist.
- Cleanup actions are logged in Cleanup History.

## 8. Troubleshooting

- App does not start:
  - Build again with `.\scripts\build.ps1`
  - Check .NET SDK 8 is installed: `dotnet --info`
- Scan warnings:
  - Some folders are protected by Windows (normal behavior).
  - Check warning table in Scan Drive page.
- Permission errors:
  - Try running as Administrator for broader access.
- Cleanup errors:
  - Locked files can be queued for reboot (if enabled).
- Logs:
  - `%LOCALAPPDATA%\StorageCleaner\logs\storage-cleaner.log`

## 9. Basic developer setup

1. Install .NET 8 SDK.
2. Clone repository.
3. Run:
   ```powershell
   dotnet restore .\StorageCleaner.sln
   dotnet build .\StorageCleaner.sln
   dotnet test .\StorageCleaner.sln
   ```
4. Start app:
   ```powershell
   dotnet run --project .\src\StorageCleaner.App\StorageCleaner.App.csproj
   ```

Project layout:
- `src/StorageCleaner.App` (WPF UI)
- `src/StorageCleaner.Core` (scanner + cleanup engine)
- `src/StorageCleaner.Cli` (CLI)
- `tests/StorageCleaner.Core.Tests` (unit tests)

## 10. License

This project is licensed under the MIT License.
