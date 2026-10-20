using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SymbolDesigner.Converters;
using SymbolDesigner.Models;

namespace SymbolDesigner.Cli;

/// <summary>离屏渲染 SymbolProject 为 PNG 预览（供外部 AI-agent 视觉自检）。
/// 机制：STA 线程 + DrawingVisual + RenderTargetBitmap → PngBitmapEncoder，无需启动 Application/Window。
/// 颜色一律取 DTO 里的 hex 用户数据（同 Objective 导出规则，属"画作内容"而非 UI 硬编码）。
/// 画布无背景色时用的兜底深色是 CLI 预览默认，与 GUI 深色画布观感一致。</summary>
internal static class Renderer
{
    private static readonly Brush DefaultCanvasBg = new SolidColorBrush(ParseColor("#FF1B1B1B"));

    /// <summary>渲染到 PNG。outW/outH 为目标像素尺寸（纵横比不符时按 fit 等比缩放 + 留边，不变形）。</summary>
    public static void Render(SymbolProject proj, string outPath, int outW, int outH)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                RenderOnSta(proj, outPath, outW, outH);
            }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }

    private static void RenderOnSta(SymbolProject proj, string outPath, int outW, int outH)
    {
        double cw = Math.Max(1, proj.CanvasWidth);
        double ch = Math.Max(1, proj.CanvasHeight);

        // fit：等比缩放留边，避免任意尺寸拉伸变形
        double scale = Math.Min((double)outW / cw, (double)outH / ch);
        double contentW = cw * scale, contentH = ch * scale;
        double offX = (outW - contentW) / 2, offY = (outH - contentH) / 2;

        var vb = new DrawingVisual();
        using (var ctx = vb.RenderOpen())
        {
            // 整幅底色
            var bg = ParseBrush(proj.CanvasBackgroundColor) ?? DefaultCanvasBg;
            ctx.DrawRectangle(bg, null, new Rect(0, 0, outW, outH));

            // 内容变换：平移画布到留边区再等比缩放
            ctx.PushTransform(new TranslateTransform(offX, offY));
            ctx.PushTransform(new ScaleTransform(scale, scale));

            var all = proj.Primitives;
            foreach (var dto in all.OrderBy(p => p.ZIndex))
                DrawPrimitive(ctx, dto, all);

            ctx.Pop();
            ctx.Pop();
        }

        var rtb = new RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(vb);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        enc.Save(fs);
    }

    private static void DrawPrimitive(DrawingContext ctx, PrimitiveItemDto dto, IReadOnlyList<PrimitiveItemDto> all)
    {
        if (!dto.IsVisible) return;

        var local = BuildLocalGeometry(dto, all);
        if (local is null) return;

        var (fill, pen) = BuildPaints(dto);

        ctx.PushOpacity(Clamp01(dto.Opacity));
        ctx.PushTransform(new TranslateTransform(dto.X, dto.Y));
        ctx.DrawGeometry(fill, pen, local);
        ctx.Pop();
        ctx.Pop();
    }

    /// <summary>构造图元局部几何（以 X/Y 为原点），语义与 PrimitiveItem 渲染管线对齐。
    /// Belt 需全部兄弟图元（滚轮）才能求解，故额外收 all。</summary>
    private static Geometry? BuildLocalGeometry(PrimitiveItemDto dto, IReadOnlyList<PrimitiveItemDto> all)
    {
        double w = dto.Width, h = dto.Height;
        switch (dto.TypeKey)
        {
            case nameof(L1Primitive.Rectangle):
                return new RectangleGeometry(new Rect(0, 0, w, h));
            case nameof(L1Primitive.RoundedRectangle):
                return new RectangleGeometry(new Rect(0, 0, w, h),
                    Math.Max(0, dto.CornerRadius), Math.Max(0, dto.CornerRadius));
            case nameof(L1Primitive.Ellipse):
                return new EllipseGeometry(new Point(w / 2, h / 2), Math.Max(0.1, w / 2), Math.Max(0.1, h / 2));
            case nameof(L1Primitive.Triangle):
                return BuildTriangle(w, h);
            case nameof(L1Primitive.Ring):
                return BuildRing(w, h, dto.InnerRadius);
            case nameof(L1Primitive.Pie):
                return BuildPie(w, h, dto.StartAngle, dto.SweepAngle);
            case nameof(L1Primitive.Belt):
                return BeltDtoGeometry.SolveGeometry(dto, all);
            case nameof(L1Primitive.Line):
            case nameof(L1Primitive.Polyline):
            case nameof(L1Primitive.BandPolyline):
            case nameof(L1Primitive.BooleanPath):
                return ParsePath(dto.PathData);
            default:
                return null;
        }
    }

    private static Geometry BuildTriangle(double w, double h)
    {
        var pg = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(w * 0.5, 0), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(new Point(w, h), true));
        fig.Segments.Add(new LineSegment(new Point(0, h), true));
        pg.Figures.Add(fig);
        pg.Freeze();
        return pg;
    }

    /// <summary>圆环：外圆顺 + 内圆逆的 Nonzero 单 Path，内圆天然空洞（复刻 PrimitiveItem.RingGeometry）。</summary>
    private static Geometry? BuildRing(double w, double h, double innerRadius)
    {
        if (w <= 0 || h <= 0) return null;
        double cx = w / 2, cy = h / 2;
        double outer = Math.Min(w, h) / 2 - BeltDtoGeometry.RingStrokeHalfThickness;
        if (outer <= 0.01) return null;
        double inner = Math.Clamp(innerRadius > 0 ? innerRadius : Math.Min(w, h) / 4, 0.01, outer - 0.01);

        var stream = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var sctx = stream.Open())
        {
            sctx.BeginFigure(new Point(cx - outer, cy), true, true);
            sctx.ArcTo(new Point(cx + outer, cy), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true, false);
            sctx.ArcTo(new Point(cx - outer, cy), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true, false);

            sctx.BeginFigure(new Point(cx - inner, cy), true, true);
            sctx.ArcTo(new Point(cx + inner, cy), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
            sctx.ArcTo(new Point(cx - inner, cy), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        stream.Freeze();
        return stream;
    }

    /// <summary>扇形 Path（复刻 PieGeometryBuilder.BuildPath，弧度关于 StartAngle 对称展开 StartAngle±Sweep/2）。</summary>
    private static Geometry? BuildPie(double w, double h, double startAngle, double sweepAngle)
    {
        double r = Math.Min(w, h) / 2;
        if (w <= 0 || h <= 0 || r <= 0) return null;
        double sweep = sweepAngle is <= 0 or >= 360 ? 359.9999 : sweepAngle;
        double a0 = Rad(startAngle - sweep / 2), a1 = Rad(startAngle + sweep / 2);
        double cx = w / 2, cy = h / 2;
        double sx = cx + r * Math.Cos(a0), sy = cy + r * Math.Sin(a0);
        double ex = cx + r * Math.Cos(a1), ey = cy + r * Math.Sin(a1);
        int largeArc = sweep > 180 ? 1 : 0;
        var sb = new System.Text.StringBuilder(96);
        sb.Append("M ").Append(F(sx)).Append(',').Append(F(sy));
        sb.Append(" A ").Append(F(r)).Append(',').Append(F(r)).Append(" 0 ")
          .Append(largeArc).Append(",1 ").Append(F(ex)).Append(',').Append(F(ey));
        sb.Append(" L ").Append(F(cx)).Append(',').Append(F(cy)).Append(" Z");
        return Geometry.Parse(sb.ToString());
    }

    private static Geometry? ParsePath(string? pathData)
    {
        if (string.IsNullOrWhiteSpace(pathData)) return null;
        try { return Geometry.Parse(pathData); }
        catch (FormatException) { return null; }
    }

    private static (Brush? fill, Pen? pen) BuildPaints(PrimitiveItemDto dto)
    {
        var fill = BuildFill(dto);
        double sw = dto.StrokeWidth ?? 0.8;
        var strokeBrush = ParseBrush(dto.StrokeColor ?? "#FFFFFF");
        Pen? pen = null;
        if (strokeBrush is not null && sw > 0)
        {
            pen = new Pen(strokeBrush, sw) { DashStyle = ResolveDash(dto.StrokeDashStyle) };
            pen.Freeze();
        }
        return (fill, pen);
    }

    private static Brush? BuildFill(PrimitiveItemDto dto)
    {
        // 皮带单线语义：与 GUI 的 IsFillable=false 对齐，不参与填充/图案
        if (dto.TypeKey == nameof(L1Primitive.Belt)) return null;

        double fb = dto.FillBrightness ?? 100;
        double fo = dto.FillOpacity ?? 100;
        switch (dto.FillStyle)
        {
            case "Solid":
                return ParseBrush(ColorUtil.ApplyBrightnessAndOpacity(dto.FillColor, fb, fo));
            case "Gradient":
                return ColorUtil.BuildLinearGradient(
                    ColorUtil.ApplyBrightnessAndOpacity(dto.GradientStartColor, fb, fo),
                    ColorUtil.ApplyBrightnessAndOpacity(dto.GradientEndColor, fb, fo));
            case "MonochromeGradient":
                return ColorUtil.BuildLinearGradient(
                    ColorUtil.ApplyBrightnessAndOpacity(dto.FillColor, fb, fo),
                    ColorUtil.DeriveMonochromeEnd(dto.FillColor, fb, fo));
            case "Pattern":
                return PatternBrushFactory.Build(
                    ParsePattern(dto.PatternStyle),
                    dto.PatternForeground ?? "#E0E0E0", dto.PatternForegroundBrightness ?? 100, dto.PatternForegroundOpacity ?? 100,
                    dto.PatternBackground, dto.PatternBackgroundBrightness ?? 100, dto.PatternBackgroundOpacity ?? 100,
                    dto.PatternTileSize ?? 8, dto.PatternSpacing ?? 6);
            default:
                return null;
        }
    }

    private static DashStyle ResolveDash(string? name)
        => name switch
        {
            "Dash" => new DashStyle(new double[] { 4, 2 }, 0),
            "Dot" => new DashStyle(new double[] { 1, 2 }, 0),
            "DashDot" => new DashStyle(new double[] { 4, 2, 1, 2 }, 0),
            _ => DashStyles.Solid,
        };

    private static PatternStyle ParsePattern(string? name)
        => Enum.TryParse<PatternStyle>(name, ignoreCase: true, out var s) ? s : PatternStyle.Diagonal;

    /// <summary>解析 hex 为 SolidColorBrush（ColorUtil.ParseHex 恒返回 SolidColorBrush；失败 → null）。</summary>
    private static SolidColorBrush? ParseBrush(string? hex) => ColorUtil.ParseHex(hex) as SolidColorBrush;

    private static Color ParseColor(string hex)
        => (Color)ColorConverter.ConvertFromString(hex);

    private static double Rad(double deg) => deg * Math.PI / 180.0;
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);
}