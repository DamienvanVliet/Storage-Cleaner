namespace StorageCleaner.Core.Extensions;

public static class SizeFormattingExtensions
{
    private static readonly string[] Suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string ToSizeString(this long bytes)
    {
        if (bytes < 0)
        {
            return "-" + ToSizeString(Math.Abs(bytes));
        }

        double value = bytes;
        var suffixIndex = 0;
        while (value >= 1024d && suffixIndex < Suffixes.Length - 1)
        {
            value /= 1024d;
            suffixIndex++;
        }

        return $"{value:0.##} {Suffixes[suffixIndex]}";
    }
}
