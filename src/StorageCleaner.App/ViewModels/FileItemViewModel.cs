using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public sealed class FileItemViewModel
{
    public FileItemViewModel(FileSearchResult result)
    {
        Result = result;
    }

    public FileSearchResult Result { get; }

    public string Name => Result.Name;

    public string FullPath => Result.FullPath;

    public long SizeBytes => Result.SizeBytes;

    public DateTime LastModifiedUtc => Result.LastModifiedUtc;

    public string ParentFolder => Result.ParentFolder;
}
