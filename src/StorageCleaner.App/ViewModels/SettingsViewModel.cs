using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using StorageCleaner.App.Models;
using StorageCleaner.App.Services;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IPathSafetyService _pathSafetyService;
    private readonly IExclusionService _exclusionService;
    private readonly ICleanupAutomationService _automationService;

    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        IDialogService dialogService,
        IPathSafetyService pathSafetyService,
        IExclusionService exclusionService,
        ICleanupAutomationService automationService)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _dialogService = dialogService;
        _pathSafetyService = pathSafetyService;
        _exclusionService = exclusionService;
        _automationService = automationService;

        var current = _settingsService.Current;
        SelectedTheme = ThemeMode.Dark;
        UseRecycleBinByDefault = current.UseRecycleBinByDefault;
        EnableIncrementalScan = current.EnableIncrementalScan;
        QueueLockedDeletesOnReboot = current.QueueLockedDeletesOnReboot;
        MaxScanParallelism = current.MaxScanParallelism;

        ProtectedProfileRoots = [];
        ExclusionRules = [];
        AutomationRules = [];
        LoadProtectedRoots();
        LoadExclusionRules();
        _ = RefreshAutomationRulesAsync();
    }

    public IReadOnlyList<ThemeMode> ThemeModes { get; } = [ThemeMode.Dark];

    [ObservableProperty]
    private ThemeMode selectedTheme;

    [ObservableProperty]
    private bool useRecycleBinByDefault;

    [ObservableProperty]
    private bool enableIncrementalScan;

    [ObservableProperty]
    private bool queueLockedDeletesOnReboot;

    [ObservableProperty]
    private int maxScanParallelism;

    [ObservableProperty]
    private string? newProtectedRootPath;

    public ObservableCollection<string> ProtectedProfileRoots { get; }

    public IReadOnlyList<ExclusionRuleKind> ExclusionKinds { get; } =
        Enum.GetValues<ExclusionRuleKind>();

    [ObservableProperty]
    private ExclusionRuleKind selectedExclusionKind = ExclusionRuleKind.PathPrefix;

    [ObservableProperty]
    private string? newExclusionValue;

    public ObservableCollection<ExclusionRule> ExclusionRules { get; }

    public IReadOnlyList<CleanupAutomationFrequency> AutomationFrequencies { get; } =
        Enum.GetValues<CleanupAutomationFrequency>();

    public IReadOnlyList<DayOfWeek> AutomationDays { get; } =
        Enum.GetValues<DayOfWeek>();

    [ObservableProperty]
    private string newAutomationName = "Nightly Safe Cleanup";

    [ObservableProperty]
    private CleanupAutomationFrequency selectedAutomationFrequency = CleanupAutomationFrequency.Daily;

    [ObservableProperty]
    private DayOfWeek selectedAutomationDay = DayOfWeek.Sunday;

    [ObservableProperty]
    private string newAutomationTime = "02:00";

    [ObservableProperty]
    private bool newAutomationPreviewOnly = true;

    [ObservableProperty]
    private bool newAutomationStrictSafety = true;

    [ObservableProperty]
    private string newAutomationCategories = "WindowsTemp,UserTemp,RecycleBin,BrowserCache,OldLogFiles";

    [ObservableProperty]
    private int newAutomationMaxRetryCount = 2;

    [ObservableProperty]
    private int newAutomationRetryBackoffMinutes = 2;

    [ObservableProperty]
    private string newAutomationSafeWindowStart = "00:00";

    [ObservableProperty]
    private string newAutomationSafeWindowEnd = "23:59";

    public ObservableCollection<AutomationRuleRowViewModel> AutomationRules { get; }

    [RelayCommand]
    private void ApplyTheme()
    {
        _themeService.ApplyTheme(SelectedTheme);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var settings = new AppSettings
        {
            ThemeMode = SelectedTheme,
            UseRecycleBinByDefault = UseRecycleBinByDefault,
            EnableIncrementalScan = EnableIncrementalScan,
            QueueLockedDeletesOnReboot = QueueLockedDeletesOnReboot,
            MaxScanParallelism = Math.Clamp(MaxScanParallelism, 1, 16)
        };

        await _settingsService.SaveAsync(settings);
        _themeService.ApplyTheme(settings.ThemeMode);
        _dialogService.ShowInfo("Settings", "Settings saved.");
    }

    [RelayCommand]
    private async Task DiscoverProfilesAsync()
    {
        var profiles = await _pathSafetyService.DiscoverAndProtectDefaultProfilesAsync();
        LoadProtectedRoots(profiles);
        _dialogService.ShowInfo("Protection Profiles", $"Protected roots discovered: {profiles.Count}.");
    }

    [RelayCommand]
    private async Task AddProtectedRootAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProtectedRootPath))
        {
            _dialogService.ShowInfo("Protection Profiles", "Enter a folder path to protect.");
            return;
        }

        await _pathSafetyService.AddProtectedProfileRootAsync(NewProtectedRootPath);
        NewProtectedRootPath = string.Empty;
        LoadProtectedRoots();
    }

    [RelayCommand]
    private async Task RemoveProtectedRootAsync(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        await _pathSafetyService.RemoveProtectedProfileRootAsync(rootPath);
        LoadProtectedRoots();
    }

    [RelayCommand]
    private async Task AddExclusionRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(NewExclusionValue))
        {
            _dialogService.ShowInfo("Exclusions", "Enter a value for the exclusion rule.");
            return;
        }

        await _exclusionService.AddRuleAsync(SelectedExclusionKind, NewExclusionValue);
        NewExclusionValue = string.Empty;
        LoadExclusionRules();
    }

    [RelayCommand]
    private async Task RemoveExclusionRuleAsync(string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return;
        }

        await _exclusionService.RemoveRuleAsync(ruleId);
        LoadExclusionRules();
    }

    [RelayCommand]
    private async Task RefreshAutomationRulesAsync()
    {
        var rules = await _automationService.ReadRulesAsync();
        AutomationRules.Clear();
        foreach (var rule in rules)
        {
            AutomationRules.Add(new AutomationRuleRowViewModel(rule));
        }
    }

    [RelayCommand]
    private async Task AddAutomationRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(NewAutomationName))
        {
            _dialogService.ShowInfo("Automation", "Provide a rule name.");
            return;
        }

        if (!TimeSpan.TryParse(NewAutomationTime, out var parsedTime))
        {
            _dialogService.ShowError("Automation", "Time must be in HH:mm format.");
            return;
        }

        var categories = ParseCategories(NewAutomationCategories);
        if (categories.Count == 0)
        {
            _dialogService.ShowError("Automation", "Provide at least one valid cleanup category.");
            return;
        }

        if (!TimeSpan.TryParse(NewAutomationSafeWindowStart, out var safeWindowStart))
        {
            _dialogService.ShowError("Automation", "Safe window start must be in HH:mm format.");
            return;
        }

        if (!TimeSpan.TryParse(NewAutomationSafeWindowEnd, out var safeWindowEnd))
        {
            _dialogService.ShowError("Automation", "Safe window end must be in HH:mm format.");
            return;
        }

        var now = DateTimeOffset.Now;
        var nextRun = CalculateNextRunAt(
            SelectedAutomationFrequency,
            SelectedAutomationDay,
            parsedTime,
            now);

        var rule = new CleanupAutomationRule
        {
            RuleId = Guid.NewGuid().ToString("N"),
            Name = NewAutomationName.Trim(),
            Enabled = true,
            Categories = categories,
            Frequency = SelectedAutomationFrequency,
            DayOfWeek = SelectedAutomationFrequency == CleanupAutomationFrequency.Weekly ? SelectedAutomationDay : null,
            RunAtLocalTime = new TimeSpan(parsedTime.Hours, parsedTime.Minutes, 0),
            PreviewOnly = NewAutomationPreviewOnly,
            StrictSafety = NewAutomationStrictSafety,
            MaxRetryCount = Math.Clamp(NewAutomationMaxRetryCount, 0, 10),
            RetryBackoff = TimeSpan.FromMinutes(Math.Clamp(NewAutomationRetryBackoffMinutes, 1, 120)),
            SafeWindowStartLocalTime = new TimeSpan(safeWindowStart.Hours, safeWindowStart.Minutes, 0),
            SafeWindowEndLocalTime = new TimeSpan(safeWindowEnd.Hours, safeWindowEnd.Minutes, 0),
            CreatedAt = DateTimeOffset.UtcNow,
            LastRunAt = null,
            NextRunAt = nextRun
        };

        await _automationService.UpsertRuleAsync(rule);
        await RefreshAutomationRulesAsync();
    }

    [RelayCommand]
    private async Task RemoveAutomationRuleAsync(string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return;
        }

        await _automationService.RemoveRuleAsync(ruleId);
        await RefreshAutomationRulesAsync();
    }

    [RelayCommand]
    private async Task RunAutomationNowAsync(string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return;
        }

        var row = AutomationRules.FirstOrDefault(item =>
            string.Equals(item.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        var allowDestructive = false;
        if (!row.PreviewOnly)
        {
            allowDestructive = _dialogService.ConfirmTyped(
                "Run Automation Cleanup",
                $"Rule \"{row.Name}\" is configured for destructive cleanup.\n\nType DELETE to run now.",
                expectedText: "DELETE",
                warning: true);
            if (!allowDestructive)
            {
                return;
            }
        }

        var run = await _automationService.ExecuteRuleAsync(ruleId, allowDestructive);
        _dialogService.ShowInfo(
            "Automation",
            $"Rule: {run.RuleName}\nSimulation: {run.IsSimulation}\nCandidates: {run.CandidateCount:N0}\nReclaimed: {run.ReclaimedBytes:N0} bytes\nMessage: {run.Message}");
        await RefreshAutomationRulesAsync();
    }

    private void LoadProtectedRoots(IReadOnlyList<string>? roots = null)
    {
        ProtectedProfileRoots.Clear();
        foreach (var root in (roots ?? _pathSafetyService.GetProtectedProfileRoots())
                     .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
        {
            ProtectedProfileRoots.Add(root);
        }
    }

    private void LoadExclusionRules()
    {
        ExclusionRules.Clear();
        foreach (var rule in _exclusionService.GetRules())
        {
            ExclusionRules.Add(rule);
        }
    }

    private static IReadOnlyList<CleanupCategory> ParseCategories(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var result = new List<CleanupCategory>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<CleanupCategory>(token, ignoreCase: true, out var category))
            {
                result.Add(category);
            }
        }

        return result.Distinct().ToArray();
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
}

public sealed class AutomationRuleRowViewModel
{
    public AutomationRuleRowViewModel(CleanupAutomationRule rule)
    {
        Rule = rule;
    }

    public CleanupAutomationRule Rule { get; }

    public string RuleId => Rule.RuleId;

    public string Name => Rule.Name;

    public bool Enabled => Rule.Enabled;

    public bool PreviewOnly => Rule.PreviewOnly;

    public bool StrictSafety => Rule.StrictSafety;

    public int MaxRetryCount => Rule.MaxRetryCount;

    public string RetryBackoff => $"{Rule.RetryBackoff.TotalMinutes:0}m";

    public string SafeWindow
    {
        get
        {
            var start = Rule.SafeWindowStartLocalTime is null ? "any" : $"{Rule.SafeWindowStartLocalTime:hh\\:mm}";
            var end = Rule.SafeWindowEndLocalTime is null ? "any" : $"{Rule.SafeWindowEndLocalTime:hh\\:mm}";
            return $"{start} - {end}";
        }
    }

    public DateTimeOffset NextRunAt => Rule.NextRunAt;

    public DateTimeOffset? LastRunAt => Rule.LastRunAt;

    public string Categories => string.Join(", ", Rule.Categories);

    public string Schedule => Rule.Frequency == CleanupAutomationFrequency.Daily
        ? $"Daily {Rule.RunAtLocalTime:hh\\:mm}"
        : $"Weekly {Rule.DayOfWeek} {Rule.RunAtLocalTime:hh\\:mm}";
}
