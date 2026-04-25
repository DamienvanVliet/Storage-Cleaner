using System.Numerics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StorageCleaner.Core.Abstractions;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.Services;

public sealed class PhotoCleanupService : IPhotoCleanupService
{
    private static readonly HashSet<string> ImageExtensions =
    [
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif"
    ];

    private static readonly HashSet<string> VideoExtensions =
    [
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v", ".3gp"
    ];

    public Task<PhotoCleanupScanResult> ScanAsync(
        IReadOnlyCollection<string> roots,
        PhotoCleanupScanOptions? options = null,
        IProgress<PhotoCleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var scanOptions = options ?? new PhotoCleanupScanOptions();

        return Task.Run(() =>
        {
            var mediaItems = new List<PhotoCleanupItem>(2048);
            var screenshotItems = new List<PhotoCleanupItem>(512);
            var largeVideos = new List<PhotoCleanupItem>(512);
            var blurryPhotos = new List<PhotoCleanupItem>(512);
            var similarityCandidates = new List<PhotoCleanupItem>(2048);

            var skippedFiles = 0;
            var errorCount = 0;
            long filesVisited = 0;
            long mediaFound = 0;

            foreach (var root in roots.Where(static x => !string.IsNullOrWhiteSpace(x)))
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var filePath in EnumerateFilesSafe(root, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    filesVisited++;
                    progress?.Report(new PhotoCleanupProgress(filesVisited, mediaFound, filePath));

                    if (filesVisited > scanOptions.MaxFilesToAnalyze)
                    {
                        break;
                    }

                    var extension = Path.GetExtension(filePath);
                    var isImage = ImageExtensions.Contains(extension);
                    var isVideo = VideoExtensions.Contains(extension);
                    if (!isImage && !isVideo)
                    {
                        continue;
                    }

                    try
                    {
                        var info = new FileInfo(filePath);
                        if (!info.Exists)
                        {
                            continue;
                        }

                        var isScreenshot = IsScreenshotLike(info.FullName, info.Name);
                        var isBlurry = false;
                        var blurScore = 0d;
                        ulong? imageHash = null;
                        string? videoHint = null;

                        if (isImage)
                        {
                            if (TryAnalyzeImage(info.FullName, out var analyzedBlurScore, out var analyzedHash))
                            {
                                blurScore = analyzedBlurScore;
                                imageHash = analyzedHash;
                                isBlurry = analyzedBlurScore > 0 && analyzedBlurScore < scanOptions.BlurThreshold;
                            }
                            else
                            {
                                skippedFiles++;
                            }
                        }

                        if (isVideo && info.Length >= scanOptions.LargeVideoThresholdBytes)
                        {
                            videoHint = "Large video";
                        }

                        var item = new PhotoCleanupItem(
                            FullPath: info.FullName,
                            Name: info.Name,
                            ParentFolder: info.DirectoryName ?? root,
                            SizeBytes: info.Length,
                            LastModifiedUtc: info.LastWriteTimeUtc,
                            IsImage: isImage,
                            IsVideo: isVideo,
                            IsScreenshot: isScreenshot,
                            IsBlurry: isBlurry,
                            BlurScore: blurScore,
                            ImageHash: imageHash,
                            VideoHint: videoHint);

                        mediaItems.Add(item);
                        mediaFound++;

                        if (isScreenshot)
                        {
                            screenshotItems.Add(item);
                        }

                        if (item.IsVideo && item.VideoHint is not null)
                        {
                            largeVideos.Add(item);
                        }

                        if (item.IsImage && item.IsBlurry)
                        {
                            blurryPhotos.Add(item);
                        }

                        if (item.IsImage && item.ImageHash is not null)
                        {
                            similarityCandidates.Add(item);
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        errorCount++;
                    }
                }
            }

            var similarGroups = BuildSimilarGroups(
                similarityCandidates,
                scanOptions.MaxImagesForSimilarity,
                scanOptions.SimilarityHammingThreshold);

            return new PhotoCleanupScanResult(
                MediaItems: mediaItems
                    .OrderByDescending(static x => x.SizeBytes)
                    .ThenBy(static x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                ScreenshotItems: screenshotItems
                    .OrderByDescending(static x => x.SizeBytes)
                    .ThenBy(static x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                LargeVideos: largeVideos
                    .OrderByDescending(static x => x.SizeBytes)
                    .ThenBy(static x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                BlurryPhotos: blurryPhotos
                    .OrderBy(static x => x.BlurScore)
                    .ThenByDescending(static x => x.SizeBytes)
                    .ToArray(),
                SimilarGroups: similarGroups,
                SkippedFiles: skippedFiles,
                ErrorCount: errorCount);
        }, cancellationToken);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*", options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current, "*", options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pending.Push(info.FullName);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }
            }
        }
    }

    private static bool IsScreenshotLike(string fullPath, string fileName)
    {
        if (fullPath.Contains(@"\Screenshots\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.Contains("screenshot", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("screen shot", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("snip", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("capture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryAnalyzeImage(string fullPath, out double blurScore, out ulong imageHash)
    {
        blurScore = 0;
        imageHash = 0;

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            var frame = decoder.Frames[0];
            var blurPixels = CopyGrayPixels(frame, 64, 64);
            var hashPixels = CopyGrayPixels(frame, 8, 8);
            if (blurPixels is null || hashPixels is null)
            {
                return false;
            }

            blurScore = CalculateEdgeVariance(blurPixels.Value.Pixels, blurPixels.Value.Width, blurPixels.Value.Height);
            imageHash = CalculateAverageHash(hashPixels.Value.Pixels);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (byte[] Pixels, int Width, int Height)? CopyGrayPixels(BitmapSource source, int targetWidth, int targetHeight)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return null;
        }

        var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        var scaleX = targetWidth / (double)Math.Max(1, gray.PixelWidth);
        var scaleY = targetHeight / (double)Math.Max(1, gray.PixelHeight);
        var transformed = new TransformedBitmap(gray, new ScaleTransform(scaleX, scaleY));

        var width = transformed.PixelWidth;
        var height = transformed.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var stride = width;
        var pixels = new byte[stride * height];
        transformed.CopyPixels(pixels, stride, 0);
        return (pixels, width, height);
    }

    private static double CalculateEdgeVariance(byte[] pixels, int width, int height)
    {
        if (width < 3 || height < 3)
        {
            return 0;
        }

        var magnitudes = new double[(width - 2) * (height - 2)];
        var idx = 0;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var left = pixels[(y * width) + (x - 1)];
                var right = pixels[(y * width) + (x + 1)];
                var top = pixels[((y - 1) * width) + x];
                var bottom = pixels[((y + 1) * width) + x];
                var gx = right - left;
                var gy = bottom - top;
                magnitudes[idx++] = Math.Abs(gx) + Math.Abs(gy);
            }
        }

        var count = magnitudes.Length;
        if (count == 0)
        {
            return 0;
        }

        var mean = magnitudes.Average();
        var variance = magnitudes.Average(value =>
        {
            var delta = value - mean;
            return delta * delta;
        });
        return variance;
    }

    private static ulong CalculateAverageHash(byte[] pixels)
    {
        if (pixels.Length < 64)
        {
            return 0;
        }

        var average = pixels.Take(64).Average(static pixel => pixel);
        ulong hash = 0;
        for (var i = 0; i < 64; i++)
        {
            if (pixels[i] >= average)
            {
                hash |= 1UL << i;
            }
        }

        return hash;
    }

    private static IReadOnlyList<SimilarPhotoGroup> BuildSimilarGroups(
        IReadOnlyList<PhotoCleanupItem> candidates,
        int maxImagesForSimilarity,
        int hammingThreshold)
    {
        if (candidates.Count < 2)
        {
            return [];
        }

        var working = candidates
            .Where(static item => item.ImageHash is not null)
            .OrderByDescending(static item => item.SizeBytes)
            .Take(maxImagesForSimilarity)
            .ToArray();

        if (working.Length < 2)
        {
            return [];
        }

        var parent = Enumerable.Range(0, working.Length).ToArray();
        static int Find(int[] parentBuffer, int x)
        {
            while (parentBuffer[x] != x)
            {
                parentBuffer[x] = parentBuffer[parentBuffer[x]];
                x = parentBuffer[x];
            }

            return x;
        }

        static void Union(int[] parentBuffer, int a, int b)
        {
            var rootA = Find(parentBuffer, a);
            var rootB = Find(parentBuffer, b);
            if (rootA != rootB)
            {
                parentBuffer[rootB] = rootA;
            }
        }

        var buckets = new Dictionary<ushort, List<int>>();
        for (var i = 0; i < working.Length; i++)
        {
            var hash = working[i].ImageHash!.Value;
            var bucket = (ushort)(hash >> 48);
            if (!buckets.TryGetValue(bucket, out var list))
            {
                list = [];
                buckets[bucket] = list;
            }

            list.Add(i);
        }

        foreach (var bucket in buckets.Values)
        {
            for (var i = 0; i < bucket.Count; i++)
            {
                var leftIndex = bucket[i];
                var leftHash = working[leftIndex].ImageHash!.Value;
                for (var j = i + 1; j < bucket.Count; j++)
                {
                    var rightIndex = bucket[j];
                    var rightHash = working[rightIndex].ImageHash!.Value;
                    var distance = BitOperations.PopCount(leftHash ^ rightHash);
                    if (distance <= hammingThreshold)
                    {
                        Union(parent, leftIndex, rightIndex);
                    }
                }
            }
        }

        var grouped = new Dictionary<int, List<PhotoCleanupItem>>();
        for (var i = 0; i < working.Length; i++)
        {
            var root = Find(parent, i);
            if (!grouped.TryGetValue(root, out var items))
            {
                items = [];
                grouped[root] = items;
            }

            items.Add(working[i]);
        }

        return grouped.Values
            .Where(static group => group.Count > 1)
            .OrderByDescending(group => group.Sum(static item => item.SizeBytes))
            .Select((group, index) => new SimilarPhotoGroup(
                GroupId: $"SIM-{index + 1:000}",
                Items: group
                    .OrderByDescending(static item => item.SizeBytes)
                    .ThenBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();
    }
}
