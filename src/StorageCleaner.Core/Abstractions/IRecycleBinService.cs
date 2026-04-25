namespace StorageCleaner.Core.Abstractions;

public interface IRecycleBinService
{
    Task DeleteFileAsync(string filePath, bool useRecycleBin, CancellationToken cancellationToken = default);

    Task DeleteDirectoryAsync(string directoryPath, bool useRecycleBin, CancellationToken cancellationToken = default);
}
