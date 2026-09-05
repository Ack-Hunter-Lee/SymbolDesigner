using System;
using System.Globalization;
using System.Windows.Data;

namespace SymbolDesigner.Converters;

/// <summary>
/// 减法绑定转换器：从传入的 double 数值里减去 ConverterParameter 指定的常量。
/// L 型管道画布里用：Width - 6 → 让竖管段贴右端对齐；Height - 6 → 让竖管段贴顶向下延伸。
/// </summary>
public sealed class SubtractConverter : IValueConverter
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
        double sub = parameter switch
        {
            double d => d,
            float f   => f,
            int i     => i,
            string s  => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0.0,
            _ => 0.0,
        };
        return v - sub;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}