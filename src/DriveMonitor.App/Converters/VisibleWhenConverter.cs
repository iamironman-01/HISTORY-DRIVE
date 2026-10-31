using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DriveMonitor.App.Converters;

/// <summary>
/// Visible when the value is "set": true, a non-empty string, a non-zero number or any other object;
/// collapsed for null, false, "" and 0. ConverterParameter "Invert" reverses the result.
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            null => false,
            bool flag => flag,
            string text => text.Length > 0,
            int number => number != 0,
            long number => number != 0,
            _ => true,
        };
        if (parameter is string option && option.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
