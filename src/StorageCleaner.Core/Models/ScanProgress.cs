namespace StorageCleaner.Core.Models;

public sealed record ScanProgress(
    long ProcessedDirectories,
    long ProcessedFiles,
    long ProcessedBytes,
    long EstimatedTotalBytes,
    double Percentage,
    string? CurrentPath,
    TimeSpan Elapsed,
    bool IsPaused);
