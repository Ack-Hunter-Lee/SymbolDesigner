using System;
using System.Globalization;
using System.Windows.Data;

namespace SymbolDesigner.Converters;

/// <summary>
/// 除以 2 转换器（用于 Line 的中点坐标）。宽度 / 高度 / 2 即为线段居中点。
/// </summary>
public sealed class HalveConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value switch
        {
            double d => d,
            float f   => f,
            int i     => i,
            _ => 0.0,
        };
        return v * 0.5;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}