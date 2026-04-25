using Microsoft.VisualBasic.FileIO;
using StorageCleaner.Core.Abstractions;

namespace StorageCleaner.Core.Services;

public sealed class RecycleBinService : IRecycleBinService
{
    public Task DeleteFileAsync(string filePath, bool useRecycleBin, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(filePath))
            {
                return;
            }

            if (useRecycleBin)
            {
                FileSystem.DeleteFile(
                    filePath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.DoNothing);
            }
            else
            {
                File.Delete(filePath);
            }
        }, cancellationToken);
    }

    public Task DeleteDirectoryAsync(string directoryPath, bool useRecycleBin, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            if (useRecycleBin)
            {
                FileSystem.DeleteDirectory(
                    directoryPath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.DoNothing);
            }
            else
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }, cancellationToken);
    }
}
