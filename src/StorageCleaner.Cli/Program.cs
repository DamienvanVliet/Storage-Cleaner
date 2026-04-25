using Microsoft.Extensions.DependencyInjection;
using StorageCleaner.Core;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Extensions;
using StorageCleaner.Core.Models;
using StorageCleaner.Core.Services;

namespace StorageCleaner.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        using var serviceProvider = BuildServices().BuildServiceProvider();

        var command = args[0].Trim().ToLowerInvariant();
        var tail = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "scan" => await HandleScanAsync(tail, serviceProvider),
                "preview" => await HandlePreviewAsync(tail, serviceProvider),
                "clean" => await HandleCleanAsync(tail, serviceProvider),
                "restore" => await HandleRestoreAsync(tail, serviceProvider),
                "exclusions" => await HandleExclusionsAsync(tail, serviceProvider),
                "automation" => await HandleAutomationAsync(tail, serviceProvider),
                "status" => await HandleStatusAsync(serviceProvider),
                "help" => PrintHelpAndReturnSuccess(),
                _ => PrintUnknownAndReturnFailure(command)
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScanCache, MemoryScanCache>();
        services.AddSingleton<IExclusionService, FileExclusionService>();
        services.AddSingleton<IStorageScanner, StorageScanner>();
        services.AddSingleton<IFileSearchService, FileSearchService>();
        services.AddSingleton<IPathSafetyService, PathSafetyService>();
        services.AddSingleton<IRecycleBinService, RecycleBinService>();
        services.AddSingleton<ICleanupHistoryStore, FileCleanupHistoryStore>();
        services.AddSingleton<ICleanupRunStore, FileCleanupRunStore>();
        services.AddSingleton<IStorageSnapshotStore, FileStorageSnapshotStore>();
        services.AddSingleton<ISnapshotDiffService, SnapshotDiffService>();
        services.AddSingleton<IRestoreVaultService, FileRestoreVaultService>();
        services.AddSingleton<IRebootDeletionScheduler, WindowsRebootDeletionScheduler>();
        services.AddSingleton<ILockInspector, WindowsLockInspector>();
        services.AddSingleton<ISafeCleanupAnalyzer, SafeCleanupAnalyzer>();
        services.AddSingleton<IFileDuplicateFinder, FileDuplicateFinder>();
        services.AddSingleton<IWasteAnalysisService, WasteAnalysisService>();
        services.AddSingleton<IStorageAnalyticsService, StorageAnalyticsService>();
        services.AddSingleton<ICleanupExecutor, CleanupExecutor>();
        services.AddSingleton<ICleanupAutomationService, FileCleanupAutomationService>();
        return services;
    }

    private static async Task<int> HandleScanAsync(string[] args, IServiceProvider services)
    {
        var scanner = services.GetRequiredService<IStorageScanner>();
        var root = GetOption(args, "--root");
        if (string.IsNullOrWhiteSpace(root))
        {
            Console.WriteLine("scan requires --root <path>");
            return 1;
        }

        if (!Directory.Exists(root))
        {
            Console.WriteLine($"Root does not exist: {root}");
            return 1;
        }

        var parallel = ParseInt(args, "--parallel", fallback: Math.Clamp(Environment.ProcessorCount / 2, 1, 16));
        var modeRaw = GetOption(args, "--mode");
        var mode = Enum.TryParse<ScanMode>(modeRaw, ignoreCase: true, out var parsedMode)
            ? parsedMode
            : ScanMode.Standard;
        var request = new ScanRequest([root], MaxDegreeOfParallelism: parallel, UseCache: false, Mode: mode);
        var started = DateTimeOffset.UtcNow;
        var progress = new Progress<ScanProgress>(value =>
        {
            Console.Write($"\rScanning... folders={value.ProcessedDirectories:N0} files={value.ProcessedFiles:N0} bytes={value.ProcessedBytes.ToSizeString()} ");
        });

        var result = await scanner.ScanAsync(request, new PauseTokenSource().Token, progress);
        Console.WriteLine();
        Console.WriteLine($"Scan mode: {mode}");
        Console.WriteLine($"Scan completed in {(DateTimeOffset.UtcNow - started).TotalSeconds:0.0}s");
        Console.WriteLine($"Total bytes: {result.TotalScannedBytes.ToSizeString()}");
        Console.WriteLine($"Files: {result.TotalFiles:N0}");
        Console.WriteLine($"Folders: {result.TotalFolders:N0}");
        Console.WriteLine($"Warnings: {result.Issues.Count:N0}");
        return 0;
    }

    private static async Task<int> HandlePreviewAsync(string[] args, IServiceProvider services)
    {
        var analyzer = services.GetRequiredService<ISafeCleanupAnalyzer>();
        var categories = ParseCategories(GetOption(args, "--categories"));
        if (categories.Count == 0)
        {
            categories =
            [
                CleanupCategory.WindowsTemp,
                CleanupCategory.UserTemp,
                CleanupCategory.RecycleBin,
                CleanupCategory.BrowserCache,
                CleanupCategory.OldLogFiles
            ];
        }

        var candidates = await analyzer.AnalyzeAsync(categories);
        Console.WriteLine($"Candidates: {candidates.Count:N0}");
        Console.WriteLine($"Estimated reclaim: {candidates.Sum(static item => item.SizeBytes).ToSizeString()}");
        foreach (var candidate in candidates.Take(25))
        {
            Console.WriteLine($"- {candidate.Category,-15} {candidate.SizeBytes.ToSizeString(),10}  {candidate.FullPath}");
        }

        if (candidates.Count > 25)
        {
            Console.WriteLine($"... {candidates.Count - 25:N0} more");
        }

        return 0;
    }

    private static async Task<int> HandleCleanAsync(string[] args, IServiceProvider services)
    {
        var analyzer = services.GetRequiredService<ISafeCleanupAnalyzer>();
        var executor = services.GetRequiredService<ICleanupExecutor>();
        var categories = ParseCategories(GetOption(args, "--categories"));
        if (categories.Count == 0)
        {
            Console.WriteLine("clean requires --categories <CategoryA,CategoryB,...>");
            return 1;
        }

        var candidates = await analyzer.AnalyzeAsync(categories);
        if (candidates.Count == 0)
        {
            Console.WriteLine("No cleanup candidates were found.");
            return 0;
        }

        var safe = HasFlag(args, "--safe") || ConfirmTyped("Type DELETE to confirm cleanup");
        if (!safe)
        {
            Console.WriteLine("Cleanup canceled.");
            return 1;
        }

        var result = await executor.ExecuteAsync(
            candidates,
            new CleanupExecutionOptions(
                UseRecycleBin: true,
                AllowRiskyPaths: false,
                SimulationOnly: false,
                QueueLockedForReboot: true,
                CaptureRestoreBackup: true));

        Console.WriteLine($"Run: {result.RunId}");
        Console.WriteLine($"Success: {result.SuccessCount:N0}");
        Console.WriteLine($"Failed: {result.FailureCount:N0}");
        Console.WriteLine($"Queued for reboot: {result.QueuedForRebootCount:N0}");
        Console.WriteLine($"Reclaimed: {result.ReclaimedBytes.ToSizeString()}");
        return result.FailureCount > 0 ? 1 : 0;
    }

    private static async Task<int> HandleRestoreAsync(string[] args, IServiceProvider services)
    {
        var restoreService = services.GetRequiredService<IRestoreVaultService>();
        if (args.Length == 0)
        {
            Console.WriteLine("restore commands: list | recover --id <entryId> | purge --id <entryId> [--safe]");
            return 1;
        }

        var sub = args[0].Trim().ToLowerInvariant();
        var tail = args.Skip(1).ToArray();
        switch (sub)
        {
            case "list":
            {
                var entries = await restoreService.ReadEntriesAsync(maxEntries: 500);
                Console.WriteLine($"Restore entries: {entries.Count:N0}");
                foreach (var entry in entries.Take(50))
                {
                    Console.WriteLine($"- {entry.EntryId} | {entry.Category} | {entry.SizeBytes.ToSizeString()} | {entry.BackedUpAt:yyyy-MM-dd HH:mm} | {entry.OriginalPath}");
                }

                if (entries.Count > 50)
                {
                    Console.WriteLine($"... {entries.Count - 50:N0} more");
                }

                return 0;
            }

            case "recover":
            {
                var entryId = GetOption(tail, "--id");
                if (string.IsNullOrWhiteSpace(entryId))
                {
                    Console.WriteLine("restore recover requires --id <entryId>");
                    return 1;
                }

                var result = await restoreService.RestoreAsync(entryId);
                Console.WriteLine(result.Message);
                return result.Success ? 0 : 1;
            }

            case "purge":
            {
                var entryId = GetOption(tail, "--id");
                if (string.IsNullOrWhiteSpace(entryId))
                {
                    Console.WriteLine("restore purge requires --id <entryId>");
                    return 1;
                }

                if (!HasFlag(tail, "--safe") && !ConfirmTyped("Type DELETE to permanently purge this backup"))
                {
                    Console.WriteLine("Purge canceled.");
                    return 1;
                }

                var result = await restoreService.PurgeAsync(entryId);
                Console.WriteLine(result.Message);
                return result.Success ? 0 : 1;
            }

            default:
                Console.WriteLine($"Unknown restore subcommand: {sub}");
                return 1;
        }
    }

    private static async Task<int> HandleExclusionsAsync(string[] args, IServiceProvider services)
    {
        var exclusions = services.GetRequiredService<IExclusionService>();
        if (args.Length == 0)
        {
            Console.WriteLine("exclusions commands: list | add --kind <PathPrefix|FileExtension|Category|AppKeyword> --value <v> | remove --id <ruleId>");
            return 1;
        }

        var sub = args[0].Trim().ToLowerInvariant();
        var tail = args.Skip(1).ToArray();
        switch (sub)
        {
            case "list":
            {
                var rules = exclusions.GetRules();
                Console.WriteLine($"Exclusion rules: {rules.Count:N0}");
                foreach (var rule in rules)
                {
                    Console.WriteLine($"- {rule.RuleId} | {rule.Kind} | {rule.Value}");
                }

                return 0;
            }

            case "add":
            {
                var kindRaw = GetOption(tail, "--kind");
                var value = GetOption(tail, "--value");
                if (!Enum.TryParse<ExclusionRuleKind>(kindRaw, ignoreCase: true, out var kind) || string.IsNullOrWhiteSpace(value))
                {
                    Console.WriteLine("exclusions add requires valid --kind and --value");
                    return 1;
                }

                await exclusions.AddRuleAsync(kind, value);
                Console.WriteLine("Exclusion rule added.");
                return 0;
            }

            case "remove":
            {
                var ruleId = GetOption(tail, "--id");
                if (string.IsNullOrWhiteSpace(ruleId))
                {
                    Console.WriteLine("exclusions remove requires --id <ruleId>");
                    return 1;
                }

                await exclusions.RemoveRuleAsync(ruleId);
                Console.WriteLine("Exclusion rule removed.");
                return 0;
            }

            default:
                Console.WriteLine($"Unknown exclusions subcommand: {sub}");
                return 1;
        }
    }

    private static async Task<int> HandleAutomationAsync(string[] args, IServiceProvider services)
    {
        var automation = services.GetRequiredService<ICleanupAutomationService>();
        if (args.Length == 0)
        {
            Console.WriteLine("automation commands: list | add ... | remove --id <ruleId> | run --id <ruleId> [--safe]");
            return 1;
        }

        var sub = args[0].Trim().ToLowerInvariant();
        var tail = args.Skip(1).ToArray();
        switch (sub)
        {
            case "list":
            {
                var rules = await automation.ReadRulesAsync();
                Console.WriteLine($"Automation rules: {rules.Count:N0}");
                foreach (var rule in rules)
                {
                    var schedule = rule.Frequency == CleanupAutomationFrequency.Daily
                        ? $"Daily {rule.RunAtLocalTime:hh\\:mm}"
                        : $"Weekly {rule.DayOfWeek} {rule.RunAtLocalTime:hh\\:mm}";
                    Console.WriteLine($"- {rule.RuleId} | {rule.Name} | {schedule} | Preview={rule.PreviewOnly} | Next={rule.NextRunAt:yyyy-MM-dd HH:mm}");
                }

                return 0;
            }

            case "add":
            {
                var name = GetOption(tail, "--name");
                var categories = ParseCategories(GetOption(tail, "--categories"));
                var frequencyRaw = GetOption(tail, "--frequency") ?? "daily";
                var timeRaw = GetOption(tail, "--time") ?? "02:00";
                var dayRaw = GetOption(tail, "--day");
                var previewOnly = ParseBool(GetOption(tail, "--preview"), defaultValue: true);
                var strictSafety = ParseBool(GetOption(tail, "--strict"), defaultValue: true);

                if (string.IsNullOrWhiteSpace(name) || categories.Count == 0)
                {
                    Console.WriteLine("automation add requires --name and --categories");
                    return 1;
                }

                if (!Enum.TryParse<CleanupAutomationFrequency>(frequencyRaw, ignoreCase: true, out var frequency))
                {
                    Console.WriteLine("Invalid --frequency. Use Daily or Weekly.");
                    return 1;
                }

                if (!TimeSpan.TryParse(timeRaw, out var runTime))
                {
                    Console.WriteLine("Invalid --time. Use HH:mm");
                    return 1;
                }

                DayOfWeek? day = null;
                if (frequency == CleanupAutomationFrequency.Weekly)
                {
                    if (!Enum.TryParse<DayOfWeek>(dayRaw, ignoreCase: true, out var parsedDay))
                    {
                        Console.WriteLine("Weekly frequency requires --day <Monday..Sunday>.");
                        return 1;
                    }

                    day = parsedDay;
                }

                var now = DateTimeOffset.Now;
                var rule = new CleanupAutomationRule
                {
                    RuleId = Guid.NewGuid().ToString("N"),
                    Name = name,
                    Enabled = true,
                    Categories = categories,
                    Frequency = frequency,
                    DayOfWeek = day,
                    RunAtLocalTime = new TimeSpan(runTime.Hours, runTime.Minutes, 0),
                    PreviewOnly = previewOnly,
                    StrictSafety = strictSafety,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastRunAt = null,
                    NextRunAt = CalculateNextRunAt(frequency, day ?? DayOfWeek.Sunday, runTime, now)
                };

                var saved = await automation.UpsertRuleAsync(rule);
                Console.WriteLine($"Automation rule saved: {saved.RuleId}");
                return 0;
            }

            case "remove":
            {
                var id = GetOption(tail, "--id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    Console.WriteLine("automation remove requires --id <ruleId>");
                    return 1;
                }

                var removed = await automation.RemoveRuleAsync(id);
                Console.WriteLine(removed ? "Automation rule removed." : "Rule not found.");
                return removed ? 0 : 1;
            }

            case "run":
            {
                var id = GetOption(tail, "--id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    Console.WriteLine("automation run requires --id <ruleId>");
                    return 1;
                }

                var safe = HasFlag(tail, "--safe") || ConfirmTyped("Type DELETE to allow destructive automation execution");
                var run = await automation.ExecuteRuleAsync(id, allowDestructive: safe);
                Console.WriteLine($"Rule: {run.RuleName}");
                Console.WriteLine($"Simulation: {run.IsSimulation}");
                Console.WriteLine($"Candidates: {run.CandidateCount:N0}");
                Console.WriteLine($"Reclaimed: {run.ReclaimedBytes.ToSizeString()}");
                Console.WriteLine($"Message: {run.Message}");
                return run.Success ? 0 : 1;
            }

            default:
                Console.WriteLine($"Unknown automation subcommand: {sub}");
                return 1;
        }
    }

    private static async Task<int> HandleStatusAsync(IServiceProvider services)
    {
        var runStore = services.GetRequiredService<ICleanupRunStore>();
        var restoreStore = services.GetRequiredService<IRestoreVaultService>();
        var automation = services.GetRequiredService<ICleanupAutomationService>();

        var runs = await runStore.ReadRecentRunsAsync(maxRuns: 10);
        var restoreEntries = await restoreStore.ReadEntriesAsync(maxEntries: 10);
        var rules = await automation.ReadRulesAsync();

        Console.WriteLine($"Recent cleanup runs: {runs.Count:N0}");
        foreach (var run in runs.Take(5))
        {
            Console.WriteLine($"- {run.RunId} | {run.StartedAt:yyyy-MM-dd HH:mm} | sim={run.IsSimulation} | reclaimed={run.ReclaimedBytes.ToSizeString()}");
        }

        Console.WriteLine($"Restore entries: {restoreEntries.Count:N0}");
        Console.WriteLine($"Automation rules: {rules.Count:N0}");
        return 0;
    }

    private static IReadOnlyList<CleanupCategory> ParseCategories(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var categories = new List<CleanupCategory>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<CleanupCategory>(token, ignoreCase: true, out var category))
            {
                categories.Add(category);
            }
        }

        return categories.Distinct().ToArray();
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Length)
            {
                return null;
            }

            return args[i + 1];
        }

        return null;
    }

    private static int ParseInt(string[] args, string name, int fallback)
    {
        var raw = GetOption(args, name);
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    private static bool ParseBool(string? raw, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return bool.TryParse(raw, out var value) ? value : defaultValue;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        return args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ConfirmTyped(string message)
    {
        Console.WriteLine(message);
        Console.Write("Confirm: ");
        var typed = Console.ReadLine();
        return string.Equals(typed?.Trim(), "DELETE", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset CalculateNextRunAt(
        CleanupAutomationFrequency frequency,
        DayOfWeek selectedDay,
        TimeSpan runAt,
        DateTimeOffset from)
    {
        var local = from.LocalDateTime;
        var candidate = new DateTime(local.Year, local.Month, local.Day, runAt.Hours, runAt.Minutes, 0, DateTimeKind.Local);

        if (frequency == CleanupAutomationFrequency.Daily)
        {
            if (candidate <= local)
            {
                candidate = candidate.AddDays(1);
            }

            return new DateTimeOffset(candidate);
        }

        var daysUntil = ((int)selectedDay - (int)local.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(daysUntil);
        if (candidate <= local)
        {
            candidate = candidate.AddDays(7);
        }

        return new DateTimeOffset(candidate);
    }

    private static int PrintHelpAndReturnSuccess()
    {
        PrintHelp();
        return 0;
    }

    private static int PrintUnknownAndReturnFailure(string command)
    {
        Console.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("StorageCleaner CLI");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  scan --root <path> [--parallel 8] [--mode Standard|NtfsFast]");
        Console.WriteLine("  preview --categories <A,B,C>");
        Console.WriteLine("  clean --categories <A,B,C> [--safe]");
        Console.WriteLine("  restore list");
        Console.WriteLine("  restore recover --id <entryId>");
        Console.WriteLine("  restore purge --id <entryId> [--safe]");
        Console.WriteLine("  exclusions list");
        Console.WriteLine("  exclusions add --kind <PathPrefix|FileExtension|Category|AppKeyword> --value <v>");
        Console.WriteLine("  exclusions remove --id <ruleId>");
        Console.WriteLine("  automation list");
        Console.WriteLine("  automation add --name <n> --categories <A,B> --frequency <Daily|Weekly> --time HH:mm [--day Monday] [--preview true] [--strict true]");
        Console.WriteLine("  automation remove --id <ruleId>");
        Console.WriteLine("  automation run --id <ruleId> [--safe]");
        Console.WriteLine("  status");
        Console.WriteLine();
        Console.WriteLine("Cleanup categories:");
        Console.WriteLine("  ManualSelection, WindowsTemp, UserTemp, RecycleBin, BrowserCache, OldLogFiles, DuplicateFiles, NeverAccessedFiles");
    }
}
