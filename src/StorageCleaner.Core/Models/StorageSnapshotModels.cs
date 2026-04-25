namespace StorageCleaner.Core.Models;

public sealed class StorageSnapshot
{
    public required string SnapshotId { get; init; }

    public required string Label { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required IReadOnlyList<string> Roots { get; init; }

    public required long TotalBytes { get; init; }

    public required long TotalFiles { get; init; }

    public required long TotalFolders { get; init; }

    public required IReadOnlyList<StorageSnapshotFolderEntry> Folders { get; init; }

    public string DisplayLabel => $"{CreatedAt:yyyy-MM-dd HH:mm:ss}  |  {Label}";
}

public sealed record StorageSnapshotFolderEntry(
    string FullPath,
    long SizeBytes,
    long FileCount,
    long FolderCount,
    DateTime LastModifiedUtc);

public sealed record SnapshotDiffFolderChange(
    string FullPath,
    long BeforeBytes,
    long AfterBytes,
    long DeltaBytes,
    long BeforeFiles,
    long AfterFiles,
    long DeltaFiles);

public sealed record SnapshotDiffCategoryChange(
    CleanupCategory Category,
    long ReclaimedBytes,
    int SuccessCount,
    int FailedCount);

public sealed record SnapshotDiffAction(
    string RunId,
    DateTimeOffset Timestamp,
    CleanupCategory Category,
    string FullPath,
    long ReclaimedBytes,
    bool Success,
    string? ErrorMessage,
    bool IsSimulation,
    bool QueuedForReboot);

public sealed class SnapshotDiffResult
{
    public required StorageSnapshot Before { get; init; }

    public required StorageSnapshot After { get; init; }

    public required long DeltaBytes { get; init; }

    public required long DeltaFiles { get; init; }

    public required long DeltaFolders { get; init; }

    public required IReadOnlyList<SnapshotDiffFolderChange> TopFolderChanges { get; init; }

    public required IReadOnlyList<SnapshotDiffCategoryChange> CategoryChanges { get; init; }

    public required IReadOnlyList<SnapshotDiffAction> Actions { get; init; }

    public long PositiveSavingsBytes => Math.Max(0, Before.TotalBytes - After.TotalBytes);
}
