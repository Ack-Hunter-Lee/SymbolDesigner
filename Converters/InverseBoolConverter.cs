using System.Globalization;
using System.Windows.Data;

namespace SymbolDesigner.Converters;

/// <summary>bool → !bool 取反转换器。用于 IsEnabled 门控：
/// 锁定图元时禁用属性面板编辑区（绑定 SelectedItem.IsLocked，取反后作为 IsEnabled）。</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : (object)true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : (object)false;
}
