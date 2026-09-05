using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml;
using SymbolDesigner.Models;

namespace SymbolDesigner.Svg;

/// <summary>SVG 导入结果:解析后的一组画布图元 + viewBox 上下文。
/// MVP 范围:只解析 &lt;rect&gt;/&lt;circle&gt;/&lt;ellipse&gt;/&lt;line&gt;/&lt;polyline&gt;/&lt;polygon&gt;/&lt;path&gt;,
/// 曲线 (C/Q/A/S/T) 通过 PathDataSampler 降采样为折线段。
/// 不支持:渐变/滤镜/图案/文字/&lt;image&gt;/&lt;use&gt; —— 这些留作后续迭代。</summary>
public sealed class SvgParseResult
{
    /// <summary>解析得到的画布图元(已按 viewBox→画布坐标缩放完毕,可直接 Primitives.Add)。</summary>
    public List<PrimitiveItem> Items { get; } = new();
    /// <summary>解析过程中的警告(无法识别的属性/元素等)。调试用,不影响导入结果。</summary>
    public List<string> Warnings { get; } = new();
    /// <summary>SVG 原始 viewBox(null=没指定,使用 element 推断的尺寸)。用于调试/日志。</summary>
    public Rect? OriginalViewBox { get; set; }
}

/// <summary>SVG → 画布图元的解析器。XmlReader 流式解析,内存占用小。
/// 入口:ParseFile(stringPath, targetCanvasSize)。</summary>
public static class SvgParser
{
    /// <summary>解析 SVG 文件并转换为一组画布图元。
    /// targetCanvasSize = 目标画布尺寸(Width, Height),SVG viewBox 内的所有点按等比缩放填进去(保留纵横比)。
    /// 缩放策略:取 viewBox 宽高与画布宽高的最小缩放比,加 letterbox/pillarbox 使 SVG 居中。</summary>
    public static SvgParseResult ParseFile(string path, Size targetCanvasSize)
    {
        var result = new SvgParseResult();
        if (!File.Exists(path))
        {
            result.Warnings.Add($"文件不存在: {path}");
            return result;
        }

        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                IgnoreProcessingInstructions = true,
                DtdProcessing = DtdProcessing.Ignore,  // 防止 XXE 攻击,SVG 不需要 DTD
            });
            ParseDocument(reader, targetCanvasSize, result);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"解析失败: {ex.Message}");
        }
        return result;
    }

    private static void ParseDocument(XmlReader reader, Size canvasSize, SvgParseResult result)
    {
        // 根 <svg> 必须有 viewBox(或 width/height)——否则无法做坐标变换
        Rect? viewBox = null;
        double? explicitWidth = null;
        double? explicitHeight = null;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName != "svg") continue;

            // 读属性
            while (reader.MoveToNextAttribute())
            {
                switch (reader.LocalName)
                {
                    case "viewBox":
                        var parts = reader.Value.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 4
                            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbX)
                            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbY)
                            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbW)
                            && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbH))
                        {
                            viewBox = new Rect(vbX, vbY, vbW, vbH);
                        }
                        break;
                    case "width":
                        explicitWidth = ParseDoubleOrPercent(reader.Value, fallback: null);
                        break;
                    case "height":
                        explicitHeight = ParseDoubleOrPercent(reader.Value, fallback: null);
                        break;
                }
            }
            reader.MoveToElement();
            break;
        }

        // 没 viewBox 时用 width/height 推断
        if (viewBox is null)
        {
            double w = explicitWidth ?? 100;  // SVG 默认 user units
            double h = explicitHeight ?? 100;
            viewBox = new Rect(0, 0, w, h);
        }
        result.OriginalViewBox = viewBox;

        // 计算 SVG→画布 变换矩阵(等比缩放,居中)
        var transform = BuildTransform(viewBox.Value, canvasSize);

        // 递归遍历子元素。XmlReader 的子节点遍历需要先回到 svg 元素再 ReadSubtree()
        if (reader.LocalName == "svg")
        {
            using var subtree = reader.ReadSubtree();
            // 子树根就是 svg 自己,跳过根,直接读子元素
            subtree.Read();  // svg 开标签
            ParseChildren(subtree, transform, result);
        }
    }

    /// <summary>SVG 单位视为画布单位 1:1(用户需求:不缩放、不居中,图元直接按 SVG 坐标入画布)。
/// viewBox 的 (minX, minY) 起点偏移作为整体平移补偿(让 SVG 原点 (0,0) 落在画布 (0,0) 上),
/// 宽高不缩放(用户可自行调整画布尺寸或在画布上手动改元素位置)。
/// 例如:SVG viewBox="0 0 40 30" 中的 rect width=40 height=30 → 画布上就是 40×30 矩形,X/Y 与 SVG 一致。</summary>
    private static SvgTransform BuildTransform(Rect viewBox, Size canvasSize)
    {
        if (viewBox.Width <= 0 || viewBox.Height <= 0)
            return new SvgTransform(1, 0, 0);
        // 仅平移 viewBox 起点,不改尺寸
        return new SvgTransform(1, -viewBox.X, -viewBox.Y);
    }

    private static void ParseChildren(XmlReader reader, SvgTransform transform, SvgParseResult result)
    {
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement) continue;
            if (reader.NodeType != XmlNodeType.Element) continue;

            ParseElement(reader, transform, result);
        }
    }

    private static void ParseElement(XmlReader reader, SvgTransform transform, SvgParseResult result)
    {
        var attrs = ReadAttributes(reader);
        var styleStr = attrs.TryGetValue("style", out var s) ? s : null;
        var stroke = ResolveColor(attrs, styleStr, "stroke", fallback: null);
        var fill = ResolveColor(attrs, styleStr, "fill", fallback: "#000000");
        var strokeWidth = ResolveStrokeWidth(attrs, styleStr);
        // 注:fill 默认黑色("#000000");stroke 默认 none(不画描边)

        switch (reader.LocalName)
        {
            case "rect":
                ParseRect(attrs, transform, stroke, fill, strokeWidth, result);
                break;
            case "circle":
                ParseCircle(attrs, transform, stroke, fill, strokeWidth, result);
                break;
            case "ellipse":
                ParseEllipse(attrs, transform, stroke, fill, strokeWidth, result);
                break;
            case "line":
                ParseLine(attrs, transform, stroke, strokeWidth, result);
                break;
            case "polyline":
                ParsePolyline(attrs, transform, stroke, strokeWidth, isClosed: false, result);
                break;
            case "polygon":
                ParsePolyline(attrs, transform, stroke, strokeWidth, isClosed: true, result);
                break;
            case "path":
                ParsePath(attrs, transform, stroke, fill, strokeWidth, result);
                break;
            case "g":
            {
                // <g> 子树继承 fill/stroke —— 这里用 ReadSubtree 递归
                using var subtree = reader.ReadSubtree();
                subtree.Read(); // g 开标签
                ParseChildren(subtree, transform, result);
                break;
            }
            default:
                result.Warnings.Add($"未支持元素 <{reader.LocalName}>,已跳过");
                break;
        }
    }

    // ─── 单元素解析 ─────────────────────────────────────────────

    private static void ParseRect(
        Dictionary<string, string> a, SvgTransform t,
        string? stroke, string? fill, double? strokeWidth, SvgParseResult r)
    {
        if (!TryDouble(a, "x", out var x)) x = 0;
        if (!TryDouble(a, "y", out var y)) y = 0;
        if (!TryDouble(a, "width", out var w) || w <= 0) return;
        if (!TryDouble(a, "height", out var h) || h <= 0) return;
        double rx = 0, ry = 0;
        TryDouble(a, "rx", out rx);
        TryDouble(a, "ry", out ry);
        if (rx == 0 && ry > 0) rx = ry;
        if (ry == 0 && rx > 0) ry = rx;

        // 背景层过滤:很多设计软件导出的 SVG 带一个铺满画布的白色(或透明)矩形当"纸面",
        // 它不是业务元素,导入后会把画布盖住或造成"只看到白框"的假象 → 直接跳过。
        if (IsLikelyBackgroundRect(fill, w, h, r.OriginalViewBox))
            return;

        // 变换到画布坐标
        var pt = t.Apply(x, y);
        double cw = w * t.Scale;
        double ch = h * t.Scale;

        var item = PrimitiveItem.Create(
            rx > 0 ? L1Primitive.RoundedRectangle : L1Primitive.Rectangle,
            pt.X, pt.Y, cw, ch);
        if (rx > 0) item.CornerRadius = rx * t.Scale;
        ApplyStyle(item, stroke, fill, strokeWidth);
        r.Items.Add(item);
    }

    /// <summary>是否判定为"背景层"矩形(应跳过不导入):
    ///   1) 面积覆盖 viewBox ≥ 85%(铺满全图的大部分矩形 = 纸面/背景)
    ///   2) 且填充为白色 / 接近白色 / 全透明(占位)。
    /// 注:若 SVG 文件自己就想要一个白色满幅矩形作为元素,无法区分——但按当前需求
    /// (只导入元素、不导入画布背景) 这样过滤符合用户预期。</summary>
    private static bool IsLikelyBackgroundRect(string? fill, double w, double h, Rect? viewBox)
    {
        // 覆盖面积判断
        if (viewBox is { Width: > 0 } vb && vb.Height > 0)
        {
            double coverage = (w * h) / (vb.Width * vb.Height);
            if (coverage < 0.85) return false;
        }
        else
        {
            // 无 viewBox 参考时,按"尺寸非常大 + 白/透明"兜底(仍需要颜色条件)
            if (w < 500 || h < 500) return false;
        }

        // 颜色判断:白色 / 接近白色 / 全透明
        if (string.IsNullOrEmpty(fill)) return true;               // fill 为空(默认黑→ 不;这里 fill 只能是解析后值,空=null)
        string c = fill.Trim();
        // #FFFFFFFF / #FFFFFF / #FFF / white 等
        if (c.Length >= 7 && c.StartsWith("#"))
        {
            string hex = c.Substring(1);
            // 支持 8 位 AARRGGBB 或 6 位 RRGGBB
            int alpha = hex.Length >= 8 ? ParseHexByte(hex.Substring(0, 2)) : 255;
            if (alpha == 0) return true;  // 全透明占位
            if (alpha < 30) return true;  // 近乎全透明
            string rgb = hex.Length >= 8 ? hex.Substring(2, 6) : hex.Substring(0, 6);
            if (rgb.Length == 6 && IsNearWhite(rgb)) return true;
        }
        // 命名色 white / snow / ghostwhite 等
        if (IsNamedWhite(c)) return true;
        return false;
    }

    private static bool IsNearWhite(string rgb)
    {
        int r = ParseHexByte(rgb.Substring(0, 2));
        int g = ParseHexByte(rgb.Substring(2, 2));
        int b = ParseHexByte(rgb.Substring(4, 2));
        return r >= 235 && g >= 235 && b >= 235;  // RGB 接近 255
    }

    private static bool IsNamedWhite(string c)
    {
        switch (c.ToLowerInvariant())
        {
            case "white": case "snow": case "ghostwhite": case "whitesmoke":
                return true;
        }
        return false;
    }

    private static int ParseHexByte(string s)
    {
        try
        {
            var b = Convert.ToByte(s, 16);
            return b;
        }
        catch (FormatException)
        {
            return 255;
        }
    }

    private static void ParseCircle(
        Dictionary<string, string> a, SvgTransform t,
        string? stroke, string? fill, double? strokeWidth, SvgParseResult r)
    {
        if (!TryDouble(a, "cx", out var cx)) cx = 0;
        if (!TryDouble(a, "cy", out var cy)) cy = 0;
        if (!TryDouble(a, "r", out var rad) || rad <= 0) return;
        double diameter = rad * 2 * t.Scale;
        var pt = t.Apply(cx - rad, cy - rad);
        var item = PrimitiveItem.Create(L1Primitive.Ellipse, pt.X, pt.Y, diameter, diameter);
        ApplyStyle(item, stroke, fill, strokeWidth);
        r.Items.Add(item);
    }

    private static void ParseEllipse(
        Dictionary<string, string> a, SvgTransform t,
        string? stroke, string? fill, double? strokeWidth, SvgParseResult r)
    {
        if (!TryDouble(a, "cx", out var cx)) cx = 0;
        if (!TryDouble(a, "cy", out var cy)) cy = 0;
        if (!TryDouble(a, "rx", out var rx) || rx <= 0) return;
        if (!TryDouble(a, "ry", out var ry) || ry <= 0) return;
        double cw = rx * 2 * t.Scale;
        double ch = ry * 2 * t.Scale;
        var pt = t.Apply(cx - rx, cy - ry);
        var item = PrimitiveItem.Create(L1Primitive.Ellipse, pt.X, pt.Y, cw, ch);
        ApplyStyle(item, stroke, fill, strokeWidth);
        r.Items.Add(item);
    }

    private static void ParseLine(
        Dictionary<string, string> a, SvgTransform t,
        string? stroke, double? strokeWidth, SvgParseResult r)
    {
        if (!TryDouble(a, "x1", out var x1)) return;
        if (!TryDouble(a, "y1", out var y1)) return;
        if (!TryDouble(a, "x2", out var x2)) return;
        if (!TryDouble(a, "y2", out var y2)) return;
        var p1 = t.Apply(x1, y1);
        var p2 = t.Apply(x2, y2);
        double minX = System.Math.Min(p1.X, p2.X);
        double minY = System.Math.Min(p1.Y, p2.Y);
        double cw = System.Math.Abs(p2.X - p1.X);
        double ch = System.Math.Abs(p2.Y - p1.Y);

        // 用 Polyline(IsClosed=false) 渲染线段(Line 元素支持拖动两端,语义不同;SVG 导入纯静态)
        var item = PrimitiveItem.Create(L1Primitive.Polyline, minX, minY, System.Math.Max(cw, 1), System.Math.Max(ch, 1));
        // pathData 用局部坐标(以 X/Y 为原点)
        double lx1 = p1.X - minX, ly1 = p1.Y - minY;
        double lx2 = p2.X - minX, ly2 = p2.Y - minY;
        item.PathData = $"M {Fmt(lx1)},{Fmt(ly1)} L {Fmt(lx2)},{Fmt(ly2)}";
        item.IsClosed = false;
        ApplyStyle(item, stroke, fill: null, strokeWidth);
        r.Items.Add(item);
    }

    private static void ParsePolyline(
        Dictionary<string, string> a, SvgTransform t,
        string? stroke, double? strokeWidth, bool isClosed, SvgParseResult r)
    {
        if (!a.TryGetValue("points", out var pts) || string.IsNullOrWhiteSpace(pts)) return;
        var pts2d = ParsePoints(pts);
        if (pts2d.Count < 2) return;

        var localPts = new List<(double x, double y)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (sx, sy) in pts2d)
        {
            var p = t.Apply(sx, sy);
            localPts.Add((p.X, p.Y));
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        double cw = System.Math.Max(maxX - minX, 1);
        double ch = System.Math.Max(maxY - minY, 1);

        var item = PrimitiveItem.Create(L1Primitive.Polyline, minX, minY, cw, ch);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < localPts.Count; i++)
        {
            var (x, y) = localPts[i];
            double lx = x - minX, ly = y - minY;
            sb.Append(i == 0 ? "M " : " L ");
            sb.Append(Fmt(lx)).Append(',').Append(Fmt(ly));
        }
        item.PathData = sb.ToString();
        item.IsClosed = isClosed;
        ApplyStyle(item, stroke, fill: isClosed ? "#000000" : null, strokeWidth);
        r.Items.Add(item);
    }

    private static void ParsePath(
        Dictionary<string, string> a, SvgTransform t,
        string? stroke, string? fill, double? strokeWidth, SvgParseResult r)
    {
        if (!a.TryGetValue("d", out var d) || string.IsNullOrWhiteSpace(d)) return;
        var (localPts, closed, subpaths) = PathDataSampler.Sample(d, t);
        if (localPts.Count < 2) return;

        // 取所有点的包围盒(全局)
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in localPts)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        double cw = System.Math.Max(maxX - minX, 1);
        double ch = System.Math.Max(maxY - minY, 1);

        // 单子路径:常规 Polyline。多子路径(>1):用 BooleanPath(多环骨架)兜底
        if (subpaths.Count <= 1)
        {
            var item = PrimitiveItem.Create(L1Primitive.Polyline, minX, minY, cw, ch);
            var sb = new System.Text.StringBuilder();
            int idx = 0;
            foreach (var sub in subpaths)
            {
                for (int i = 0; i < sub.Count; i++)
                {
                    var p = sub[i];
                    sb.Append(i == 0 ? "M " : " L ");
                    sb.Append(Fmt(p.X - minX)).Append(',').Append(Fmt(p.Y - minY));
                }
                if (closed && idx == subpaths.Count - 1) sb.Append('Z');
                idx++;
            }
            item.PathData = sb.ToString();
            item.IsClosed = closed;
            ApplyStyle(item, stroke, fill, strokeWidth);
            r.Items.Add(item);
        }
        else
        {
            // 多环 → BooleanPath(多环骨架)
            var item = PrimitiveItem.Create(L1Primitive.BooleanPath, minX, minY, cw, ch);
            var sb = new System.Text.StringBuilder();
            for (int s = 0; s < subpaths.Count; s++)
            {
                var sub = subpaths[s];
                for (int i = 0; i < sub.Count; i++)
                {
                    var p = sub[i];
                    sb.Append(i == 0 ? "M " : " L ");
                    sb.Append(Fmt(p.X - minX)).Append(',').Append(Fmt(p.Y - minY));
                }
                if (closed) sb.Append('Z');
                if (s < subpaths.Count - 1) sb.Append(' ');
            }
            item.PathData = sb.ToString();
            ApplyStyle(item, stroke, fill, strokeWidth);
            r.Items.Add(item);
        }
    }

    // ─── 属性解析工具 ─────────────────────────────────────────────

    private static Dictionary<string, string> ReadAttributes(XmlReader reader)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
                dict[reader.LocalName] = reader.Value;
            reader.MoveToElement();
        }
        return dict;
    }

    private static bool TryDouble(Dictionary<string, string> a, string key, out double value)
    {
        value = 0;
        if (!a.TryGetValue(key, out var s)) return false;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static double? ParseDoubleOrPercent(string s, double? fallback)
    {
        s = s.Trim();
        if (s.EndsWith("%")) return fallback;  // MVP 不支持百分比宽高
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>解析 SVG 颜色:hex / rgb(...) / named。null/none/transparent → null。
    /// "currentColor" 不解析(留给上层),fallback 不为 null 时找不到 → 返回 fallback。</summary>
    private static string? ResolveColor(Dictionary<string, string> attrs, string? style, string attr, string? fallback)
    {
        // 先查属性,再查 style 字符串(后者覆盖前者)
        string? raw = null;
        if (attrs.TryGetValue(attr, out var a1)) raw = a1;
        if (style != null)
        {
            foreach (var part in style.Split(';'))
            {
                var eq = part.IndexOf(':');
                if (eq < 0) continue;
                var key = part.Substring(0, eq).Trim();
                if (string.Equals(key, attr, StringComparison.OrdinalIgnoreCase))
                {
                    raw = part.Substring(eq + 1).Trim();
                    break;
                }
            }
        }
        if (raw is null) return fallback;
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return null;
        return NormalizeColor(raw) ?? fallback;
    }

    private static string? NormalizeColor(string raw)
    {
        raw = raw.Trim();
        // 已经是 # 开头直接返回
        if (raw.StartsWith("#")) return raw;
        // rgb(r, g, b) / rgba(r, g, b, a)
        if (raw.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            int open = raw.IndexOf('(');
            int close = raw.IndexOf(')');
            if (open < 0 || close <= open) return null;
            var inner = raw.Substring(open + 1, close - open - 1);
            var parts = inner.Split(',');
            if (parts.Length < 3) return null;
            if (!byte.TryParse(parts[0].Trim(), out var r)) return null;
            if (!byte.TryParse(parts[1].Trim(), out var g)) return null;
            if (!byte.TryParse(parts[2].Trim(), out var b)) return null;
            byte a = 255;
            if (parts.Length >= 4 && byte.TryParse(parts[3].Trim(), out var av)) a = av;
            return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        }
        // 命名色 → 留给 ColorUtil 不友好,这里只返回原值,由 ColorUtil 兜底解析
        return raw;
    }

    private static double? ResolveStrokeWidth(Dictionary<string, string> attrs, string? style)
    {
        string? raw = null;
        if (attrs.TryGetValue("stroke-width", out var s1)) raw = s1;
        if (style != null)
        {
            foreach (var part in style.Split(';'))
            {
                var eq = part.IndexOf(':');
                if (eq < 0) continue;
                var key = part.Substring(0, eq).Trim();
                if (string.Equals(key, "stroke-width", StringComparison.OrdinalIgnoreCase))
                {
                    raw = part.Substring(eq + 1).Trim();
                    break;
                }
            }
        }
        if (raw is null) return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }

    private static List<(double x, double y)> ParsePoints(string pts)
    {
        var result = new List<(double x, double y)>();
        var tokens = pts.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            if (double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                result.Add((x, y));
            }
        }
        return result;
    }

    /// <summary>把颜色 / 描边宽度应用到 PrimitiveItem。
    /// stroke 为 null 表示无描边(SVG 默认 fill=black + stroke=none → 只画黑色实心填充);
    /// stroke != null 但 fill 为 null(SVG 常见 stroke-only)→ 不画填充。</summary>
    private static void ApplyStyle(PrimitiveItem item, string? stroke, string? fill, double? strokeWidth)
    {
        // 描边
        if (!string.IsNullOrEmpty(stroke))
        {
            item.StrokeColor = stroke;
            // 描边宽度:SVG 单位,转换到画布单位 = 原值 × scale(取与画布相同的单位)
            // 当前 PrimitiveItem.StrokeWidth 默认 0.8,SVG 通常给 1,这里按 transform.Scale 缩放
            // (简单起见,Import 后允许用户在属性面板微调)
        }
        // 填充
        if (!string.IsNullOrEmpty(fill))
        {
            item.FillColor = fill;
            item.FillStyle = FillStyle.Solid;
        }
        else
        {
            item.FillStyle = FillStyle.None;
        }
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>SVG→画布坐标变换(等比缩放 + 居中偏移)。</summary>
    public readonly struct SvgTransform
    {
        public readonly double Scale;
        public readonly double OffsetX;
        public readonly double OffsetY;
        public SvgTransform(double scale, double offsetX, double offsetY)
        {
            Scale = scale; OffsetX = offsetX; OffsetY = offsetY;
        }
        public Point Apply(double x, double y) => new Point(x * Scale + OffsetX, y * Scale + OffsetY);
        public Point Apply(Point p) => Apply(p.X, p.Y);
    }
}