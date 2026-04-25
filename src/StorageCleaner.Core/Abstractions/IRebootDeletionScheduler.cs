namespace StorageCleaner.Core.Abstractions;

public interface IRebootDeletionScheduler
{
    bool TryScheduleDelete(string path, out string? errorMessage);
}
