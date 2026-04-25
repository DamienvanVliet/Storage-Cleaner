namespace StorageCleaner.Core.Models;

public sealed class FolderNode
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public string? ParentPath { get; init; }

    public required long SizeBytes { get; init; }

    public required double PercentageOfScanned { get; init; }

    public required long FileCount { get; init; }

    public required long FolderCount { get; init; }

    public required DateTime LastModifiedUtc { get; init; }

    public bool IsInaccessible { get; init; }

    public string? Warning { get; init; }

    public required IReadOnlyList<FolderNode> Children { get; init; }
}
