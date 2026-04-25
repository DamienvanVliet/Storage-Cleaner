namespace StorageCleaner.Core.Models;

public sealed class CleanupCandidate
{
    public required CleanupCategory Category { get; init; }

    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTime LastModifiedUtc { get; init; }

    public required PathRisk Risk { get; init; }

    public string DisplayName => Path.GetFileName(FullPath) is { Length: > 0 } name ? name : FullPath;
}
