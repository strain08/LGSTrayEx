using LGSTrayPrimitives;
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LGSTrayUI.Converters;

/// <summary>
/// Converts DataSource enum to colored brush for badge background
/// </summary>
public class DataSourceBadgeConverter : IValueConverter
{
    private static readonly SolidColorBrush NativeBrush = new(Color.FromRgb(0x4A, 0x90, 0xE2)); // Blue
    private static readonly SolidColorBrush GHubBrush = new(Color.FromRgb(0x50, 0xC8, 0x78));   // Green

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DataSource dataSource)
            return NativeBrush;

        return dataSource == DataSource.Native ? NativeBrush : GHubBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
