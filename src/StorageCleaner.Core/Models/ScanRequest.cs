namespace StorageCleaner.Core.Models;

public sealed record ScanRequest(
    IReadOnlyCollection<string> Roots,
    int MaxDegreeOfParallelism = 4,
    bool UseCache = true,
    ScanMode Mode = ScanMode.Standard);
