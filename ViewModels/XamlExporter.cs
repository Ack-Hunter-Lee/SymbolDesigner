using System.Globalization;
using System.Text;
using SymbolDesigner.Converters;
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

        var ctx = new ExportContext();

        // 先遍历所有图元，让 ctx 把所有 Pattern 配置的资源 XAML 都收齐（BuildFillExpression 会按需填充）
        foreach (var p in items)
        {
            // 调用 ExportOne 会触发 BuildFillExpression，Pattern 分支会写入 ctx.PatternResources
            _ = ExportOne(p, minX, minY, ctx);
        }

        var sb = new StringBuilder();
        sb.AppendLine("<Canvas xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
        sb.AppendLine("        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
        sb.AppendLine($"        Width=\"{Fmt(maxX - minX)}\" Height=\"{Fmt(maxY - minY)}\"");
        sb.AppendLine($"        Background=\"#1a2a3a\">");

        // 写出 Pattern 资源定义（Canvas.Resources）。
        if (ctx.PatternResources.Count > 0)
        {
            sb.AppendLine("    <Canvas.Resources>");
            foreach (var (_, resXaml) in ctx.PatternResources)
            {
                sb.AppendLine("        " + resXaml);
            }
            sb.AppendLine("    </Canvas.Resources>");
        }

        // 再遍历所有图元，写出实际元素（此时 ctx 已经填好，FillExpression 是 {StaticResource ...} 引用）
        foreach (var p in items)
        {
            sb.AppendLine("    " + ExportOne(p, minX, minY, ctx));
        }

        sb.AppendLine("</Canvas>");
        return sb.ToString();
    }

    /// <summary>导出上下文：扫描阶段收集所有 Pattern 资源 Key + Resource XAML，
    /// 在写出阶段统一按 Key 引用。Key 由 PatternBrushFactory.BuildResourceKey 生成。</summary>
    private sealed class ExportContext
    {
        public Dictionary<string, string> PatternResources { get; } = new();
        // 当前图元的 Fill 表达式 / Stroke 表达式 / 线宽。ExportOne 调用前由调用方设置。
        public string FillExpression = "#FFa8c4d8";
        public string StrokeExpression = "#FF5a7a8c";
        public string StrokeWidthExpression = "0.8";
    }

    /// <summary>把单个 PrimitiveItem 导出为 XAML 元素字符串（坐标相对包围盒左上角归一化）。</summary>
    public static string ExportOne(PrimitiveItem p, double originX, double originY)
        => ExportOne(p, originX, originY, new ExportContext());

    private static string ExportOne(PrimitiveItem p, double originX, double originY, ExportContext ctx)
    {
        string left = Fmt(p.X - originX);
        string top = Fmt(p.Y - originY);
        string w = Fmt(p.Width);
        string h = Fmt(p.Height);
        string z = p.ZIndex.ToString(CultureInfo.InvariantCulture);

        // 计算当前图元的 Fill / Stroke / 线宽 表达式（按 FillStyle 分派）
        ctx.FillExpression = BuildFillExpression(p, ctx);
        ctx.StrokeExpression = BuildStrokeExpression(p);
        ctx.StrokeWidthExpression = Fmt(p.StrokeWidth);

        // 旧版 L2 / L1 分类分支已合并——所有图元都按 TypeKey 字符串分支处理（包括 FanBlade 已被整体移除）。
        return p.TypeKey switch
        {
            "Rectangle" or "RoundedRectangle"
                => RectangleElement(left, top, w, h, z, p.CornerRadius, ctx),

            "Ellipse"
                => EllipseElement(left, top, w, h, z, ctx),

            "Line"
                => LineElement(left, top, w, h, z, ctx),

            "Triangle"
                => TriangleElement(left, top, w, h, z, ctx),

            "BandPolyline"
                => BandPolylineElement(p, left, top, z, ctx),

            "Polyline"
                => PolylineElement(p, left, top, z, ctx),

            "BooleanPath"
                => BooleanPathElement(p, left, top, z, ctx),

            _ => Comment($"未知的图元类型: {p.TypeKey}"),
        };
    }

    // ========================== 颜色/画刷表达式生成 ==========================

    /// <summary>按图元 FillStyle 决定 Fill 属性值：
    /// - None         → "Transparent"
    /// - Solid        → 单色（经亮度/透明度合成）
    /// - Gradient     → LinearGradientBrush 45°
    /// - MonochromeGradient → LinearGradientBrush 45°（亮→暗，单色）
    /// - Pattern      → {StaticResource Brush.Pattern.XXX}（资源在 ExportCanvas 阶段统一写出）
    /// - ImageTexture → "Transparent"（暂不实现导出）</summary>
    private static string BuildFillExpression(PrimitiveItem p, ExportContext ctx)
    {
        switch (p.FillStyle)
        {
            case Models.FillStyle.None:
                return "Transparent";

            case Models.FillStyle.Solid:
                {
                    var c = ColorUtil.ApplyBrightnessAndOpacity(p.FillColor, p.FillBrightness, p.FillOpacity);
                    return HexToArgb(c);
                }

            case Models.FillStyle.Gradient:
                return BuildLinearGradient(p.GradientStartColor, p.GradientEndColor, p.FillBrightness, p.FillOpacity, 45);

            case Models.FillStyle.MonochromeGradient:
                {
                    // 单色渐变：结束色用 ColorUtil.DeriveMonochromeEnd（亮度50%/透明60%）自动派生。
                    // 注意：DeriveMonochromeEnd 已把 brightness/opacity 合成进去，所以 BuildLinearGradient 不再二次合成。
                    var startHex = ColorUtil.ApplyBrightnessAndOpacity(p.FillColor, p.FillBrightness, p.FillOpacity);
                    var endHex = ColorUtil.DeriveMonochromeEnd(p.FillColor, p.FillBrightness, p.FillOpacity);
                    return BuildLinearGradientPreApplied(startHex, endHex, 45);
                }

            case Models.FillStyle.Pattern:
                {
                    // 生成稳定 Key，并按需把资源 XAML 加到 ctx.PatternResources（首次见到某 Key 时）
                    string key = PatternBrushFactory.BuildResourceKey(
                        p.PatternStyle,
                        p.PatternForeground, p.PatternForegroundBrightness, p.PatternForegroundOpacity,
                        p.PatternBackground, p.PatternBackgroundBrightness, p.PatternBackgroundOpacity,
                        p.PatternTileSize, p.PatternSpacing);
                    if (!ctx.PatternResources.ContainsKey(key))
                    {
                        string resXaml = PatternBrushFactory.BuildResourceXaml(
                            p.PatternStyle,
                            p.PatternForeground, p.PatternForegroundBrightness, p.PatternForegroundOpacity,
                            p.PatternBackground, p.PatternBackgroundBrightness, p.PatternBackgroundOpacity,
                            p.PatternTileSize, p.PatternSpacing,
                            key);
                        ctx.PatternResources[key] = resXaml;
                    }
                    return $"{{StaticResource {key}}}";
                }

            case Models.FillStyle.ImageTexture:
                return "Transparent"; // TODO: 未来导出图片纹理

            default:
                return "Transparent";
        }
    }

    /// <summary>StrokeExpression（描边色）：图元 StrokeColor 经亮度合成；
    /// 当前 PrimitiveItem 暂未独立 StrokeColor，沿用 #FF5a7a8c 默认 + p.StrokeWidth 控制粗细。</summary>
    private static string BuildStrokeExpression(PrimitiveItem p)
    {
        return "#FF5a7a8c";
    }

    private static string BuildLinearGradient(string startHex, string endHex, double brightness, double opacity, double angle)
    {
        string start = HexToArgb(ColorUtil.ApplyBrightnessAndOpacity(startHex, brightness, opacity));
        string end = HexToArgb(ColorUtil.ApplyBrightnessAndOpacity(endHex, brightness, opacity));
        double rad = angle * Math.PI / 180.0;
        double dx = Math.Cos(rad), dy = Math.Sin(rad);
        double x1 = 0.5 - dx * 0.5, y1 = 0.5 - dy * 0.5;
        double x2 = 0.5 + dx * 0.5, y2 = 0.5 + dy * 0.5;
        return $"<LinearGradientBrush StartPoint=\"{Fmt(x1)},{Fmt(y1)}\" EndPoint=\"{Fmt(x2)},{Fmt(y2)}\">" +
               $"<GradientStop Offset=\"0\" Color=\"{start}\"/>" +
               $"<GradientStop Offset=\"1\" Color=\"{end}\"/>" +
               $"</LinearGradientBrush>";
    }

    /// <summary>渐变两端已经合成过（亮度/透明度已并入），直接写 GradientStop。
    /// 用于 MonochromeGradient：DeriveMonochromeEnd 已内部走完亮度合成，不能二次合成。</summary>
    private static string BuildLinearGradientPreApplied(string startHex, string endHex, double angle)
    {
        string start = HexToArgb(startHex);
        string end = HexToArgb(endHex);
        double rad = angle * Math.PI / 180.0;
        double dx = Math.Cos(rad), dy = Math.Sin(rad);
        double x1 = 0.5 - dx * 0.5, y1 = 0.5 - dy * 0.5;
        double x2 = 0.5 + dx * 0.5, y2 = 0.5 + dy * 0.5;
        return $"<LinearGradientBrush StartPoint=\"{Fmt(x1)},{Fmt(y1)}\" EndPoint=\"{Fmt(x2)},{Fmt(y2)}\">" +
               $"<GradientStop Offset=\"0\" Color=\"{start}\"/>" +
               $"<GradientStop Offset=\"1\" Color=\"{end}\"/>" +
               $"</LinearGradientBrush>";
    }

    private static string HexToArgb(string hex)
    {
        // 兼容 "#RRGGBB" / "#AARRGGBB" / "RRGGBB" / "AARRGGBB"，统一输出 #AARRGGBB
        string h = hex.StartsWith("#") ? hex.Substring(1) : hex;
        if (h.Length == 6)
        {
            byte r = System.Convert.ToByte(h.Substring(0, 2), 16);
            byte g = System.Convert.ToByte(h.Substring(2, 2), 16);
            byte b = System.Convert.ToByte(h.Substring(4, 2), 16);
            return $"#FF{r:X2}{g:X2}{b:X2}";
        }
        if (h.Length == 8)
        {
            return "#" + h.ToUpperInvariant();
        }
        return "#FF000000";
    }

    /// <summary>布尔运算结果（BooleanPath）导出：与闭合折线同管道——导出 OutlineGeometry
    /// （多环骨架 + 逐顶点圆角已重建为真圆弧 A 段）序列化的 Path 数据，含填充规则，粘贴即用。</summary>
    private static string BooleanPathElement(PrimitiveItem p, string left, string top, string z,
        ExportContext ctx)
    {
        // OutlineGeometry 为 null 时（PathData 空）退化为注释
        var geom = p.OutlineGeometry;
        if (geom is null) return Comment($"BooleanPath PathData 为空，无法导出");
        string dataStr = geom.ToString(CultureInfo.InvariantCulture);
        dataStr = System.Security.SecurityElement.Escape(dataStr);

        return $"<Path Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
               $" Data=\"{dataStr}\"" +
               $" Panel.ZIndex=\"{z}\"" +
               $" Stroke=\"{ctx.StrokeExpression}\" StrokeThickness=\"{ctx.StrokeWidthExpression}\" StrokeLineJoin=\"Miter\"" +
               $" Fill=\"{ctx.FillExpression}\" Stretch=\"None\" />";
    }

    /// <summary>
    /// 带带宽的折线（支线渲染产物）：导出为 Path + Data（Path mini-language 字符串）。
    /// 用 p.OutlineGeometry 直接序列化（已经按 VertexBandWidths 算好轮廓）。
    /// 局部坐标（不含 X/Y 偏移），Canvas.Left/Top 负责在 Canvas 上定位。
    /// 粘贴到其他项目时只需 Path + Data + Fill/Stroke，无任何编辑器依赖。</summary>
    private static string BandPolylineElement(PrimitiveItem p, string left, string top, string z,
        ExportContext ctx)
    {
        // OutlineGeometry 为 null 时（PathData 空）退化为注释
        var geom = p.OutlineGeometry;
        if (geom is null) return Comment($"BandPolyline PathData 为空，无法导出");

        // StreamGeometry.ToString(IFormatProvider) 输出 Path mini-language 字符串（"M0,0L10,5Z"）
        // 用 InvariantCulture 避免小数点变逗号
        string dataStr = geom.ToString(CultureInfo.InvariantCulture);
        // XML 属性值需要转义（仅 " 和 <；mini-language 不含这些字符，但保险起见）
        dataStr = System.Security.SecurityElement.Escape(dataStr);

        return $"<Path Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
               $" Data=\"{dataStr}\"" +
               $" Panel.ZIndex=\"{z}\"" +
               $" Stroke=\"{ctx.StrokeExpression}\" StrokeThickness=\"{ctx.StrokeWidthExpression}\" StrokeLineJoin=\"Miter\"" +
               $" Fill=\"{ctx.FillExpression}\" Stretch=\"None\" />";
    }

    /// <summary>纯折线（Polyline）导出：
    /// - 闭合形态（IsClosed）：与 BandPolyline 同管道导出闭合填充 Path（OutlineGeometry 已含闭合轮廓）；
    /// - 开放形态：仅骨架 Path（Stroke-only，无填充），保持开放折线观感。</summary>
    private static string PolylineElement(PrimitiveItem p, string left, string top, string z,
        ExportContext ctx)
    {
        if (p.IsClosed)
        {
            // 闭合：复用完整轮廓导出（含闭合轮廓/填充），与 BandPolyline 一致
            return BandPolylineElement(p, left, top, z, ctx);
        }

        // 开放折线：骨架几何（PolylineGeometry）序列化为 open Path mini-language，仅描边无填充
        var geom = p.PolylineGeometry;
        if (geom is null) return Comment($"Polyline PathData 为空，无法导出");
        string dataStr = geom.ToString(CultureInfo.InvariantCulture);
        dataStr = System.Security.SecurityElement.Escape(dataStr);

        return $"<Path Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
               $" Data=\"{dataStr}\"" +
               $" Panel.ZIndex=\"{z}\"" +
               $" Stroke=\"{ctx.StrokeExpression}\" StrokeThickness=\"{ctx.StrokeWidthExpression}\"" +
               $" StrokeStartLineCap=\"Round\" StrokeEndLineCap=\"Round\" StrokeLineJoin=\"Round\"" +
               $" Stretch=\"None\" />";
    }

    // ---------- 各类型元素的 XML 生成 ----------

    private static string RectangleElement(string left, string top, string w, string h, string z,
        double cornerRadius, ExportContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append($"<Rectangle Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"");
        sb.Append($" Width=\"{w}\" Height=\"{h}\" Panel.ZIndex=\"{z}\"");
        sb.Append($" Fill=\"{ctx.FillExpression}\" Stroke=\"{ctx.StrokeExpression}\" StrokeThickness=\"{ctx.StrokeWidthExpression}\"");

        if (cornerRadius > 0.0001)
        {
            string r = Fmt(cornerRadius);
            sb.Append($" RadiusX=\"{r}\" RadiusY=\"{r}\"");
        }

        sb.Append(" />");
        return sb.ToString();
    }

    private static string EllipseElement(string left, string top, string w, string h, string z,
        ExportContext ctx)
        => $"<Ellipse Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
           $" Width=\"{w}\" Height=\"{h}\" Panel.ZIndex=\"{z}\"" +
           $" Fill=\"{ctx.FillExpression}\" Stroke=\"{ctx.StrokeExpression}\" StrokeThickness=\"{ctx.StrokeWidthExpression}\" />";

    private static string LineElement(string left, string top, string w, string h, string z,
        ExportContext ctx)
        => $"<Line Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
           $" X1=\"0\" Y1=\"{h}\" X2=\"{w}\" Y2=\"0\"" +
           $" Panel.ZIndex=\"{z}\"" +
           $" Stroke=\"{ctx.StrokeExpression}\" StrokeThickness=\"{ctx.StrokeWidthExpression}\" />";

    /// <summary>
    /// 三角形：用 Polygon + Stretch="Uniform" 让一个绝对坐标多边形适配 w×h 包围盒。
    /// 原模板用的是 Points="0.5,0 1,1 0,1" + Stretch="Fill"，按比例缩放到 w×h。
    /// </summary>
    private static string TriangleElement(string left, string top, string w, string h, string z,
        ExportContext ctx)
        => $"<Polygon Canvas.Left=\"{left}\" Canvas.Top=\"{top}\"" +
           $" Points=\"0.5,0 1,1 0,1\" Stretch=\"Fill\"" +
           $" Width=\"{w}\" Height=\"{h}\" Panel.ZIndex=\"{z}\"" +
           $" Fill=\"{ctx.FillExpression}\" Stroke=\"{ctx.StrokeExpression}\" StrokeThickness=\"{ctx.StrokeWidthExpression}\" />";

    /// <summary>FanBladeElement 已随 FanBladeView 移除而删除（PrimitiveKind 概念废弃 + L2 路径分支消失）。</summary>

    private static string Comment(string text)
        => $"<!-- {System.Security.SecurityElement.Escape(text)} -->";

    private static string Fmt(double v)
        => v.ToString("0.##", CultureInfo.InvariantCulture);
}