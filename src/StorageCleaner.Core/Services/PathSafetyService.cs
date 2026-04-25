using System.Collections.Concurrent;
using System.Text.Json;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class PathSafetyService : IPathSafetyService
{
    private const int MaxWorkspaceAncestorDepth = 8;
    private const int MaxProfileDiscoveryDepth = 3;

    private static readonly string[] WorkspaceMarkerDirectories =
    [
        ".git",
        ".hg",
        ".svn",
        ".idea",
        ".vscode",
        ".vs"
    ];

    private static readonly string[] WorkspaceMarkerFiles =
    [
        "package.json",
        "pnpm-workspace.yaml",
        "yarn.lock",
        "package-lock.json",
        "pyproject.toml",
        "requirements.txt",
        "Pipfile",
        "poetry.lock",
        "Cargo.toml",
        "go.mod",
        "pom.xml",
        "build.gradle",
        "build.gradle.kts",
        "settings.gradle",
        "settings.gradle.kts",
        "composer.json",
        "Gemfile",
        "pubspec.yaml",
        "CMakeLists.txt",
        "Makefile",
        "WORKSPACE",
        "WORKSPACE.bazel"
    ];

    private static readonly string[] ProfileNameHints =
    [
        "repo",
        "repos",
        "project",
        "projects",
        "source",
        "src",
        "mod",
        "mods",
        "docker",
        "wsl",
        "vm",
        "vms",
        "virtualbox",
        "vmware",
        "hyper-v",
        "kubernetes",
        "k8s"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _windowsRoot;
    private readonly string _windowsTempRoot;
    private readonly string _programFilesRoot;
    private readonly string _programFilesX86Root;
    private readonly string _roamingAppDataRoot;
    private readonly string _localAppDataRoot;
    private readonly string _userTempRoot;
    private readonly string _system32Root;
    private readonly HashSet<string> _protectedPaths;
    private readonly ConcurrentDictionary<string, WorkspaceDetectionResult> _workspaceDetectionCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _profilesPath;
    private readonly SemaphoreSlim _profilesLock = new(1, 1);
    private volatile string[] _protectedProfileRoots = [];

    public PathSafetyService(string? profilesPath = null)
    {
        _windowsRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        _windowsTempRoot = NormalizePath(Path.Combine(_windowsRoot, "Temp"));
        _programFilesRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        _programFilesX86Root = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        _roamingAppDataRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        _localAppDataRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _userTempRoot = NormalizePath(Path.GetTempPath());
        _system32Root = NormalizePath(Path.Combine(_windowsRoot, "System32"));

        _protectedPaths =
        [
            _windowsRoot,
            _system32Root,
            _programFilesRoot,
            _programFilesX86Root
        ];

        _profilesPath = profilesPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageCleaner",
            "protection-profiles.json");

        _protectedProfileRoots = LoadProfilesSync();
    }

    public PathRisk Evaluate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new PathRisk(PathRiskLevel.HighRisk, "Path is empty.");
        }

        string normalizedPath;
        try
        {
            normalizedPath = NormalizePath(path);
        }
        catch
        {
            return new PathRisk(PathRiskLevel.HighRisk, "Path could not be normalized.");
        }

        if (IsDriveRoot(normalizedPath))
        {
            return new PathRisk(PathRiskLevel.Protected, "Deleting a drive root is blocked.");
        }

        if (_protectedPaths.Any(protectedPath =>
                IsSamePath(normalizedPath, protectedPath) ||
                IsParentOf(normalizedPath, protectedPath)))
        {
            return new PathRisk(PathRiskLevel.Protected, "System-critical location.");
        }

        foreach (var protectedRoot in _protectedProfileRoots)
        {
            if (IsSamePath(normalizedPath, protectedRoot) || IsSubPathOf(normalizedPath, protectedRoot))
            {
                return new PathRisk(PathRiskLevel.Protected, $"Protected profile root: {protectedRoot}");
            }
        }

        if (TryDetectWorkspace(normalizedPath, out var workspaceDetection))
        {
            return new PathRisk(
                PathRiskLevel.HighRisk,
                $"Project/workspace marker '{workspaceDetection.MarkerName}' detected at '{workspaceDetection.WorkspaceRoot}'.");
        }

        if (TryEvaluateKnownCleanupPath(normalizedPath, out var cleanupRisk))
        {
            return cleanupRisk;
        }

        if (IsSubPathOf(normalizedPath, _windowsRoot) ||
            IsSubPathOf(normalizedPath, _programFilesRoot) ||
            IsSubPathOf(normalizedPath, _programFilesX86Root) ||
            IsSubPathOf(normalizedPath, _system32Root))
        {
            return new PathRisk(PathRiskLevel.HighRisk, "Risky system path.");
        }

        if (IsSubPathOf(normalizedPath, _roamingAppDataRoot) ||
            IsSubPathOf(normalizedPath, _localAppDataRoot))
        {
            return new PathRisk(PathRiskLevel.Caution, "AppData can contain active application data.");
        }

        return new PathRisk(PathRiskLevel.Safe, "No elevated risk detected.");
    }

    public IReadOnlyList<string> GetProtectedProfileRoots()
    {
        return _protectedProfileRoots.ToArray();
    }

    public async Task AddProtectedProfileRootAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = NormalizePath(path);

        await _profilesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _protectedProfileRoots.ToList();
            if (current.Any(existing => IsSamePath(existing, normalized)))
            {
                return;
            }

            current.Add(normalized);
            current = current
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await PersistProfilesAsync(current, cancellationToken).ConfigureAwait(false);
            _protectedProfileRoots = current.ToArray();
        }
        finally
        {
            _profilesLock.Release();
        }
    }

    public async Task RemoveProtectedProfileRootAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = NormalizePath(path);

        await _profilesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _protectedProfileRoots
                .Where(existing => !IsSamePath(existing, normalized))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await PersistProfilesAsync(current, cancellationToken).ConfigureAwait(false);
            _protectedProfileRoots = current.ToArray();
        }
        finally
        {
            _profilesLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> DiscoverAndProtectDefaultProfilesAsync(
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        var discovered = await Task.Run(
            () => DiscoverProfileRoots(cancellationToken),
            cancellationToken).ConfigureAwait(false);

        await _profilesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var merged = replaceExisting
                ? discovered
                : _protectedProfileRoots.Concat(discovered);

            var normalized = merged
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await PersistProfilesAsync(normalized, cancellationToken).ConfigureAwait(false);
            _protectedProfileRoots = normalized.ToArray();
            return _protectedProfileRoots.ToArray();
        }
        finally
        {
            _profilesLock.Release();
        }
    }

    private IEnumerable<string> DiscoverProfileRoots(CancellationToken cancellationToken)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var commonDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);

        var rootCandidates = new[]
        {
            userProfile,
            Path.Combine(userProfile, "source"),
            Path.Combine(userProfile, "src"),
            Path.Combine(userProfile, "repos"),
            Path.Combine(userProfile, "projects"),
            Path.Combine(userProfile, "dev"),
            documents,
            desktop,
            Path.Combine(userProfile, "Games"),
            Path.Combine(userProfile, "VirtualBox VMs"),
            Path.Combine(roamingAppData, "Docker"),
            Path.Combine(localAppData, "Docker"),
            Path.Combine(commonDocuments, "Hyper-V")
        };

        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoriesToVisit = new Stack<(string Path, int Depth)>();

        foreach (var root in rootCandidates.Where(Directory.Exists))
        {
            var normalizedRoot = NormalizePath(root);
            directoriesToVisit.Push((normalizedRoot, 0));
            if (IsLikelyProtectedRoot(normalizedRoot))
            {
                discovered.Add(normalizedRoot);
            }
        }

        while (directoriesToVisit.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, depth) = directoriesToVisit.Pop();
            if (depth > MaxProfileDiscoveryDepth)
            {
                continue;
            }

            if (IsLikelyProtectedRoot(current))
            {
                discovered.Add(current);
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = 0
                });
            }
            catch (Exception ex) when (IsRecoverableLookupException(ex))
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new DirectoryInfo(child);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    directoriesToVisit.Push((NormalizePath(info.FullName), depth + 1));
                }
                catch (Exception ex) when (IsRecoverableLookupException(ex))
                {
                    continue;
                }
            }
        }

        return discovered;
    }

    private bool IsLikelyProtectedRoot(string path)
    {
        var marker = TryFindWorkspaceMarker(path);
        if (!string.IsNullOrWhiteSpace(marker))
        {
            return true;
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(name) &&
            ProfileNameHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            if (Directory.EnumerateFiles(path, "*.vhd*", SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFiles(path, "*.vmdk", SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFiles(path, "*.qcow2", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }
        }
        catch (Exception ex) when (IsRecoverableLookupException(ex))
        {
            return false;
        }

        return false;
    }

    private bool TryEvaluateKnownCleanupPath(string normalizedPath, out PathRisk risk)
    {
        if (IsSubPathOf(normalizedPath, _windowsTempRoot))
        {
            risk = new PathRisk(PathRiskLevel.Caution, "Windows Temp cleanup path.");
            return true;
        }

        if (IsSubPathOf(normalizedPath, _userTempRoot))
        {
            risk = new PathRisk(PathRiskLevel.Safe, "User Temp cleanup path.");
            return true;
        }

        if (IsRecycleBinPath(normalizedPath))
        {
            risk = new PathRisk(PathRiskLevel.Caution, "Recycle Bin cleanup path.");
            return true;
        }

        if (IsBrowserCachePath(normalizedPath))
        {
            risk = new PathRisk(PathRiskLevel.Safe, "Browser cache cleanup path.");
            return true;
        }

        risk = default!;
        return false;
    }

    private bool TryDetectWorkspace(string normalizedPath, out WorkspaceDetectionResult result)
    {
        result = WorkspaceDetectionResult.None;

        var startDirectory = Directory.Exists(normalizedPath)
            ? normalizedPath
            : Path.GetDirectoryName(normalizedPath);

        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return false;
        }

        startDirectory = NormalizePath(startDirectory);
        if (_workspaceDetectionCache.TryGetValue(startDirectory, out var cachedStart))
        {
            result = cachedStart ?? WorkspaceDetectionResult.None;
            return result.IsWorkspace;
        }

        var visited = new List<string>(MaxWorkspaceAncestorDepth + 1);
        var current = startDirectory;

        for (var depth = 0; depth <= MaxWorkspaceAncestorDepth; depth++)
        {
            visited.Add(current);

            if (_workspaceDetectionCache.TryGetValue(current, out var cached))
            {
                result = cached ?? WorkspaceDetectionResult.None;
                break;
            }

            var markerName = TryFindWorkspaceMarker(current);
            if (!string.IsNullOrWhiteSpace(markerName))
            {
                result = new WorkspaceDetectionResult(true, current, markerName);
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            var normalizedParent = NormalizePath(parent);
            if (IsSamePath(normalizedParent, current))
            {
                break;
            }

            current = normalizedParent;
        }

        result ??= WorkspaceDetectionResult.None;
        foreach (var directory in visited)
        {
            _workspaceDetectionCache.TryAdd(directory, result);
        }

        return result.IsWorkspace;
    }

    private static string? TryFindWorkspaceMarker(string directoryPath)
    {
        try
        {
            foreach (var markerDirectory in WorkspaceMarkerDirectories)
            {
                if (Directory.Exists(Path.Combine(directoryPath, markerDirectory)))
                {
                    return markerDirectory;
                }
            }

            foreach (var markerFile in WorkspaceMarkerFiles)
            {
                if (File.Exists(Path.Combine(directoryPath, markerFile)))
                {
                    return markerFile;
                }
            }

            if (Directory.EnumerateFiles(directoryPath, "*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                return "*.sln";
            }

            if (Directory.EnumerateFiles(directoryPath, "*.csproj", SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFiles(directoryPath, "*.fsproj", SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFiles(directoryPath, "*.vbproj", SearchOption.TopDirectoryOnly).Any())
            {
                return "*.csproj";
            }
        }
        catch (Exception ex) when (IsRecoverableLookupException(ex))
        {
            return null;
        }

        return null;
    }

    private async Task PersistProfilesAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_profilesPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_profilesPath);
        await JsonSerializer.SerializeAsync(
                stream,
                new ProtectionProfilesPayload(roots),
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private string[] LoadProfilesSync()
    {
        if (!File.Exists(_profilesPath))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_profilesPath);
            var payload = JsonSerializer.Deserialize<ProtectionProfilesPayload>(stream, JsonOptions);
            return payload?.Roots?
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsRecoverableLookupException(Exception ex)
    {
        return ex is UnauthorizedAccessException or
               IOException or
               DirectoryNotFoundException or
               FileNotFoundException or
               PathTooLongException or
               NotSupportedException or
               ArgumentException;
    }

    private static bool IsRecycleBinPath(string normalizedPath)
    {
        return normalizedPath.Contains("\\$Recycle.Bin\\", StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.EndsWith("\\$Recycle.Bin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserCachePath(string normalizedPath)
    {
        var path = normalizedPath.ToLowerInvariant();
        return ((path.Contains("\\google\\chrome\\user data\\") ||
                 path.Contains("\\microsoft\\edge\\user data\\") ||
                 path.Contains("\\bravesoftware\\brave-browser\\user data\\")) &&
                (path.Contains("\\cache\\") ||
                 path.Contains("\\code cache\\"))) ||
               (path.Contains("\\mozilla\\firefox\\profiles\\") &&
                path.Contains("\\cache2\\"));
    }

    private static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root) &&
               string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsParentOf(string candidateParent, string candidateChild)
    {
        if (IsSamePath(candidateParent, candidateChild))
        {
            return false;
        }

        return candidateChild.StartsWith(AppendSeparator(candidateParent), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSubPathOf(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        return path.StartsWith(AppendSeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendSeparator(string path)
    {
        if (path.EndsWith('\\'))
        {
            return path;
        }

        return path + '\\';
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

    private sealed record WorkspaceDetectionResult(
        bool IsWorkspace,
        string WorkspaceRoot,
        string MarkerName)
    {
        public static WorkspaceDetectionResult None { get; } = new(false, string.Empty, string.Empty);
    }

    private sealed record ProtectionProfilesPayload(IReadOnlyList<string> Roots);
}
