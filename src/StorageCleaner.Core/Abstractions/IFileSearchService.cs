using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Abstractions;

public interface IFileSearchService
{
    Task<IReadOnlyList<FileSearchResult>> SearchFilesAsync(
        IReadOnlyCollection<string> roots,
        string query,
        int maxResults = 500,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileSearchResult>> GetFolderFilesAsync(
        string folderPath,
        string? query = null,
        int maxResults = 500,
        CancellationToken cancellationToken = default);
}
