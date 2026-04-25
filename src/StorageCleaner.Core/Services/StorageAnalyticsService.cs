using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class StorageAnalyticsService : IStorageAnalyticsService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".flv"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".raw", ".tif", ".tiff", ".svg"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".wma"
    };

    private static readonly HashSet<string> AppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".dll", ".sys", ".appx", ".appxbundle", ".bat", ".cmd", ".ps1"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".rtf", ".odt", ".md"
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".ts", ".tsx", ".jsx", ".cpp", ".c", ".h", ".hpp", ".py", ".java", ".go", ".rs",
        ".json", ".xml", ".yaml", ".yml", ".sql", ".html", ".css", ".scss", ".sh"
    };

    private static readonly HashSet<string> LogExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".etl", ".trace", ".dmp"
    };

    private static readonly HashSet<string> CacheExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".cache", ".dat", ".idx"
    };

    private readonly IExclusionService _exclusionService;

    public StorageAnalyticsService(IExclusionService? exclusionService = null)
    {
        _exclusionService = exclusionService ?? NoopExclusionService.Instance;
    }

    public Task<StorageAnalyticsResult> AnalyzeAsync(
        IReadOnlyCollection<string> roots,
        ScanResult? scanResult = null,
        int maxTreemapTiles = 300,
        IProgress<StorageAnalyticsProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var normalizedRoots = roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.Run(() =>
        {
            if (normalizedRoots.Length == 0)
            {
                return new StorageAnalyticsResult
                {
                    Categories = [],
                    TreemapTiles = [],
                    TotalFiles = 0,
                    TotalBytes = 0
                };
            }

            var byCategory = new Dictionary<string, (long Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
            long totalFiles = 0;
            long totalBytes = 0;

            foreach (var root in normalizedRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AnalyzeRoot(root, byCategory, progress, ref totalFiles, ref totalBytes, cancellationToken);
            }

            var categories = byCategory
                .Select(pair =>
                {
                    var percentage = totalBytes <= 0 ? 0 : (double)pair.Value.Bytes / totalBytes * 100d;
                    return new FileTypeAnalyticsBucket(
                        pair.Key,
                        pair.Value.Files,
                        pair.Value.Bytes,
                        percentage);
                })
                .OrderByDescending(static bucket => bucket.TotalBytes)
                .ThenBy(static bucket => bucket.Category, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var treemapTiles = BuildTreemapTiles(scanResult, normalizedRoots, maxTreemapTiles);

            return new StorageAnalyticsResult
            {
                Categories = categories,
                TreemapTiles = treemapTiles,
                TotalFiles = totalFiles,
                TotalBytes = totalBytes
            };
        }, cancellationToken);
    }

    private void AnalyzeRoot(
        string rootPath,
        Dictionary<string, (long Files, long Bytes)> byCategory,
        IProgress<StorageAnalyticsProgress>? progress,
        ref long totalFiles,
        ref long totalBytes,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootPath) || _exclusionService.Match(rootPath).IsExcluded)
        {
            return;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            if (_exclusionService.Match(current).IsExcluded)
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*", options);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                continue;
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_exclusionService.Match(filePath).IsExcluded)
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(filePath);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    continue;
                }

                var category = Classify(info.FullName, info.Extension);
                if (!byCategory.TryGetValue(category, out var bucket))
                {
                    bucket = (0, 0);
                }

                byCategory[category] = (bucket.Files + 1, bucket.Bytes + info.Length);
                totalFiles++;
                totalBytes += info.Length;

                if ((totalFiles & 0x7FF) == 0)
                {
                    progress?.Report(new StorageAnalyticsProgress(totalFiles, info.FullName));
                }
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current, "*", options);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                continue;
            }

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var dirInfo = new DirectoryInfo(directory);
                    if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (_exclusionService.Match(dirInfo.FullName).IsExcluded)
                    {
                        continue;
                    }

                    stack.Push(dirInfo.FullName);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    continue;
                }
            }
        }
    }

    private static IReadOnlyList<TreemapTile> BuildTreemapTiles(
        ScanResult? scanResult,
        IReadOnlyList<string> roots,
        int maxTreemapTiles)
    {
        if (scanResult is null || scanResult.Roots.Count == 0)
        {
            return [];
        }

        var boundedCount = Math.Clamp(maxTreemapTiles, 25, 2_000);
        return scanResult.FlattenedFolders
            .OrderByDescending(static folder => folder.SizeBytes)
            .Take(boundedCount)
            .Select(folder =>
            {
                var root = roots
                    .Where(candidate => IsSubPathOfOrEqual(folder.FullPath, candidate))
                    .OrderByDescending(static candidate => candidate.Length)
                    .FirstOrDefault()
                    ?? roots[0];

                var rootNode = scanResult.Roots
                    .FirstOrDefault(node => string.Equals(node.FullPath, root, StringComparison.OrdinalIgnoreCase));

                var rootBytes = rootNode?.SizeBytes ?? scanResult.TotalScannedBytes;
                var percentage = rootBytes <= 0 ? 0 : (double)folder.SizeBytes / rootBytes * 100d;

                return new TreemapTile(
                    folder.FullPath,
                    folder.Name,
                    folder.SizeBytes,
                    Math.Clamp(percentage, 0, 100),
                    CalculateDepth(root, folder.FullPath),
                    folder.FileCount,
                    folder.FolderCount,
                    folder.IsInaccessible,
                    Classify(folder.FullPath, extension: string.Empty));
            })
            .ToArray();
    }

    private static string Classify(string fullPath, string extension)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return "Other";
        }

        if (Contains(fullPath, @"\windows\") ||
            Contains(fullPath, @"\system32\") ||
            Contains(fullPath, @"\winsxs\") ||
            Contains(fullPath, @"\program files"))
        {
            return "System Files";
        }

        if (Contains(fullPath, @"\cache\") || CacheExtensions.Contains(extension))
        {
            return "Cache";
        }

        if (Contains(fullPath, @"\downloads\"))
        {
            return "Downloads";
        }

        if (LogExtensions.Contains(extension))
        {
            return "Logs";
        }

        if (VideoExtensions.Contains(extension))
        {
            return "Videos";
        }

        if (ImageExtensions.Contains(extension))
        {
            return "Images";
        }

        if (AudioExtensions.Contains(extension))
        {
            return "Audio";
        }

        if (AppExtensions.Contains(extension))
        {
            return "Applications";
        }

        if (ArchiveExtensions.Contains(extension))
        {
            return "Archives";
        }

        if (DocumentExtensions.Contains(extension))
        {
            return "Documents";
        }

        if (CodeExtensions.Contains(extension))
        {
            return "Code";
        }

        return "Other";
    }

    private static bool Contains(string path, string token)
    {
        return path.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static int CalculateDepth(string rootPath, string fullPath)
    {
        if (string.Equals(rootPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var relative = fullPath[rootPath.Length..].Trim('\\');
        if (string.IsNullOrWhiteSpace(relative))
        {
            return 0;
        }

        return relative.Count(static c => c == '\\') + 1;
    }

    private static bool IsSubPathOfOrEqual(string path, string root)
    {
        if (string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedRoot = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
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

    private static bool IsRecoverable(Exception ex)
    {
        return ex is UnauthorizedAccessException or
               IOException or
               DirectoryNotFoundException or
               FileNotFoundException or
               PathTooLongException or
               NotSupportedException or
               ArgumentException;
    }
}
