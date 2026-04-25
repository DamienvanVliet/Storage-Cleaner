using System.Text.Json;
using System.Collections.Concurrent;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class FileCleanupAutomationService : ICleanupAutomationService
{
    private sealed class AutomationState
    {
        public List<CleanupAutomationRule> Rules { get; set; } = [];

        public List<CleanupAutomationRun> Runs { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ISafeCleanupAnalyzer _safeCleanupAnalyzer;
    private readonly ICleanupExecutor _cleanupExecutor;
    private readonly string _statePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly ConcurrentDictionary<string, byte> RunningRules = new(StringComparer.OrdinalIgnoreCase);

    public FileCleanupAutomationService(
        ISafeCleanupAnalyzer safeCleanupAnalyzer,
        ICleanupExecutor cleanupExecutor,
        string? statePath = null)
    {
        _safeCleanupAnalyzer = safeCleanupAnalyzer;
        _cleanupExecutor = cleanupExecutor;
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageCleaner",
            "automation-state.json");
    }

    public async Task<IReadOnlyList<CleanupAutomationRule>> ReadRulesAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.Rules
            .OrderBy(static rule => rule.NextRunAt)
            .ThenBy(static rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CleanupAutomationRule> UpsertRuleAsync(
        CleanupAutomationRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            throw new ArgumentException("Automation rule name is required.", nameof(rule));
        }

        var normalized = NormalizeRule(rule);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            var existingIndex = state.Rules.FindIndex(item =>
                string.Equals(item.RuleId, normalized.RuleId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                state.Rules[existingIndex] = normalized;
            }
            else
            {
                state.Rules.Add(normalized);
            }

            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return normalized;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RemoveRuleAsync(
        string ruleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return false;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            var removed = state.Rules.RemoveAll(rule =>
                string.Equals(rule.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed)
            {
                return false;
            }

            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<CleanupAutomationRun>> ReadRunsAsync(
        int maxRuns = 500,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.Runs
            .OrderByDescending(static run => run.StartedAt)
            .Take(Math.Clamp(maxRuns, 1, 5_000))
            .ToArray();
    }

    public async Task<CleanupAutomationRun> ExecuteRuleAsync(
        string ruleId,
        bool allowDestructive,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            throw new ArgumentException("Rule id is required.", nameof(ruleId));
        }

        CleanupAutomationRule? rule;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            rule = state.Rules.FirstOrDefault(item =>
                string.Equals(item.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }

        if (rule is null)
        {
            return CreateRun(
                ruleId,
                ruleName: "Unknown",
                success: false,
                simulation: true,
                candidateCount: 0,
                reclaimedBytes: 0,
                cleanupRunId: null,
                message: "Rule not found.",
                auditTrail: ["Rule lookup failed."]);
        }

        var audit = new List<string>();

        if (!rule.Enabled)
        {
            var disabledRun = CreateRun(
                rule.RuleId,
                rule.Name,
                success: false,
                simulation: true,
                candidateCount: 0,
                reclaimedBytes: 0,
                cleanupRunId: null,
                message: "Rule is disabled.",
                skipped: true,
                skipReason: "Disabled",
                auditTrail: ["Execution skipped because rule is disabled."]);
            await AppendRunAndUpdateRuleAsync(rule, disabledRun, cancellationToken).ConfigureAwait(false);
            return disabledRun;
        }

        var nowLocal = DateTime.Now.TimeOfDay;
        if (!IsWithinSafeWindow(rule, nowLocal))
        {
            var skippedRun = CreateRun(
                rule.RuleId,
                rule.Name,
                success: false,
                simulation: true,
                candidateCount: 0,
                reclaimedBytes: 0,
                cleanupRunId: null,
                message: "Skipped: outside configured safe window.",
                skipped: true,
                skipReason: "Outside safe window",
                auditTrail:
                [
                    $"Safe window start={FormatTime(rule.SafeWindowStartLocalTime)} end={FormatTime(rule.SafeWindowEndLocalTime)} current={nowLocal:hh\\:mm}."
                ]);
            await AppendRunAndUpdateRuleAsync(rule, skippedRun, cancellationToken).ConfigureAwait(false);
            return skippedRun;
        }

        if (!RunningRules.TryAdd(rule.RuleId, 0))
        {
            var conflictRun = CreateRun(
                rule.RuleId,
                rule.Name,
                success: false,
                simulation: true,
                candidateCount: 0,
                reclaimedBytes: 0,
                cleanupRunId: null,
                message: "Skipped: another execution of this rule is already running.",
                skipped: true,
                skipReason: "Concurrent execution conflict",
                conflictDetected: true,
                auditTrail: ["Execution skipped due to per-rule concurrency guard."]);
            await AppendRunAndUpdateRuleAsync(rule, conflictRun, cancellationToken).ConfigureAwait(false);
            return conflictRun;
        }

        var started = DateTimeOffset.UtcNow;
        try
        {
            var attemptsAllowed = Math.Clamp(rule.MaxRetryCount, 0, 10) + 1;
            var backoff = rule.RetryBackoff <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : rule.RetryBackoff;
            var simulationOnly = rule.PreviewOnly || !allowDestructive;
            var latestCandidateCount = 0;
            var latestReclaimedBytes = 0L;
            string? latestCleanupRunId = null;

            for (var attempt = 1; attempt <= attemptsAllowed; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                audit.Add($"Attempt {attempt} started at {DateTimeOffset.UtcNow:O}.");

                IReadOnlyList<CleanupCandidate> candidates;
                try
                {
                    candidates = await _safeCleanupAnalyzer.AnalyzeAsync(rule.Categories, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    audit.Add($"Attempt {attempt} failed during candidate analysis: {ex.Message}");
                    if (attempt == attemptsAllowed)
                    {
                        var failedRun = CreateRun(
                            rule.RuleId,
                            rule.Name,
                            success: false,
                            simulation: true,
                            candidateCount: latestCandidateCount,
                            reclaimedBytes: latestReclaimedBytes,
                            cleanupRunId: latestCleanupRunId,
                            message: $"Automation execution failed during analysis: {ex.Message}",
                            attemptCount: attempt,
                            nextRetryAt: null,
                            auditTrail: audit);
                        await AppendRunAndUpdateRuleAsync(rule, failedRun, cancellationToken).ConfigureAwait(false);
                        return failedRun;
                    }

                    await Task.Delay(GetRetryDelay(backoff, attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (rule.StrictSafety)
                {
                    candidates = candidates
                        .Where(static candidate => !candidate.Risk.RequiresExplicitOverride)
                        .ToArray();
                }

                latestCandidateCount = candidates.Count;
                if (latestCandidateCount == 0)
                {
                    audit.Add("No cleanup candidates available.");
                    var noCandidatesRun = CreateRun(
                        rule.RuleId,
                        rule.Name,
                        success: true,
                        simulation: true,
                        candidateCount: 0,
                        reclaimedBytes: 0,
                        cleanupRunId: null,
                        message: "No cleanup candidates available.",
                        attemptCount: attempt,
                        auditTrail: audit);
                    await AppendRunAndUpdateRuleAsync(rule, noCandidatesRun, cancellationToken).ConfigureAwait(false);
                    return noCandidatesRun;
                }

                try
                {
                    var execution = await _cleanupExecutor.ExecuteAsync(
                            candidates,
                            new CleanupExecutionOptions(
                                UseRecycleBin: true,
                                AllowRiskyPaths: false,
                                SimulationOnly: simulationOnly,
                                QueueLockedForReboot: true,
                                CaptureRestoreBackup: !simulationOnly),
                            cancellationToken)
                        .ConfigureAwait(false);

                    latestReclaimedBytes = execution.ReclaimedBytes;
                    latestCleanupRunId = execution.RunId;
                    audit.Add(
                        $"Attempt {attempt} finished. success={execution.FailureCount == 0} reclaimed={execution.ReclaimedBytes} failures={execution.FailureCount}.");

                    if (execution.FailureCount == 0 || attempt == attemptsAllowed)
                    {
                        var run = new CleanupAutomationRun
                        {
                            AutomationRunId = $"{started:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}",
                            RuleId = rule.RuleId,
                            RuleName = rule.Name,
                            StartedAt = started,
                            CompletedAt = DateTimeOffset.UtcNow,
                            Success = execution.FailureCount == 0,
                            IsSimulation = simulationOnly,
                            CandidateCount = candidates.Count,
                            ReclaimedBytes = execution.ReclaimedBytes,
                            Message = simulationOnly
                                ? $"Simulation finished. Potential reclaim: {execution.ReclaimedBytes} bytes."
                                : $"Cleanup finished. Reclaimed: {execution.ReclaimedBytes} bytes. Failures: {execution.FailureCount}.",
                            CleanupRunId = execution.RunId,
                            AttemptCount = attempt,
                            AuditTrail = audit.ToArray(),
                            NextRetryAt = execution.FailureCount == 0
                                ? null
                                : DateTimeOffset.UtcNow + GetRetryDelay(backoff, attempt)
                        };

                        await AppendRunAndUpdateRuleAsync(rule, run, cancellationToken).ConfigureAwait(false);
                        return run;
                    }
                }
                catch (Exception ex)
                {
                    audit.Add($"Attempt {attempt} failed during cleanup execution: {ex.Message}");
                    if (attempt == attemptsAllowed)
                    {
                        var failedRun = CreateRun(
                            rule.RuleId,
                            rule.Name,
                            success: false,
                            simulation: true,
                            candidateCount: latestCandidateCount,
                            reclaimedBytes: latestReclaimedBytes,
                            cleanupRunId: latestCleanupRunId,
                            message: $"Automation execution failed: {ex.Message}",
                            attemptCount: attempt,
                            nextRetryAt: null,
                            auditTrail: audit);
                        await AppendRunAndUpdateRuleAsync(rule, failedRun, cancellationToken).ConfigureAwait(false);
                        return failedRun;
                    }
                }

                var nextRetryAt = DateTimeOffset.UtcNow + GetRetryDelay(backoff, attempt);
                audit.Add($"Retry scheduled for approximately {nextRetryAt:O}.");
                await Task.Delay(GetRetryDelay(backoff, attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            RunningRules.TryRemove(rule.RuleId, out _);
        }

        var fallbackRun = CreateRun(
            rule.RuleId,
            rule.Name,
            success: false,
            simulation: true,
            candidateCount: 0,
            reclaimedBytes: 0,
            cleanupRunId: null,
            message: "Automation execution ended in an unknown state.",
            auditTrail: audit);
        await AppendRunAndUpdateRuleAsync(rule, fallbackRun, cancellationToken).ConfigureAwait(false);
        return fallbackRun;
    }

    public async Task<IReadOnlyList<CleanupAutomationRun>> RunDueRulesAsync(
        bool allowDestructive,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var dueAt = now ?? DateTimeOffset.Now;
        IReadOnlyList<CleanupAutomationRule> rules;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            rules = state.Rules
                .Where(rule => rule.Enabled && rule.NextRunAt <= dueAt)
                .OrderBy(static rule => rule.NextRunAt)
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }

        var runs = new List<CleanupAutomationRun>();
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = await ExecuteRuleAsync(rule.RuleId, allowDestructive, cancellationToken).ConfigureAwait(false);
            runs.Add(run);
        }

        return runs;
    }

    private async Task AppendRunAndUpdateRuleAsync(
        CleanupAutomationRule rule,
        CleanupAutomationRun run,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            var ruleIndex = state.Rules.FindIndex(item =>
                string.Equals(item.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase));
            if (ruleIndex >= 0)
            {
                var current = state.Rules[ruleIndex];
                current.LastRunAt = run.CompletedAt;
                current.NextRunAt = run.NextRetryAt ?? CalculateNextRunAt(current, from: run.CompletedAt.LocalDateTime);
                state.Rules[ruleIndex] = current;
            }

            state.Runs.Add(run);
            state.Runs = state.Runs
                .OrderByDescending(static item => item.StartedAt)
                .Take(5_000)
                .ToList();

            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static CleanupAutomationRun CreateRun(
        string ruleId,
        string ruleName,
        bool success,
        bool simulation,
        int candidateCount,
        long reclaimedBytes,
        string? cleanupRunId,
        string message,
        int attemptCount = 1,
        bool skipped = false,
        string? skipReason = null,
        bool conflictDetected = false,
        DateTimeOffset? nextRetryAt = null,
        IReadOnlyList<string>? auditTrail = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new CleanupAutomationRun
        {
            AutomationRunId = $"{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}",
            RuleId = ruleId,
            RuleName = ruleName,
            StartedAt = now,
            CompletedAt = now,
            Success = success,
            IsSimulation = simulation,
            CandidateCount = candidateCount,
            ReclaimedBytes = reclaimedBytes,
            Message = message,
            CleanupRunId = cleanupRunId,
            AttemptCount = Math.Max(1, attemptCount),
            Skipped = skipped,
            SkipReason = skipReason,
            ConflictDetected = conflictDetected,
            NextRetryAt = nextRetryAt,
            AuditTrail = auditTrail?.ToArray() ?? []
        };
    }

    private CleanupAutomationRule NormalizeRule(CleanupAutomationRule rule)
    {
        var normalizedCategories = rule.Categories
            .Distinct()
            .ToArray();
        if (normalizedCategories.Length == 0)
        {
            throw new ArgumentException("At least one cleanup category is required.", nameof(rule));
        }

        var ruleId = string.IsNullOrWhiteSpace(rule.RuleId)
            ? Guid.NewGuid().ToString("N")
            : rule.RuleId.Trim();

        var nextRun = rule.NextRunAt;
        if (nextRun <= DateTimeOffset.MinValue)
        {
            nextRun = CalculateNextRunAt(rule, from: DateTime.Now);
        }

        return new CleanupAutomationRule
        {
            RuleId = ruleId,
            Name = rule.Name.Trim(),
            Enabled = rule.Enabled,
            Categories = normalizedCategories,
            Frequency = rule.Frequency,
            DayOfWeek = rule.DayOfWeek,
            RunAtLocalTime = ClampTime(rule.RunAtLocalTime),
            PreviewOnly = rule.PreviewOnly,
            StrictSafety = rule.StrictSafety,
            MaxRetryCount = Math.Clamp(rule.MaxRetryCount, 0, 10),
            RetryBackoff = rule.RetryBackoff <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : rule.RetryBackoff,
            SafeWindowStartLocalTime = rule.SafeWindowStartLocalTime is null
                ? null
                : ClampTime(rule.SafeWindowStartLocalTime.Value),
            SafeWindowEndLocalTime = rule.SafeWindowEndLocalTime is null
                ? null
                : ClampTime(rule.SafeWindowEndLocalTime.Value),
            CreatedAt = rule.CreatedAt == default ? DateTimeOffset.UtcNow : rule.CreatedAt,
            LastRunAt = rule.LastRunAt,
            NextRunAt = nextRun
        };
    }

    private static DateTimeOffset CalculateNextRunAt(CleanupAutomationRule rule, DateTime from)
    {
        var localFrom = from;
        var runTime = ClampTime(rule.RunAtLocalTime);
        var candidate = new DateTime(
            localFrom.Year,
            localFrom.Month,
            localFrom.Day,
            runTime.Hours,
            runTime.Minutes,
            0,
            DateTimeKind.Local);

        switch (rule.Frequency)
        {
            case CleanupAutomationFrequency.Daily:
                if (candidate <= localFrom)
                {
                    candidate = candidate.AddDays(1);
                }

                break;

            case CleanupAutomationFrequency.Weekly:
                var targetDay = rule.DayOfWeek ?? DayOfWeek.Sunday;
                var daysUntil = ((int)targetDay - (int)localFrom.DayOfWeek + 7) % 7;
                candidate = candidate.AddDays(daysUntil);
                if (candidate <= localFrom)
                {
                    candidate = candidate.AddDays(7);
                }

                break;
        }

        return new DateTimeOffset(candidate);
    }

    private static TimeSpan ClampTime(TimeSpan value)
    {
        var hours = Math.Clamp(value.Hours, 0, 23);
        var minutes = Math.Clamp(value.Minutes, 0, 59);
        return new TimeSpan(hours, minutes, 0);
    }

    private static bool IsWithinSafeWindow(CleanupAutomationRule rule, TimeSpan currentLocalTime)
    {
        var start = rule.SafeWindowStartLocalTime;
        var end = rule.SafeWindowEndLocalTime;
        if (start is null && end is null)
        {
            return true;
        }

        var current = ClampTime(currentLocalTime);
        if (start is not null && end is null)
        {
            return current >= ClampTime(start.Value);
        }

        if (start is null && end is not null)
        {
            return current <= ClampTime(end.Value);
        }

        var windowStart = ClampTime(start!.Value);
        var windowEnd = ClampTime(end!.Value);
        if (windowStart <= windowEnd)
        {
            return current >= windowStart && current <= windowEnd;
        }

        // Overnight window, example: 22:00-03:00.
        return current >= windowStart || current <= windowEnd;
    }

    private static string FormatTime(TimeSpan? value)
    {
        return value is null
            ? "any"
            : ClampTime(value.Value).ToString(@"hh\:mm");
    }

    private static TimeSpan GetRetryDelay(TimeSpan backoff, int attempt)
    {
        var normalizedBackoff = backoff <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : backoff;
        var exponent = Math.Max(0, attempt - 1);
        var multiplier = Math.Pow(2, exponent);
        var seconds = normalizedBackoff.TotalSeconds * multiplier;
        var boundedSeconds = Math.Clamp(seconds, 15, TimeSpan.FromHours(6).TotalSeconds);
        return TimeSpan.FromSeconds(boundedSeconds);
    }

    private async Task<AutomationState> LoadStateAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<AutomationState> LoadStateCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return new AutomationState();
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            var state = await JsonSerializer.DeserializeAsync<AutomationState>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return state ?? new AutomationState();
        }
        catch (JsonException)
        {
            return new AutomationState();
        }
        catch (IOException)
        {
            return new AutomationState();
        }
    }

    private async Task SaveStateCoreAsync(AutomationState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_statePath);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
