using System.Globalization;
using System.Text;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>
/// 把画布上的所有 PrimitiveItem 序列化成可粘贴/可保存的 XAML 片段。
/// 输出格式：一个独立 Canvas + 子元素（Rectangle/Ellipse/Line/Polygon/FanBladeView），
/// 含 Canvas.Left/Top 定位、Panel.ZIndex 遮挡顺序，并继承同一个命名空间映射到 controls 命名空间。
///
/// 设计原则：
/// 1. 输出可直接粘贴到其他项目使用（不依赖编辑器专用的 DraggablePrimitive / ViewModel）
/// 2. 用纯字符串拼接而非 XamlReader 反序列化（避免依赖 System.Xaml 类型映射、画笔资源等）
/// 3. 数值用 InvariantCulture 格式化，避免本地化导致小数点变逗号
/// </summary>
public static class XamlExporter
{
    /// <summary>把整个画布（Primitives）导出为 XAML 字符串。
    /// 无限画布：根 Canvas 尺寸 = 全部图元的内容包围盒（maxX-minX, maxY-minY），
    /// 所有子图元坐标归一化平移到 (0,0) 起（Left = X - minX / Top = Y - minY）。</summary>
    public static string ExportCanvas(IEnumerable<PrimitiveItem> primitives)
    {
        // 内容包围盒（含每个图元的宽高延伸）
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        var items = primitives.ToList();
        foreach (var p in items)
        {
            minX = System.Math.Min(minX, p.X);
            minY = System.Math.Min(minY, p.Y);
            maxX = System.Math.Max(maxX, p.X + p.Width);
            maxY = System.Math.Max(maxY, p.Y + p.Height);
        }

        // 空画布：给一个合理的最小包围盒，避免根 Canvas 尺寸为 0
        if (items.Count == 0)
        {
            minX = 0; minY = 0; maxX = 1; maxY = 1;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<Canvas xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
        sb.AppendLine("        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
        sb.AppendLine($"        Width=\"{Fmt(maxX - minX)}\" Height=\"{Fmt(maxY - minY)}\"");
        sb.AppendLine($"        Background=\"#1a2a3a\">");

        foreach (var p in items)
        {
            sb.AppendLine("    " + ExportOne(p, minX, minY));
        }

        sb.AppendLine("</Canvas>");
        return sb.ToString();
    }

    /// <summary>把单个 PrimitiveItem 导出为 XAML 元素字符串（坐标相对包围盒左上角归一化）。</summary>
    public static string ExportOne(PrimitiveItem p, double originX, double originY)
    {
        string left = Fmt(p.X - originX);
        string top = Fmt(p.Y - originY);
        string w = Fmt(p.Width);
        string h = Fmt(p.Height);
        string z = p.ZIndex.ToString(CultureInfo.InvariantCulture);

        // 通用图形颜色（与编辑器画布模板一致）
        const string fill = "#a8c4d8";
        const string stroke = "#5a7a8c";
        const string sw = "0.8";

        // 旧版 L2 / L1 分类分支已合并——所有图元都按 TypeKey 字符串分支处理（包括 FanBlade 已被整体移除）。
        return p.TypeKey switch
        {
            "Rectangle" or "RoundedRectangle"
                => RectangleElement(left, top, w, h, z, p.CornerRadius, fill, stroke, sw),

            "Ellipse"
                => EllipseElement(left, top, w, h, z, fill, stroke, sw),

            "Line"
                => LineElement(left, top, w, h, z, stroke, sw),

            "Triangle"
                => TriangleElement(left, top, w, h, z, fill, stroke, sw),

            "ThickPolyline"
                => ThickPolylineElement(p, left, top, z, fill, stroke),

            "Polyline"
                => PolylineElement(p, left, top, z, fill, stroke),

            "BooleanPath"
                => BooleanPathElement(p, left, top, z, fill, stroke),

            _ => Comment($"未知的图元类型: {p.TypeKey}"),
        };
    }

    /// <summary>布尔运算结果（BooleanPath）导出：与闭合折线同管道——导出 OutlineGeometry
    /// （多环骨架 + 逐顶点圆角已重建为真圆弧 A 段）序列化的 Path 数据，含填充规则，粘贴即用。</summary>
    private static string BooleanPathElement(PrimitiveItem p, string left, string top, string z,
        string fill, string stroke)
    {
        // OutlineGeometry 为 null 时（PathData 空）退化为注释
        var geom = p.OutlineGeometry;
        if (geom is null) return Comment($"BooleanPath PathData 为空，无法导出");
        string dataStr = geom.ToString(CultureInfo.InvariantCulture);
        dataStr = System.Security.SecurityElement.Escape(dataStr);

        return $"<Path Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
               $" Data=\"{dataStr}\"" +
               $" Panel.ZIndex=\"{z}\"" +
               $" Stroke=\"{stroke}\" StrokeThickness=\"0.4\" StrokeLineJoin=\"Miter\"" +
               $" Fill=\"{fill}\" Stretch=\"None\" />";
    }

    /// <summary>
    /// 带厚度的折线（支线渲染产物）：导出为 Path + Data（Path mini-language 字符串）。
    /// 用 p.OutlineGeometry 直接序列化（已经按 VertexThicknesses 算好轮廓）。
    /// 局部坐标（不含 X/Y 偏移），Canvas.Left/Top 负责在 Canvas 上定位。
    /// 粘贴到其他项目时只需 Path + Data + Fill/Stroke，无任何编辑器依赖。</summary>
    private static string ThickPolylineElement(PrimitiveItem p, string left, string top, string z,
        string fill, string stroke)
    {
        // OutlineGeometry 为 null 时（PathData 空）退化为注释
        var geom = p.OutlineGeometry;
        if (geom is null) return Comment($"ThickPolyline PathData 为空，无法导出");

        // StreamGeometry.ToString(IFormatProvider) 输出 Path mini-language 字符串（"M0,0L10,5Z"）
        // 用 InvariantCulture 避免小数点变逗号
        string dataStr = geom.ToString(CultureInfo.InvariantCulture);
        // XML 属性值需要转义（仅 " 和 <；mini-language 不含这些字符，但保险起见）
        dataStr = System.Security.SecurityElement.Escape(dataStr);

        return $"<Path Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
               $" Data=\"{dataStr}\"" +
               $" Panel.ZIndex=\"{z}\"" +
               $" Stroke=\"{stroke}\" StrokeThickness=\"0.4\" StrokeLineJoin=\"Miter\"" +
               $" Fill=\"{fill}\" Stretch=\"None\" />";
    }

    /// <summary>纯折线（Polyline）导出：
    /// - 闭合形态（IsClosed）：与 ThickPolyline 同管道导出闭合填充 Path（OutlineGeometry 已含闭合轮廓）；
    /// - 开放形态：仅骨架 Path（Stroke-only，无填充），保持开放折线观感。</summary>
    private static string PolylineElement(PrimitiveItem p, string left, string top, string z,
        string fill, string stroke)
    {
        if (p.IsClosed)
        {
            // 闭合：复用完整轮廓导出（含闭合轮廓/填充），与 ThickPolyline 一致
            return ThickPolylineElement(p, left, top, z, fill, stroke);
        }

        // 开放折线：骨架几何（PolylineGeometry）序列化为 open Path mini-language，仅描边无填充
        var geom = p.PolylineGeometry;
        if (geom is null) return Comment($"Polyline PathData 为空，无法导出");
        string dataStr = geom.ToString(CultureInfo.InvariantCulture);
        dataStr = System.Security.SecurityElement.Escape(dataStr);

        return $"<Path Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
               $" Data=\"{dataStr}\"" +
               $" Panel.ZIndex=\"{z}\"" +
               $" Stroke=\"{stroke}\" StrokeThickness=\"0.8\"" +
               $" StrokeStartLineCap=\"Round\" StrokeEndLineCap=\"Round\" StrokeLineJoin=\"Round\"" +
               $" Stretch=\"None\" />";
    }

    // ---------- 各类型元素的 XML 生成 ----------

    private static string RectangleElement(string left, string top, string w, string h, string z,
        double cornerRadius, string fill, string stroke, string sw)
    {
        var sb = new StringBuilder();
        sb.Append($"<Rectangle Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"");
        sb.Append($" Width=\"{w}\" Height=\"{h}\" Panel.ZIndex=\"{z}\"");
        sb.Append($" Fill=\"{fill}\" Stroke=\"{stroke}\" StrokeThickness=\"{sw}\"");

        if (cornerRadius > 0.0001)
        {
            string r = Fmt(cornerRadius);
            sb.Append($" RadiusX=\"{r}\" RadiusY=\"{r}\"");
        }

        sb.Append(" />");
        return sb.ToString();
    }

    private static string EllipseElement(string left, string top, string w, string h, string z,
        string fill, string stroke, string sw)
        => $"<Ellipse Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
           $" Width=\"{w}\" Height=\"{h}\" Panel.ZIndex=\"{z}\"" +
           $" Fill=\"{fill}\" Stroke=\"{stroke}\" StrokeThickness=\"{sw}\" />";

    private static string LineElement(string left, string top, string w, string h, string z,
        string stroke, string sw)
        => $"<Line Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
           $" X1=\"0\" Y1=\"{h}\" X2=\"{w}\" Y2=\"0\"" +
           $" Panel.ZIndex=\"{z}\"" +
           $" Stroke=\"{stroke}\" StrokeThickness=\"{sw}\" />";

    /// <summary>
    /// 三角形：用 Polygon + Stretch="Uniform" 让一个绝对坐标多边形适配 w×h 包围盒。
    /// 原模板用的是 Points="0.5,0 1,1 0,1" + Stretch="Fill"，按比例缩放到 w×h。
    /// </summary>
    private static string TriangleElement(string left, string top, string w, string h, string z,
        string fill, string stroke, string sw)
        => $"<Polygon Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
           $" Points=\"0.5,0 1,1 0,1\" Stretch=\"Fill\"" +
           $" Width=\"{w}\" Height=\"{h}\" Panel.ZIndex=\"{z}\"" +
           $" Fill=\"{fill}\" Stroke=\"{stroke}\" StrokeThickness=\"{sw}\" />";

    /// <summary>FanBladeElement 已随 FanBladeView 移除而删除（PrimitiveKind 概念废弃 + L2 路径分支消失）。</summary>

    private static string Comment(string text)
        => $"<!-- {System.Security.SecurityElement.Escape(text)} -->";

    private static string Fmt(double v)
        => v.ToString("0.##", CultureInfo.InvariantCulture);
}