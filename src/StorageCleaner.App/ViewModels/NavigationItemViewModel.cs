using StorageCleaner.App.Models;

namespace StorageCleaner.App.ViewModels;

public sealed class NavigationItemViewModel
{
    public required NavigationSection Section { get; init; }

    public required string Title { get; init; }
}
