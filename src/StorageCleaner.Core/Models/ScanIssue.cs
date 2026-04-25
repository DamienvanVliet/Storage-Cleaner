namespace StorageCleaner.Core.Models;

public sealed record ScanIssue(
    string Path,
    string Message,
    string ExceptionType,
    DateTimeOffset Timestamp);
