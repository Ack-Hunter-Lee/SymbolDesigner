using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

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

/// <summary>缩放反比虚线数组转换器：输入渲染缩放系数 scale（&gt;0），
/// 把参数("1.6,1.2" 逗号分隔)的虚线长度全部除以 scale。
/// 用于选中装饰虚线：画布缩放后虚线段仍保持屏幕恒定长度（否则缩小时虚线段缩成小点、放大时拉成超长段）。</summary>
public sealed class DivideDashByScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double scale = value is double d ? d : 1.0;
        if (scale <= 1e-9) scale = 1.0;
        var result = new System.Windows.Media.DoubleCollection();
        if (parameter is string s)
        {
            foreach (var part in s.Split(','))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    result.Add(v / scale);
            }
        }
        return result.Count > 0 ? result : new System.Windows.Media.DoubleCollection { 1.0 / scale };
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