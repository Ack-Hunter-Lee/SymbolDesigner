using System;
using System.Globalization;
using System.Windows.Data;

namespace SymbolDesigner.Converters;

/// <summary>
/// 枚举相等比较（用于 RadioButton.IsChecked 双向绑定）：
///   value         = 当前枚举值（如 SelectedItem.FillStyle）
///   parameter     = 该 RadioButton 代表的枚举值（XAML ConverterParameter="{x:Static models:FillStyle.Solid}"）
/// Convert  返回 bool（true = 当前值 == 该 RadioButton, 应当选中）
/// ConvertBack  返回 object（true 时返回 parameter → 写入 SelectedItem.FillStyle;false 返回 DoNothing 保留原值）
///
/// 用于 RadioButton 列表中"当前选项 = 当前枚举值"判定,实现单选切换:用户点别的 RadioButton,
/// 该 RadioButton 的 IsChecked=true → ConvertBack 返回自身 parameter → 写入 SelectedItem.FillStyle,
/// 其它 RadioButton 因 Convert 返回 false 自动取消选中。
/// </summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return value.Equals(parameter);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // RadioButton 触发 IsChecked=true → 写回 parameter（该 RadioButton 代表的枚举值）
        if (value is bool b && b && parameter is not null)
            return parameter;
        // false 不写回 → 保留当前值,避免 RadioButton 取消选中所有
        return Binding.DoNothing;
    }
}