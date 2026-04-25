using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class WasteAnalysisService : IWasteAnalysisService
{
    private readonly IExclusionService _exclusionService;

    public WasteAnalysisService(IExclusionService? exclusionService = null)
    {
        _exclusionService = exclusionService ?? NoopExclusionService.Instance;
    }

    public Task<WasteAnalysisResult> AnalyzeAsync(
        IReadOnlyCollection<string> roots,
        int maxNeverAccessedItems = 200,
        IProgress<WasteAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
        {
            return Task.FromResult(new WasteAnalysisResult
            {
                TopExtensions = [],
                AgeBuckets = [],
                NeverAccessedCandidates = [],
                TotalFiles = 0,
                TotalBytes = 0
            });
        }

        var normalizedRoots = roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.Run(() =>
        {
            var byExtension = new Dictionary<string, (long Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
            var ageBuckets = new Dictionary<string, (long Count, long Bytes)>(StringComparer.OrdinalIgnoreCase)
            {
                ["0-7 days"] = (0, 0),
                ["8-30 days"] = (0, 0),
                ["31-90 days"] = (0, 0),
                ["91-365 days"] = (0, 0),
                [">365 days"] = (0, 0)
            };

            var neverAccessed = new List<FileSearchResult>();
            long totalFiles = 0;
            long totalBytes = 0;
            var now = DateTime.UtcNow;

            foreach (var root in normalizedRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnumerateFiles(root, filePath =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(filePath);
                        if (!info.Exists)
                        {
                            return;
                        }
                    }
                    catch (Exception ex) when (IsRecoverable(ex))
                    {
                        return;
                    }

                    var exclusion = _exclusionService.Match(info.FullName);
                    if (exclusion.IsExcluded)
                    {
                        return;
                    }

                    totalFiles++;
                    totalBytes += info.Length;

                    var extension = string.IsNullOrWhiteSpace(info.Extension) ? "(no extension)" : info.Extension.ToLowerInvariant();
                    if (!byExtension.TryGetValue(extension, out var extBucket))
                    {
                        extBucket = (0, 0);
                    }

                    byExtension[extension] = (extBucket.Count + 1, extBucket.Bytes + info.Length);

                    var age = (now - info.LastWriteTimeUtc).TotalDays;
                    var ageKey = age switch
                    {
                        <= 7 => "0-7 days",
                        <= 30 => "8-30 days",
                        <= 90 => "31-90 days",
                        <= 365 => "91-365 days",
                        _ => ">365 days"
                    };

                    var currentAgeBucket = ageBuckets[ageKey];
                    ageBuckets[ageKey] = (currentAgeBucket.Count + 1, currentAgeBucket.Bytes + info.Length);

                    var lastAccessAge = (now - info.LastAccessTimeUtc).TotalDays;
                    if (lastAccessAge >= 365)
                    {
                        neverAccessed.Add(new FileSearchResult(
                            info.Name,
                            info.FullName,
                            info.Length,
                            info.LastWriteTimeUtc,
                            info.DirectoryName ?? root));
                    }

                    if ((totalFiles & 0x7FF) == 0)
                    {
                        progress?.Report(new WasteAnalysisProgress(totalFiles, info.FullName));
                    }
                });
            }

            var topExtensions = byExtension
                .Select(static pair => new WasteCategoryBucket(pair.Key, pair.Value.Count, pair.Value.Bytes))
                .OrderByDescending(static bucket => bucket.TotalBytes)
                .ThenByDescending(static bucket => bucket.FileCount)
                .Take(30)
                .ToArray();

            var orderedAgeBuckets = new[]
            {
                "0-7 days",
                "8-30 days",
                "31-90 days",
                "91-365 days",
                ">365 days"
            }
            .Select(label =>
            {
                var bucket = ageBuckets[label];
                return new WasteAgeBucket(label, bucket.Count, bucket.Bytes);
            })
            .ToArray();

            var topNeverAccessed = neverAccessed
                .OrderByDescending(static file => file.SizeBytes)
                .ThenBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(20, maxNeverAccessedItems))
                .ToArray();

            return new WasteAnalysisResult
            {
                TopExtensions = topExtensions,
                AgeBuckets = orderedAgeBuckets,
                NeverAccessedCandidates = topNeverAccessed,
                TotalFiles = totalFiles,
                TotalBytes = totalBytes
            };
        }, cancellationToken);
    }

    private static void EnumerateFiles(string rootPath, Action<string> onFile)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        var stack = new Stack<string>();
        stack.Push(rootPath);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*", options);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                continue;
            }

            foreach (var file in files)
            {
                onFile(file);
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
                try
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    stack.Push(info.FullName);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    continue;
                }
            }
        }
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
