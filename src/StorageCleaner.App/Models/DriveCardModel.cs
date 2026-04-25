namespace StorageCleaner.App.Models;

public sealed record DriveCardModel(
    string Name,
    string RootPath,
    long TotalBytes,
    long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    public double UsedPercent => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes * 100d;
}
