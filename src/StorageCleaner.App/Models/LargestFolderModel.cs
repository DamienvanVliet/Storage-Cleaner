namespace StorageCleaner.App.Models;

public sealed record LargestFolderModel(
    string Name,
    string FullPath,
    long SizeBytes,
    double PercentageOfScanned);
