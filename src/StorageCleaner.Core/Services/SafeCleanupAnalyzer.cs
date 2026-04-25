using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class SafeCleanupAnalyzer : ISafeCleanupAnalyzer
{
    private const int DefaultLogAgeDays = 30;

    private readonly IPathSafetyService _pathSafetyService;
    private readonly IExclusionService _exclusionService;

    public SafeCleanupAnalyzer(IPathSafetyService pathSafetyService, IExclusionService? exclusionService = null)
    {
        _pathSafetyService = pathSafetyService;
        _exclusionService = exclusionService ?? NoopExclusionService.Instance;
    }

    public async Task<IReadOnlyList<CleanupCandidate>> AnalyzeAsync(
        IReadOnlyCollection<CleanupCategory> categories,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categories);

        if (categories.Count == 0)
        {
            return [];
        }

        return await Task.Run(() =>
        {
            var candidates = new Dictionary<string, CleanupCandidate>(StringComparer.OrdinalIgnoreCase);
            long estimatedBytes = 0;

            foreach (var category in categories.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (category)
                {
                    case CleanupCategory.WindowsTemp:
                        AnalyzeWindowsTemp(category, candidates, ref estimatedBytes, progress, cancellationToken);
                        break;
                    case CleanupCategory.UserTemp:
                        AnalyzeUserTemp(category, candidates, ref estimatedBytes, progress, cancellationToken);
                        break;
                    case CleanupCategory.RecycleBin:
                        AnalyzeRecycleBin(category, candidates, ref estimatedBytes, progress, cancellationToken);
                        break;
                    case CleanupCategory.BrowserCache:
                        AnalyzeBrowserCaches(category, candidates, ref estimatedBytes, progress, cancellationToken);
                        break;
                    case CleanupCategory.OldLogFiles:
                        AnalyzeOldLogs(category, candidates, ref estimatedBytes, progress, cancellationToken);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(categories), category, "Unknown cleanup category.");
                }
            }

            return candidates.Values
                .OrderByDescending(static x => x.SizeBytes)
                .ThenBy(static x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    private void AnalyzeWindowsTemp(
        CleanupCategory category,
        Dictionary<string, CleanupCandidate> candidates,
        ref long estimatedBytes,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var tempPath = Path.Combine(windows, "Temp");
        CollectFiles(
            category,
            tempPath,
            static _ => true,
            maxDepth: 12,
            candidates,
            ref estimatedBytes,
            progress,
            cancellationToken);
    }

    private void AnalyzeUserTemp(
        CleanupCategory category,
        Dictionary<string, CleanupCandidate> candidates,
        ref long estimatedBytes,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.GetTempPath();
        CollectFiles(
            category,
            tempPath,
            static _ => true,
            maxDepth: 10,
            candidates,
            ref estimatedBytes,
            progress,
            cancellationToken);
    }

    private void AnalyzeRecycleBin(
        CleanupCategory category,
        Dictionary<string, CleanupCandidate> candidates,
        ref long estimatedBytes,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(static d => d.IsReady))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recyclePath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            CollectFiles(
                category,
                recyclePath,
                static _ => true,
                maxDepth: 6,
                candidates,
                ref estimatedBytes,
                progress,
                cancellationToken);
        }
    }

    private void AnalyzeBrowserCaches(
        CleanupCategory category,
        Dictionary<string, CleanupCandidate> candidates,
        ref long estimatedBytes,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var staticCachePaths = new[]
        {
            Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Cache"),
            Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Code Cache"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Cache"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Code Cache"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Cache"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Code Cache")
        };

        foreach (var path in staticCachePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectFiles(
                category,
                path,
                static _ => true,
                maxDepth: 6,
                candidates,
                ref estimatedBytes,
                progress,
                cancellationToken);
        }

        var firefoxProfiles = Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(firefoxProfiles))
        {
            return;
        }

        try
        {
            foreach (var profilePath in Directory.EnumerateDirectories(firefoxProfiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cache2Path = Path.Combine(profilePath, "cache2");
                CollectFiles(
                    category,
                    cache2Path,
                    static _ => true,
                    maxDepth: 6,
                    candidates,
                    ref estimatedBytes,
                    progress,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }
    }

    private void AnalyzeOldLogs(
        CleanupCategory category,
        Dictionary<string, CleanupCandidate> candidates,
        ref long estimatedBytes,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddDays(-DefaultLogAgeDays);

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Temp")
        };

        foreach (var root in roots.Where(static x => !string.IsNullOrWhiteSpace(x)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectFiles(
                category,
                root,
                fileInfo => fileInfo.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
                            fileInfo.LastWriteTimeUtc <= cutoff,
                maxDepth: 6,
                candidates,
                ref estimatedBytes,
                progress,
                cancellationToken);
        }
    }

    private void CollectFiles(
        CleanupCategory category,
        string rootPath,
        Func<FileInfo, bool> predicate,
        int maxDepth,
        Dictionary<string, CleanupCandidate> candidates,
        ref long estimatedBytes,
        IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootPath))
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

        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((rootPath, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (currentPath, depth) = stack.Pop();
            if (depth > maxDepth)
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentPath, "*", options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo fileInfo;
                try
                {
                    fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists || !predicate(fileInfo))
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                var risk = _pathSafetyService.Evaluate(fileInfo.FullName);
                if (risk.IsProtected)
                {
                    continue;
                }

                var exclusion = _exclusionService.Match(fileInfo.FullName, category);
                if (exclusion.IsExcluded)
                {
                    continue;
                }

                if (candidates.ContainsKey(fileInfo.FullName))
                {
                    continue;
                }

                var candidate = new CleanupCandidate
                {
                    Category = category,
                    FullPath = fileInfo.FullName,
                    IsDirectory = false,
                    SizeBytes = fileInfo.Length,
                    LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                    Risk = risk
                };

                candidates[fileInfo.FullName] = candidate;
                estimatedBytes += fileInfo.Length;

                progress?.Report(new CleanupProgress(category, candidates.Count, estimatedBytes, currentPath));
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(currentPath, "*", options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var directoryInfo = new DirectoryInfo(directory);
                    if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    stack.Push((directoryInfo.FullName, depth + 1));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }
            }
        }
    }
}
