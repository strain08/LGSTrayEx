using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LGSTrayUI;
/// <summary>
/// Provides a value converter that returns a SolidColorBrush based on the application's theme and an optional
/// parameter. Intended for use in data binding scenarios to select appropriate brush colors for light or dark themes.
/// </summary>
/// <remarks>This converter is typically used in WPF or XAML-based applications to dynamically select brush colors
/// for UI elements depending on whether the application is in light or dark mode. The optional parameter can be set to
/// "Text" to select brushes specifically intended for text foregrounds. The converter does not support two-way binding;
/// calling ConvertBack will throw a NotImplementedException.</remarks>
public class ThemeBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Black = new(Color.FromRgb(0x2e, 0x2e, 0x2e));
    private static readonly SolidColorBrush BlackText = new(Colors.Black);
    private static readonly SolidColorBrush White = new(Color.FromRgb(0xd0, 0xd0, 0xd0));
    private static readonly SolidColorBrush WhiteTest = new(Colors.White);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not bool lightTheme) return Black;
        string? param = parameter as string;

        return lightTheme switch
        {
            true when param == "Text" => BlackText,
            false when param == "Text" => WhiteTest,
            true => White,
            false => Black,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
