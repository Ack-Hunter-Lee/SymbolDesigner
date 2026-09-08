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
        try
        {
            var subject = new PathsD();
            var clip = new PathsD();
            if (!GeometryToPaths(a, subject) || !GeometryToPaths(b, clip)) return null;
            if (subject.Count == 0 || clip.Count == 0) return null;

            var c = new ClipperD();
            c.AddPaths(subject, PathType.Subject, false);
            c.AddPaths(clip, PathType.Clip, false);

            ClipType? clipType = mode switch
            {
                GeometryCombineMode.Union => ClipType.Union,
                GeometryCombineMode.Exclude => ClipType.Difference,   // a - b（subject - clip）
                GeometryCombineMode.Intersect => ClipType.Intersection,
                _ => null,
            };
            if (clipType is null) return null;

            var solution = new PathsD();
            if (!c.Execute(clipType.Value, Clipper2Lib.FillRule.EvenOdd, solution)) return null;
            if (solution.Count == 0) return null;

            // 计算包围盒，转换为局部坐标
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
                if (path.Count < 2) continue;
                any = true;
                sb.Append(" M ").Append(Fmt(path[0].x - ox)).Append(',').Append(Fmt(path[0].y - oy));
                for (int i = 1; i < path.Count; i++)
                    sb.Append(" L ").Append(Fmt(path[i].x - ox)).Append(',').Append(Fmt(path[i].y - oy));
                sb.Append(" Z");
            }
            if (!any) return null;

            return new BooleanOps.BooleanResult(
                sb.ToString(),
                Round(minX),
                Round(minY),
                Math.Max(0.01, Round(maxX - minX)),
                Math.Max(0.01, Round(maxY - minY)));
        }
        catch
        {
            // 任何异常回退 Combine（由调用方处理）
            return null;
        }
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
                var pt = g.Transform.Transform(p);
                path.Add(new PointD(pt.X, pt.Y));
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
        => Math.Abs(v) < 1e-3 ? 0 : Math.Round(v, 3, MidpointRounding.AwayFromZero);

    private static string Fmt(double v)
    {
        v = Math.Abs(v) < 1e-3 ? 0 : Math.Round(v, 3, MidpointRounding.AwayFromZero);
        if (v == Math.Floor(v) && Math.Abs(v) < 1e9) return ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}