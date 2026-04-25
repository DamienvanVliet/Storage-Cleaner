using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using StorageCleaner.App.Models;
using StorageCleaner.App.Services;

namespace StorageCleaner.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly DashboardViewModel _dashboardViewModel;
    private readonly ScanDriveViewModel _scanDriveViewModel;
    private readonly FolderExplorerViewModel _folderExplorerViewModel;
    private readonly SafeCleanupViewModel _safeCleanupViewModel;
    private readonly PhotoCleanupViewModel _photoCleanupViewModel;
    private readonly AppUninstallerViewModel _appUninstallerViewModel;
    private readonly AdvancedToolsViewModel _advancedToolsViewModel;
    private readonly CleanupHistoryViewModel _cleanupHistoryViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    public MainViewModel(
        ScanWorkspaceService scanWorkspaceService,
        DashboardViewModel dashboardViewModel,
        ScanDriveViewModel scanDriveViewModel,
        FolderExplorerViewModel folderExplorerViewModel,
        SafeCleanupViewModel safeCleanupViewModel,
        PhotoCleanupViewModel photoCleanupViewModel,
        AppUninstallerViewModel appUninstallerViewModel,
        AdvancedToolsViewModel advancedToolsViewModel,
        CleanupHistoryViewModel cleanupHistoryViewModel,
        SettingsViewModel settingsViewModel)
    {
        _dashboardViewModel = dashboardViewModel;
        _scanDriveViewModel = scanDriveViewModel;
        _folderExplorerViewModel = folderExplorerViewModel;
        _safeCleanupViewModel = safeCleanupViewModel;
        _photoCleanupViewModel = photoCleanupViewModel;
        _appUninstallerViewModel = appUninstallerViewModel;
        _advancedToolsViewModel = advancedToolsViewModel;
        _cleanupHistoryViewModel = cleanupHistoryViewModel;
        _settingsViewModel = settingsViewModel;
        ScanWorkspace = scanWorkspaceService;

        NavigationItems =
        [
            new NavigationItemViewModel { Section = NavigationSection.Dashboard, Title = "Dashboard" },
            new NavigationItemViewModel { Section = NavigationSection.ScanDrive, Title = "Scan Drive" },
            new NavigationItemViewModel { Section = NavigationSection.FolderExplorer, Title = "Folder Explorer" },
            new NavigationItemViewModel { Section = NavigationSection.SafeCleanup, Title = "Safe Cleanup" },
            new NavigationItemViewModel { Section = NavigationSection.PhotoCleanup, Title = "Photo Cleanup" },
            new NavigationItemViewModel { Section = NavigationSection.AppUninstaller, Title = "App Uninstaller" },
            new NavigationItemViewModel { Section = NavigationSection.AdvancedTools, Title = "Advanced Tools" },
            new NavigationItemViewModel { Section = NavigationSection.CleanupHistory, Title = "Cleanup History" },
            new NavigationItemViewModel { Section = NavigationSection.Settings, Title = "Settings" }
        ];

        SelectedNavigationItem = NavigationItems[0];
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public ScanWorkspaceService ScanWorkspace { get; }

    [ObservableProperty]
    private NavigationItemViewModel? selectedNavigationItem;

    [ObservableProperty]
    private object? currentPageViewModel;

    [ObservableProperty]
    private string currentSectionTitle = "Dashboard";

    [ObservableProperty]
    private bool isHomeScreen = true;

    [RelayCommand]
    private void NavigateToSection(NavigationSection? section)
    {
        if (section is null)
        {
            return;
        }

        var target = NavigationItems.FirstOrDefault(item => item.Section == section.Value);
        if (target is null)
        {
            return;
        }

        SelectedNavigationItem = target;
    }

    [RelayCommand]
    private void NavigateHome()
    {
        NavigateToSection(NavigationSection.Dashboard);
    }

    partial void OnSelectedNavigationItemChanged(NavigationItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentSectionTitle = value.Title;
        IsHomeScreen = value.Section == NavigationSection.Dashboard;
        CurrentPageViewModel = value.Section switch
        {
            NavigationSection.Dashboard => _dashboardViewModel,
            NavigationSection.ScanDrive => _scanDriveViewModel,
            NavigationSection.FolderExplorer => _folderExplorerViewModel,
            NavigationSection.SafeCleanup => _safeCleanupViewModel,
            NavigationSection.PhotoCleanup => _photoCleanupViewModel,
            NavigationSection.AppUninstaller => _appUninstallerViewModel,
            NavigationSection.AdvancedTools => _advancedToolsViewModel,
            NavigationSection.CleanupHistory => _cleanupHistoryViewModel,
            NavigationSection.Settings => _settingsViewModel,
            _ => _dashboardViewModel
        };

        if (value.Section == NavigationSection.CleanupHistory)
        {
            _cleanupHistoryViewModel.RefreshCommand.Execute(null);
        }

        if (value.Section == NavigationSection.AppUninstaller &&
            _appUninstallerViewModel.InstalledApps.Count == 0)
        {
            _appUninstallerViewModel.RefreshAppsCommand.Execute(null);
        }
    }
}
