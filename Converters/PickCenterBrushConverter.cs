using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SymbolDesigner.Converters;

/// <summary>多选拾取标记填充色：圆心用橙色、端点用绿色（Visible 区分圆心 vs 端点）。
/// 表明"被识别为子对象"的命中点类型，让用户直观区分。</summary>
public sealed class PickCenterBrushConverter : IValueConverter
{
    private static readonly Brush CenterBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)); // 橙
    private static readonly Brush EndpointBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // 绿

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool isCenter && isCenter ? CenterBrush : EndpointBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}