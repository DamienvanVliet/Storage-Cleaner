using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using StorageCleaner.App.Services;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class ScanDriveViewModel : ViewModelBase
{
    private readonly ScanWorkspaceService _scanWorkspaceService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;

    public ScanDriveViewModel(
        ScanWorkspaceService scanWorkspaceService,
        ISettingsService settingsService,
        IDialogService dialogService,
        IAppLogger logger)
    {
        _scanWorkspaceService = scanWorkspaceService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _logger = logger;

        AvailableRoots = [];
        RefreshDrives();
        SelectedRootPath = AvailableRoots.FirstOrDefault();
    }

    public ObservableCollection<string> AvailableRoots { get; }

    public ScanWorkspaceService ScanWorkspace => _scanWorkspaceService;

    public IReadOnlyList<ScanMode> ScanModes { get; } = Enum.GetValues<ScanMode>();

    [ObservableProperty]
    private string? selectedRootPath;

    [ObservableProperty]
    private ScanMode selectedScanMode = ScanMode.Standard;

    [RelayCommand]
    private void RefreshDrives()
    {
        AvailableRoots.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(static d => d.IsReady))
        {
            AvailableRoots.Add(drive.RootDirectory.FullName);
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Folder To Scan",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            if (!string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                SelectedRootPath = dialog.FolderName;
            }
        }
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedRootPath) || !Directory.Exists(SelectedRootPath))
        {
            _dialogService.ShowError("Invalid Path", "Select a valid drive or folder to scan.");
            return;
        }

        try
        {
            await _scanWorkspaceService.StartScanAsync(
                [SelectedRootPath],
                _settingsService.Current.MaxScanParallelism,
                scanMode: SelectedScanMode,
                useCache: _settingsService.Current.EnableIncrementalScan);

            if (_scanWorkspaceService.LastIssues.Count > 0)
            {
                _dialogService.ShowInfo(
                    "Scan Completed With Warnings",
                    $"Scan completed with {_scanWorkspaceService.LastIssues.Count} warning(s).\n\nCommon causes include permission-denied folders, locked files, and system-protected directories.\n\nDetails are logged at:\n{_logger.LogFilePath}");
            }
        }
        catch (InvalidOperationException)
        {
            _dialogService.ShowInfo("Scan In Progress", "A scan is already running.");
        }
        catch (Exception ex)
        {
            _logger.LogError("ScanDriveViewModel.StartScanAsync failed.", ex);
            _dialogService.ShowError("Scan Failed", $"{ex.Message}\n\nDetails are logged at:\n{_logger.LogFilePath}");
        }
    }

    [RelayCommand]
    private void PauseOrResume()
    {
        if (!_scanWorkspaceService.IsScanning)
        {
            return;
        }

        if (_scanWorkspaceService.IsPaused)
        {
            _scanWorkspaceService.Resume();
        }
        else
        {
            _scanWorkspaceService.Pause();
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanWorkspaceService.Cancel();
    }
}
