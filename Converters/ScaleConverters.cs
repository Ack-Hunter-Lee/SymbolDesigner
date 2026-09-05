using System;
using System.Globalization;
using System.Windows.Data;

namespace SymbolDesigner.Converters;

/// <summary>缩放反比转换器：输入渲染缩放系数 scale（&gt;0），返回 1/scale。
/// 配合 RenderTransformOrigin=0.5,0.5 使用：让元素以自身中心做反缩放，
/// 使"画布被放大 scale 倍"时该元素视觉尺寸保持恒定（如草稿端点方块恒为 2×2px）。</summary>
public sealed class InverseScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double scale = value is double d ? d : 1.0;
        return scale > 1e-9 ? 1.0 / scale : 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>缩放相除转换器：输入渲染缩放系数 scale（&gt;0），返回 基准值 / scale。
/// 用于预览线的 StrokeThickness：基准线宽(0.5/1.0/1.2)÷scale，
/// 使画布放大 scale 倍后线条在屏幕上保持固定粗细。</summary>
public sealed class DivideByScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double scale = value is double d ? d : 1.0;
        double baseValue = 0.5;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            baseValue = b;
        return scale > 1e-9 ? baseValue / scale : baseValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}