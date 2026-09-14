using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SymbolDesigner.Converters;

/// <summary>
/// 把 "bg,border,fg" 形式的字符串拆成 Brush[3]，索引 0=背景、1=边框、2=前景。
/// 给顶部工具栏图标按钮用：Tag="bg,border,fg"，Style 里通过 Self binding + ConverterParameter 取第 N 个 brush。
///
/// 支持两种 token 格式（同名 key 不区分大小写）：
///   1. hex：#RRGGBB 或 #AARRGGBB，直接当颜色（保留旧行为，向后兼容）
///   2. Brush key：在 Application.Resources 里按名字查找（如 BgHover, BorderAccent, Brush.Text.Primary）
///      解析顺序：hex 优先 → Application.TryFindResource(key) → 回退默认色。
/// 这样旧的 hex Tag 不需要全部重写；新 Tag 可以直接引用 Theme.xaml 里的 Brush key。
///
/// 索引越界 / 颜色不合法 / 资源未找到 → 返回默认 (#2a3a4a 半透明感)，不抛异常，保证 UI 始终可显示。
/// </summary>
public class TagToBrushesConverter : IValueConverter
{
    /// <summary>按 token（hex 或默认标记）缓存已冻结画刷，避免悬停/重绘时反复 new SolidColorBrush。</summary>
    private readonly Dictionary<string, Brush> _cache = new(StringComparer.Ordinal);
    private const string DefaultBrushKey = "\u0000default";
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // parameter = "0" / "1" / "2"
        int idx = 1;
        if (parameter is string s && int.TryParse(s, out var p)) idx = p;

        if (value is not string tag || string.IsNullOrEmpty(tag))
            return DefaultBrush();

        var parts = tag.Split(',');
        if (idx < 0 || idx >= parts.Length) return DefaultBrush();

        var token = parts[idx].Trim();
        if (token.Length == 0) return DefaultBrush();

        // 1. hex 形式（按 token 缓存，命中直接返回冻结画刷）
        if (token.StartsWith("#", StringComparison.Ordinal))
        {
            if (_cache.TryGetValue(token, out var cached)) return cached;
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(token)!);
                brush.Freeze();
                _cache[token] = brush;
                return brush;
            }
            catch
            {
                return DefaultBrush();
            }
        }

        // 2. Brush key 形式：到 Application.Resources 查找
        //    未命中时回退到默认色（不抛异常）。
        try
        {
            if (Application.Current?.TryFindResource(token) is Brush resourceBrush)
            {
                // 不冻结（资源可能被共享），返回原引用即可
                return resourceBrush;
            }
        }
        catch
        {
            // TryFindResource 内部对无效 key 会返回 null，不会抛——catch 仅兜底
        }

        return DefaultBrush();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private SolidColorBrush DefaultBrush()
    {
        if (_cache.TryGetValue(DefaultBrushKey, out var cached)) return (SolidColorBrush)cached;
        var b = new SolidColorBrush(Color.FromRgb(0x2a, 0x3a, 0x4a));
        b.Freeze();
        _cache[DefaultBrushKey] = b;
        return b;
    }
}