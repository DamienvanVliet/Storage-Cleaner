namespace StorageCleaner.Core.Abstractions;

public interface ILockInspector
{
    IReadOnlyList<string> TryGetLockingProcesses(string path);
}
