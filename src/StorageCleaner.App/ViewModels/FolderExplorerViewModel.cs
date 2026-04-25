using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageCleaner.App.Services;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Extensions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class FolderExplorerViewModel : ViewModelBase
{
    private readonly ScanWorkspaceService _scanWorkspaceService;
    private readonly IFileSearchService _fileSearchService;
    private readonly ICleanupExecutor _cleanupExecutor;
    private readonly IPathSafetyService _pathSafetyService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly IAppLogger _logger;

    public FolderExplorerViewModel(
        ScanWorkspaceService scanWorkspaceService,
        IFileSearchService fileSearchService,
        ICleanupExecutor cleanupExecutor,
        IPathSafetyService pathSafetyService,
        IDialogService dialogService,
        ISettingsService settingsService,
        IAppLogger logger)
    {
        _scanWorkspaceService = scanWorkspaceService;
        _fileSearchService = fileSearchService;
        _cleanupExecutor = cleanupExecutor;
        _pathSafetyService = pathSafetyService;
        _dialogService = dialogService;
        _settingsService = settingsService;
        _logger = logger;

        RootFolders = [];
        FolderSearchResults = [];
        AllFolders = [];
        FolderFiles = [];
        FileSearchResults = [];
        AllFoldersView = CollectionViewSource.GetDefaultView(AllFolders);
        AllFoldersView.SortDescriptions.Add(new SortDescription(nameof(FolderRowViewModel.SizeBytes), ListSortDirection.Descending));

        _scanWorkspaceService.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ScanWorkspaceService.CurrentResult))
            {
                RebuildTree();
            }
        };

        RebuildTree();
    }

    public ObservableCollection<FolderNodeViewModel> RootFolders { get; }

    public ObservableCollection<FolderNodeViewModel> FolderSearchResults { get; }

    public ObservableCollection<FolderRowViewModel> AllFolders { get; }

    public ICollectionView AllFoldersView { get; }

    public ObservableCollection<FileItemViewModel> FolderFiles { get; }

    public ObservableCollection<FileItemViewModel> FileSearchResults { get; }

    public ScanWorkspaceService ScanWorkspace => _scanWorkspaceService;

    [ObservableProperty]
    private FolderNodeViewModel? selectedFolder;

    [ObservableProperty]
    private FileItemViewModel? selectedFolderFile;

    [ObservableProperty]
    private FileItemViewModel? selectedSearchFile;

    [ObservableProperty]
    private FolderRowViewModel? selectedAllFolder;

    [ObservableProperty]
    private string folderSearchText = string.Empty;

    [ObservableProperty]
    private string fileSearchText = string.Empty;

    [ObservableProperty]
    private bool isSearchingFiles;

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value)
    {
        _ = RefreshSelectedFolderFilesAsync();
    }

    partial void OnFolderSearchTextChanged(string value)
    {
        ApplyFolderSearch(value);
    }

    partial void OnSelectedAllFolderChanged(FolderRowViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        var selected = FindFolderNodeByPath(value.FullPath);
        if (selected is not null)
        {
            SelectedFolder = selected;
        }
    }

    [RelayCommand]
    private async Task RefreshSelectedFolderFilesAsync()
    {
        FolderFiles.Clear();
        if (SelectedFolder is null)
        {
            return;
        }

        IReadOnlyList<FileSearchResult> files;
        try
        {
            files = await _fileSearchService.GetFolderFilesAsync(SelectedFolder.FullPath, null, maxResults: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed loading selected folder files.", ex);
            _dialogService.ShowError("Folder Files", $"{ex.Message}\n\nDetails are logged at:\n{_logger.LogFilePath}");
            return;
        }

        foreach (var file in files)
        {
            FolderFiles.Add(new FileItemViewModel(file));
        }
    }

    [RelayCommand]
    private async Task SearchFilesAsync()
    {
        FileSearchResults.Clear();
        if (string.IsNullOrWhiteSpace(FileSearchText))
        {
            return;
        }

        var roots = _scanWorkspaceService.CurrentResult?.Roots.Select(static root => root.FullPath).ToArray()
                    ?? _scanWorkspaceService.LastRoots;

        if (roots.Count == 0)
        {
            _dialogService.ShowInfo("Search Files", "Run a scan first to search within scanned roots.");
            return;
        }

        IsSearchingFiles = true;
        try
        {
            var results = await _fileSearchService.SearchFilesAsync(roots, FileSearchText.Trim(), maxResults: 600);
            foreach (var result in results)
            {
                FileSearchResults.Add(new FileItemViewModel(result));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("File search failed.", ex);
            _dialogService.ShowError("File Search", $"{ex.Message}\n\nDetails are logged at:\n{_logger.LogFilePath}");
        }
        finally
        {
            IsSearchingFiles = false;
        }
    }

    [RelayCommand]
    private void OpenFolder(FolderNodeViewModel? folder)
    {
        var path = folder?.FullPath ?? SelectedFolder?.FullPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        OpenPathInExplorer(path);
    }

    [RelayCommand]
    private void OpenFolderPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        OpenPathInExplorer(fullPath);
    }

    [RelayCommand]
    private void CopyFolderPath(FolderNodeViewModel? folder)
    {
        var path = folder?.FullPath ?? SelectedFolder?.FullPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        System.Windows.Clipboard.SetText(path);
        _dialogService.ShowInfo("Copied", $"Copied path:\n{path}");
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(FolderNodeViewModel? folder)
    {
        var target = folder ?? SelectedFolder;
        if (target is null)
        {
            return;
        }

        var risk = _pathSafetyService.Evaluate(target.FullPath);
        if (risk.IsProtected)
        {
            _dialogService.ShowError("Deletion Blocked", $"Protected path:\n{target.FullPath}");
            return;
        }

        var confirmation = _dialogService.Confirm(
            "Delete Folder",
            $"Delete folder to Recycle Bin?\n\n{target.FullPath}\n\nEstimated space: {target.SizeBytes.ToSizeString()}");

        if (!confirmation)
        {
            return;
        }

        var allowRisky = true;
        if (risk.IsRisky)
        {
            allowRisky = _dialogService.ConfirmTyped(
                "Risky Location",
                $"This folder is in a risky location ({risk.Reason}).\n\nType DELETE to continue.",
                expectedText: "DELETE",
                warning: true);
        }

        if (!allowRisky)
        {
            return;
        }

        var result = await _cleanupExecutor.ExecuteAsync(
            [new CleanupCandidate
            {
                Category = CleanupCategory.ManualSelection,
                FullPath = target.FullPath,
                IsDirectory = true,
                SizeBytes = target.SizeBytes,
                LastModifiedUtc = target.LastModifiedUtc,
                Risk = risk
            }],
            new CleanupExecutionOptions(
                UseRecycleBin: _settingsService.Current.UseRecycleBinByDefault,
                AllowRiskyPaths: allowRisky,
                SimulationOnly: false,
                QueueLockedForReboot: _settingsService.Current.QueueLockedDeletesOnReboot,
                CaptureRestoreBackup: true));

        if (result.SuccessCount > 0)
        {
            _dialogService.ShowInfo("Folder Deleted", $"Removed {target.Name}.\nReclaimed: {result.ReclaimedBytes.ToSizeString()}");
            await _scanWorkspaceService.RescanLastAsync(_settingsService.Current.MaxScanParallelism, useCache: false);
        }
        else
        {
            _dialogService.ShowError("Delete Failed", result.Items.First().ErrorMessage ?? "Unknown error.");
        }
    }

    [RelayCommand]
    private async Task DeleteFileAsync(FileItemViewModel? fileItem)
    {
        var target = fileItem ?? SelectedFolderFile ?? SelectedSearchFile;
        if (target is null)
        {
            return;
        }

        var risk = _pathSafetyService.Evaluate(target.FullPath);
        if (risk.IsProtected)
        {
            _dialogService.ShowError("Deletion Blocked", $"Protected path:\n{target.FullPath}");
            return;
        }

        var confirmation = _dialogService.Confirm(
            "Delete File",
            $"Delete file to Recycle Bin?\n\n{target.FullPath}\n\nEstimated space: {target.SizeBytes.ToSizeString()}");

        if (!confirmation)
        {
            return;
        }

        var allowRisky = true;
        if (risk.IsRisky)
        {
            allowRisky = _dialogService.ConfirmTyped(
                "Risky Location",
                $"This file is in a risky location ({risk.Reason}).\n\nType DELETE to continue.",
                expectedText: "DELETE",
                warning: true);
        }

        if (!allowRisky)
        {
            return;
        }

        var result = await _cleanupExecutor.ExecuteAsync(
            [new CleanupCandidate
            {
                Category = CleanupCategory.ManualSelection,
                FullPath = target.FullPath,
                IsDirectory = false,
                SizeBytes = target.SizeBytes,
                LastModifiedUtc = target.LastModifiedUtc,
                Risk = risk
            }],
            new CleanupExecutionOptions(
                UseRecycleBin: _settingsService.Current.UseRecycleBinByDefault,
                AllowRiskyPaths: allowRisky,
                SimulationOnly: false,
                QueueLockedForReboot: _settingsService.Current.QueueLockedDeletesOnReboot,
                CaptureRestoreBackup: true));

        if (result.SuccessCount > 0)
        {
            _dialogService.ShowInfo("File Deleted", $"Removed {target.Name}.\nReclaimed: {target.SizeBytes.ToSizeString()}");
            await RefreshSelectedFolderFilesAsync();
        }
        else
        {
            _dialogService.ShowError("Delete Failed", result.Items.First().ErrorMessage ?? "Unknown error.");
        }
    }

    [RelayCommand]
    private void OpenFileLocation(FileItemViewModel? fileItem)
    {
        var target = fileItem ?? SelectedFolderFile ?? SelectedSearchFile;
        if (target is null)
        {
            return;
        }

        OpenPathInExplorer(target.FullPath, select: true);
    }

    [RelayCommand]
    private void OpenFilePath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        OpenPathInExplorer(fullPath, select: true);
    }

    [RelayCommand]
    private async Task RescanSelectedFolderAsync()
    {
        if (SelectedFolder is null)
        {
            return;
        }

        try
        {
            await _scanWorkspaceService.StartScanAsync(
                [SelectedFolder.FullPath],
                _settingsService.Current.MaxScanParallelism,
                useCache: false);
        }
        catch (Exception ex)
        {
            _logger.LogError("Rescan selected folder failed.", ex);
            _dialogService.ShowError("Rescan Failed", $"{ex.Message}\n\nDetails are logged at:\n{_logger.LogFilePath}");
        }
    }

    private void RebuildTree()
    {
        try
        {
            RootFolders.Clear();
            AllFolders.Clear();
            var result = _scanWorkspaceService.CurrentResult;
            if (result is null)
            {
                return;
            }

            foreach (var root in result.Roots.OrderByDescending(static x => x.SizeBytes))
            {
                RootFolders.Add(new FolderNodeViewModel(root));
            }

            foreach (var folder in result.FlattenedFolders.OrderByDescending(static folder => folder.SizeBytes))
            {
                AllFolders.Add(new FolderRowViewModel(folder));
            }

            SelectedAllFolder = AllFolders.FirstOrDefault();
            SelectedFolder = SelectedAllFolder is null
                ? RootFolders.FirstOrDefault()
                : FindFolderNodeByPath(SelectedAllFolder.FullPath) ?? RootFolders.FirstOrDefault();
            ApplyFolderSearch(FolderSearchText);
        }
        catch (Exception ex)
        {
            _logger.LogError("Folder tree rebuild failed.", ex);
            _dialogService.ShowError("Explorer Refresh Failed", $"{ex.Message}\n\nDetails are logged at:\n{_logger.LogFilePath}");
        }
    }

    private void ApplyFolderSearch(string searchText)
    {
        FolderSearchResults.Clear();
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return;
        }

        var query = searchText.Trim();
        foreach (var node in EnumerateAllNodes()
                     .Where(node =>
                         node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         node.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(static node => node.SizeBytes)
                     .Take(300))
        {
            FolderSearchResults.Add(node);
        }
    }

    private IEnumerable<FolderNodeViewModel> EnumerateAllNodes()
    {
        var stack = new Stack<FolderNodeViewModel>(RootFolders.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }

    private FolderNodeViewModel? FindFolderNodeByPath(string fullPath)
    {
        return EnumerateAllNodes()
            .FirstOrDefault(node => string.Equals(node.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static void OpenPathInExplorer(string fullPath, bool select = false)
    {
        if (select)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true });
    }
}
