namespace StorageCleaner.Core.Models;

public sealed record CleanupProgress(
    CleanupCategory Category,
    long CandidatesFound,
    long EstimatedBytes,
    string? CurrentPath);
