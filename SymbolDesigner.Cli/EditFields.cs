using System.Globalization;
using SymbolDesigner.Models;

namespace SymbolDesigner.Cli;

/// <summary>edit 子命令的字段写入：按 DTO 属性名（camelCase，见 PrimitiveItemDto）把字符串值设入图元。
/// 只允许"外观/几何"白名单字段，禁止改 Id/TypeKey/PathData 等结构字段（避免破坏工程关联）。
/// 非法字段名或无法解析的数字 → ArgError(1)，并注明当前合法字段集。</summary>
internal static class EditFields
{
    /// <summary>可编辑字段 → 对应的 DTO 属性类型。读写一致用反射写值。
    /// 白名单刻意排除 Id/Kind/TypeKey/PathData/BooleanSources/VertexThicknesses 等结构性字段。</summary>
    private static readonly HashSet<string> s_allowed = new(StringComparer.Ordinal)
    {
        // 几何 / 定位
        "x", "y", "width", "height", "cornerRadius", "innerRadius",
        "startAngle", "sweepAngle", "rotation", "zIndex",
        // 外观
        "thickness", "opacity", "isVisible", "isClosed",
        "strokeColor", "strokeWidth", "strokeDashStyle",
        "fillStyle", "fillColor", "gradientStartColor", "gradientEndColor",
        "fillBrightness", "fillOpacity",
        "patternStyle", "patternForeground", "patternBackground",
        "patternForegroundBrightness", "patternForegroundOpacity",
        "patternBackgroundBrightness", "patternBackgroundOpacity",
        "patternTileSize", "patternSpacing",
    };

    public static void Apply(PrimitiveItemDto dto, string field, string value)
    {
        if (!s_allowed.Contains(field))
            throw new ArgError($"字段 {field} 不能编辑；合法字段: {string.Join("/", s_allowed)}");

        var prop = typeof(PrimitiveItemDto).GetProperty(ToPropName(field))
                   ?? throw new ArgError($"字段 {field} 无对应 DTO 属性");

        try
        {
            object converted = ConvertValue(prop.PropertyType, value);
            prop.SetValue(dto, converted);
        }
        catch (ArgumentException e)
        {
            throw new ArgError($"字段 {field} 值非法: {value}（{e.Message}）");
        }
        catch (FormatException e)
        {
            throw new ArgError($"字段 {field} 期望 {TypeLabel(prop.PropertyType)}，得到值: {value}（{e.Message}）");
        }
    }

    private static object ConvertValue(Type type, string value)
    {
        if (type == typeof(string)) return value;
        if (type == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(bool)) return bool.Parse(value);
        throw new FormatException($"不支持的类型 {type.Name}");
    }

    /// <summary>DTO 属性名转换为 JSON 字段名习惯（首字母小写）。字段白名单即用小写形式。</summary>
    private static string ToPropName(string field)
        => field.Length == 0 ? field : char.ToUpperInvariant(field[0]) + field[1..];

    private static string TypeLabel(Type type)
        => type == typeof(bool) ? "bool(true/false)" : type.Name;
}