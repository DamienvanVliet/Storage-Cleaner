namespace StorageCleaner.Core.Models;

public sealed record StorageAnalyticsProgress(
    long ProcessedFiles,
    string? CurrentPath);

public sealed record FileTypeAnalyticsBucket(
    string Category,
    long FileCount,
    long TotalBytes,
    double PercentageOfBytes);

public sealed record TreemapTile(
    string FullPath,
    string Name,
    long SizeBytes,
    double PercentageOfScanned,
    int Depth,
    long FileCount,
    long FolderCount,
    bool IsInaccessible,
    string CategoryHint);

public sealed class StorageAnalyticsResult
{
    public required IReadOnlyList<FileTypeAnalyticsBucket> Categories { get; init; }

    public required IReadOnlyList<TreemapTile> TreemapTiles { get; init; }

    public required long TotalFiles { get; init; }

    public required long TotalBytes { get; init; }
}
