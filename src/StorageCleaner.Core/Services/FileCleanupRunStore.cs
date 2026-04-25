using System.Text.Json;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class FileCleanupRunStore : ICleanupRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _runsDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileCleanupRunStore(string? runsDirectory = null)
    {
        _runsDirectory = runsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageCleaner",
            "runs");
    }

    public async Task<CleanupRunManifest> StartRunAsync(
        string runId,
        IReadOnlyCollection<CleanupCandidate> candidates,
        CleanupExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(candidates);

        var manifest = new CleanupRunManifest
        {
            RunId = runId,
            StartedAt = DateTimeOffset.UtcNow,
            IsSimulation = options.SimulationOnly,
            UseRecycleBin = options.UseRecycleBin,
            AllowRiskyPaths = options.AllowRiskyPaths,
            State = CleanupRunState.InProgress,
            LastCheckpointAt = DateTimeOffset.UtcNow,
            LastCheckpointMessage = "Cleanup run initialized.",
            BeforeState = candidates
                .Select(static candidate => new CleanupRunCandidateState(
                    candidate.FullPath,
                    candidate.IsDirectory,
                    candidate.SizeBytes,
                    candidate.LastModifiedUtc,
                    File.Exists(candidate.FullPath) || Directory.Exists(candidate.FullPath),
                    candidate.Category,
                    candidate.Risk.Level,
                    candidate.Risk.Reason))
                .OrderBy(static candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Results = []
        };

        await SaveManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        await AppendCheckpointAsync(
            runId,
            "Cleanup run started.",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    public async Task CompleteRunAsync(
        string runId,
        IReadOnlyList<CleanupItemResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(results);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = await ReadManifestCoreAsync(runId, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                return;
            }

            manifest.Results = results.ToArray();
            manifest.CompletedAt = DateTimeOffset.UtcNow;
            manifest.State = CleanupRunState.Completed;
            manifest.LastCheckpointAt = manifest.CompletedAt;
            manifest.LastCheckpointMessage = "Cleanup run completed.";
            await WriteManifestCoreAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AppendCheckpointAsync(
        string runId,
        string message,
        string? fullPath = null,
        bool isError = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = await ReadManifestCoreAsync(runId, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                return;
            }

            var checkpoints = manifest.Checkpoints.ToList();
            checkpoints.Add(new CleanupRunCheckpoint(
                Timestamp: DateTimeOffset.UtcNow,
                Message: message,
                Path: fullPath,
                IsError: isError));

            if (checkpoints.Count > 500)
            {
                checkpoints = checkpoints
                    .OrderByDescending(static checkpoint => checkpoint.Timestamp)
                    .Take(500)
                    .OrderBy(static checkpoint => checkpoint.Timestamp)
                    .ToList();
            }

            manifest.Checkpoints = checkpoints;
            manifest.LastCheckpointAt = checkpoints[^1].Timestamp;
            manifest.LastCheckpointMessage = checkpoints[^1].Message;
            await WriteManifestCoreAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<CleanupRunManifest>> RecoverInterruptedRunsAsync(
        TimeSpan staleThreshold,
        CancellationToken cancellationToken = default)
    {
        var recovered = new List<CleanupRunManifest>();
        var staleCutoff = DateTimeOffset.UtcNow - staleThreshold;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_runsDirectory);
            var files = Directory.EnumerateFiles(_runsDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CleanupRunManifest? manifest;
                try
                {
                    await using var stream = File.OpenRead(file);
                    manifest = await JsonSerializer.DeserializeAsync<CleanupRunManifest>(stream, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    continue;
                }

                if (manifest is null || manifest.State != CleanupRunState.InProgress)
                {
                    continue;
                }

                var lastSeen = manifest.LastCheckpointAt ?? manifest.StartedAt;
                if (lastSeen > staleCutoff)
                {
                    continue;
                }

                manifest.State = CleanupRunState.Recovered;
                manifest.CompletedAt = DateTimeOffset.UtcNow;
                manifest.RecoveryMessage = "Run was marked as recovered after unexpected interruption.";

                var checkpoints = manifest.Checkpoints.ToList();
                checkpoints.Add(new CleanupRunCheckpoint(
                    Timestamp: DateTimeOffset.UtcNow,
                    Message: manifest.RecoveryMessage,
                    Path: null,
                    IsError: true));
                manifest.Checkpoints = checkpoints.TakeLast(500).ToArray();
                manifest.LastCheckpointAt = checkpoints[^1].Timestamp;
                manifest.LastCheckpointMessage = checkpoints[^1].Message;

                await WriteManifestCoreAsync(manifest, cancellationToken).ConfigureAwait(false);
                recovered.Add(manifest);
            }
        }
        finally
        {
            _lock.Release();
        }

        return recovered;
    }

    public async Task<IReadOnlyList<CleanupRunManifest>> ReadRecentRunsAsync(
        int maxRuns = 100,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_runsDirectory);

        var files = Directory.EnumerateFiles(_runsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxRuns))
            .ToArray();

        var result = new List<CleanupRunManifest>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                var manifest = await JsonSerializer.DeserializeAsync<CleanupRunManifest>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (manifest is not null)
                {
                    result.Add(manifest);
                }
            }
            catch (JsonException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
        }

        return result
            .OrderByDescending(static run => run.StartedAt)
            .Take(Math.Max(1, maxRuns))
            .ToArray();
    }

    public async Task<string> ExportRunManifestAsync(
        string runId,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var sourceFile = GetRunPath(runId);
        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException("Run manifest not found.", sourceFile);
        }

        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(
            destinationDirectory,
            $"storage-cleaner-run-{runId}.json");

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var source = File.OpenRead(sourceFile);
            await using var destination = File.Create(destinationPath);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        return destinationPath;
    }

    private Task SaveManifestAsync(CleanupRunManifest manifest, CancellationToken cancellationToken)
    {
        return WriteManifestCoreAsync(manifest, cancellationToken);
    }

    private async Task<CleanupRunManifest?> ReadManifestCoreAsync(string runId, CancellationToken cancellationToken)
    {
        var path = GetRunPath(runId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CleanupRunManifest>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteManifestCoreAsync(CleanupRunManifest manifest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_runsDirectory);
        var path = GetRunPath(manifest.RunId);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private string GetRunPath(string runId)
    {
        return Path.Combine(_runsDirectory, $"{runId}.json");
    }
}
