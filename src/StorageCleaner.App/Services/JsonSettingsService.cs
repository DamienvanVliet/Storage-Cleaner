using System.Text.Json;
using System.IO;
using StorageCleaner.App.Models;

namespace StorageCleaner.App.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonSettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageCleaner",
            "settings.json");

        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Current = new AppSettings();
                return Current;
            }

            await using var stream = File.OpenRead(_settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            Current = settings ?? new AppSettings();
            return Current;
        }
        catch (JsonException)
        {
            Current = new AppSettings();
            return Current;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.Create(_settingsPath);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
            Current = settings;
        }
        finally
        {
            _lock.Release();
        }
    }
}
