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
}