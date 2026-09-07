using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SymbolDesigner.Models;

namespace SymbolDesigner.Converters;

/// <summary>布尔结果快照的段类型。</summary>
public enum SegKind
{
    Line,
    Arc,
    Cubic,   // 三次贝塞尔
    Quad,    // 二次贝塞尔
}

/// <summary>布尔结果快照的图元（一个闭合环）。段之间链式连接：段 0 起点 = Start，段 i 起点 = 段 i-1 终点。</summary>
public sealed class FigureData
{
    /// <summary>环起点（图元 M 坐标），同时也是最后一段的终点（闭合）。</summary>
    public Point Start;
    /// <summary>闭合环节点序列（按绘制顺序）。</summary>
    public List<SegData> Segs = new();
}

/// <summary>布尔结果快照中的一段轮廓。
/// 弧段保留完整参数（终点 + 半径 + 大弧标志 + 扫掠方向），不摊平——渲染时 Geometry.Parse 原生画弧。</summary>
public sealed class SegData
{
    /// <summary>本段起点（链式推导，编辑拖动后由调用方同步为前段终点）。</summary>
    public Point P0;
    /// <summary>控制点 1（Cubic: 起侧控制点；Quad: 控制点；Line/Arc: 不使用）。</summary>
    public Point P1;
    /// <summary>控制点 2（仅 Cubic 使用）。</summary>
    public Point P2;
    /// <summary>终点（本段在骨架中的"顶点"）。</summary>
    public Point P3;

    public SegKind Kind = SegKind.Line;

    // 弧专用参数（Kind=Arc）
    public double Rx, Ry;
    public double Rotation;
    public bool LargeArc;
    /// <summary>mini-language 扫掠标志：0=逆时针(Counterclockwise)，1=顺时针(Clockwise)。</summary>
    public int SweepFlag;

    public Point End { get => P3; set => P3 = value; }
}

/// <summary>
/// 图元布尔运算：对两个"闭合可填充图元"执行 合并(Union) / 剪除(Exclude) / 相交(Intersect)。
/// 结果整理为"段结构快照 PathData"（局部坐标 mini-language：M/L/A/C/Q/Z + F0 填充规则前缀）。
///
/// 关键设计——保留真弧，不摊平：
/// Geometry.Combine 的 PathGeometry 原样保留各段类型（圆/椭圆边界 = ArcSegment/BezierSegment），
/// 快照 PathData 直接可被 Geometry.Parse 原生渲染（弧 = "终点+半径+大弧标志+扫掠方向"，零中间点）。
/// 骨架点（可编辑顶点）= 每环"起点 + 各段终点"（圆∩矩形就那几个角点），拖动只改相邻段端点；
/// 轮廓圆角 = 渲染期把 L-L 尖角烘焙成 A 命令真圆弧（BakeCornerRadii），半径存
/// VertexBandWidths[].LeftCornerRadius（与闭合折线同一存储）。
/// 快照可直接作为下一次布尔的输入（再次 Geometry.Parse + Combine）。
/// </summary>
public static class BooleanOps
{
    /// <summary>运算容差（画布逻辑坐标，与厚度/圆角精度同量级）。</summary>
    public const double Tolerance = 0.05;

    /// <summary>近重合合并阈值：Combine 在切点处把同一交点算成两个数值不等的端点，圆被采样为贝塞尔后
    /// 求交误差可达 ~0.46 逻辑单位（实测 矩形∩圆 = 0.458）；小于此值的相邻点视为同一点并合并。
    /// 有效消除"剪除/相交后轮廓点一堆近重合点 + 微段"的视觉问题。</summary>
    public const double MergeTol = 0.5;

    /// <summary>坐标序列化精度（3 位小数足够画布粒度，避免快照字符串膨胀）。</summary>
    private const double CoordEps = 1e-3;

    /// <summary>布尔运算结果快照：段结构 PathData（M/L/A/C/Q/Z + F0 前缀）+ 包围盒（局部坐标）。</summary>
    public sealed record BooleanResult(string PathData, double X, double Y, double Width, double Height);

    /// <summary>
    /// 对两个画布坐标填充几何执行布尔运算，整理为局部坐标段结构快照（弧段保留，不摊平）。
    /// 两几何不相交 / 结果无面积（如不相交的 Intersect）→ 返回 null（调用方拒绝/提示）。
    /// a/b 必须是画布坐标系（调用方自行平移 X/Y，见 PrimitiveItem.CanvasFillGeometry）。</summary>
    public static BooleanResult? Compute(Geometry a, Geometry b, GeometryCombineMode mode)
    {
        var combined = Geometry.Combine(a, b, mode, null, Tolerance, ToleranceType.Absolute);
        if (combined is null || combined.IsEmpty() || combined.Bounds.IsEmpty) return null;
        var bounds = combined.Bounds;

        double ox = bounds.Left, oy = bounds.Top;
        string data = SerializeFigures(ParsePathGeometry(combined, ox, oy));
        if (string.IsNullOrEmpty(data)) return null;

        return new BooleanResult(
            data,
            Round(bounds.Left),
            Round(bounds.Top),
            Math.Max(0.01, Round(bounds.Width)),
            Math.Max(0.01, Round(bounds.Height)));
    }

    /// <summary>把 Combine 的 PathGeometry 转成段结构（坐标转为局部：减去 ox/oy；跳过零长度直线）。</summary>
    private static List<FigureData> ParsePathGeometry(PathGeometry g, double ox, double oy)
    {
        var figs = new List<FigureData>(g.Figures.Count);
        foreach (var f in g.Figures)
        {
            var fig = new FigureData { Start = new Point(f.StartPoint.X - ox, f.StartPoint.Y - oy) };
            Point cur = f.StartPoint;
            foreach (var seg in f.Segments)
            {
                switch (seg)
                {
                    case LineSegment ls:
                        if (!Close(cur, ls.Point))
                            fig.Segs.Add(MakeLine(cur.X - ox, cur.Y - oy, ls.Point.X - ox, ls.Point.Y - oy));
                        cur = ls.Point;
                        break;
                    case PolyLineSegment pls:
                        foreach (var p in pls.Points)
                        {
                            if (!Close(cur, p))
                                fig.Segs.Add(MakeLine(cur.X - ox, cur.Y - oy, p.X - ox, p.Y - oy));
                            cur = p;
                        }
                        break;
                    case ArcSegment af:
                        fig.Segs.Add(new SegData
                        {
                            Kind = SegKind.Arc,
                            P3 = new Point(af.Point.X - ox, af.Point.Y - oy),
                            Rx = af.Size.Width,
                            Ry = af.Size.Height,
                            Rotation = af.RotationAngle,
                            LargeArc = af.IsLargeArc,
                            SweepFlag = af.SweepDirection == SweepDirection.Clockwise ? 1 : 0,
                        });
                        cur = af.Point;
                        break;
                    case BezierSegment bz:
                        fig.Segs.Add(new SegData
                        {
                            Kind = SegKind.Cubic,
                            P1 = new Point(bz.Point1.X - ox, bz.Point1.Y - oy),
                            P2 = new Point(bz.Point2.X - ox, bz.Point2.Y - oy),
                            P3 = new Point(bz.Point3.X - ox, bz.Point3.Y - oy),
                        });
                        cur = bz.Point3;
                        break;
                    case QuadraticBezierSegment qz:
                        fig.Segs.Add(new SegData
                        {
                            Kind = SegKind.Quad,
                            P1 = new Point(qz.Point1.X - ox, qz.Point1.Y - oy),
                            P3 = new Point(qz.Point2.X - ox, qz.Point2.Y - oy),
                        });
                        cur = qz.Point2;
                        break;
                }
            }
            if (fig.Segs.Count > 0) figs.Add(fig);
        }
        return figs;
    }

    private static SegData MakeLine(double x0, double y0, double x1, double y1) => new()
    {
        Kind = SegKind.Line,
        P0 = new Point(x0, y0),
        P3 = new Point(x1, y1),
    };

    /// <summary>解析段结构快照 → 图元列表（链式填充各段 P0）。跳过 F0/F1 前缀；无图元返回空列表。</summary>
    public static List<FigureData> ParseFigures(string pathData)
    {
        var figs = new List<FigureData>();
        if (string.IsNullOrWhiteSpace(pathData)) return figs;

        // 分词：命令字母 / 数字（"-12.5,3" 以逗号拆开；字母与数字可能粘连）
        var toks = new List<string>();
        int i = 0, n = pathData.Length;
        while (i < n)
        {
            char c = pathData[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsLetter(c)) { toks.Add(c.ToString()); i++; continue; }
            int start = i;
            while (i < n && !char.IsWhiteSpace(pathData[i]) && !char.IsLetter(pathData[i])) i++;
            foreach (var part in pathData.Substring(start, i - start).Split(','))
            {
                if (part.Length > 0) toks.Add(part);
            }
        }

        FigureData? fig = null;
        char cmd = '\0';
        var buf = new List<double>(7);
        foreach (var tok in toks)
        {
            if (tok.Length == 1 && char.IsLetter(tok[0]))
            {
                cmd = tok[0];
                buf.Clear();
                continue;
            }
            if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            int need = chNeed(cmd);
            if (cmd is '\0' or 'F' or 'Z') { continue; }  // 前缀 F0 的数字直接忽略
            buf.Add(v);
            if (buf.Count == need)
            {
                ApplyFig(ref fig, figs, cmd, buf);
                buf.Clear();
            }
        }
        return figs;
    }

    private static int chNeed(char c) => c switch
    {
        'M' => 2, 'L' => 2, 'A' => 7, 'C' => 6, 'Q' => 4, 'Z' => 0, _ => 0,
    };

    /// <summary>用累积的参数构建图元/段。每次调用前 cmd 必须有足够参数（buf 已满足 chNeed）。</summary>
    private static void ApplyFig(ref FigureData? fig, List<FigureData> figs, char cmd, List<double> b)
    {
        switch (cmd)
        {
            case 'M':
                fig = new FigureData { Start = new Point(b[0], b[1]) };
                figs.Add(fig);
                break;
            case 'L':
                if (fig is null) break;
                fig.Segs.Add(MakeLine(fig.Segs.Count == 0 ? fig.Start.X : fig.Segs[^1].End.X,
                                     fig.Segs.Count == 0 ? fig.Start.Y : fig.Segs[^1].End.Y,
                                     b[0], b[1]));
                break;
            case 'A':
                if (fig is null) break;
                fig.Segs.Add(new SegData
                {
                    Kind = SegKind.Arc,
                    P3 = new Point(b[5], b[6]),
                    Rx = b[0], Ry = b[1], Rotation = b[2],
                    LargeArc = b[3] > 0.5,
                    SweepFlag = b[4] > 0.5 ? 1 : 0,
                });
                break;
            case 'C':
                if (fig is null) break;
                fig.Segs.Add(new SegData
                {
                    Kind = SegKind.Cubic,
                    P1 = new Point(b[0], b[1]),
                    P2 = new Point(b[2], b[3]),
                    P3 = new Point(b[4], b[5]),
                });
                break;
            case 'Q':
                if (fig is null) break;
                fig.Segs.Add(new SegData
                {
                    Kind = SegKind.Quad,
                    P1 = new Point(b[0], b[1]),
                    P3 = new Point(b[2], b[3]),
                });
                break;
        }
    }

    /// <summary>图元列表 → 段结构快照（F0 前缀 + 每环 M/段.../Z）。无视 P0，链由 M 起点 + 各段终点构成。</summary>
    public static string SerializeFigures(List<FigureData> figs)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("F0");
        bool any = false;
        foreach (var fig in figs)
        {
            if (fig.Segs.Count == 0) continue;
            any = true;
            sb.Append(" M ").Append(Fmt(fig.Start.X)).Append(',').Append(Fmt(fig.Start.Y));
            foreach (var s in fig.Segs)
            {
                switch (s.Kind)
                {
                    case SegKind.Line:
                        sb.Append(" L ").Append(Fmt(s.End.X)).Append(',').Append(Fmt(s.End.Y));
                        break;
                    case SegKind.Arc:
                        sb.Append(" A ").Append(Fmt(s.Rx)).Append(',').Append(Fmt(s.Ry)).Append(' ')
                          .Append(Fmt(s.Rotation)).Append(' ').Append(s.LargeArc ? 1 : 0).Append(' ')
                          .Append(s.SweepFlag).Append(' ')
                          .Append(Fmt(s.End.X)).Append(',').Append(Fmt(s.End.Y));
                        break;
                    case SegKind.Cubic:
                        sb.Append(" C ").Append(Fmt(s.P1.X)).Append(',').Append(Fmt(s.P1.Y)).Append(' ')
                          .Append(Fmt(s.P2.X)).Append(',').Append(Fmt(s.P2.Y)).Append(' ')
                          .Append(Fmt(s.End.X)).Append(',').Append(Fmt(s.End.Y));
                        break;
                    case SegKind.Quad:
                        sb.Append(" Q ").Append(Fmt(s.P1.X)).Append(',').Append(Fmt(s.P1.Y)).Append(' ')
                          .Append(Fmt(s.End.X)).Append(',').Append(Fmt(s.End.Y));
                        break;
                }
            }
            sb.Append(" Z");
        }
        return any ? sb.ToString() : string.Empty;
    }

    /// <summary>骨架点（可编辑顶点）= 每环"起点 + 各段终点"，相邻近重合点（≤ MergeTol）合并、首尾重合去重。
    /// 返回顺序 = 环出现顺序，环内 = 骨架点顺序（平铺即 VertexBandWidths 半径的存储序）。
    /// 环点数不足 2（退化）丢弃。</summary>
    public static List<List<Point>> GetSkeletonRings(string pathData)
    {
        var rings = new List<List<Point>>();
        foreach (var fig in ParseFigures(pathData))
        {
            var dedup = new List<Point>();
            foreach (var p in RingPoints(fig))
            {
                if (dedup.Count == 0 || DistSq(dedup[^1], p) > MergeTol * MergeTol) dedup.Add(p);
            }
            if (dedup.Count > 1 && DistSq(dedup[^1], dedup[0]) <= MergeTol * MergeTol) dedup.RemoveAt(dedup.Count - 1);
            if (dedup.Count >= 2) rings.Add(dedup);
        }
        return rings;
    }

    /// <summary>全图元骨架点总数（VertexBandWidths 同步用）。</summary>
    public static int GetSkeletonCount(string pathData)
    {
        int count = 0;
        foreach (var ring in GetSkeletonRings(pathData)) count += ring.Count;
        return count;
    }

    /// <summary>单环点序列（Start + 各段终点）。</summary>
    private static List<Point> RingPoints(FigureData fig)
    {
        var pts = new List<Point>(fig.Segs.Count + 1) { fig.Start };
        foreach (var s in fig.Segs) pts.Add(s.End);
        return pts;
    }

    /// <summary>渲染期圆角烘焙：把 L-L 尖角替换为 A 命令真圆弧（半径 = radii[骨架平铺索引]）。
    /// 只圆角"两邻段都是直线"的角；邻段含弧/贝塞尔（曲线连续）时天然平滑，跳过。
    /// styles 按同一平铺索引提供 per-vertex 样式（null = 全部视为 Round，兼容旧调用）：
    /// 仅 Round 烘焙圆角；Miter/Bevel → 该角保持尖角不烘焙（布尔轮廓点样式语义）。
    /// 切线偏移超出相邻边长时按比例缩小（防自交）。纯函数：不修改入参。</summary>
    public static string BakeCornerRadii(string pathData, IReadOnlyList<double> radii, IReadOnlyList<JoinStyle>? styles = null)
    {
        var figs = ParseFigures(pathData);
        ResyncStarts(figs);
        int flat = 0;
        foreach (var fig in figs)
        {
            var segs = fig.Segs;
            int m = segs.Count;
            // 角(k 段索引)→骨架索引映射：角 k 的顶点 = (k==0? Start : segs[k].P0)；
            // 相邻顶点近重合（≤ MergeTol）合并为同一骨架点（与 GetSkeletonRings 同策略），
            // 保证 radii/styles 按合并后的骨架序号索引，不发生错位。
            var skelMap = new int[m];
            int skel = -1;
            Point lastKeep = default;
            for (int k = 0; k < m; k++)
            {
                var P = k == 0 ? fig.Start : segs[k].P0;
                if (k == 0 || DistSq(lastKeep, P) > MergeTol * MergeTol) { skel++; lastKeep = P; }
                skelMap[k] = skel;
            }
            int ringSkel = skel + 1;  // 本环骨架点数 = 合并后顶点数
            List<(int k, double len, double r, int flag)>? rounds = null;
            for (int k = 0; k < m; k++)
            {
                var sp = k == 0 ? segs[m - 1] : segs[k - 1];
                var sc = segs[k];
                if (sp.Kind != SegKind.Line || sc.Kind != SegKind.Line) continue;
                // 样式门控：Miter/Bevel 尖角不烘焙（styles 为空 = 兼容旧调用，全部按 Round）
                int si = flat + skelMap[k];
                if (styles != null)
                {
                    if (si >= styles.Count) continue;
                    if (styles[si] != JoinStyle.Round) continue;
                }
                double r = (si < radii.Count) ? radii[si] : 0;
                if (r <= 0) continue;
                var P = k == 0 ? fig.Start : sc.P0;
                var inD = P - sp.P0;
                var outD = sc.End - P;
                inD.Normalize();
                outD.Normalize();
                if (inD.Length < 1e-9 || outD.Length < 1e-9) continue;
                double det = Vector.Determinant(inD, outD);
                if (Math.Abs(det) < 1e-9) continue;  // 共线退化为直线，不圆角（与 MakeNormalArcStep 一致）
                double sinHalf = Math.Sqrt(Math.Max(0, (1.0 - Vector.Multiply(-inD, outD)) / 2.0));
                double cosHalf = sinHalf < 1e-9 ? 0 : Math.Sqrt(Math.Max(0, 1.0 - sinHalf * sinHalf));
                double len = (sinHalf < 1e-9 || cosHalf <= 0) ? r : r * cosHalf / sinHalf;
                double legA = (sp.P0 - P).Length, legB = (sc.End - P).Length;
                double maxLen = Math.Min(legA, legB) * 0.95;
                if (maxLen > 0 && len > maxLen && cosHalf > 0)
                {
                    len = maxLen;
                    r = len * sinHalf / cosHalf;  // 缩小半径保持切点处相切
                }
                (rounds ??= new List<(int, double, double, int)>())
                    .Add((k, len, Math.Max(CoordEps, r), det < 0 ? 0 : 1));
            }
            if (rounds is { Count: > 0 })
            {
                // 从后往前插入弧，避免索引位移
                for (int j = rounds.Count - 1; j >= 0; j--)
                {
                    (int k, double len, double r, int flag) = rounds[j];
                    var sp = k == 0 ? segs[m - 1] : segs[k - 1];
                    var sc = segs[k];
                    var P = k == 0 ? fig.Start : sc.P0;
                    var inD = P - sp.P0;
                    var outD = sc.End - P;
                    inD.Normalize();
                    outD.Normalize();
                    var arcStart = P - inD * len;
                    var arcEnd = P + outD * len;
                    sp.End = arcStart;
                    if (k == 0) fig.Start = arcStart;  // M 随闭合链起点（= 首段起点）移动
                    sc.P0 = arcEnd;
                    var arc = new SegData
                    {
                        Kind = SegKind.Arc,
                        P0 = arcStart,
                        P3 = arcEnd,
                        Rx = r, Ry = r,
                        Rotation = 0,
                        LargeArc = false,
                        SweepFlag = flag,
                    };
                    segs.Insert(k == 0 ? 0 : k, arc);
                }
            }
            flat += ringSkel;  // 骨架点数 = 合并近重合后的顶点数（与 GetSkeletonRings / 半径数组长度一致）
        }
        return SerializeFigures(figs);
    }

    /// <summary>整体平移段结构快照（包围盒重算后归零原点）。</summary>
    public static string Translate(string pathData, double dx, double dy)
    {
        var figs = ParseFigures(pathData);
        ResyncStarts(figs);
        foreach (var fig in figs)
        {
            fig.Start = new Point(fig.Start.X + dx, fig.Start.Y + dy);
            foreach (var s in fig.Segs)
            {
                s.P0 = new Point(s.P0.X + dx, s.P0.Y + dy);
                s.P1 = new Point(s.P1.X + dx, s.P1.Y + dy);
                s.P2 = new Point(s.P2.X + dx, s.P2.Y + dy);
                s.P3 = new Point(s.P3.X + dx, s.P3.Y + dy);
            }
        }
        return SerializeFigures(figs);
    }

    /// <summary>链式同步各段 P0 = 前段终点（首段 = fig.Start）。编辑/烘焙前调用保证角度计算不漂移。</summary>
    private static void ResyncStarts(List<FigureData> figs)
    {
        foreach (var fig in figs)
        {
            Point cur = fig.Start;
            foreach (var s in fig.Segs)
            {
                s.P0 = cur;
                cur = s.End;
            }
        }
    }

    // ===================== 基础 =====================

    /// <summary>近重合判定（Combine 输出数值噪声上界 ≤ MergeTol）：相邻点距离 &lt; MergeTol 视为同一点。
    /// 仅用于 Combine 结果的段过滤（ParsePathGeometry）；快照回读（ParseFigures）走 CoordEps。</summary>
    private static bool Close(Point p, Point q) => DistSq(p, q) <= MergeTol * MergeTol;

    private static double DistSq(Point p, Point q)
        => (p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y);

    private static double Round(double v)
    {
        if (Math.Abs(v) < CoordEps) return 0;
        return Math.Round(v, 3, MidpointRounding.AwayFromZero);
    }

    private static string Fmt(double v)
    {
        v = Round(v);
        if (v == Math.Floor(v) && Math.Abs(v) < 1e9)
            return ((long)v).ToString(CultureInfo.InvariantCulture);
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}