namespace StorageCleaner.Core.Models;

public sealed record CleanupExecutionOptions(
    bool UseRecycleBin = true,
    bool AllowRiskyPaths = false,
    bool SimulationOnly = false,
    bool QueueLockedForReboot = true,
    bool CaptureRestoreBackup = true);
