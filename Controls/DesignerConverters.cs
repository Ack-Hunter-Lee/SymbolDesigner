using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SymbolDesigner.Controls;

/// <summary>
/// bool 反转 → Visibility：True → Collapsed、False → Visible。
/// 注：主工程 FactoryKanban.Controls 也保留一份同名转换器（SymbolTemplates.xaml 运行时模板用），
/// 两边各自程序集内使用，XAML 通过 assembly 限定区分。
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>
/// 两个 int 相等 → bool。多值绑定：Index == SelectedBonePointIndex 时返回 true，触发选中态样式。
/// 用于 DataTrigger.Value=True 配合 MultiBinding.Binding/Converter 实现"骨点组标题点击高亮 Slider"。
/// </summary>
public sealed class IntEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        if (values[0] is int a && values[1] is int b) return a == b;
        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}