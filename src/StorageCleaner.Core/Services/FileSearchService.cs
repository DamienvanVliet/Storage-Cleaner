using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class FileSearchService : IFileSearchService
{
    private readonly IExclusionService _exclusionService;

    public FileSearchService(IExclusionService? exclusionService = null)
    {
        _exclusionService = exclusionService ?? NoopExclusionService.Instance;
    }

    public Task<IReadOnlyList<FileSearchResult>> SearchFilesAsync(
        IReadOnlyCollection<string> roots,
        string query,
        int maxResults = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<FileSearchResult>>([]);
        }

        var normalizedRoots = roots
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.Run<IReadOnlyList<FileSearchResult>>(() =>
        {
            var results = new List<FileSearchResult>(Math.Min(maxResults, 256));
            foreach (var root in normalizedRoots)
            {
                SearchInDirectory(root, query, recursive: true, maxResults, results, cancellationToken, _exclusionService);
                if (results.Count >= maxResults)
                {
                    break;
                }
            }

            return results
                .OrderByDescending(static x => x.SizeBytes)
                .ThenBy(static x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<FileSearchResult>> GetFolderFilesAsync(
        string folderPath,
        string? query = null,
        int maxResults = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        var normalizedPath = NormalizePath(folderPath);

        return Task.Run<IReadOnlyList<FileSearchResult>>(() =>
        {
            var results = new List<FileSearchResult>(Math.Min(maxResults, 256));
            SearchInDirectory(normalizedPath, query, recursive: false, maxResults, results, cancellationToken, _exclusionService);
            return results
                .OrderByDescending(static x => x.SizeBytes)
                .ThenBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    private static void SearchInDirectory(
        string rootPath,
        string? query,
        bool recursive,
        int maxResults,
        List<FileSearchResult> results,
        CancellationToken cancellationToken,
        IExclusionService exclusionService)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        var normalizedQuery = query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(normalizedQuery);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        while (pendingDirectories.Count > 0 && results.Count < maxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pendingDirectories.Pop();
            if (exclusionService.Match(current).IsExcluded)
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*", options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }

                    if (exclusionService.Match(fileInfo.FullName).IsExcluded)
                    {
                        continue;
                    }

                    if (hasQuery && fileInfo.Name.IndexOf(normalizedQuery!, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    results.Add(new FileSearchResult(
                        fileInfo.Name,
                        fileInfo.FullName,
                        fileInfo.Length,
                        fileInfo.LastWriteTimeUtc,
                        fileInfo.DirectoryName ?? current));

                    if (results.Count >= maxResults)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }
            }

            if (!recursive)
            {
                continue;
            }

            IEnumerable<string> subDirectories;
            try
            {
                subDirectories = Directory.EnumerateDirectories(current, "*", options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new DirectoryInfo(subDirectory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (exclusionService.Match(info.FullName).IsExcluded)
                    {
                        continue;
                    }

                    pendingDirectories.Push(info.FullName);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }
            }
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        if (Path.GetPathRoot(fullPath)?.Equals(fullPath, StringComparison.OrdinalIgnoreCase) == true)
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
