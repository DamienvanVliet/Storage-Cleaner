namespace StorageCleaner.Core.Models;

public sealed record DuplicateScanProgress(
    long ScannedFiles,
    long CandidateGroups,
    string? CurrentPath);

public sealed class DuplicateFileItem
{
    public required string FullPath { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTime LastModifiedUtc { get; init; }

    public required DateTime LastAccessUtc { get; init; }

    public string? FileIdentityKey { get; init; }

    public bool IsHardLinkAlias { get; init; }
}

public sealed class DuplicateFileGroup
{
    public required string Hash { get; init; }

    public required long SizeBytes { get; init; }

    public required IReadOnlyList<DuplicateFileItem> Files { get; init; }

    public int DistinctPhysicalFileCount =>
        Files.Count(static file => !file.IsHardLinkAlias);

    public long WastedBytes => Math.Max(0, DistinctPhysicalFileCount - 1) * SizeBytes;
}
