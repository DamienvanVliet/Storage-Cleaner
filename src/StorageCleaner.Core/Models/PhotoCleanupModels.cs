namespace StorageCleaner.Core.Models;

public sealed record PhotoCleanupScanOptions(
    long LargeVideoThresholdBytes = 500L * 1024L * 1024L,
    double BlurThreshold = 26.0,
    int MaxFilesToAnalyze = 25_000,
    int MaxImagesForSimilarity = 4_000,
    int SimilarityHammingThreshold = 6);

public sealed record PhotoCleanupProgress(
    long FilesVisited,
    long MediaFilesFound,
    string? CurrentPath);

public sealed record PhotoCleanupItem(
    string FullPath,
    string Name,
    string ParentFolder,
    long SizeBytes,
    DateTime LastModifiedUtc,
    bool IsImage,
    bool IsVideo,
    bool IsScreenshot,
    bool IsBlurry,
    double BlurScore,
    ulong? ImageHash,
    string? VideoHint);

public sealed record SimilarPhotoGroup(
    string GroupId,
    IReadOnlyList<PhotoCleanupItem> Items);

public sealed record PhotoCleanupScanResult(
    IReadOnlyList<PhotoCleanupItem> MediaItems,
    IReadOnlyList<PhotoCleanupItem> ScreenshotItems,
    IReadOnlyList<PhotoCleanupItem> LargeVideos,
    IReadOnlyList<PhotoCleanupItem> BlurryPhotos,
    IReadOnlyList<SimilarPhotoGroup> SimilarGroups,
    int SkippedFiles,
    int ErrorCount);
