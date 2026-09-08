using System.Windows;
using System.Windows.Media;
using SymbolDesigner.Models;

namespace SymbolDesigner.Converters;

/// <summary>图案画刷工厂（FillStyle=Pattern 时调用）。
/// 4 种内置图案用 DrawingBrush TileMode=Tile 平铺：
///   Diagonal   - 45° 斜线
///   Horizontal - 水平线
///   Vertical   - 竖直线
///   Cross      - 正交网格（水平+竖直）
/// 前景/背景色经 ColorUtil.ApplyBrightnessAndOpacity 合成后再写到笔刷。
/// 笔刷返回前 Freeze（跨线程安全 + 性能）。
/// 不缓存：每次按输入重画（PrimitiveItem.FillBrush getter 已被各 partial 钩子触发重算）。</summary>
public static class PatternBrushFactory
{
    /// <summary>根据图元当前所有 Pattern 字段生成 DrawingBrush。
    /// PatternBackground 完全透明时仅画前景线（不画底），符合"无背景 hatch"语义。</summary>
    public static Brush? Build(
        PatternStyle style,
        string fgHex, double fgBrightness, double fgOpacity,
        string? bgHex, double bgBrightness, double bgOpacity,
        double tileSize, double spacing)
    {
        if (tileSize < 4) tileSize = 4;
        if (tileSize > 40) tileSize = 40;
        if (spacing < 1) spacing = 1;
        if (spacing > 40) spacing = 40;

        var fgColor = ParseColor(ColorUtil.ApplyBrightnessAndOpacity(fgHex, fgBrightness, fgOpacity));
        var bgColor = bgHex is null
            ? Colors.Transparent
            : ParseColor(ColorUtil.ApplyBrightnessAndOpacity(bgHex, bgBrightness, bgOpacity));

        // GeometryDrawing 用 Rect 决定"平铺单元"大小。尺寸 = tileSize × tileSize。
        // 实际线宽相对 tile 单元保持协调（越疏的线相应略粗，避免线条过细）。
        var tileRect = new Rect(0, 0, tileSize, tileSize);
        var geometry = BuildTileGeometry(style, tileSize, spacing);
        // Pen 端点 Flat：保证 LineGeometry 端点处不画半圆 cap 凸出 tile 外（避免"小方框切线"视觉）。
        // 端点恰好落在 tile 边界上，Flat 让线段自然在边界终止，邻 tile 的端点重合接续成连续直线。
        var pen = new Pen(new SolidColorBrush(fgColor), Math.Max(1.0, tileSize / 8.0))
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat,
            LineJoin = PenLineJoin.Miter,
        };
        pen.Freeze();

        var drawing = new GeometryDrawing
        {
            // 背景作为 Brush：仅当 bg 不透明才铺底，否则画底相当于画一块透明矩形（浪费）
            Brush = bgColor.A == 0 ? null : new SolidColorBrush(bgColor),
            Geometry = new RectangleGeometry(tileRect),
            Pen = pen,
        };

        // 如果用户填了 GeometryDrawing.Pen + 背景矩形 + 笔绘几何，这种"外组合" WPF 不允许
        // （GeometryDrawing 同时只能有一个 Geometry/Pen 或一个 Brush/Geometry）。
        // 重新组装：背景为底层 GeometryDrawing，前景线为上层 GeometryDrawing（共享 tileRect）。
        if (drawing.Brush != null)
        {
            var bgDrawing = new GeometryDrawing(
                drawing.Brush,
                null,
                drawing.Geometry);
            bgDrawing.Freeze();

            var fgDrawing = new GeometryDrawing(
                null,
                pen,
                geometry);
            fgDrawing.Freeze();

            var group = new DrawingGroup();
            group.Children.Add(bgDrawing);
            group.Children.Add(fgDrawing);

            var brush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, tileSize, tileSize),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            ApplyDiagonalRotation(brush, style, tileSize);
            brush.Freeze();
            return brush;
        }
        else
        {
            // 无背景：新建一个 fg-only 的 GeometryDrawing。
            // 不能复用上面的 drawing —— 它内部是 RectangleGeometry（背景矩形）+ Pen（前景色），
            // 会画出整个 tile 的矩形描边，相邻矩形接起来看起来就是"网格"，而不是预期图案。
            // 前景只画线（Diagonal/Horizontal/Vertical/Cross），无填充矩形。
            var fgOnly = new GeometryDrawing(null, pen, geometry);
            fgOnly.Freeze();
            var brush = new DrawingBrush(fgOnly)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, tileSize, tileSize),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            ApplyDiagonalRotation(brush, style, tileSize);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Diagonal（斜纹）的正确实现方式：
    /// 不能在正方形 tile 里直接画对角线——四个角必然留空/接缝，平铺后永远对不齐（变成"多个 tile 拼块"）。
    /// 正解（业内公认）：把 Diagonal 画成**横线**（横线平铺天然水平无缝），
    /// 再给整个 DrawingBrush 加 45° 旋转。旋转保持 tile 的平移对称性，
    /// 整片横线被旋转成斜纹后依旧无缝连续。Grid(Cross) 不旋转。</summary>
    private static void ApplyDiagonalRotation(DrawingBrush brush, PatternStyle style, double tileSize)
    {
        if (style != PatternStyle.Diagonal) return;
        var center = tileSize / 2.0;
        var rot = new RotateTransform(45, center, center);
        rot.Freeze();
        brush.Transform = rot;
    }

    /// <summary>单元内的前景几何 —— 只负责画横线/竖线。
    /// 斜纹(Diagonal) 在这里**复用横线**几何：真正的"斜"由 ApplyDiagonalRotation 对整个 DrawingBrush
    /// 做 45° 旋转完成（见该方法的注释）。这样斜纹无需在正方形 tile 里硬画对角线，
    /// 从根本上避免了对角线在 tile 四角留空/接缝、平铺对不齐的经典问题。
    ///
    /// 密度由 spacing 控制：把 tileSize 均匀分成 n = round(tileSize/spacing) 份，
    /// 线均匀居中分布（实际线距 = tileSize/n ≈ spacing，n 大则密、n 小则疏）。
    /// 线端点精确落在 [0,T] 边界 → 在各自轴向平铺天然无缝连续。
    ///   Horizontal / Diagonal(旋转前) - n 根水平线
    ///   Vertical - n 根竖直线
    ///   Cross    - 水平 + 竖直叠加（网格）</summary>
    private static Geometry BuildTileGeometry(PatternStyle style, double tileSize, double spacing)
    {
        // 用线数 n 均匀铺在 [0,T]：n 大则密、n 小则疏，且保证 tile 边界无缝（线与 tile 边界两侧对齐）。
        int n = Math.Max(1, (int)Math.Round(tileSize / spacing));

        bool useHorizontal = style == PatternStyle.Diagonal || style == PatternStyle.Horizontal || style == PatternStyle.Cross;
        bool useVertical = style == PatternStyle.Vertical || style == PatternStyle.Cross;

        var group = new GeometryGroup();
        if (useHorizontal)
            for (int i = 0; i < n; i++)
            {
                double y = tileSize * (i + 0.5) / n;
                group.Children.Add(new LineGeometry(
                    new Point(0, y), new Point(tileSize, y)));
            }
        if (useVertical)
            for (int i = 0; i < n; i++)
            {
                double x = tileSize * (i + 0.5) / n;
                group.Children.Add(new LineGeometry(
                    new Point(x, 0), new Point(x, tileSize)));
            }
        group.Freeze();
        return group;
    }

    /// <summary>hex → Color（解析失败回退到不透明黑，避免画笔空指针）。</summary>
    private static Color ParseColor(string hex)
    {
        var c = ColorUtil.ParseHex(hex);
        return c is SolidColorBrush scb ? scb.Color : Colors.Black;
    }

    // ========================== 导出（XamlExporter 调用） ==========================

    /// <summary>根据 Pattern 配置生成稳定的资源 Key（XamlExporter 用，相同配置复用同一 key）。
    /// Key 形如 Brush.Pattern.{Style}.{fg4}.{bg4}.{hash8}：
    ///   Style - Diagonal / Horizontal / Vertical / Cross（人类可读）
    ///   fg4/bg4 - 前景/背景 hex 的前 4 位（去掉 #，人类可一眼看出主色调）
    ///   hash8 - 配置指纹（亮/透/tile/spacing 全参 → 8 位 hex）保证唯一性
    /// 两个图元 Pattern 配置完全一致 → 共享同一 key → 同一资源。</summary>
    public static string BuildResourceKey(
        PatternStyle style,
        string fgHex, double fgBrightness, double fgOpacity,
        string bgHex, double bgBrightness, double bgOpacity,
        double tileSize, double spacing)
    {
        // 量化亮/透到 0.1 步长，避免浮点漂移造成 hash 不一致
        int qb = (int)Math.Round(fgBrightness * 10);
        int qo = (int)Math.Round(fgOpacity * 10);
        int qBb = (int)Math.Round(bgBrightness * 10);
        int qBo = (int)Math.Round(bgOpacity * 10);
        int qTs = (int)Math.Round(tileSize);
        int qSp = (int)Math.Round(spacing);

        string raw = $"{style}|{fgHex}|{qb}|{qo}|{bgHex}|{qBb}|{qBo}|{qTs}|{qSp}";
        int hash = raw.GetHashCode();

        // 提取 hex 前 4 位（不含 #），便于人类识别："Blue8" = "#FF8...xxx" 的前 4
        string fgTag = ColorTag(fgHex);
        string bgTag = ColorTag(bgHex);

        // XAML key 合法字符：字母/数字/_；不能用 #
        return $"Brush.Pattern.{style}.{fgTag}.{bgTag}.{(uint)hash:X8}";
    }

    private static string ColorTag(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "0000";
        string h = hex.StartsWith("#") ? hex.Substring(1) : hex;
        return h.Length >= 4 ? h.Substring(0, 4).ToUpperInvariant() : "0000";
    }

    /// <summary>把当前 Pattern 配置序列化成完整 DrawingBrush XAML 字符串（含 TileMode/Transform/Viewport），
    /// 直接放进 &lt;Canvas.Resources&gt; 用。颜色用 InvariantCulture（#AARRGGBB），防止本地化漂移。
    /// 资源 Key 由 BuildResourceKey 单独生成并配套使用。</summary>
    public static string BuildResourceXaml(
        PatternStyle style,
        string fgHex, double fgBrightness, double fgOpacity,
        string bgHex, double bgBrightness, double bgOpacity,
        double tileSize, double spacing,
        string resourceKey)
    {
        if (tileSize < 4) tileSize = 4;
        if (tileSize > 40) tileSize = 40;
        if (spacing < 1) spacing = 1;
        if (spacing > 40) spacing = 40;

        var fgColor = ParseColor(ColorUtil.ApplyBrightnessAndOpacity(fgHex, fgBrightness, fgOpacity));
        var bgColor = bgHex is null
            ? Colors.Transparent
            : ParseColor(ColorUtil.ApplyBrightnessAndOpacity(bgHex, bgBrightness, bgOpacity));

        // 线宽 = tileSize/8，与 Build() 里的 Pen 保持一致（视觉一致）
        double penWidth = Math.Max(1.0, tileSize / 8.0);
        string fgArgb = ColorToArgb(fgColor);
        string bgArgb = ColorToArgb(bgColor);

        var sb = new System.Text.StringBuilder();
        sb.Append($"<DrawingBrush x:Key=\"{resourceKey}\"");
        sb.Append($" TileMode=\"Tile\"");
        sb.Append($" ViewportUnits=\"Absolute\"");
        sb.Append($" Stretch=\"None\"");
        sb.Append($" AlignmentX=\"Left\" AlignmentY=\"Top\"");
        sb.Append($" Viewport=\"0,0,{Fmt(tileSize)},{Fmt(tileSize)}\">");

        // Diagonal 时挂 45° 旋转，与 Build() 行为一致
        if (style == PatternStyle.Diagonal)
        {
            double center = tileSize / 2.0;
            sb.Append($"<DrawingBrush.Transform><RotateTransform Angle=\"45\" CenterX=\"{Fmt(center)}\" CenterY=\"{Fmt(center)}\"/></DrawingBrush.Transform>");
        }

        sb.Append("<DrawingBrush.Drawing>");
        sb.Append("<DrawingGroup>");

        // 背景：仅在 bg 不透明时铺底矩形
        if (bgColor.A > 0)
        {
            sb.Append($"<GeometryDrawing Brush=\"{bgArgb}\" Geometry=\"M0,0H{Fmt(tileSize)}V{Fmt(tileSize)}H0Z\"/>");
        }

        // 前景几何：直接用 BuildTileGeometryMiniLanguage 生成 Path mini-language 字符串。
        // 注意：不能对 BuildTileGeometry 生成的 GeometryGroup 调 ToString()——GeometryGroup 的
        // ToString() 只返回类型名 "System.Windows.Media.GeometryGroup"，不是可序列化的路径数据。
        string geomStr = BuildTileGeometryMiniLanguage(style, tileSize, spacing);
        // Pen 用显式子元素（GeometryDrawing.Pen 是 Pen 对象，字符串属性转换不可靠）；
        // Flat cap 保证端点正好落在 tile 边界，平铺接缝不凸出（与运行时 Build() 的 Pen 一致）。
        sb.Append($"<GeometryDrawing Geometry=\"{geomStr}\">");
        sb.Append($"<GeometryDrawing.Pen><Pen Brush=\"{fgArgb}\" Thickness=\"{Fmt(penWidth)}\" StartLineCap=\"Flat\" EndLineCap=\"Flat\"/></GeometryDrawing.Pen>");
        sb.Append("</GeometryDrawing>");

        sb.Append("</DrawingGroup>");
        sb.Append("</DrawingBrush.Drawing>");
        sb.Append("</DrawingBrush>");
        return sb.ToString();
    }

    private static string ColorToArgb(Color c)
    {
        // 输出 #AARRGGBB（WPF 颜色格式）
        return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    /// <summary>生成前景线的 Path mini-language 字符串（与 BuildTileGeometry 的线段分布完全一致）。
    /// 输出形如 "M0,5L22.27,5 M0,11.13L22.27,11.13 ..."（GeometryDrawing.Geometry 直接可用）。
    ///   Diagonal/Horizontal - n 条水平线，y = T*(i+0.5)/n
    ///   Vertical            - n 条竖直线，x = T*(i+0.5)/n
    ///   Cross               - 水平 + 竖直叠加
    /// 注意：必须独立生成，不能靠 GeometryGroup.ToString()（那只返回类型名）。</summary>
    private static string BuildTileGeometryMiniLanguage(PatternStyle style, double tileSize, double spacing)
    {
        int n = Math.Max(1, (int)Math.Round(tileSize / spacing));
        var sb = new System.Text.StringBuilder();
        string t = Fmt(tileSize);

        bool useHorizontal = style == PatternStyle.Diagonal || style == PatternStyle.Horizontal || style == PatternStyle.Cross;
        bool useVertical = style == PatternStyle.Vertical || style == PatternStyle.Cross;

        if (useHorizontal)
            for (int i = 0; i < n; i++)
            {
                double y = tileSize * (i + 0.5) / n;
                string ys = y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                sb.Append("M0,").Append(ys).Append("L").Append(t).Append(",").Append(ys);
            }
        if (useVertical)
            for (int i = 0; i < n; i++)
            {
                double x = tileSize * (i + 0.5) / n;
                string xs = x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                sb.Append("M").Append(xs).Append(",0L").Append(xs).Append(",").Append(t);
            }
        return sb.ToString();
    }

    private static string Fmt(double v)
        => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}