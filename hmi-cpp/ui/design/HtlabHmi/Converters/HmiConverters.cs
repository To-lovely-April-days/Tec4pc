using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace HtlabHmi.Converters;

public class ResourceKeyToGeometryConverter : IValueConverter
{
    public static readonly ResourceKeyToGeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current != null &&
            Application.Current.TryGetResource(key, Application.Current.ActualThemeVariant, out var res))
        {
            return res as Geometry;
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToBrushConverter : IValueConverter
{
    public IBrush TrueBrush { get; set; } = Brushes.White;
    public IBrush FalseBrush { get; set; } = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueBrush : FalseBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class EqualsStringConverter : IValueConverter
{
    public static readonly EqualsStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class PercentToWidthConverter : IValueConverter
{
    public static readonly PercentToWidthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double pct = value is double d ? d : 0;
        double maxW = parameter is string s && double.TryParse(s, out var w) ? w : 100;
        return Math.Clamp(pct / 100.0, 0, 1) * maxW;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToKnobMarginConverter : IValueConverter
{
    public static readonly BoolToKnobMarginConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new Thickness(25, 3, 0, 0) : new Thickness(3, 3, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToFontWeightConverter : IValueConverter
{
    public static readonly BoolToFontWeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.SemiBold : FontWeight.Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class CommStatusToBrushConverter : IValueConverter
{
    public static readonly CommStatusToBrushConverter DotInstance = new() { IsDot = true };
    public static readonly CommStatusToBrushConverter TextInstance = new() { IsDot = false };

    public bool IsDot { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string s = value?.ToString() ?? "";
        if (IsDot)
        {
            return s switch
            {
                "正常" => SolidColorBrush.Parse("#00B368"),
                "无应答" => SolidColorBrush.Parse("#E5484D"),
                _ => SolidColorBrush.Parse("#B8C4D0")
            };
        }
        return s switch
        {
            "无应答" => SolidColorBrush.Parse("#E5484D"),
            "未启用" => SolidColorBrush.Parse("#8792A0"),
            _ => SolidColorBrush.Parse("#4F5B67")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class EqualsStringToBrushConverter : IValueConverter
{
    public IBrush TrueBrush { get; set; } = SolidColorBrush.Parse("#00559C");
    public IBrush FalseBrush { get; set; } = SolidColorBrush.Parse("#A9B7C6");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() ? TrueBrush : FalseBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class EqualsStringToFontWeightConverter : IValueConverter
{
    public static readonly EqualsStringToFontWeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() ? FontWeight.SemiBold : FontWeight.Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
