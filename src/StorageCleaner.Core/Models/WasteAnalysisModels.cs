namespace StorageCleaner.Core.Models;

public sealed record WasteAnalysisProgress(
    long ProcessedFiles,
    string? CurrentPath);

public sealed record WasteCategoryBucket(
    string Category,
    long FileCount,
    long TotalBytes);

public sealed record WasteAgeBucket(
    string Label,
    long FileCount,
    long TotalBytes);

public sealed class WasteAnalysisResult
{
    public required IReadOnlyList<WasteCategoryBucket> TopExtensions { get; init; }

    public required IReadOnlyList<WasteAgeBucket> AgeBuckets { get; init; }

    public required IReadOnlyList<FileSearchResult> NeverAccessedCandidates { get; init; }

    public required long TotalFiles { get; init; }

    public required long TotalBytes { get; init; }
}
