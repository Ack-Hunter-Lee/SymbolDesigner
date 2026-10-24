using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Clipper2Lib;

namespace SymbolDesigner.Converters;

/// <summary>
/// 基于 Clipper2 的折线布尔引擎（第一版：椭圆 2° 均匀离散，其余几何取原生折线）。
///
/// 动机：WPF Geometry.Combine 对弧/贝塞尔先按未知密度摊平再布尔，导致"矩形与圆求并时
/// 正圆弧变形、偏心"。Clipper2 是三方的成熟多边形布尔内核（Vatti 裁剪），输入若为均匀
/// 折线则输出稳定、无偏心崩塌。
///
/// 输出是折线 PathData（F0 + M/L/Z），非真弧 —— 视觉上圆是"光滑多边形"。后续第二版再
/// 对结果做"弧重拟合"升级为真圆弧（方案②）。
///
/// 调用方（BooleanOps.Compute）应"本引擎优先，异常/失败回退 Geometry.Combine"。
/// </summary>
public static class ClipperBooleanEngine
{
    /// <summary>椭圆离散步长（度）。越小越圆、输出段数越多。</summary>
    public const double EllipseStepDeg = 2.0;

    /// <summary>对两个画布坐标几何执行布尔，返回局部坐标段结构 PathData。失败/不支持 → null。</summary>
    public static BooleanOps.BooleanResult? Compute(Geometry a, Geometry b, GeometryCombineMode mode)
    {
        // —— 参数准备（自身逻辑：异常应上抛暴露 bug，不吞） ——
        var subject = new PathsD();
        var clip = new PathsD();
        if (!GeometryToPaths(a, subject) || !GeometryToPaths(b, clip)) return null;
        if (subject.Count == 0 || clip.Count == 0) return null;

        ClipType? clipType = mode switch
        {
            GeometryCombineMode.Union => ClipType.Union,
            GeometryCombineMode.Exclude => ClipType.Difference,   // a - b（subject - clip）
            GeometryCombineMode.Intersect => ClipType.Intersection,
            _ => null,
        };
        if (clipType is null) return null;

        // —— Clipper2 交互段（第三方内核：异常降级 Combine，由调用方兜底；留痕不吞） ——
        var solution = new PathsD();
        try
        {
            var c = new ClipperD();
            c.AddPaths(subject, PathType.Subject, false);
            c.AddPaths(clip, PathType.Clip, false);
            if (!c.Execute(clipType.Value, Clipper2Lib.FillRule.EvenOdd, solution)) return null;
        }
        catch (Exception)
        {
            // Clipper 侧缺陷/资源异常 → 降级 Geometry.Combine（BooleanOps 兜底）。
            // 返回 null 由上层静默回退，不弹窗打断。
            return null;
        }
        if (solution.Count == 0) return null;

        // —— 序列化/装配（自身 bug：异常上抛，不静默回退） ——
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var path in solution)
            foreach (var p in path)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
        double ox = minX, oy = minY;

        var sb = new System.Text.StringBuilder();
        sb.Append("F0");
        bool any = false;
        foreach (var path in solution)
        {
            if (path.Count < 3) continue;
            if (Math.Abs(SignedDoubleArea(path)) < GeometryMath.SliverAreaTol) continue;   // 丢数值碎环
            string ring = SerializeRing(path, ox, oy);
            if (string.IsNullOrEmpty(ring)) continue;
            any = true;
            sb.Append(ring).Append(" Z");
        }
        if (!any) return null;

        return new BooleanOps.BooleanResult(
            sb.ToString(),
            Round(minX),
            Round(minY),
            Math.Max(0.01, Round(maxX - minX)),
            Math.Max(0.01, Round(maxY - minY)));
    }

    /// <summary>把 Geometry 转为画布坐标折线集合。椭圆 2° 均匀离散；其余走原生折线 + 套 Transform。</summary>
    private static bool GeometryToPaths(Geometry g, PathsD result)
    {
        switch (g)
        {
            case EllipseGeometry eg:
                return EllipseToPath(eg, result);
            case RectangleGeometry rg:
                return RectToPath(rg, result);
            case PathGeometry:
            case StreamGeometry:
            case GeometryGroup:
                return FlattenToPath(g, result);
            default:
                return FlattenToPath(g, result);
        }
    }

    /// <summary>椭圆按 2° 均匀离散成画布坐标折线（避免 WPF 摊平偏心）。</summary>
    private static bool EllipseToPath(EllipseGeometry eg, PathsD result)
    {
        double cx = eg.Center.X, cy = eg.Center.Y;
        double rx = Math.Max(0.01, eg.RadiusX), ry = Math.Max(0.01, eg.RadiusY);
        // 局部坐标点 → 套 Transform 得画布坐标
        var path = new PathD();
        int n = (int)Math.Ceiling(360 / EllipseStepDeg);
        for (int i = 0; i < n; i++)
        {
            double deg = i * EllipseStepDeg;
            double rad = deg * Math.PI / 180.0;
            var local = new System.Windows.Point(cx + rx * Math.Cos(rad), cy + ry * Math.Sin(rad));
            var pt = eg.Transform.Transform(local);
            path.Add(new PointD(pt.X, pt.Y));
        }
        if (path.Count >= 3) { result.Add(path); return true; }
        return false;
    }

    private static bool RectToPath(RectangleGeometry rg, PathsD result)
    {
        var r = rg.Rect;
        var path = new PathD
        {
            new PointD(r.Left, r.Top),
            new PointD(r.Right, r.Top),
            new PointD(r.Right, r.Bottom),
            new PointD(r.Left, r.Bottom),
        };
        // 套 Transform（圆角矩形的 Rect 不受圆角影响，直接用 4 角）
        for (int i = 0; i < path.Count; i++)
        {
            var pt = rg.Transform.Transform(new System.Windows.Point(path[i].x, path[i].y));
            path[i] = new PointD(pt.X, pt.Y);
        }
        result.Add(path);
        return true;
    }

    /// <summary>把 PathGeometry/StreamGeometry/GeometryGroup 摊平成画布坐标折线（各闭合 figure 一条 PathD）。</summary>
    private static bool FlattenToPath(Geometry g, PathsD result)
    {
        var flat = g.GetFlattenedPathGeometry(0.5, ToleranceType.Absolute);
        foreach (var f in flat.Figures)
        {
            if (f.Segments.Count == 0 && !f.IsClosed) continue;
            var path = new PathD();
            void Add(Point p)
            {
                // GetFlattenedPathGeometry 已经应用了 Geometry.Transform（输出即画布坐标），
                // 这里不得再乘 g.Transform，否则带 Translate 的图元（如闭合折线/布尔桥源）会被双重平移，
                // 导致布尔结果位置错乱、图元"消失"。
                path.Add(new PointD(p.X, p.Y));
            }
            Add(f.StartPoint);
            foreach (var seg in f.Segments)
            {
                if (seg is LineSegment ls)
                {
                    Add(ls.Point);
                }
                else if (seg is PolyLineSegment pl)
                {
                    foreach (var p in pl.Points) Add(p);
                }
            }
            if (path.Count < 3) continue;
            // 若未显式闭合，补充起点
            if (f.IsClosed && (path[0] != path[path.Count - 1]))
                path.Add(path[0]);
            result.Add(path);
        }
        return result.Count > 0;
    }

    private static double Round(double v)
        => Math.Abs(v) < GeometryMath.CoordEps ? 0 : Math.Round(v, 3, MidpointRounding.AwayFromZero);

    /// <summary>环绕有向面积（shoelace）。正 = 屏幕顺时针（y 向下），负 = 逆时针。用于碎环剔除与弧方向。</summary>
    private static double SignedDoubleArea(PathD path)
    {
        double sum = 0;
        for (int i = 0, n = path.Count; i < n; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % n];
            sum += a.x * b.y - b.x * a.y;
        }
        return sum;
    }

    /// <summary>把一个闭合环序列化成带弧重拟合的 PathData（M + L/A，不含 Z）。相对 (ox,oy) 局部坐标。
    /// 步骤：去重 → 严格共线合并 → 逐段贪心圆弧拟合 → L/A 输出。</summary>
    private static string SerializeRing(PathD path, double ox, double oy)
    {
        // 1) 去相邻重复点
        var ring = new List<PointD>(path.Count);
        foreach (var p in path)
            if (ring.Count == 0 || !NearEq(ring[ring.Count - 1], p))
                ring.Add(p);
        if (ring.Count > 2 && NearEq(ring[0], ring[ring.Count - 1])) ring.RemoveAt(ring.Count - 1);
        if (ring.Count < 3) return string.Empty;

        // 2) 严格共线点合并（只吃掉真正落在直线上的点，圆弧点不动，交给弧拟合）
        RemoveCollinear(ring);

        // 3) 弧拟合：合并圆弧点运行，其余按直线
        bool cw = SignedDoubleArea(new PathD(ring)) > 0;   // 屏幕顺时针（y 向下）→ sweep=1
        var sb = new System.Text.StringBuilder();
        sb.Append(" M ").Append(Fmt(ring[0].x - ox)).Append(',').Append(Fmt(ring[0].y - oy));

        int i = 0, n = ring.Count;
        while (i < n - 1)
        {
            // 以 ring[i] 为起点，贪心找最长圆弧[ i .. bestEnd ]
            int bestEnd = i + 1;
            PointD bestC = default; double bestR = 0;
            bool haveArc = false;
            for (int end = i + 2; end < n; end++)
            {
                // 最小二乘（Kasa）圆拟合：对 2° 采样的小幅圆弧数值稳定。
                // 不能用三点定圆——相邻点仅 2° 间隔的近共线三元组会灾难性消元，
                // 拟合出半径数千的畸形圆，残差超限导致弧段全被拒（此前"拟合未生效"根因）。
                if (!FitCircleKs(ring, i, end, out var C, out double R)) break;   // 病态/共线 → 不是弧
                if (R <= 0 || double.IsNaN(C.x) || double.IsNaN(C.y) || double.IsInfinity(R)) break;
                // 拒绝"直线退化成的巨圆"：真圆弧半径与窗口跨度同量级，直线拟合半径会大若干个量级
                if (R > 200.0 * MaxExtent(ring, i, end)) break;
                // 残差：段内所有点到圆心距离与 R 的最大偏离
                double maxDev = 0;
                for (int k = i; k <= end; k++)
                    maxDev = Math.Max(maxDev, Math.Abs(Dist(ring[k], C) - R));
                if (maxDev > GeometryMath.ArcFitTol) break;
                bestEnd = end; bestC = C; bestR = R; haveArc = true;
            }
            // 只有真正跨越多点的圆弧才收编为 A。短 window（3~7 点）任何三元组都能"精确"拟合出圆
            // （残差 0），矩形拐角衔接处会出现 R 严重失真的伪弧。真实圆弧（源圆 2° 采样）至少
            // 覆盖 ≥9 点（≥18°）；不足的直接走 L，仍精确只是不做压缩。
            if (haveArc && bestEnd - i >= 8)
            {
                AppendArc(sb, ring[i], ring[bestEnd], bestC, bestR, cw, ox, oy);
                i = bestEnd;
            }
            else
            {
                sb.Append(" L ").Append(Fmt(ring[i + 1].x - ox)).Append(',').Append(Fmt(ring[i + 1].y - oy));
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>窗口内包围盒的最大边长（用于拒绝"直线退化的巨圆弧"）。</summary>
    private static double MaxExtent(List<PointD> ring, int start, int end)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int k = start; k <= end; k++)
        {
            if (ring[k].x < minX) minX = ring[k].x;
            if (ring[k].x > maxX) maxX = ring[k].x;
            if (ring[k].y < minY) minY = ring[k].y;
            if (ring[k].y > maxY) maxY = ring[k].y;
        }
        return Math.Max(maxX - minX, maxY - minY);
    }

    /// <summary>Kasa 最小二乘圆拟合（未知数 A/B/C，x²+y²+Ax+By+C=0）。
    /// 对整个窗口多点做最小二乘，对小幅圆弧也稳定（不依赖 3 点近共线的 ill-conditioning）。</summary>
    private static bool FitCircleKs(List<PointD> pts, int start, int end, out PointD C, out double R)
    {
        double Sx = 0, Sy = 0, Sxx = 0, Syy = 0, Sxy = 0, Sx3 = 0, Sy3 = 0, Sxxy = 0, Sxyy = 0;
        for (int k = start; k <= end; k++)
        {
            double x = pts[k].x, y = pts[k].y;
            Sx += x; Sy += y; Sxx += x * x; Syy += y * y; Sxy += x * y;
            Sx3 += x * x * x; Sy3 += y * y * y; Sxxy += x * x * y; Sxyy += x * y * y;
        }
        int n = end - start + 1;
        // [Sxx Sxy Sx; Sxy Syy Sy; Sx Sy n]·[A;B;C] = -[Sx3+Sxyy; Sxxy+Sy3; Sxx+Syy]
        var m = new double[3, 4];
        m[0, 0] = Sxx; m[0, 1] = Sxy; m[0, 2] = Sx; m[0, 3] = -(Sx3 + Sxyy);
        m[1, 0] = Sxy; m[1, 1] = Syy; m[1, 2] = Sy; m[1, 3] = -(Sxxy + Sy3);
        m[2, 0] = Sx; m[2, 1] = Sy; m[2, 2] = n; m[2, 3] = -(Sxx + Syy);
        for (int col = 0; col < 3; col++)
        {
            int piv = col; double best = Math.Abs(m[col, col]);
            for (int r = col + 1; r < 3; r++) if (Math.Abs(m[r, col]) > best) { best = Math.Abs(m[r, col]); piv = r; }
            if (best < 1e-12) { C = default; R = 0; return false; }
            if (piv != col) for (int c2 = 0; c2 < 4; c2++) { var t = m[col, c2]; m[col, c2] = m[piv, c2]; m[piv, c2] = t; }
            double pv = m[col, col];
            for (int c2 = col; c2 < 4; c2++) m[col, c2] /= pv;
            for (int r = 0; r < 3; r++)
            {
                if (r == col) continue;
                double f = m[r, col];
                if (Math.Abs(f) < 1e-12) continue;
                for (int c2 = col; c2 < 4; c2++) m[r, c2] -= f * m[col, c2];
            }
        }
        double A = m[0, 3], B = m[1, 3], CC = m[2, 3];
        double cx = -A / 2, cy = -B / 2;
        double rad2 = (A * A + B * B) / 4 - CC;
        if (rad2 <= 0) { C = default; R = 0; return false; }
        R = Math.Sqrt(rad2);
        C = new PointD(cx, cy);
        return true;
    }

    /// <summary>严格共线点合并：中间点到端点连线的侧偏 ≤ CollinearTol 视为同一直线，丢弃中间点。</summary>
    private static void RemoveCollinear(List<PointD> ring)
    {
        if (ring.Count < 3) return;
        var outList = new List<PointD>(ring.Count);
        int n = ring.Count;
        outList.Add(ring[0]);
        for (int j = 1; j < n - 1; j++)
            if (SideOffset(outList[outList.Count - 1], ring[j + 1], ring[j]) > GeometryMath.CollinearTol)
                outList.Add(ring[j]);
        outList.Add(ring[n - 1]);
        ring.Clear();
        ring.AddRange(outList);
    }

    /// <summary>点 c 到线段(a,b) 的垂直距离（仅用于共线判定）。</summary>
    private static double SideOffset(PointD a, PointD b, PointD c)
    {
        double ax = b.x - a.x, ay = b.y - a.y;
        double len2 = ax * ax + ay * ay;
        if (len2 < 1e-14) return Dist(a, c);
        double cross = (c.x - a.x) * ay - (c.y - a.y) * ax;
        return Math.Abs(cross) / Math.Sqrt(len2);
    }

    private static double Dist(PointD a, PointD b)
    {
        double dx = a.x - b.x, dy = a.y - b.y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool NearEq(PointD a, PointD b)
        => Math.Abs(a.x - b.x) < 1e-6 && Math.Abs(a.y - b.y) < 1e-6;

    /// <summary>输出 A 指令。direction：屏幕顺时针(cw=true)→sweep=1；largeArc 由圆心角是否&gt;180°决定。</summary>
    private static void AppendArc(System.Text.StringBuilder sb, PointD start, PointD end, PointD C, double R, bool cw, double ox, double oy)
    {
        double a0 = Math.Atan2(start.y - C.y, start.x - C.x);
        double a1 = Math.Atan2(end.y - C.y, end.x - C.x);
        // 屏幕顺时针(cw)= 角度增加方向：从 a0 到 a1 的顺时针有向角 = Nor(a1-a0)。
        // 逆时针(ccw)= 角度减小方向：从 a0 到 a1 的逆时针有向角 = Nor(a0-a1)。
        // 之前两个分支写反，导致 largeArc 总取"补弧"距离——优弧(>180°)被误判成劣弧坍缩。
        double span = cw ? Nor(a1 - a0) : Nor(a0 - a1);
        bool largeArc = span > Math.PI;
        sb.Append(" A ")
          .Append(Fmt(R)).Append(',')
          .Append(Fmt(R)).Append(" 0 ")
          .Append(largeArc ? "1" : "0").Append(' ')
          .Append(cw ? "1" : "0").Append(' ')
          .Append(Fmt(end.x - ox)).Append(',').Append(Fmt(end.y - oy));
    }

    /// <summary>归一化到 [0, 2π)。</summary>
    private static double Nor(double v)
    {
        v %= Math.PI * 2;
        if (v < 0) v += Math.PI * 2;
        return v;
    }

    private static string Fmt(double v)
    {
        v = Math.Abs(v) < GeometryMath.CoordEps ? 0 : Math.Round(v, 3, MidpointRounding.AwayFromZero);
        if (v == Math.Floor(v) && Math.Abs(v) < 1e9) return ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}