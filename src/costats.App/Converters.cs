using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace costats.App;

/// <summary>
/// Converts progress bar value, maximum, and actual width to the indicator width.
/// </summary>
public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public static ProgressWidthConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 ||
            values[0] is not double value ||
            values[1] is not double maximum ||
            values[2] is not double actualWidth)
        {
            return 0.0;
        }

        if (maximum <= 0 || actualWidth <= 0)
        {
            return 0.0;
        }

        var ratio = Math.Clamp(value / maximum, 0, 1);
        return ratio * actualWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a string to Visibility (Collapsed if null or empty).
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public static StringToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a boolean to Visibility.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static BoolToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var invert = parameter as string == "Invert";
        var isVisible = value is true;
        if (invert) isVisible = !isVisible;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Negates a boolean value.
/// </summary>
public sealed class BoolNegationConverter : IValueConverter
{
    public static BoolNegationConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? false : true;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a hex color string (e.g. "#EF4444") to a SolidColorBrush.
/// Used for threshold-based dynamic coloring in multicc cards.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static HexToBrushConverter Instance { get; } = new();

    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrEmpty(hex))
            return Brushes.Transparent;

        if (Cache.TryGetValue(hex, out var cached))
            return cached;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts an integer index to boolean for RadioButton binding.
/// Returns true if the value equals the ConverterParameter.
/// </summary>
public sealed class IndexToBoolConverter : IValueConverter
{
    public static IndexToBoolConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int index || parameter is not string paramStr)
            return false;

        return int.TryParse(paramStr, out var targetIndex) && index == targetIndex;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string paramStr && int.TryParse(paramStr, out var index))
            return index;

        return Binding.DoNothing;
    }
}
