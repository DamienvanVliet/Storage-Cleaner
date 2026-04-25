namespace StorageCleaner.Core.Models;

public sealed record InstalledAppInfo(
    string AppId,
    string DisplayName,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    long? EstimatedSizeBytes,
    DateTime? LastUsedLocal,
    string? UninstallCommand,
    string RegistryPath);

public sealed record AppLeftoverCandidate(
    string FullPath,
    bool IsDirectory,
    long EstimatedBytes,
    string Reason);

public sealed record UninstallLaunchResult(
    bool Started,
    string Message);
