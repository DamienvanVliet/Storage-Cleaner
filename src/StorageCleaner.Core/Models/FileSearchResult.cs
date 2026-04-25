namespace StorageCleaner.Core.Models;

public sealed record FileSearchResult(
    string Name,
    string FullPath,
    long SizeBytes,
    DateTime LastModifiedUtc,
    string ParentFolder);
