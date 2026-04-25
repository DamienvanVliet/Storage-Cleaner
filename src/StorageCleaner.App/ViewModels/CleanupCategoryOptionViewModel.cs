using CommunityToolkit.Mvvm.ComponentModel;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class CleanupCategoryOptionViewModel : ObservableObject
{
    public CleanupCategoryOptionViewModel(CleanupCategory category, string title, string description, bool isSelected = true)
    {
        Category = category;
        Title = title;
        Description = description;
        IsSelected = isSelected;
    }

    public CleanupCategory Category { get; }

    public string Title { get; }

    public string Description { get; }

    [ObservableProperty]
    private bool isSelected;
}
