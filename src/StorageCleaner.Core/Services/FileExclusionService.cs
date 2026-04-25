using System.Text.Json;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.Core.Services;

public sealed class FileExclusionService : IExclusionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _rulesPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyList<ExclusionRule> _rules = [];

    public FileExclusionService(string? rulesPath = null)
    {
        _rulesPath = rulesPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageCleaner",
            "exclusions.json");

        _rules = LoadSync();
    }

    public IReadOnlyList<ExclusionRule> GetRules()
    {
        return _rules.ToArray();
    }

    public async Task<IReadOnlyList<ExclusionRule>> AddRuleAsync(
        ExclusionRuleKind kind,
        string value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return _rules;
        }

        var normalized = NormalizeValue(kind, value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return _rules;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _rules.ToList();
            if (current.Any(rule =>
                    rule.Kind == kind &&
                    string.Equals(NormalizeValue(rule.Kind, rule.Value), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return _rules;
            }

            current.Add(new ExclusionRule
            {
                RuleId = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Value = normalized,
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow
            });

            var saved = current
                .Where(static rule => rule.Enabled)
                .OrderBy(static rule => rule.Kind)
                .ThenBy(static rule => rule.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await SaveRulesAsync(saved, cancellationToken).ConfigureAwait(false);
            _rules = saved;
            return saved;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ExclusionRule>> RemoveRuleAsync(
        string ruleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return _rules;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = _rules
                .Where(rule => !string.Equals(rule.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static rule => rule.Kind)
                .ThenBy(static rule => rule.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await SaveRulesAsync(saved, cancellationToken).ConfigureAwait(false);
            _rules = saved;
            return saved;
        }
        finally
        {
            _lock.Release();
        }
    }

    public ExclusionMatch Match(string path, CleanupCategory? category = null, string? appName = null)
    {
        var rules = _rules;
        if (rules.Count == 0)
        {
            return new ExclusionMatch(false, null, "No exclusion rule matched.");
        }

        var normalizedPath = NormalizePathOrFallback(path);
        var extension = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetExtension(path).Trim();

        foreach (var rule in rules)
        {
            switch (rule.Kind)
            {
                case ExclusionRuleKind.PathPrefix:
                {
                    var prefix = NormalizePathOrFallback(rule.Value);
                    if (IsSubPathOfOrEqual(normalizedPath, prefix))
                    {
                        return new ExclusionMatch(true, rule.RuleId, $"Excluded by path rule: {rule.Value}");
                    }

                    break;
                }
                case ExclusionRuleKind.FileExtension:
                {
                    var expected = NormalizeValue(rule.Kind, rule.Value);
                    var actual = NormalizeValue(rule.Kind, extension);
                    if (!string.IsNullOrWhiteSpace(actual) &&
                        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ExclusionMatch(true, rule.RuleId, $"Excluded by extension rule: {rule.Value}");
                    }

                    break;
                }
                case ExclusionRuleKind.Category:
                {
                    if (category is null)
                    {
                        break;
                    }

                    if (Enum.TryParse<CleanupCategory>(rule.Value, ignoreCase: true, out var parsed) &&
                        parsed == category.Value)
                    {
                        return new ExclusionMatch(true, rule.RuleId, $"Excluded by category rule: {rule.Value}");
                    }

                    break;
                }
                case ExclusionRuleKind.AppKeyword:
                {
                    if (!string.IsNullOrWhiteSpace(appName) &&
                        appName.Contains(rule.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ExclusionMatch(true, rule.RuleId, $"Excluded by app rule: {rule.Value}");
                    }

                    if (!string.IsNullOrWhiteSpace(normalizedPath) &&
                        normalizedPath.Contains(rule.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ExclusionMatch(true, rule.RuleId, $"Excluded by app keyword rule: {rule.Value}");
                    }

                    break;
                }
            }
        }

        return new ExclusionMatch(false, null, "No exclusion rule matched.");
    }

    private async Task SaveRulesAsync(IReadOnlyList<ExclusionRule> rules, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_rulesPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_rulesPath);
        await JsonSerializer.SerializeAsync(stream, rules, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<ExclusionRule> LoadSync()
    {
        if (!File.Exists(_rulesPath))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_rulesPath);
            var rules = JsonSerializer.Deserialize<IReadOnlyList<ExclusionRule>>(stream, JsonOptions);
            return rules?
                .Where(static rule => rule.Enabled)
                .OrderBy(static rule => rule.Kind)
                .ThenBy(static rule => rule.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string NormalizeValue(ExclusionRuleKind kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return kind switch
        {
            ExclusionRuleKind.FileExtension => trimmed.StartsWith('.') ? trimmed.ToLowerInvariant() : "." + trimmed.ToLowerInvariant(),
            ExclusionRuleKind.Category => trimmed,
            _ => trimmed
        };
    }

    private static string NormalizePathOrFallback(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            if (Path.GetPathRoot(full)?.Equals(full, StringComparison.OrdinalIgnoreCase) == true)
            {
                return full;
            }

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static bool IsSubPathOfOrEqual(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        if (string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedRoot = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
