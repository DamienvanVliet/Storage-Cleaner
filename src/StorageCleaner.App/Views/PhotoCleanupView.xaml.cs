using System.Windows;
using System.Windows.Controls;
using StorageCleaner.App.ViewModels;

namespace StorageCleaner.App.Views;

public partial class PhotoCleanupView : UserControl
{
    public PhotoCleanupView()
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
        if (DataContext is PhotoCleanupViewModel viewModel)
        {
            viewModel.IsCompactMode = ActualWidth < 1320;
            SimilarGroupsRowDefinition.Height = viewModel.IsCompactMode
                ? new GridLength(0)
                : new GridLength(180);
        }
    }
}
