using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace SymbolDesigner.Svg;

/// <summary>SVG &lt;path d="..."&gt; 解析 + 曲线降采样为折线段。
/// MVP 范围:M/L/H/V/C/S/Q/T/A/Z(全大写=绝对,小写=相对)。
/// 曲线段:三次/二次贝塞尔用 de Casteljau 平分降采样,弧段用 W3C SVG Implementation Notes 算法计算端点切线后采样。
/// 输出:(localPts, closed, subpaths)。
///   localPts = 所有采样点(已按 transform 转到画布坐标系,且按子路径最小 x/y 偏移归零)
///   closed   = 原路径是否以 Z/z 结尾(单子路径场景)
///   subpaths = 各子路径独立列表(多环 path 用于 BooleanPath)
/// </summary>
public static class PathDataSampler
{
    /// <summary>采样精度(画布单位)。SVG 路径经 viewBox 缩放后,弦高小于该值即停止采样。
    /// 0.5 对画布 160×96 是亚像素精度,曲线边缘看起来不会"锯齿"。</summary>
    public const double Flatness = 0.5;

    /// <summary>解析 SVG path "d" 字符串,输出画布坐标系下的采样点集。</summary>
    /// <param name="d">SVG path d 属性。</param>
    /// <param name="transform">SVG→画布坐标变换(等比缩放 + 居中偏移)。</param>
    public static (List<Point> localPts, bool closed, List<List<Point>> subpaths) Sample(string d, SvgParser.SvgTransform transform)
    {
        var subpathsRaw = new List<List<Point>>();
        var current = new List<Point>();
        Point startPoint = new Point(0, 0);
        Point lastControl = new Point(0, 0);   // S/s 反射 / T/t 反射用
        Point lastPoint = new Point(0, 0);
        bool lastWasCubic = false;
        bool lastWasQuadratic = false;
        bool subpathClosed = false;

        int i = 0;
        while (i < d.Length)
        {
            // 跳过空白
            while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
            if (i >= d.Length) break;

            char cmd = d[i];
            // 命令字母
            if (IsCommandLetter(cmd))
            {
                i++;
                // 重复同字母可省略(M 之后可继续 M 等)——这里简化:每条命令显式给字母
                bool relative = char.IsLower(cmd);
                char upper = char.ToUpperInvariant(cmd);
                var args = new List<double>();
                int maxArgs = ArgsForCommand(upper);
                while (args.Count < maxArgs && i < d.Length)
                {
                    while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
                    if (i >= d.Length) break;
                    int sign = 1;
                    if (d[i] == '-') { sign = -1; i++; }
                    else if (d[i] == '+') { i++; }
                    int start = i;
                    while (i < d.Length && (char.IsDigit(d[i]) || d[i] == '.')) i++;
                    if (start == i) break;  // 没读到数字,跳出参数解析
                    if (double.TryParse(d.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        args.Add(sign * v);
                    else
                        break;
                }
                // 多次重复执行该命令(参数个数对齐)
                int argsPerRep = maxArgs;
                if (maxArgs == 0)
                {
                    // Z/z 无参数
                    ExecuteCommand(upper, relative, args, ref lastPoint, ref startPoint, ref lastControl,
                        ref lastWasCubic, ref lastWasQuadratic, ref subpathClosed, current, subpathsRaw, transform);
                }
                else
                {
                    int idx = 0;
                    while (idx + argsPerRep <= args.Count)
                    {
                        var rep = args.GetRange(idx, argsPerRep);
                        ExecuteCommand(upper, relative, rep, ref lastPoint, ref startPoint, ref lastControl,
                            ref lastWasCubic, ref lastWasQuadratic, ref subpathClosed, current, subpathsRaw, transform);
                        idx += argsPerRep;
                    }
                }
            }
            else
            {
                // 隐式延续上一命令:数字开头 → 当作 L 处理(按 SVG spec,首次出现 M 后数字序列默为 L,否则默为上条命令)
                // 简化处理:遇到非命令字符直接跳过(防呆)
                i++;
            }
        }
        // 收尾:把 current 加到 subpathsRaw
        if (current.Count > 0)
            subpathsRaw.Add(current);
        // 计算全局 localPts 和 subpaths(每条子路径独立)
        var localSubpaths = new List<List<Point>>();
        double minX = double.MaxValue, minY = double.MaxValue;
        foreach (var sub in subpathsRaw)
        {
            foreach (var p in sub)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
            }
        }
        if (minX == double.MaxValue) minX = 0;
        if (minY == double.MaxValue) minY = 0;
        var localAll = new List<Point>();
        foreach (var sub in subpathsRaw)
        {
            var sub2 = new List<Point>(sub.Count);
            foreach (var p in sub)
            {
                var lp = new Point(p.X - minX, p.Y - minY);
                sub2.Add(lp);
                localAll.Add(lp);
            }
            localSubpaths.Add(sub2);
        }
        return (localAll, subpathClosed, localSubpaths);
    }

    private static bool IsCommandLetter(char c)
    {
        switch (char.ToUpperInvariant(c))
        {
            case 'M': case 'L': case 'H': case 'V':
            case 'C': case 'S': case 'Q': case 'T':
            case 'A': case 'Z':
                return true;
        }
        return false;
    }

    private static int ArgsForCommand(char upper)
    {
        switch (upper)
        {
            case 'Z': return 0;
            case 'H': case 'V': return 1;
            case 'M': case 'L': case 'T': return 2;
            case 'Q': case 'S': return 4;
            case 'C': return 6;
            case 'A': return 7;
        }
        return 0;
    }

    private static void ExecuteCommand(
        char upper, bool relative,
        List<double> args,
        ref Point lastPoint, ref Point startPoint, ref Point lastControl,
        ref bool lastWasCubic, ref bool lastWasQuadratic, ref bool closed,
        List<Point> current, List<List<Point>> subpathsRaw,
        SvgParser.SvgTransform t)
    {
        switch (upper)
        {
            case 'M':
            {
                var p = new Point(args[0], args[1]);
                if (relative) { p.X += lastPoint.X; p.Y += lastPoint.Y; }
                // M 之后落笔:current 非空 → 上一个子路径结束,提交
                if (current.Count > 0) subpathsRaw.Add(current);
                current = new List<Point>();
                current.Add(t.Apply(p));
                startPoint = p;
                lastPoint = p;
                lastWasCubic = false; lastWasQuadratic = false;
                break;
            }
            case 'L':
            {
                var p = new Point(args[0], args[1]);
                if (relative) { p.X += lastPoint.X; p.Y += lastPoint.Y; }
                current.Add(t.Apply(p));
                lastPoint = p;
                lastWasCubic = false; lastWasQuadratic = false;
                break;
            }
            case 'H':
            {
                var p = new Point(args[0], lastPoint.Y);
                if (relative) p.X += lastPoint.X;
                current.Add(t.Apply(p));
                lastPoint = p;
                lastWasCubic = false; lastWasQuadratic = false;
                break;
            }
            case 'V':
            {
                var p = new Point(lastPoint.X, args[0]);
                if (relative) p.Y += lastPoint.Y;
                current.Add(t.Apply(p));
                lastPoint = p;
                lastWasCubic = false; lastWasQuadratic = false;
                break;
            }
            case 'C':
            {
                var c1 = new Point(args[0], args[1]);
                var c2 = new Point(args[2], args[3]);
                var p = new Point(args[4], args[5]);
                if (relative) { c1.X += lastPoint.X; c1.Y += lastPoint.Y; c2.X += lastPoint.X; c2.Y += lastPoint.Y; p.X += lastPoint.X; p.Y += lastPoint.Y; }
                SampleCubic(lastPoint, c1, c2, p, current, t);
                lastControl = c2; lastPoint = p;
                lastWasCubic = true; lastWasQuadratic = false;
                break;
            }
            case 'S':
            {
                // 反射控制点 = lastPoint + (lastPoint - lastControl)
                Point c1 = lastWasCubic
                    ? new Point(2 * lastPoint.X - lastControl.X, 2 * lastPoint.Y - lastControl.Y)
                    : lastPoint;
                var c2 = new Point(args[0], args[1]);
                var p = new Point(args[2], args[3]);
                if (relative) { c2.X += lastPoint.X; c2.Y += lastPoint.Y; p.X += lastPoint.X; p.Y += lastPoint.Y; }
                SampleCubic(lastPoint, c1, c2, p, current, t);
                lastControl = c2; lastPoint = p;
                lastWasCubic = true; lastWasQuadratic = false;
                break;
            }
            case 'Q':
            {
                var c = new Point(args[0], args[1]);
                var p = new Point(args[2], args[3]);
                if (relative) { c.X += lastPoint.X; c.Y += lastPoint.Y; p.X += lastPoint.X; p.Y += lastPoint.Y; }
                SampleQuadratic(lastPoint, c, p, current, t);
                lastControl = c; lastPoint = p;
                lastWasCubic = false; lastWasQuadratic = true;
                break;
            }
            case 'T':
            {
                Point c = lastWasQuadratic
                    ? new Point(2 * lastPoint.X - lastControl.X, 2 * lastPoint.Y - lastControl.Y)
                    : lastPoint;
                var p = new Point(args[0], args[1]);
                if (relative) { p.X += lastPoint.X; p.Y += lastPoint.Y; }
                SampleQuadratic(lastPoint, c, p, current, t);
                lastControl = c; lastPoint = p;
                lastWasCubic = false; lastWasQuadratic = true;
                break;
            }
            case 'A':
            {
                double rx = args[0], ry = args[1];
                double xAxisRotation = args[2];
                int largeArc = (int)args[3];
                int sweep = (int)args[4];
                var p = new Point(args[5], args[6]);
                if (relative) { p.X += lastPoint.X; p.Y += lastPoint.Y; }
                SampleArc(lastPoint, p, rx, ry, xAxisRotation, largeArc != 0, sweep != 0, current, t);
                lastPoint = p;
                lastWasCubic = false; lastWasQuadratic = false;
                break;
            }
            case 'Z':
            {
                if (current.Count > 0 && (current[^1].X != startPoint.X || current[^1].Y != startPoint.Y))
                    current.Add(t.Apply(startPoint));
                if (current.Count > 0) subpathsRaw.Add(current);
                current = new List<Point>();
                lastPoint = startPoint;
                closed = true;  // 全局 closed 标志(单子路径路径下用)
                lastWasCubic = false; lastWasQuadratic = false;
                break;
            }
        }
    }

    // ─── 曲线降采样 ─────────────────────────────────────────────

    /// <summary>三次贝塞尔曲线采样(自适应细分,弦高 &lt;= Flatness)。
    /// 实现:de Casteljau 切分 + 递归,直到线段弦高满足精度。</summary>
    private static void SampleCubic(Point p0, Point p1, Point p2, Point p3,
        List<Point> outList, SvgParser.SvgTransform t)
    {
        // 初始:加端点 p3(起点已在 outList 中)
        SampleCubicRec(p0, p1, p2, p3, 0, outList, t);
    }

    private static void SampleCubicRec(Point p0, Point p1, Point p2, Point p3, int depth,
        List<Point> outList, SvgParser.SvgTransform t)
    {
        if (depth > 18 || CubicFlatEnough(p0, p1, p2, p3))
        {
            outList.Add(t.Apply(p3));
            return;
        }
        // de Casteljau 切分
        var p01 = Mid(p0, p1);
        var p12 = Mid(p1, p2);
        var p23 = Mid(p2, p3);
        var p012 = Mid(p01, p12);
        var p123 = Mid(p12, p23);
        var p0123 = Mid(p012, p123);
        SampleCubicRec(p0, p01, p012, p0123, depth + 1, outList, t);
        SampleCubicRec(p0123, p123, p23, p3, depth + 1, outList, t);
    }

    private static bool CubicFlatEnough(Point p0, Point p1, Point p2, Point p3)
    {
        // 弦高近似:控制点 p1/p2 到直线 p0-p3 的距离
        double dx = p3.X - p0.X, dy = p3.Y - p0.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return true;
        // |(p1-p0) × (p3-p0)| / |p3-p0|
        double n1 = System.Math.Abs((p1.X - p0.X) * dy - (p1.Y - p0.Y) * dx);
        double n2 = System.Math.Abs((p2.X - p0.X) * dy - (p2.Y - p0.Y) * dx);
        double chord = System.Math.Sqrt(len2);
        double h1 = n1 / chord;
        double h2 = n2 / chord;
        // 与画布单位的精度对齐:这里还没乘 Scale,所以误差容差乘 Scale 反向换算。
        // 简化:用 Flatness 作为原始 SVG 空间精度(因为 chord 也是 SVG 单位,误差容差统一)
        double tol = Flatness;
        return h1 <= tol && h2 <= tol;
    }

    private static void SampleQuadratic(Point p0, Point p1, Point p2,
        List<Point> outList, SvgParser.SvgTransform t)
    {
        SampleQuadraticRec(p0, p1, p2, 0, outList, t);
    }

    private static void SampleQuadraticRec(Point p0, Point p1, Point p2, int depth,
        List<Point> outList, SvgParser.SvgTransform t)
    {
        if (depth > 18 || QuadraticFlatEnough(p0, p1, p2))
        {
            outList.Add(t.Apply(p2));
            return;
        }
        var p01 = Mid(p0, p1);
        var p12 = Mid(p1, p2);
        var p012 = Mid(p01, p12);
        SampleQuadraticRec(p0, p01, p012, depth + 1, outList, t);
        SampleQuadraticRec(p012, p12, p2, depth + 1, outList, t);
    }

    private static bool QuadraticFlatEnough(Point p0, Point p1, Point p2)
    {
        double dx = p2.X - p0.X, dy = p2.Y - p0.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return true;
        double n = System.Math.Abs((p1.X - p0.X) * dy - (p1.Y - p0.Y) * dx);
        double h = n / System.Math.Sqrt(len2);
        return h <= Flatness;
    }

    /// <summary>椭圆弧段采样。按 W3C SVG Implementation Notes 把弧转换为圆心参数后,等分角度采样。
    /// 实现细节见 https://www.w3.org/TR/SVG/implnote.html#ArcImplementationNotes(Endpoint to center conversion)。</summary>
    private static void SampleArc(Point p1, Point p2, double rx, double ry, double xAxisRotationDeg,
        bool largeArc, bool sweep, List<Point> outList, SvgParser.SvgTransform t)
    {
        if (rx == 0 || ry == 0)
        {
            // 退化为直线
            outList.Add(t.Apply(p2));
            return;
        }
        rx = System.Math.Abs(rx);
        ry = System.Math.Abs(ry);

        double phi = xAxisRotationDeg * System.Math.PI / 180.0;
        double cosPhi = System.Math.Cos(phi), sinPhi = System.Math.Sin(phi);

        // Step 1: Compute (x1', y1')
        double dx = (p1.X - p2.X) / 2;
        double dy = (p1.Y - p2.Y) / 2;
        double x1p = cosPhi * dx + sinPhi * dy;
        double y1p = -sinPhi * dx + cosPhi * dy;

        // Step 2: 确保半径足够大(否则放大)
        double lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
        if (lam > 1) { double s = System.Math.Sqrt(lam); rx *= s; ry *= s; }

        // Step 3: Compute (cx', cy')
        double sign = (largeArc == sweep) ? -1 : 1;
        double sq = System.Math.Max(0, (rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p)
            / (rx * rx * y1p * y1p + ry * ry * x1p * x1p));
        double coef = sign * System.Math.Sqrt(sq);
        double cxp = coef * (rx * y1p / ry);
        double cyp = coef * -(ry * x1p / rx);

        // Step 4: Compute (cx, cy) in original coordinate system
        double cx = cosPhi * cxp - sinPhi * cyp + (p1.X + p2.X) / 2;
        double cy = sinPhi * cxp + cosPhi * cyp + (p1.Y + p2.Y) / 2;

        double angle1 = VectorAngle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double angleDelta = VectorAngle((x1p - cxp) / rx, (y1p - cyp) / ry,
                                        (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && angleDelta > 0) angleDelta -= 2 * System.Math.PI;
        else if (sweep && angleDelta < 0) angleDelta += 2 * System.Math.PI;

        // Step 5: 按角度等分采样。步长按半径与精度换算:每段弧长 ≈ Flatness → 角度 ≈ Flatness / min(rx, ry)
        double arcRadius = System.Math.Min(rx, ry);
        int segments = (int)System.Math.Ceiling(System.Math.Abs(angleDelta) * arcRadius / Flatness);
        if (segments < 1) segments = 1;
        if (segments > 256) segments = 256;  // 安全上限,避免病态 SVG 卡顿
        for (int s = 1; s <= segments; s++)
        {
            double theta = angle1 + angleDelta * s / segments;
            double cosT = System.Math.Cos(theta), sinT = System.Math.Sin(theta);
            double x = cosPhi * rx * cosT - sinPhi * ry * sinT + cx;
            double y = sinPhi * rx * cosT + cosPhi * ry * sinT + cy;
            outList.Add(t.Apply(new Point(x, y)));
        }
    }

    private static double VectorAngle(double ux, double uy, double vx, double vy)
    {
        double dot = ux * vx + uy * vy;
        double len = System.Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        double ang = System.Math.Acos(System.Math.Max(-1, System.Math.Min(1, dot / len)));
        if ((ux * vy - uy * vx) < 0) ang = -ang;
        return ang;
    }

    private static Point Mid(Point a, Point b) => new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
}
