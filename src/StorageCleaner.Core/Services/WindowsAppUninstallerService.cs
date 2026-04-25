using Microsoft.Win32;
using System.Runtime.Versioning;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsAppUninstallerService : IAppUninstallerService
{
    private static readonly string[] UninstallRegistryPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public Task<IReadOnlyList<InstalledAppInfo>> GetInstalledAppsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<InstalledAppInfo>>(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return [];
            }

            var apps = new List<InstalledAppInfo>(600);
            CollectFromHive(RegistryHive.LocalMachine, RegistryView.Registry64, apps, cancellationToken);
            CollectFromHive(RegistryHive.LocalMachine, RegistryView.Registry32, apps, cancellationToken);
            CollectFromHive(RegistryHive.CurrentUser, RegistryView.Default, apps, cancellationToken);

            return apps
                .DistinctBy(static app => $"{app.DisplayName}|{app.UninstallCommand}|{app.InstallLocation}", StringComparer.OrdinalIgnoreCase)
                .OrderBy(static app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AppLeftoverCandidate>> DetectLeftoversAsync(
        InstalledAppInfo app,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        return Task.Run<IReadOnlyList<AppLeftoverCandidate>>(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return [];
            }

            var candidates = new List<AppLeftoverCandidate>();
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var nameTokens = BuildNameTokens(app.DisplayName);
            var publisherTokens = BuildNameTokens(app.Publisher ?? string.Empty);

            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            }.Where(static root => !string.IsNullOrWhiteSpace(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var token in nameTokens.Concat(publisherTokens).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var path = Path.Combine(root, token);
                    TryAdd(path, $"Potential leftover folder for {app.DisplayName}");
                }

                if (root.Contains("AppData", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var token in nameTokens)
                    {
                        var appDataPrograms = Path.Combine(root, "Programs", token);
                        TryAdd(appDataPrograms, $"Potential AppData program folder for {app.DisplayName}");
                    }
                }
            }

            return candidates
                .OrderByDescending(static candidate => candidate.EstimatedBytes)
                .ThenBy(static candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            void TryAdd(string path, string reason)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var fullPath = Path.GetFullPath(path);
                if (!tried.Add(fullPath))
                {
                    return;
                }

                if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(app.InstallLocation) &&
                    string.Equals(Path.GetFullPath(app.InstallLocation), fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var isDirectory = Directory.Exists(fullPath);
                var size = EstimatePathSize(fullPath, isDirectory, cancellationToken);
                if (size <= 0 && isDirectory)
                {
                    return;
                }

                candidates.Add(new AppLeftoverCandidate(fullPath, isDirectory, size, reason));
            }
        }, cancellationToken);
    }

    public Task<UninstallLaunchResult> LaunchUninstallAsync(
        InstalledAppInfo app,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(app.UninstallCommand))
        {
            return Task.FromResult(new UninstallLaunchResult(
                Started: false,
                Message: $"No uninstall command was found for {app.DisplayName}."));
        }

        try
        {
            var command = NormalizeUninstallCommand(app.UninstallCommand!);
            var (fileName, arguments) = SplitCommand(command);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            };

            if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
            {
                startInfo.WorkingDirectory = app.InstallLocation;
            }

            var process = System.Diagnostics.Process.Start(startInfo);
            return Task.FromResult(new UninstallLaunchResult(
                Started: process is not null,
                Message: process is null
                    ? $"Unable to launch uninstall workflow for {app.DisplayName}."
                    : $"Uninstall workflow started for {app.DisplayName}."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new UninstallLaunchResult(
                Started: false,
                Message: $"Failed to launch uninstall for {app.DisplayName}: {ex.Message}"));
        }
    }

    private static void CollectFromHive(
        RegistryHive hive,
        RegistryView view,
        List<InstalledAppInfo> apps,
        CancellationToken cancellationToken)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        foreach (var uninstallPath in UninstallRegistryPaths)
        {
            using var uninstallKey = baseKey.OpenSubKey(uninstallPath);
            if (uninstallKey is null)
            {
                continue;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var subKey = uninstallKey.OpenSubKey(subKeyName);
                if (subKey is null)
                {
                    continue;
                }

                var displayName = GetString(subKey, "DisplayName");
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var uninstallCommand = GetString(subKey, "QuietUninstallString");
                if (string.IsNullOrWhiteSpace(uninstallCommand))
                {
                    uninstallCommand = GetString(subKey, "UninstallString");
                }

                var installLocation = GetString(subKey, "InstallLocation");
                var estimatedSizeBytes = GetEstimatedSizeBytes(subKey);
                var lastUsed = GetLastUsedLocal(subKey);
                var publisher = GetString(subKey, "Publisher");
                var version = GetString(subKey, "DisplayVersion");
                var registryPath = $@"{hive}\{uninstallPath}\{subKeyName}";

                apps.Add(new InstalledAppInfo(
                    AppId: $"{hive}-{view}-{subKeyName}",
                    DisplayName: displayName!.Trim(),
                    Publisher: publisher,
                    Version: version,
                    InstallLocation: NormalizeDirectory(installLocation),
                    EstimatedSizeBytes: estimatedSizeBytes,
                    LastUsedLocal: lastUsed,
                    UninstallCommand: uninstallCommand,
                    RegistryPath: registryPath));
            }
        }
    }

    private static string? GetString(RegistryKey key, string valueName)
    {
        var value = key.GetValue(valueName);
        return value?.ToString();
    }

    private static long? GetEstimatedSizeBytes(RegistryKey key)
    {
        var value = key.GetValue("EstimatedSize");
        if (value is null)
        {
            return null;
        }

        if (long.TryParse(value.ToString(), out var kb) && kb > 0)
        {
            return kb * 1024L;
        }

        return null;
    }

    private static DateTime? GetLastUsedLocal(RegistryKey key)
    {
        var knownKeys = new[] { "LastUsedTimeStop", "LastUsedTimeStart", "LastUsedTime", "InstallDate" };
        foreach (var knownKey in knownKeys)
        {
            var value = key.GetValue(knownKey);
            if (value is null)
            {
                continue;
            }

            if (value is long longValue && longValue > 0)
            {
                try
                {
                    return DateTime.FromFileTimeUtc(longValue).ToLocalTime();
                }
                catch
                {
                    // ignore parse failures
                }
            }

            var asString = value.ToString();
            if (string.IsNullOrWhiteSpace(asString))
            {
                continue;
            }

            if (DateTime.TryParse(asString, out var parsed))
            {
                return parsed;
            }

            if (asString.Length == 8 &&
                int.TryParse(asString[..4], out var year) &&
                int.TryParse(asString.Substring(4, 2), out var month) &&
                int.TryParse(asString.Substring(6, 2), out var day))
            {
                try
                {
                    return new DateTime(year, month, day);
                }
                catch
                {
                    // ignore invalid date parts
                }
            }
        }

        return null;
    }

    private static string NormalizeUninstallCommand(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed
                .Replace(" /I{", " /X{", StringComparison.OrdinalIgnoreCase)
                .Replace(" /I ", " /X ", StringComparison.OrdinalIgnoreCase);
        }

        return trimmed;
    }

    private static (string FileName, string Arguments) SplitCommand(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                var executable = trimmed[1..closingQuote];
                var args = closingQuote + 1 < trimmed.Length
                    ? trimmed[(closingQuote + 1)..].Trim()
                    : string.Empty;
                return (executable, args);
            }
        }

        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return (trimmed, string.Empty);
        }

        return (trimmed[..firstSpace], trimmed[(firstSpace + 1)..].Trim());
    }

    private static IReadOnlyList<string> BuildNameTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var normalized = new string(value
            .Trim()
            .Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_')
            .ToArray());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        var firstWord = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? normalized;
        return [normalized, compact, firstWord];
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static long EstimatePathSize(string fullPath, bool isDirectory, CancellationToken cancellationToken)
    {
        if (!isDirectory)
        {
            try
            {
                return new FileInfo(fullPath).Length;
            }
            catch
            {
                return 0;
            }
        }

        long total = 0;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(fullPath, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    total += new FileInfo(filePath).Length;
                }
                catch
                {
                    // ignore inaccessible file sizes
                }
            }
        }
        catch
        {
            return total;
        }

        return total;
    }
}
