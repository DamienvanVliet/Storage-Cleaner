using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public sealed class FolderRowViewModel
{
    public FolderRowViewModel(FolderNode folder)
    {
        Folder = folder;
    }

    public FolderNode Folder { get; }

    public string Name => Folder.Name;

    public string FullPath => Folder.FullPath;

    public long SizeBytes => Folder.SizeBytes;

    public double Percentage => Folder.PercentageOfScanned;

    public long FileCount => Folder.FileCount;

    public long FolderCount => Folder.FolderCount;

    public DateTime LastModifiedUtc => Folder.LastModifiedUtc;

    public bool IsInaccessible => Folder.IsInaccessible;
}
