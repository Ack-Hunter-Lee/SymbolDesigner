using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SymbolDesigner.Converters;

/// <summary>hex 字符串 → Brush 的 Binding Converter（用于取色面板"最近使用颜色"行）。
/// 解析失败 → null（依赖 Brush=null 的 BindingFallback，避免硬编码兜底颜色）。</summary>
public class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? ColorUtil.ParseHex(s) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}