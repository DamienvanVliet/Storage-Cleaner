using System.Globalization;
using System.Windows.Data;
using StorageCleaner.Core.Extensions;

namespace StorageCleaner.App.Converters;

public sealed class BytesToSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            long longValue => longValue.ToSizeString(),
            int intValue => ((long)intValue).ToSizeString(),
            _ => "0 B"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
