using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security;
using System.Threading.Channels;
using StorageCleaner.Core;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class StorageScanner : IStorageScanner
{
    private const int MaxScanIssues = 3000;
    private readonly IScanCache _cache;
    private readonly IExclusionService _exclusionService;

    public StorageScanner(IScanCache? cache = null, IExclusionService? exclusionService = null)
    {
        _cache = cache ?? new MemoryScanCache();
        _exclusionService = exclusionService ?? NoopExclusionService.Instance;
    }

    public async Task<ScanResult> ScanAsync(
        ScanRequest request,
        PauseToken pauseToken,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Roots is null || request.Roots.Count == 0)
        {
            throw new ArgumentException("At least one scan root must be provided.", nameof(request));
        }

        var issues = new ConcurrentBag<ScanIssue>();
        var normalizedRoots = new List<string>(request.Roots.Count);
        long issueCount = 0;
        long suppressedIssueCount = 0;
        foreach (var root in request.Roots.Where(static x => !string.IsNullOrWhiteSpace(x)))
        {
            try
            {
                var normalizedRoot = NormalizePath(root);
                var exclusion = _exclusionService.Match(normalizedRoot);
                if (exclusion.IsExcluded)
                {
                    AddIssue(normalizedRoot, exclusion.Reason);
                    continue;
                }

                normalizedRoots.Add(normalizedRoot);
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                AddIssue(
                    root,
                    $"Invalid root skipped: {ex.Message}",
                    ex);
            }
        }

        normalizedRoots = normalizedRoots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedRoots.Count == 0)
        {
            throw new ArgumentException("No valid scan roots were provided.", nameof(request));
        }

        var useNtfsFastMode = request.Mode == ScanMode.NtfsFast;
        var useFastDirectoryMetadata = false;
        if (useNtfsFastMode)
        {
            var rootChecks = normalizedRoots
                .Select(root =>
                {
                    var supported = NtfsUsnJournalProbe.TryCheck(root, out var detail);
                    return (Root: root, Supported: supported, Detail: detail);
                })
                .ToArray();

            if (rootChecks.All(static check => check.Supported))
            {
                useFastDirectoryMetadata = true;
            }
            else
            {
                foreach (var check in rootChecks.Where(static check => !check.Supported))
                {
                    AddIssue(check.Root, $"NTFS fast scan fallback: {check.Detail}");
                }
            }
        }

        if (request.UseCache && _cache.TryGet(normalizedRoots, out var cachedResult))
        {
            return cachedResult;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var nodes = new ConcurrentDictionary<string, MutableFolderNode>(StringComparer.OrdinalIgnoreCase);
        var workChannel = Channel.CreateUnbounded<ScanWorkItem>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        long pendingDirectories = 0;
        long processedDirectories = 0;
        long processedFiles = 0;
        long processedBytes = 0;
        string? currentPath = null;
        var estimatedTotalBytes = EstimateTotalBytes(normalizedRoots);

        var progressGate = new object();
        var progressClock = Stopwatch.StartNew();
        void ReportProgress(bool force = false)
        {
            if (progress is null)
            {
                return;
            }

            lock (progressGate)
            {
                if (!force && progressClock.ElapsedMilliseconds < 200)
                {
                    return;
                }

                progress.Report(new ScanProgress(
                    Interlocked.Read(ref processedDirectories),
                    Interlocked.Read(ref processedFiles),
                    Interlocked.Read(ref processedBytes),
                    estimatedTotalBytes,
                    CalculatePercentage(Interlocked.Read(ref processedBytes), estimatedTotalBytes, force),
                    Volatile.Read(ref currentPath),
                    stopwatch.Elapsed,
                    pauseToken.IsPaused));

                progressClock.Restart();
            }
        }

        foreach (var root in normalizedRoots)
        {
            var rootNode = new MutableFolderNode(root, parentPath: null);
            rootNode.TryMarkScheduled();
            nodes[root] = rootNode;
            Interlocked.Increment(ref pendingDirectories);
            await workChannel.Writer.WriteAsync(new ScanWorkItem(root), cancellationToken).ConfigureAwait(false);
        }

        var workerCount = Math.Max(1, request.MaxDegreeOfParallelism);
        if (useFastDirectoryMetadata)
        {
            workerCount = Math.Max(workerCount, Math.Min(Environment.ProcessorCount, 24));
        }
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => RunWorkerAsync())
            .ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            workChannel.Writer.TryComplete();
            throw;
        }

        ReportProgress(force: true);
        stopwatch.Stop();

        var suppressed = Interlocked.Read(ref suppressedIssueCount);
        if (suppressed > 0)
        {
            issues.Add(new ScanIssue(
                "Scanner",
                $"Suppressed {suppressed:N0} additional warnings to keep scan memory stable.",
                "IssueLimit",
                DateTimeOffset.UtcNow));
        }

        AggregateNodes(nodes);

        var totalBytes = normalizedRoots
            .Where(nodes.ContainsKey)
            .Sum(root => nodes[root].TotalBytes);

        var totalFiles = normalizedRoots
            .Where(nodes.ContainsKey)
            .Sum(root => nodes[root].TotalFileCount);

        var totalFolders = normalizedRoots
            .Where(nodes.ContainsKey)
            .Sum(root => 1 + nodes[root].TotalFolderCount);

        var builtNodes = BuildNodes(nodes, totalBytes);

        var rootNodes = normalizedRoots
            .Where(builtNodes.ContainsKey)
            .Select(root => builtNodes[root])
            .OrderByDescending(static x => x.SizeBytes)
            .ToArray();

        var result = new ScanResult
        {
            Roots = rootNodes,
            Issues = issues
                .OrderBy(static x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static x => x.Timestamp)
                .ToArray(),
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            TotalScannedBytes = totalBytes,
            TotalFiles = totalFiles,
            TotalFolders = totalFolders
        };

        if (request.UseCache)
        {
            _cache.Store(normalizedRoots, result);
        }

        return result;

        async Task RunWorkerAsync()
        {
            while (await workChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (workChannel.Reader.TryRead(out var work))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref currentPath, work.Path);

                    if (!nodes.TryGetValue(work.Path, out var node))
                    {
                        CompleteWorkItem();
                        continue;
                    }

                    try
                    {
                        await ProcessDirectoryAsync(work.Path, node).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsRecoverableFilesystemException(ex))
                    {
                        node.IsInaccessible = true;
                        node.Warning = ex.Message;
                        AddIssue(work.Path, $"Directory processing failed: {ex.Message}", ex);
                    }
                    finally
                    {
                        Interlocked.Increment(ref processedDirectories);
                        ReportProgress();
                        CompleteWorkItem();
                    }
                }
            }
        }

        async Task ProcessDirectoryAsync(string directoryPath, MutableFolderNode node)
        {
            var directoryExclusion = _exclusionService.Match(directoryPath);
            if (directoryExclusion.IsExcluded)
            {
                node.Warning = directoryExclusion.Reason;
                node.IsInaccessible = true;
                AddIssue(directoryPath, directoryExclusion.Reason);
                return;
            }

            if (!Directory.Exists(directoryPath))
            {
                node.IsInaccessible = true;
                node.Warning = "Directory no longer exists.";
                AddIssue(directoryPath, "Directory no longer exists.");
                return;
            }

            if (!useFastDirectoryMetadata)
            {
                try
                {
                    node.LastModifiedUtc = Directory.GetLastWriteTimeUtc(directoryPath);
                }
                catch (Exception ex) when (IsRecoverableFilesystemException(ex))
                {
                    AddIssue(directoryPath, $"Unable to read folder metadata: {ex.Message}", ex);
                }
            }

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0
            };

            try
            {
                var filesSinceProgress = 0;
                foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);

                    var exclusion = _exclusionService.Match(filePath);
                    if (exclusion.IsExcluded)
                    {
                        continue;
                    }

                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (!fileInfo.Exists)
                        {
                            continue;
                        }

                        node.DirectFileBytes += fileInfo.Length;
                        node.DirectFileCount++;
                        Interlocked.Increment(ref processedFiles);
                        Interlocked.Add(ref processedBytes, fileInfo.Length);
                        filesSinceProgress++;

                        // Large folders can keep workers busy for a long time; pulse progress during file loops.
                        if ((filesSinceProgress & 0x1FF) == 0)
                        {
                            ReportProgress();
                        }
                    }
                    catch (Exception ex) when (IsRecoverableFilesystemException(ex))
                    {
                        AddIssue(filePath, $"Unable to read file metadata: {ex.Message}", ex);
                    }
                }
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                node.IsInaccessible = true;
                node.Warning = ex.Message;
                AddIssue(directoryPath, $"Unable to enumerate files: {ex.Message}", ex);
            }

            try
            {
                var directoriesSinceProgress = 0;
                foreach (var childDirectory in Directory.EnumerateDirectories(directoryPath, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);

                    var exclusion = _exclusionService.Match(childDirectory);
                    if (exclusion.IsExcluded)
                    {
                        continue;
                    }

                    DirectoryInfo childInfo;
                    try
                    {
                        childInfo = new DirectoryInfo(childDirectory);
                        if ((childInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            // Reparse points can create cycles and explode scan time.
                            continue;
                        }
                    }
                    catch (Exception ex) when (IsRecoverableFilesystemException(ex))
                    {
                        AddIssue(childDirectory, $"Unable to access subfolder attributes: {ex.Message}", ex);
                        continue;
                    }

                    string normalizedChild;
                    try
                    {
                        normalizedChild = NormalizePath(childDirectory);
                    }
                    catch (Exception ex) when (IsRecoverableFilesystemException(ex))
                    {
                        AddIssue(childDirectory, $"Unable to normalize child path: {ex.Message}", ex);
                        continue;
                    }

                    var childNode = nodes.GetOrAdd(normalizedChild, static (path, parent) => new MutableFolderNode(path, parent), node.FullPath);

                    node.ChildPaths.Add(childNode.FullPath);

                    if (!childNode.TryMarkScheduled())
                    {
                        continue;
                    }

                    Interlocked.Increment(ref pendingDirectories);
                    await workChannel.Writer.WriteAsync(new ScanWorkItem(childNode.FullPath), cancellationToken).ConfigureAwait(false);
                    directoriesSinceProgress++;

                    // Keep progress updates visible while traversing folders with many children.
                    if ((directoriesSinceProgress & 0x1F) == 0)
                    {
                        ReportProgress();
                    }
                }
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                node.IsInaccessible = true;
                node.Warning = ex.Message;
                AddIssue(directoryPath, $"Unable to enumerate subfolders: {ex.Message}", ex);
            }
        }

        void CompleteWorkItem()
        {
            if (Interlocked.Decrement(ref pendingDirectories) == 0)
            {
                workChannel.Writer.TryComplete();
            }
        }

        void AddIssue(string path, string message, Exception? ex = null)
        {
            var current = Interlocked.Increment(ref issueCount);
            if (current > MaxScanIssues)
            {
                Interlocked.Increment(ref suppressedIssueCount);
                return;
            }

            issues.Add(new ScanIssue(
                path,
                message,
                ex?.GetType().Name ?? "ScannerWarning",
                DateTimeOffset.UtcNow));
        }
    }

    private static IReadOnlyDictionary<string, FolderNode> BuildNodes(
        ConcurrentDictionary<string, MutableFolderNode> allNodes,
        long totalScannedBytes)
    {
        var result = new Dictionary<string, FolderNode>(allNodes.Count, StringComparer.OrdinalIgnoreCase);
        var sorted = allNodes.Values
            .OrderByDescending(static x => x.Depth)
            .ToArray();

        foreach (var mutableNode in sorted)
        {
            var children = mutableNode.ChildPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(result.ContainsKey)
                .Select(path => result[path])
                .OrderByDescending(static x => x.SizeBytes)
                .ToArray();

            result[mutableNode.FullPath] = new FolderNode
            {
                Name = mutableNode.Name,
                FullPath = mutableNode.FullPath,
                ParentPath = mutableNode.ParentPath,
                SizeBytes = mutableNode.TotalBytes,
                PercentageOfScanned = totalScannedBytes <= 0 ? 0 : (double)mutableNode.TotalBytes / totalScannedBytes * 100d,
                FileCount = mutableNode.TotalFileCount,
                FolderCount = mutableNode.TotalFolderCount,
                LastModifiedUtc = mutableNode.LastModifiedUtc,
                IsInaccessible = mutableNode.IsInaccessible,
                Warning = mutableNode.Warning,
                Children = children
            };
        }

        return result;
    }

    private static void AggregateNodes(ConcurrentDictionary<string, MutableFolderNode> nodes)
    {
        var sorted = nodes.Values
            .OrderByDescending(static x => x.Depth)
            .ToArray();

        foreach (var node in sorted)
        {
            var totalBytes = node.DirectFileBytes;
            var totalFiles = node.DirectFileCount;
            long totalFolders = 0;

            foreach (var childPath in node.ChildPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!nodes.TryGetValue(childPath, out var childNode))
                {
                    continue;
                }

                totalBytes += childNode.TotalBytes;
                totalFiles += childNode.TotalFileCount;
                totalFolders += 1 + childNode.TotalFolderCount;
                if (childNode.LastModifiedUtc > node.LastModifiedUtc)
                {
                    node.LastModifiedUtc = childNode.LastModifiedUtc;
                }
            }

            node.TotalBytes = totalBytes;
            node.TotalFileCount = totalFiles;
            node.TotalFolderCount = totalFolders;
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

    private static bool IsRecoverableFilesystemException(Exception ex)
    {
        return ex is UnauthorizedAccessException or
               IOException or
               SecurityException or
               PathTooLongException or
               DirectoryNotFoundException or
               FileNotFoundException or
               NotSupportedException or
               ArgumentException;
    }

    private static long EstimateTotalBytes(IReadOnlyCollection<string> roots)
    {
        long total = 0;
        foreach (var root in roots)
        {
            try
            {
                if (!IsDriveRoot(root))
                {
                    continue;
                }

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    continue;
                }

                total += Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
            }
            catch
            {
                // Best-effort estimate only.
            }
        }

        return total;
    }

    private static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        return string.Equals(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static double CalculatePercentage(long processedBytes, long estimatedTotalBytes, bool completed)
    {
        if (estimatedTotalBytes <= 0)
        {
            return 0;
        }

        if (completed)
        {
            return 100;
        }

        var pct = (double)processedBytes / estimatedTotalBytes * 100d;
        if (pct >= 100)
        {
            return 99.5;
        }

        return Math.Clamp(pct, 0, 99.5);
    }

    private sealed record ScanWorkItem(string Path);

    private sealed class MutableFolderNode
    {
        public MutableFolderNode(string fullPath, string? parentPath)
        {
            FullPath = fullPath;
            ParentPath = parentPath;
            Name = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = fullPath;
            }

            Depth = fullPath.Count(static c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
            LastModifiedUtc = DateTime.UtcNow;
        }

        public string Name { get; }

        public string FullPath { get; }

        public string? ParentPath { get; }

        public int Depth { get; }

        private int _isScheduled;

        public bool IsInaccessible { get; set; }

        public string? Warning { get; set; }

        public DateTime LastModifiedUtc { get; set; }

        public long DirectFileBytes { get; set; }

        public long DirectFileCount { get; set; }

        public long TotalBytes { get; set; }

        public long TotalFileCount { get; set; }

        public long TotalFolderCount { get; set; }

        public List<string> ChildPaths { get; } = [];

        public bool TryMarkScheduled()
        {
            return Interlocked.CompareExchange(ref _isScheduled, 1, 0) == 0;
        }
    }
}
