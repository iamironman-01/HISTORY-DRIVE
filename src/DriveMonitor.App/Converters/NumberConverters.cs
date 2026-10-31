using System.Globalization;
using System.Windows.Data;
using DriveMonitor.Core;

namespace DriveMonitor.App.Converters;

/// <summary>A byte count as "1.5 MB"; empty for zero so that the figures that matter stand out.</summary>
[ValueConversion(typeof(long), typeof(string))]
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long bytes when bytes != 0 => Formatting.Bytes(bytes),
        int bytes when bytes != 0 => Formatting.Bytes(bytes),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A count with thousands separators; empty for zero.</summary>
[ValueConversion(typeof(long), typeof(string))]
public sealed class CountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long count when count != 0 => count.ToString("N0", CultureInfo.CurrentCulture),
        int count when count != 0 => count.ToString("N0", CultureInfo.CurrentCulture),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
