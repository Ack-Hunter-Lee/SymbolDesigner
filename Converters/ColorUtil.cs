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