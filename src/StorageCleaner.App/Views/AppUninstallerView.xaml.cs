using System.Windows;
using System.Windows.Controls;
using StorageCleaner.App.ViewModels;

namespace StorageCleaner.App.Views;

public partial class AppUninstallerView : UserControl
{
    public AppUninstallerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCompactMode();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCompactMode();
    }

    private void UpdateCompactMode()
    {
        if (DataContext is AppUninstallerViewModel viewModel)
        {
            viewModel.IsCompactMode = ActualWidth < 1360;
            AppsRowDefinition.Height = viewModel.IsCompactMode
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(1.2, GridUnitType.Star);
            LeftoversRowDefinition.Height = new GridLength(1, GridUnitType.Star);
        }
    }
}
