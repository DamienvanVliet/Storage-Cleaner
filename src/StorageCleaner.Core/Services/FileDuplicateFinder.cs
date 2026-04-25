using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class FileDuplicateFinder : IFileDuplicateFinder
{
    private readonly IExclusionService _exclusionService;

    public FileDuplicateFinder(IExclusionService? exclusionService = null)
    {
        _exclusionService = exclusionService ?? NoopExclusionService.Instance;
    }

    public Task<IReadOnlyList<DuplicateFileGroup>> FindDuplicatesAsync(
        IReadOnlyCollection<string> roots,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<DuplicateFileGroup>>([]);
        }

        var normalizedRoots = roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.Run<IReadOnlyList<DuplicateFileGroup>>(() =>
        {
            var filesBySize = new Dictionary<long, List<string>>();
            long scannedFiles = 0;

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
                        if (!info.Exists || info.Length <= 0)
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

                    if (!filesBySize.TryGetValue(info.Length, out var bucket))
                    {
                        bucket = [];
                        filesBySize[info.Length] = bucket;
                    }

                    bucket.Add(info.FullName);
                    scannedFiles++;
                    if ((scannedFiles & 0x3FF) == 0)
                    {
                        progress?.Report(new DuplicateScanProgress(scannedFiles, 0, info.FullName));
                    }
                });
            }

            var duplicateGroups = new List<DuplicateFileGroup>();
            foreach (var sizeGroup in filesBySize.Where(static pair => pair.Value.Count > 1))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var byHash = new Dictionary<string, List<DuplicateFileItem>>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in sizeGroup.Value)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DuplicateFileItem? item = null;

                    try
                    {
                        var hash = ComputeSha256(path, cancellationToken);
                        var info = new FileInfo(path);
                        if (!info.Exists)
                        {
                            continue;
                        }

                        item = new DuplicateFileItem
                        {
                            FullPath = info.FullName,
                            SizeBytes = info.Length,
                            LastModifiedUtc = info.LastWriteTimeUtc,
                            LastAccessUtc = info.LastAccessTimeUtc,
                            FileIdentityKey = TryGetFileIdentityKey(path)
                        };

                        if (!byHash.TryGetValue(hash, out var hashBucket))
                        {
                            hashBucket = [];
                            byHash[hash] = hashBucket;
                        }

                        hashBucket.Add(item);
                    }
                    catch (Exception ex) when (IsRecoverable(ex) || ex is CryptographicException)
                    {
                        continue;
                    }
                }

                foreach (var hashGroup in byHash.Where(static pair => pair.Value.Count > 1))
                {
                    var seenIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var normalizedItems = new List<DuplicateFileItem>(hashGroup.Value.Count);

                    foreach (var item in hashGroup.Value
                                 .OrderBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase))
                    {
                        var isAlias = false;
                        if (!string.IsNullOrWhiteSpace(item.FileIdentityKey))
                        {
                            if (!seenIdentities.Add(item.FileIdentityKey))
                            {
                                isAlias = true;
                            }
                        }

                        normalizedItems.Add(new DuplicateFileItem
                        {
                            FullPath = item.FullPath,
                            SizeBytes = item.SizeBytes,
                            LastModifiedUtc = item.LastModifiedUtc,
                            LastAccessUtc = item.LastAccessUtc,
                            FileIdentityKey = item.FileIdentityKey,
                            IsHardLinkAlias = isAlias
                        });
                    }

                    var physicalCount = normalizedItems.Count(static file => !file.IsHardLinkAlias);
                    if (physicalCount <= 1)
                    {
                        continue;
                    }

                    duplicateGroups.Add(new DuplicateFileGroup
                    {
                        Hash = hashGroup.Key,
                        SizeBytes = sizeGroup.Key,
                        Files = normalizedItems
                    });
                }
            }

            return duplicateGroups
                .OrderByDescending(static group => group.WastedBytes)
                .ThenByDescending(static group => group.Files.Count)
                .ToArray();
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

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash);
    }

    private static string? TryGetFileIdentityKey(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            {
                return null;
            }

            return $"{info.dwVolumeSerialNumber:X8}:{info.nFileIndexHigh:X8}{info.nFileIndexLow:X8}";
        }
        catch
        {
            return null;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }
}
