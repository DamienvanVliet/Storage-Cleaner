using System.Collections.ObjectModel;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public sealed class FolderNodeViewModel : ViewModelBase
{
    public FolderNodeViewModel(FolderNode node)
    {
        Node = node;
        Children = new ObservableCollection<FolderNodeViewModel>(node.Children.Select(static child => new FolderNodeViewModel(child)));
    }

    public FolderNode Node { get; }

    public ObservableCollection<FolderNodeViewModel> Children { get; }

    public string Name => Node.Name;

    public string FullPath => Node.FullPath;

    public long SizeBytes => Node.SizeBytes;

    public double Percentage => Node.PercentageOfScanned;

    public long FileCount => Node.FileCount;

    public long FolderCount => Node.FolderCount;

    public DateTime LastModifiedUtc => Node.LastModifiedUtc;

    public bool IsInaccessible => Node.IsInaccessible;

    public string? Warning => Node.Warning;
}
