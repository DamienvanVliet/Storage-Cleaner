using System.Globalization;
using System.Windows.Data;

namespace StorageCleaner.App.Converters;

public sealed class PercentToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double doubleValue)
        {
            return $"{doubleValue:0.##}%";
        }

        if (value is float floatValue)
        {
            return $"{floatValue:0.##}%";
        }

        return "0%";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
