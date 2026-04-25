using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using StorageCleaner.App.Models;
using StorageCleaner.App.Services;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly ScanWorkspaceService _scanWorkspaceService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;

    public DashboardViewModel(
        ScanWorkspaceService scanWorkspaceService,
        ISettingsService settingsService,
        IDialogService dialogService,
        IAppLogger logger)
    {
        _scanWorkspaceService = scanWorkspaceService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _logger = logger;

        Drives = [];
        LargestFolders = [];
        FeatureTiles = [];

        RefreshDriveCards();
        RefreshLargestFolders();
        BuildFeatureTiles();

        _scanWorkspaceService.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ScanWorkspaceService.CurrentResult))
            {
                RefreshLargestFolders();
            }
        };
    }

    public ObservableCollection<DriveCardModel> Drives { get; }

    public ObservableCollection<LargestFolderModel> LargestFolders { get; }

    public ObservableCollection<FeatureTileModel> FeatureTiles { get; }

    public ScanWorkspaceService ScanWorkspace => _scanWorkspaceService;

    [RelayCommand]
    private void RefreshDrives()
    {
        RefreshDriveCards();
    }

    [RelayCommand]
    private async Task ScanDriveAsync(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            _dialogService.ShowError("Scan Drive", "No drive path was provided.");
            return;
        }

        try
        {
            await _scanWorkspaceService.StartScanAsync(
                [rootPath],
                _settingsService.Current.MaxScanParallelism,
                scanMode: ScanMode.NtfsFast,
                useCache: _settingsService.Current.EnableIncrementalScan);
            if (_scanWorkspaceService.LastIssues.Count > 0)
            {
                _dialogService.ShowInfo(
                    "Scan Completed With Warnings",
                    $"Scan completed with {_scanWorkspaceService.LastIssues.Count} warning(s).\n\nDetails are logged at:\n{_logger.LogFilePath}");
            }
        }
        catch (InvalidOperationException)
        {
            _dialogService.ShowInfo("Scan In Progress", "A scan is already running.");
        }
        catch (Exception ex)
        {
            _logger.LogError("DashboardViewModel.ScanDriveAsync failed.", ex);
            _dialogService.ShowError("Scan Failed", $"{ex.Message}\n\nDetails are logged at:\n{_logger.LogFilePath}");
        }
    }

    private void RefreshDriveCards()
    {
        Drives.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(static d => d.IsReady))
        {
            Drives.Add(new DriveCardModel(
                drive.Name,
                drive.RootDirectory.FullName,
                drive.TotalSize,
                drive.AvailableFreeSpace));
        }
    }

    private void RefreshLargestFolders()
    {
        LargestFolders.Clear();
        var result = _scanWorkspaceService.CurrentResult;
        if (result is null)
        {
            return;
        }

        foreach (var folder in result.FlattenedFolders
                     .Where(static folder => folder.ParentPath is not null)
                     .OrderByDescending(static folder => folder.SizeBytes)
                     .Take(12))
        {
            LargestFolders.Add(new LargestFolderModel(
                folder.Name,
                folder.FullPath,
                folder.SizeBytes,
                folder.PercentageOfScanned));
        }
    }

    private void BuildFeatureTiles()
    {
        FeatureTiles.Clear();
        FeatureTiles.Add(new FeatureTileModel(
            title: "Smart Cleanup",
            description: "Clean temporary and cache files safely.",
            iconGlyph: "\uE74D",
            targetSection: NavigationSection.SafeCleanup,
            isAvailable: true,
            destinationHint: string.Empty));
        FeatureTiles.Add(new FeatureTileModel(
            title: "Duplicate Lab",
            description: "Review duplicate files and keep the right copy.",
            iconGlyph: "\uE8A5",
            targetSection: NavigationSection.AdvancedTools,
            isAvailable: true,
            destinationHint: string.Empty));
        FeatureTiles.Add(new FeatureTileModel(
            title: "Large Files",
            description: "Find the largest folders and files quickly.",
            iconGlyph: "\uE7C3",
            targetSection: NavigationSection.FolderExplorer,
            isAvailable: true,
            destinationHint: string.Empty));
        FeatureTiles.Add(new FeatureTileModel(
            title: "Photo Cleanup",
            description: "Review similar photos and heavy media files.",
            iconGlyph: "\uE91B",
            targetSection: NavigationSection.PhotoCleanup,
            isAvailable: true,
            destinationHint: string.Empty));
        FeatureTiles.Add(new FeatureTileModel(
            title: "App Uninstaller",
            description: "Remove apps and leftover files safely.",
            iconGlyph: "\uE71D",
            targetSection: NavigationSection.AppUninstaller,
            isAvailable: true,
            destinationHint: string.Empty));
        FeatureTiles.Add(new FeatureTileModel(
            title: "Restore Center",
            description: "Recover items cleaned by Storage Cleaner.",
            iconGlyph: "\uE72B",
            targetSection: NavigationSection.AdvancedTools,
            isAvailable: true,
            destinationHint: string.Empty));
        FeatureTiles.Add(new FeatureTileModel(
            title: "Storage Map",
            description: "Visualize disk usage with an interactive map.",
            iconGlyph: "\uE81E",
            targetSection: NavigationSection.AdvancedTools,
            isAvailable: true,
            destinationHint: string.Empty));
        FeatureTiles.Add(new FeatureTileModel(
            title: "Settings",
            description: "Adjust safety, exclusions, and automation rules.",
            iconGlyph: "\uE823",
            targetSection: NavigationSection.Settings,
            isAvailable: true,
            destinationHint: string.Empty));
    }
}
