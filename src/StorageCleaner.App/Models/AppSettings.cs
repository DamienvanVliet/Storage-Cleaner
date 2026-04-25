namespace StorageCleaner.App.Models;

public sealed class AppSettings
{
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Dark;

    public bool UseRecycleBinByDefault { get; set; } = true;

    public bool EnableIncrementalScan { get; set; } = true;

    public bool QueueLockedDeletesOnReboot { get; set; } = true;

    public int MaxScanParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
}
