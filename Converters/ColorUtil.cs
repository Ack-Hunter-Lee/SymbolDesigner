using System.Windows.Media;

namespace SymbolDesigner.Converters;

/// <summary>颜色字符串 → Brush 的解析工具（PrimitiveItem 外观属性绑定渲染用）。
/// 输入是用户运行时选色数据（hex 色值），非 UI 硬编码；解析失败返回 null，由调用方兜底。
/// 缓存 + Freeze：Brush 创建后冻结，避免绑定反复调用 getter 时频繁分配画刷对象。</summary>
public static class ColorUtil
{
    private static readonly Dictionary<string, Brush> _solidCache = new();
    private static readonly Dictionary<string, Brush> _gradientCache = new();

    /// <summary>解析 hex 颜色字符串（"#RRGGBB"/"#AARRGGBB"/"#RGB"）为 SolidColorBrush。失败 → null。</summary>
    public static Brush? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        string key = hex.Trim();
        if (_solidCache.TryGetValue(key, out var cached)) return cached;

        Color color;
        try
        {
            // 支持 #RGB 快捷形式；其余交给 ColorConverter（可解析 #RRGGBB / #AARRGGBB）
            string normalized = key.Length == 4 && key[0] == '#'
                ? "#" + string.Concat(key[1], key[1], key[2], key[2], key[3], key[3])
                : key;
            color = (Color)ColorConverter.ConvertFromString(normalized);
        }
        catch (FormatException)
        {
            return null;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _solidCache[key] = brush;
        return brush;
    }

    /// <summary>把 hex 颜色按指定"亮度百分比"和"不透明度百分比"复合输出新 hex。
    /// brightness=100 → RGB 不变；brightness=0 → RGB 全 0（保留 alpha）。
    /// opacity=100 → alpha 不变；opacity=0 → alpha=0（完全透明）。
    /// 失败 → 返回 "#00000000"。返回结果走 ParseHex 缓存路径（Key 唯一→同色不重建画刷）。</summary>
    public static string ApplyBrightnessAndOpacity(string? hex, double brightnessPercent, double opacityPercent)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "#00000000";
        try
        {
            string normalized = NormalizeHex(hex.Trim());
            var c = (Color)ColorConverter.ConvertFromString(normalized);
            double b = System.Math.Clamp(brightnessPercent / 100.0, 0, 1);
            byte r = (byte)System.Math.Round(c.R * b);
            byte g = (byte)System.Math.Round(c.G * b);
            byte bb = (byte)System.Math.Round(c.B * b);
            double o = System.Math.Clamp(opacityPercent / 100.0, 0, 1);
            byte a = (byte)System.Math.Round(c.A * o);
            return $"#{a:X2}{r:X2}{g:X2}{bb:X2}";
        }
        catch (FormatException)
        {
            return "#00000000";
        }
    }

    /// <summary>单色渐变终止色：在原色基础上把 RGB 亮度降到 50%、alpha 降到 60%（更深更透）。
    /// 用户只选一个色（FillColor），结束色自动派生，避免"渐变两色选什么"的认知负担。
    /// 入参 brightnessPercent/opacityPercent 用于把"当前 FillColor 经亮度/透明度合成后的色"再派生——
    /// 保证单色渐变与亮度/透明度 Slider 联动。</summary>
    public static string DeriveMonochromeEnd(string? startHex, double brightnessPercent, double opacityPercent)
    {
        string applied = ApplyBrightnessAndOpacity(startHex, brightnessPercent, opacityPercent);
        try
        {
            string normalized = NormalizeHex(applied.Trim());
            var c = (Color)ColorConverter.ConvertFromString(normalized);
            byte r = (byte)System.Math.Round(c.R * 0.5);
            byte g = (byte)System.Math.Round(c.G * 0.5);
            byte bb = (byte)System.Math.Round(c.B * 0.5);
            byte a = (byte)System.Math.Round(c.A * 0.6);
            return $"#{a:X2}{r:X2}{g:X2}{bb:X2}";
        }
        catch (FormatException)
        {
            return applied;
        }
    }

    /// <summary>把 "#RGB" 标准化为 "#AARRGGBB"（无 alpha 时填 0xFF）。
    /// 内部 ColorConverter 不接受 3 位简写,这里统一补足。</summary>
    private static string NormalizeHex(string hex)
    {
        if (hex.Length == 4 && hex[0] == '#')
            return "#FF" + string.Concat(hex[1], hex[1], hex[2], hex[2], hex[3], hex[3]);
        return hex;
    }

    /// <summary>构建两色线性渐变画刷（GradientStart → GradientEnd，垂直方向）。任一端解析失败 → null。</summary>
    public static Brush? BuildLinearGradient(string? startHex, string? endHex)
    {
        if (ParseHex(startHex) is not SolidColorBrush start) return null;
        if (ParseHex(endHex) is not SolidColorBrush end) return null;

        string key = startHex + "|" + endHex;
        if (_gradientCache.TryGetValue(key, out var cached)) return cached;

        var g = new LinearGradientBrush(start.Color, end.Color,
            new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
        g.Freeze();
        _gradientCache[key] = g;
        return g;
    }
}