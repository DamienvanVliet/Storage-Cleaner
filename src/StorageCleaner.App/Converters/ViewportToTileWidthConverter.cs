using System.Globalization;
using System.Windows.Data;

namespace StorageCleaner.App.Converters;

public sealed class ViewportToTileWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width || width <= 0 || double.IsNaN(width))
        {
            return 320d;
        }

        const double gap = 12d;

        if (width >= 1500d)
        {
            // 4 columns, account for spacing between cards.
            return Math.Max(260d, Math.Floor((width - (3d * gap)) / 4d));
        }

        if (width >= 920d)
        {
            // 2 columns.
            return Math.Max(280d, Math.Floor((width - gap) / 2d));
        }

        // 1 column.
        return Math.Max(260d, Math.Floor(width));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
