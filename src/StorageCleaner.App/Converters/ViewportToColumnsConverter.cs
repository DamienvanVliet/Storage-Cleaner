using System.Globalization;
using System.Windows.Data;

namespace StorageCleaner.App.Converters;

public sealed class ViewportToColumnsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width || width <= 0 || double.IsNaN(width))
        {
            return 1;
        }

        if (width >= 1500)
        {
            return 4;
        }

        if (width >= 920)
        {
            return 2;
        }

        return 1;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
