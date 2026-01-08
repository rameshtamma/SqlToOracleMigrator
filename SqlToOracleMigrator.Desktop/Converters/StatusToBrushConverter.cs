using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SqlToOracleMigrator.Core;

namespace SqlToOracleMigrator.Desktop.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ConnectionTestStatus s)
        {
            return s switch
            {
                ConnectionTestStatus.Green => Brushes.LimeGreen,
                ConnectionTestStatus.Red => Brushes.IndianRed,
                _ => Brushes.Goldenrod
            };
        }
        return Brushes.Goldenrod;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
