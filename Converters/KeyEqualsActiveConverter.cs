using System;
using System.Globalization;
using System.Windows.Data;

namespace SymbolDesigner.Converters;

/// <summary>
/// 判断当前工具栏 item 是否处于激活态：values[0] = 当前 item 的 TypeKey，
/// values[1] = VM 的 ActiveToolKey。两者相等 → true → item 高亮。
/// 用 MultiBinding 逐个 item 比较，避免"所有 item 复用同一份固定值 DataTrigger 导致一排全亮"。
/// </summary>
public class KeyEqualsActiveConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        return string.Equals(values[0] as string, values[1] as string, StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}