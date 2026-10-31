using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DriveMonitor.Core.Models;

namespace DriveMonitor.App.Converters;

/// <summary>The colour of a <see cref="FileOperation"/>: solid for text, or a light tint for badge backgrounds.</summary>
[ValueConversion(typeof(FileOperation), typeof(Brush))]
public sealed class OperationBrushConverter : IValueConverter
{
    private const byte TintAlpha = 0x1F;

    private static readonly Brush[] Solid = CreateBrushes(tint: false);
    private static readonly Brush[] Tinted = CreateBrushes(tint: true);

    /// <summary>True for the badge background tint, false for the text colour.</summary>
    public bool Tint { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileOperation operation || (int)operation >= Solid.Length)
            return Brushes.Transparent;
        return (Tint ? Tinted : Solid)[(int)operation];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Color ColorOf(FileOperation operation) => operation switch
    {
        FileOperation.Read => Color.FromRgb(0x25, 0x63, 0xEB),
        FileOperation.Write => Color.FromRgb(0xD9, 0x77, 0x06),
        FileOperation.Create => Color.FromRgb(0x16, 0xA3, 0x4A),
        FileOperation.Delete => Color.FromRgb(0xDC, 0x26, 0x26),
        FileOperation.Rename => Color.FromRgb(0x7C, 0x3A, 0xED),
        FileOperation.Open => Color.FromRgb(0x64, 0x74, 0x8B),
        FileOperation.List => Color.FromRgb(0x08, 0x91, 0xB2),
        _ => Color.FromRgb(0x47, 0x55, 0x69), // SetInfo, Flush
    };

    private static Brush[] CreateBrushes(bool tint)
    {
        FileOperation[] operations = Enum.GetValues<FileOperation>();
        var brushes = new Brush[operations.Max(o => (int)o) + 1];
        for (int i = 0; i < brushes.Length; i++)
        {
            Color color = ColorOf((FileOperation)i);
            if (tint)
                color.A = TintAlpha;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            brushes[i] = brush;
        }

        return brushes;
    }
}
