using System.Globalization;
using System.Text;
using System.Windows;
using SymbolDesigner.Models;

namespace SymbolDesigner.Geometry;

/// <summary>纯几何 / 图元类型判定的静态工具集。
/// 从 SymbolDesignerViewModel 剥离：不依赖任何 VM 状态，可独立测试、供各层复用。</summary>
public static class GeometryMath
{
    /// <summary>是否为直线类图元（可作为相切锚）。</summary>
    public static bool IsLineLike(PrimitiveItem p) =>
        p.TypeKey is nameof(L1Primitive.Line)
            or nameof(L1Primitive.Polyline)
            or nameof(L1Primitive.BandPolyline);

    /// <summary>是否为圆类图元（可作为相切目标）。</summary>
    public static bool IsCircleLike(PrimitiveItem p) =>
        p.TypeKey is nameof(L1Primitive.Ellipse) or nameof(L1Primitive.Ring);

    /// <summary>两点欧氏距离。</summary>
    public static double Dist(Point p0, Point p1)
    {
        double dx = p0.X - p1.X, dy = p0.Y - p1.Y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>子对象命中容差（画布逻辑单位）。点击位置距端点/圆心 ≤ 此值 → 命中该子对象。</summary>
    public const double SubObjectSnapTol = 6.0;

    // —— 旋转映射（图元渲染用 RenderTransform 绕包围盒中心旋转；手柄/拖动写回需与之保持一致） ——

    /// <summary>图元旋转中心（画布坐标）= 包围盒中心，与 XAML 容器 RenderTransformOrigin=0.5,0.5 一致。</summary>
    public static Point RotationCenter(PrimitiveItem item) =>
        new(item.X + item.Width / 2, item.Y + item.Height / 2);

    /// <summary>绕指定中心旋转一点（角度为度，屏幕坐标 y 向下；正角=顺时针）。
    /// angle==0 时原样返回，保证未旋转图元零开销、零浮点误差。</summary>
    public static Point RotateAbout(Point center, double angleDeg, Point p)
    {
        if (angleDeg == 0) return p;
        double rad = angleDeg * Math.PI / 180;
        double cos = System.Math.Cos(rad), sin = System.Math.Sin(rad);
        double dx = p.X - center.X, dy = p.Y - center.Y;
        return new Point(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    /// <summary>未旋转画布坐标 → 旋转后的画布坐标（与渲染一致）。</summary>
    public static Point RotateCanvasPoint(PrimitiveItem item, Point unrotatedCanvas)
        => RotateAbout(RotationCenter(item), item.Rotation, unrotatedCanvas);

    /// <summary>旋转后的画布坐标 → 未旋转画布坐标（<see cref="RotateCanvasPoint"/> 的逆）。
    /// 用于把手柄拖动时的鼠标位置换算回图元未旋转坐标系再写回几何。</summary>
    public static Point UnrotateCanvasPoint(PrimitiveItem item, Point canvas)
        => RotateAbout(RotationCenter(item), -item.Rotation, canvas);

    /// <summary>图元局部坐标（相对 X/Y 原点）→ 旋转后的画布坐标。</summary>
    public static Point LocalToCanvas(PrimitiveItem item, Point local)
        => RotateCanvasPoint(item, new Point(item.X + local.X, item.Y + local.Y));

    // —— 布尔 / 折线数值容差（集中自 BooleanOps / ClipperBooleanEngine，统一语义，避免散布） ——

    /// <summary>近重合合并阈值：Combine 在切点处把同一交点算成两个数值不等的端点，圆被采样为贝塞尔后
    /// 求交误差可达 ~0.46 逻辑单位（实测 矩形∩圆 = 0.458）；小于此值的相邻点视为同一点并合并。
    /// 有效消除"剪除/相交后轮廓点一堆近重合点 + 微段"的视觉问题。</summary>
    public const double MergeTol = 0.5;

    /// <summary>坐标容差 / 序列化精度（画布逻辑单位）。绝对值小于此值的坐标量零（快照字符串 3 位小数足够）。</summary>
    public const double CoordEps = 1e-3;

    /// <summary>弧重拟合：点到拟合圆的最大允许径向偏离（画布单位）。2° 采样点心到真圆偏差远小于此。</summary>
    public const double ArcFitTol = 0.35;

    /// <summary>直线合并：共线中间点到两点连线的最大允许侧偏（真正直线段的合并容差）。</summary>
    public const double CollinearTol = 0.02;

    /// <summary>碎环剔除：|环绕面积| 小于此值的环视为数值噪声（重叠相切产生的细长/退化环）丢弃。</summary>
    public const double SliverAreaTol = 1e-3;

    /// <summary>形状两段式 Shift 锁正形：让 |dx| = |dy|（取较大值，CAD 习惯）。
    /// 形状模式下 Shift 按住 → 矩形变正方形、椭圆变正圆、圆环变正圆、三角形变为等边。
    /// 坐标系：center 为形状中心点（DraftPoints[0]），target 为当前鼠标位置。
    /// 返回等比化后的目标点（确保 |dx|=|dy|，方向与原 target 一致）。</summary>
    public static Point ConstrainShapeToSquare(Point center, Point target)
    {
        double dx = target.X - center.X;
        double dy = target.Y - center.Y;
        // 都归零（鼠标正好在中心）→ 直接返回
        if (dx == 0 && dy == 0) return target;
        double absDx = System.Math.Abs(dx);
        double absDy = System.Math.Abs(dy);
        // 取较大值，确保用户在 dx 方向拖得更远时按 dx 锁；dy 方向拖得更远时按 dy 锁
        double side = System.Math.Max(absDx, absDy);
        double sx = side == 0 ? dx : dx / absDx * side;
        double sy = side == 0 ? dy : dy / absDy * side;
        return new Point(center.X + sx, center.Y + sy);
    }

    /// <summary>把点列表序列化为 Path Data 字符串（"M x,y L x,y L ..."）。</summary>
    public static string BuildPathData(System.Collections.Generic.List<Point> pts)
    {
        if (pts.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendFormat(CultureInfo.InvariantCulture, "M {0:0.##},{1:0.##}", pts[0].X, pts[0].Y);
        for (int i = 1; i < pts.Count; i++)
            sb.AppendFormat(CultureInfo.InvariantCulture, " L {0:0.##},{1:0.##}", pts[i].X, pts[i].Y);
        return sb.ToString();
    }

    /// <summary>为 PathData 型图元（Line/Polyline/BandPolyline/BooleanPath）重算包围盒。
    /// 拖动端点后，PathData 局部坐标可能超出当前 X/Y→X+Width 范围，
    /// 导致部分线段不可见。此方法同步更新 X/Y/Width/Height，
    /// 同时把 PathData 平移到新原点。BooleanPath 多环骨架保持环结构重建（F0 前缀）。</summary>
    public static void RecalcBoundingBox(PrimitiveItem item)
    {
        if (string.IsNullOrEmpty(item.PathData)) return;
        bool isBoolean = item.TypeKey == nameof(L1Primitive.BooleanPath);
        var rings = isBoolean ? BooleanOps.GetSkeletonRings(item.PathData) : new List<List<Point>>();
        var flat = new List<Point>();
        if (isBoolean)
        {
            foreach (var r in rings) flat.AddRange(r);
        }
        else
        {
            var pts = BandOutlineBuilder.ParsePathData(item.PathData);
            for (int i = 0; i < pts.Count; i++) flat.Add(pts[i]);
        }
        if (flat.Count == 0) return;

        // 计算当前 PathData 局部坐标的 bbox
        double minX = flat[0].X, minY = flat[0].Y;
        double maxX = minX, maxY = minY;
        foreach (var p in flat)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        // 若局部坐标在当前 bbox 范围内，跳过——包围盒已对齐
        // 仅在 minX/minY 负偏移（向左/上拖）或 maxX/maxY 超出当前 W/H（向右/下拖）时重算
        if (minX >= -1e-6 && minY >= -1e-6 && maxX <= item.Width + 1e-6 && maxY <= item.Height + 1e-6) return;

        // 平移 PathData 到新原点
        double w = System.Math.Max(2, maxX - minX);
        double h = System.Math.Max(2, maxY - minY);

        // 更新 X/Y → 旧 X/Y + 原来偏移量 = 新画布坐标
        item.X += minX;
        item.Y += minY;
        item.Width = w;
        item.Height = h;

        if (isBoolean)
        {
            // 段结构快照整体平移（坐标全平移，弧参数/环结构不动）
            item.PathData = BooleanOps.Translate(item.PathData, -minX, -minY);
        }
        else
        {
            var shifted = new List<Point>(flat.Count);
            foreach (var p in flat)
                shifted.Add(new Point(p.X - minX, p.Y - minY));
            item.PathData = BuildPathData(shifted);
        }
    }

    /// <summary>按点击位置识别某图元命中的子对象类别（圆心/端点 vs 圆弧/线段/普通）。
    /// 圆心与端点会被确定"命中"并输出对应子对象；圆弧/线段返回类别但无单一命中点。</summary>
    public static PickKind ClassifyPick(PrimitiveItem item, Point canvasPick, out Point snapPoint)
    {
        snapPoint = default;
        if (IsCircleLike(item))
        {
            var center = new Point(item.X + item.Width / 2.0, item.Y + item.Height / 2.0);
            if (Dist(canvasPick, center) <= SubObjectSnapTol) { snapPoint = center; return PickKind.Center; }
            return PickKind.Arc;
        }
        if (IsLineLike(item))
        {
            if (TangentSolver.TryGetLineEndpoint(item, 0, out var e0)
                && Dist(canvasPick, e0) <= SubObjectSnapTol) { snapPoint = e0; return PickKind.EndPoint0; }
            if (TangentSolver.TryGetLineEndpoint(item, 1, out var e1)
                && Dist(canvasPick, e1) <= SubObjectSnapTol) { snapPoint = e1; return PickKind.EndPoint1; }
            return PickKind.Segment;
        }
        return PickKind.None;
    }
}