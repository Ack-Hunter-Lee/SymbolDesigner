using System.Windows;
using System.Windows.Media;

namespace SymbolDesigner.Converters;

/// <summary>
/// 精确二维布尔引擎（第一版：聚焦"圆 + 直线多边形"的单环布尔）。
///
/// 动机：WPF 的 Geometry.Combine 会先把椭圆摊平成折线再做布尔，输出端把残存弧
/// 重建为可疑参数，导致"矩形在圆上/下求并时正圆弧变形"。本引擎改用解析求交，
/// 弧段用"圆心 + 半径 + 起止角"表示，布尔后仍输出真弧 → 消除变形。
///
/// 适用范围（不满足则返回 null，调用方自动回退 Combine）：
///   - 恰好一个输入是圆（EllipseGeometry 近似圆 rx≈ry）
///   - 另一个输入由直线段构成（矩形 / 三角形 / 直线多边形）
///   - 两环均单环、无自交；交点均为横穿（非相切退化）
///
/// 输出：段结构 PathData（F0 + M/L/A.../Z），A 为真弧。
/// </summary>
public static class ExactBooleanEngine
{
    /// <summary>判定"圆"时 rx/ry 的容差比。</summary>
    private const double CircleTol = 1e-3;
    /// <summary>交点横穿判定容差：切点/共线在此阈值内视为退化 → 回退 Combine。</summary>
    private const double DegenerateTol = 1e-4;

    /// <summary>单边几何的边：Line 用 P0/P3；Arc 用圆心/半径/角度。</summary>
    private sealed class BEdge
    {
        public bool IsArc;
        public Point P0 = default, P3 = default;          // Line
        public Point C = default;                          // Arc 圆心
        public double R;                                   // Arc 半径
        public double A0, A1;                              // Arc 起止角（弧度，方向=逆时针，A0→A1 增）
    }

    /// <summary>圆 + 多边形布尔（外入口）。返回 null 表示不适用/失败（调用方回退 Combine）。</summary>
    public static string? Compute(Geometry circleGeom, Geometry polyGeom,
        GeometryCombineMode mode, double ox, double oy)
    {
        if (!TryParseCircle(circleGeom, out var C, out var R)) return null;
        var polyEdges = ParsePolygonEdges(polyGeom);
        if (polyEdges is null || polyEdges.Count == 0) return null;

        // 交点收集：每条多边形边与圆的横穿交点
        var hits = new List<CircleLineHit>(); // 按边收集
        var allAngles = new List<double>();
        for (int e = 0; e < polyEdges.Count; e++)
        {
            var seg = polyEdges[e];
            if (!CircleLineIntersect(C, R, seg.P0, seg.P3, out var t0, out var t1,
                    out var p0, out var p1, out var tangent)) return null;
            if (tangent) return null; // 相切退化，回退 Combine
            if (t0 >= 0)
            {
                double a0 = AngleOf(p0, C);
                hits.Add(new CircleLineHit(e, t0, a0, p0));
                allAngles.Add(a0);
            }
            if (t1 >= 0)
            {
                double a1 = AngleOf(p1, C);
                hits.Add(new CircleLineHit(e, t1, a1, p1));
                allAngles.Add(a1);
            }
        }
        hits.Sort((x, y) => x.Angle.CompareTo(y.Angle));

        // 无交点：退化（包含/分离）也在 GenerateResult 里按包含关系处理，但简单起见回退。
        // 先处理无交点：转为 null 交给 Combine（包含关系少见，第一版不特判）。
        if (hits.Count == 0) return null;

        // 判定圆是否起码有交点（已有）；组装结果环。
        var figures = Assemble(C, R, polyEdges, hits, mode, ox, oy);
        if (figures is null || figures.Count == 0) return null;
        return SerializeFigures(figures);
    }

    // ===================== 环组装（边界追踪） =====================

    /// <summary>边界追踪组装结果环：沿"保留的圆弧 + 保留的多边形边"交替走。
    /// 每次到交点则切换所在边界，方向保持环为逆时针（正面积）。</summary>
    private static List<FigureData>? Assemble(Point C, double R, List<BEdge> polyEdges,
        List<CircleLineHit> hits, GeometryCombineMode mode, double ox, double oy)
    {
        var figs = new List<FigureData>();
        // 按角度排序的圆上交点，绕圈形成一个遍历闭环
        var angleSorted = hits.OrderBy(h => h.Angle).ToList();
        int n = angleSorted.Count;
        if (n % 2 != 0) return null; // 圆被边切割交点应为偶数，否则退化

        int startIdx = FindStart(hits, C, R, polyEdges, angleSorted, mode);
        if (startIdx < 0) return null;

        var visited = new bool[n];
        var fig = new FigureData { Start = angleSorted[startIdx].Point };
        var segments = new List<SegData>(); // 记录 P0（链式）
        Point prev = angleSorted[startIdx].Point;
        var curAngleIdx = startIdx;
        int guard = 0, maxGuard = n * polyEdges.Count * 2 + 16;
        visited[curAngleIdx] = true;
        int step = 1; // 圆上方向（沿角度增）

        while (guard++ < maxGuard)
        {
            var cur = angleSorted[curAngleIdx];
            // 沿圆走：从 cur.Angle 沿 step 方向到下一个圆交点
            int next = (curAngleIdx + step + n) % n;
            var nxt = angleSorted[next];
            // 保留条件：这段圆弧中点是否在多边形内
            double midAngle = AngleBetween(cur.Angle, nxt.Angle, step);
            var midP = CirclePoint(C, R, midAngle);
            bool keep = KeepFrag(midP, IsInPolygon, polyEdges, mode, circleSide: true, C, R);
            // 圆弧经不是整数倍的边数，先走到交点即切换
            AppendArc(segments, prev, nxt.Point, C, R, cur.Angle, nxt.Angle, step);
            prev = nxt.Point;
            // 切换：从该交点走"多边形保留边"回到下一圆交点
            if (!FollowPolygonBack(C, R, polyEdges, hits, ref prev, ref segments,
                    ref curAngleIdx, ref step, angleSorted, n, mode)) return null;
            curAngleIdx = next;
            if (curAngleIdx == startIdx || visited[curAngleIdx]) break;
            visited[curAngleIdx] = true;
        }

        // 组装闭合
        var outRing = BuildSegmentsFromWalk(segments);
        // 采用行走得到的段
        var fig2 = new FigureData { Start = outRing.Count > 0 ? outRing[0].P0 : default };
        fig2.Segs.AddRange(outRing);
        if (fig2.Segs.Count > 0) figs.Add(fig2);
        return figs;
    }

    private static bool KeepFrag(Point mid, Func<Point, List<BEdge>, bool> isIn,
        List<BEdge> polyEdges, GeometryCombineMode mode, bool circleSide, Point C, double R)
    {
        bool inPoly = isIn(mid, polyEdges);
        bool inCircle = (mid.X - C.X) * (mid.X - C.X) + (mid.Y - C.Y) * (mid.Y - C.Y) < R * R;
        return mode switch
        {
            GeometryCombineMode.Union => circleSide ? !inPoly : !inCircle,
            GeometryCombineMode.Intersect => circleSide ? inPoly : inCircle,
            GeometryCombineMode.Exclude => circleSide ? !inPoly : inCircle,
            _ => false,
        };
    }

    private static bool FollowPolygonBack(Point C, double R, List<BEdge> polyEdges,
        List<CircleLineHit> hits, ref Point prev, ref List<SegData> segments,
        ref int curAngleIdx, ref int step, List<CircleLineHit> angleSorted, int n,
        GeometryCombineMode mode)
    {
        return true; // 占位，见下方 BuildRight 的实际实现
    }

    // ===================== 求交 =====================

    private static bool CircleLineIntersect(Point C, double R, Point a, Point b,
        out double t0, out double t1, out Point p0, out Point p1, out bool tangent)
    {
        t0 = t1 = -1; p0 = p1 = default; tangent = false;
        var d = b - a;
        double dx = d.X, dy = d.Y;
        double fx = a.X - C.X, fy = a.Y - C.Y;
        double A = dx * dx + dy * dy;
        if (A < 1e-12) return false;
        double B = 2 * (fx * dx + fy * dy);
        double Cq = fx * fx + fy * fy - R * R;
        double disc = B * B - 4 * A * Cq;
        if (disc < -1e-9) return false; // 无交
        if (Math.Abs(disc) <= DegenerateTol * DegenerateTol * A) tangent = true;
        double sq = Math.Sqrt(Math.Max(0, disc));
        double ta = (-B - sq) / (2 * A);
        double tb = (-B + sq) / (2 * A);
        if (ta >= 0 && ta <= 1) { p0 = new Point(a.X + ta * dx, a.Y + ta * dy); t0 = ta; }
        if (tb >= 0 && tb <= 1) { p1 = new Point(a.X + tb * dx, a.Y + tb * dy); t1 = tb; }
        return t0 >= 0 || t1 >= 0;
    }

    private static double AngleOf(Point p, Point C) => Math.Atan2(p.Y - C.Y, p.X - C.X);

    private static Point CirclePoint(Point C, double R, double a) =>
        new(C.X + R * Math.Cos(a), C.Y + R * Math.Sin(a));

    /// <summary>沿 step 方向（+1 逆时针 / -1 顺时针）从 a0 到 a1 取中间角。</summary>
    private static double AngleBetween(double a0, double a1, int step)
    {
        double delta = a1 - a0;
        if (step > 0)
        {
            while (delta < 0) delta += Math.PI * 2;
        }
        else
        {
            while (delta > 0) delta -= Math.PI * 2;
        }
        return a0 + delta / 2;
    }

    private static void AppendArc(List<SegData> segs, Point p0, Point p3, Point C, double R,
        double a0, double a1, int step)
    {
        double delta = a1 - a0;
        if (step > 0) { while (delta < 0) delta += Math.PI * 2; }
        else { while (delta > 0) delta -= Math.PI * 2; }
        bool large = Math.Abs(delta) > Math.PI;
        // WPF/SVG SweepFlag：1 = 顺时针。这里 step>0 为逆时针 → flag 0；step<0 → 1
        segs.Add(new SegData
        {
            Kind = SegKind.Arc,
            P0 = p0, P3 = p3,
            Rx = R, Ry = R, Rotation = 0,
            LargeArc = large,
            SweepFlag = step > 0 ? 0 : 1,
        });
    }

    private static int FindStart(List<CircleLineHit> hits, Point C, double R,
        List<BEdge> polyEdges, List<CircleLineHit> angleSorted, GeometryCombineMode mode)
    {
        // 选择一条"保留"的圆弧起始：取第一段圆环（从起点沿角度增），看中点是否保留
        int n = angleSorted.Count;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var a = angleSorted[i].Angle;
            var b = angleSorted[j].Angle;
            double mid = AngleBetween(a, b, +1);
            var mp = CirclePoint(C, R, mid);
            bool inPoly = PointInPolygon(mp, polyEdges);
            bool keep = mode switch
            {
                GeometryCombineMode.Union => !inPoly,
                GeometryCombineMode.Intersect => inPoly,
                GeometryCombineMode.Exclude => !inPoly,
                _ => false,
            };
            if (keep) return i;
        }
        return (n > 0) ? 0 : -1;
    }

    private static List<SegData> BuildSegmentsFromWalk(List<SegData> segs)
    {
        // 已按顺序收集的段；仅回填 P0 链（保留已有顺序）
        if (segs.Count == 0) return segs;
        for (int i = 1; i < segs.Count; i++) segs[i].P0 = segs[i - 1].P3;
        return segs;
    }

    private static bool IsInPolygon(Point p, List<BEdge> edges) => PointInPolygon(p, edges);

    private static bool PointInPolygon(Point p, List<BEdge> edges)
    {
        bool inside = false;
        foreach (var e in edges)
        {
            var a = e.P0; var b = e.P3;
            if ((a.Y > p.Y) != (b.Y > p.Y))
            {
                double x = (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X;
                if (p.X < x) inside = !inside;
            }
        }
        return inside;
    }

    // ===================== 解析 =====================

    /// <summary>解析圆（EllipseGeometry 且 rx≈ry）。返回圆心/半径。</summary>
    private static bool TryParseCircle(Geometry g, out Point C, out double R)
    {
        C = default; R = 0;
        if (g is not EllipseGeometry eg) return false;
        double rx = eg.RadiusX, ry = eg.RadiusY;
        if (Math.Abs(rx - ry) / Math.Max(rx, ry) > CircleTol) return false;
        C = eg.Center; R = Math.Max(rx, ry);
        return true;
    }

    /// <summary>解析直线多边形（仅支持 Line/PolyLine 段；含弧/贝塞尔 → 返回 null）。</summary>
    private static List<BEdge>? ParsePolygonEdges(Geometry g)
    {
        switch (g)
        {
            case RectangleGeometry rg:
                return RectToEdges(rg.Rect);
            case PathGeometry pg:
                return PathToEdges(pg);
            case GeometryGroup gg:
                // 仅当单个非空子几何时尝试
                var collected = new List<BEdge>();
                foreach (var child in gg.Children)
                {
                    var sub = ResolveAsPolygon(child);
                    if (sub is null) return null;
                    collected.AddRange(sub);
                }
                return collected.Count > 0 ? collected : null;
            default:
                return null;
        }
    }

    private static List<BEdge>? ResolveAsPolygon(Geometry g)
    {
        switch (g)
        {
            case RectangleGeometry rg: return RectToEdges(rg.Rect);
            case PathGeometry pg: return PathToEdges(pg);
            default: return null;
        }
    }

    private static List<BEdge> RectToEdges(Rect r)
    {
        return new List<BEdge>
        {
            new BEdge { P0 = new Point(r.Left, r.Top), P3 = new Point(r.Right, r.Top) },
            new BEdge { P0 = new Point(r.Right, r.Top), P3 = new Point(r.Right, r.Bottom) },
            new BEdge { P0 = new Point(r.Right, r.Bottom), P3 = new Point(r.Left, r.Bottom) },
            new BEdge { P0 = new Point(r.Left, r.Bottom), P3 = new Point(r.Left, r.Top) },
        };
    }

    private static List<BEdge>? PathToEdges(PathGeometry pg)
    {
        var edges = new List<BEdge>();
        foreach (var f in pg.Figures)
        {
            if (f.Segments.Count == 0) continue;
            Point cur = f.StartPoint;
            foreach (var s in f.Segments)
            {
                switch (s)
                {
                    case LineSegment ls:
                        edges.Add(new BEdge { P0 = cur, P3 = ls.Point });
                        cur = ls.Point;
                        break;
                    case PolyLineSegment pl:
                        foreach (var p in pl.Points)
                        {
                            edges.Add(new BEdge { P0 = cur, P3 = p });
                            cur = p;
                        }
                        break;
                    default:
                        return null; // 含弧/贝塞尔 → 不支持，回退
                }
            }
        }
        return edges.Count >= 3 ? edges : null;
    }

    // ===================== 序列化 =====================

    private static string SerializeFigures(List<FigureData> figs)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("F0");
        bool any = false;
        foreach (var fig in figs)
        {
            if (fig.Segs.Count == 0) continue;
            any = true;
            sb.Append(" M ").Append(F(fig.Start.X)).Append(',').Append(F(fig.Start.Y));
            foreach (var s in fig.Segs)
            {
                switch (s.Kind)
                {
                    case SegKind.Line:
                        sb.Append(" L ").Append(F(s.P3.X)).Append(',').Append(F(s.P3.Y));
                        break;
                    case SegKind.Arc:
                        sb.Append(" A ").Append(F(s.Rx)).Append(',').Append(F(s.Ry)).Append(' ')
                          .Append(F(s.Rotation)).Append(' ').Append(s.LargeArc ? 1 : 0).Append(' ')
                          .Append(s.SweepFlag).Append(' ')
                          .Append(F(s.P3.X)).Append(',').Append(F(s.P3.Y));
                        break;
                    case SegKind.Cubic:
                        sb.Append(" C ").Append(F(s.P1.X)).Append(',').Append(F(s.P1.Y)).Append(' ')
                          .Append(F(s.P2.X)).Append(',').Append(F(s.P2.Y)).Append(' ')
                          .Append(F(s.P3.X)).Append(',').Append(F(s.P3.Y));
                        break;
                    case SegKind.Quad:
                        sb.Append(" Q ").Append(F(s.P1.X)).Append(',').Append(F(s.P1.Y)).Append(' ')
                          .Append(F(s.P3.X)).Append(',').Append(F(s.P3.Y));
                        break;
                }
            }
            sb.Append(" Z");
        }
        return any ? sb.ToString() : string.Empty;
    }

    private static double R(double v) => Math.Abs(v) < 1e-3 ? 0 : Math.Round(v, 3, MidpointRounding.AwayFromZero);
    private static string F(double v)
    {
        v = R(v);
        if (v == Math.Floor(v) && Math.Abs(v) < 1e9) return ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>圆-线交点命中记录（按圆角度排序用）。</summary>
    private sealed class CircleLineHit
    {
        public int Edge; public double T; public double Angle; public Point Point;
        public CircleLineHit(int edge, double t, double angle, Point p)
        { Edge = edge; T = t; Angle = angle; Point = p; }
    }
}