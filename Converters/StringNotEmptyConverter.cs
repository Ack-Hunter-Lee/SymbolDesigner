using System;
using System.Globalization;
using System.Windows.Data;

namespace SymbolDesigner.Converters;

/// <summary>
/// 字符串非空判断转换器。null / 空字符串 → false；非空 → true。
/// 用于 XAML DataTrigger "PathData 非空才显示路径" 这类条件。
/// </summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrEmpty(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}