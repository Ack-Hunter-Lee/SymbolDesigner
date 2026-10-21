using System;
using System.Collections.Generic;
using System.Windows;

namespace SymbolDesigner.Converters;

/// <summary>
/// 沿路径（直线段 + 圆弧段）等弧长采样 N 个放置点（位置 + 朝向）的算法库。
///
/// 设计目标（POC 阶段）：
///   1. 与 WPF PathGeometry 解耦 —— 用内部 PathSegment 结构，POC 阶段独立单测
///   2. 支持两种段型：Line（直线段）/ Arc（圆弧段）—— 同 CAD Path 语义
///   3. 等弧长采样：副本之间沿路径的弧长间隔相等（与"画笔笔触等距"概念一致）
///   4. 朝向 = 段切线方向（X 轴正向 = 0°，逆时针为正），便于后面让副本"沿路方向站立"
///   5. 起点闭环：副本总数 = N（含源/不含源）
///
/// 不做（后续阶段）：
///   - 自相交 / 多段闭合的开头闭环处理（仅直线+简单圆弧）
///   - 段间曲率突变（C1 不连续时朝向可"折"，按切线保留），后续考虑加平滑过渡
///   - 副本旋转 90° 偏置 / 路径镜像翻转
/// </summary>
public static class PathDistributor
{
    /// <summary>单个采样点的放置参数（位置 + 朝向 + 弧长）。</summary>
    public readonly struct Placement
    {
        public double X { get; }
        public double Y { get; }
        /// <summary>段切线方向（度：X 0° / 向上 90° / 向下 -90°）。</summary>
        public double AngleDeg { get; }
        /// <summary>该点到路径起点的累计弧长。用于排查"为什么第 N 个看上去偏移"等问题。</summary>
        public double ArcLength { get; }

        public Placement(double x, double y, double angleDeg, double arcLength)
        {
            X = x;
            Y = y;
            AngleDeg = angleDeg;
            ArcLength = arcLength;
        }

        public override string ToString()
            => $"({X:0.##},{Y:0.##}) ∠{AngleDeg:0.#}° L={ArcLength:0.##}";
    }

    /// <summary>一段路径：Line（直线段）或 Arc（圆弧段）。
    /// 设计说明：圆心由调用方算好后显式传入（POC 简化 + 解析 WPF ArcSegment 时由外部算准确）。
    /// 这样可避开"起点终点的两条中垂线 + 短弧/长弧二选一"歧义问题（WPF ArcSegment 用 IsLargeArc
    /// + SweepDirection 4 种组合选圆心，POC 阶段把这一算法放进解析层）。
    /// 同 WPF 弧度约定：CW = sweep=1，弧度数按"沿 CW 方向"度量（CCW 时取反）。</summary>
    public sealed class PathSegment
    {
        public Point Start { get; }
        public Point End { get; }
        /// <summary>非空 = 弧段。圆心已知，半径 = sqrt((Start.X-cx)² + (Start.Y-cy)²)。</summary>
        public Point? ArcCenter { get; }
        /// <summary>true = 顺时针沿弧走（与 WPF SweepDirection.Clockwise 一致）。</summary>
        public bool SweepClockwise { get; }

        private double? _cachedLength;

        public PathSegment(Point start, Point end)
        {
            Start = start;
            End = end;
            ArcCenter = null;
            SweepClockwise = false;
        }

        public PathSegment(Point start, Point end, Point arcCenter, bool sweepClockwise)
        {
            Start = start;
            End = end;
            ArcCenter = arcCenter;
            SweepClockwise = sweepClockwise;
        }

        /// <summary>该段弧长。直线 = 直线距离；弧 = 半径 × 弧度角（CW 时按符号取反）。</summary>
        public double Length()
        {
            if (_cachedLength.HasValue) return _cachedLength.Value;

            double len;
            if (ArcCenter is not { } center)
            {
                var dx = End.X - Start.X;
                var dy = End.Y - Start.Y;
                len = Math.Sqrt(dx * dx + dy * dy);
            }
            else
            {
                double r = Math.Sqrt((Start.X - center.X) * (Start.X - center.X)
                                   + (Start.Y - center.Y) * (Start.Y - center.Y));
                double a1 = Math.Atan2(Start.Y - center.Y, Start.X - center.X);
                double a2 = Math.Atan2(End.Y - center.Y, End.X - center.X);
                double sweep = NormalizeAngle(a2 - a1, SweepClockwise);
                len = Math.Abs(sweep) * r;
            }

            _cachedLength = len;
            return len;
        }

        /// <summary>在 t ∈ [0, Length()] 处采样一个点（位置 + 朝向）。
        /// 朝向：
        ///   - 直线段 = 段方向向量角度
        ///   - 弧段 = 切线方向（圆心→点的法线方向，CW 取 +90°）
        /// POC 阶段不处理 360° 闭环的重叠段（用两段覆盖）。
        /// </summary>
        public (Point pos, double angleDeg) SampleAt(double arc)
        {
            if (arc < 0 || arc > Length()) throw new ArgumentOutOfRangeException(nameof(arc));

            double segLen = Length();
            double t = segLen > 0 ? arc / segLen : 0;

            if (ArcCenter is not { } center)
            {
                double x = Start.X + (End.X - Start.X) * t;
                double y = Start.Y + (End.Y - Start.Y) * t;
                double angle = Math.Atan2(End.Y - Start.Y, End.X - Start.X) * 180 / Math.PI;
                return (new Point(x, y), angle);
            }
            else
            {
                double r = Math.Sqrt((Start.X - center.X) * (Start.X - center.X)
                                   + (Start.Y - center.Y) * (Start.Y - center.Y));
                double a1 = Math.Atan2(Start.Y - center.Y, Start.X - center.X);
                double a2 = Math.Atan2(End.Y - center.Y, End.X - center.X);
                double sweep = NormalizeAngle(a2 - a1, SweepClockwise);
                double cur = a1 + sweep * t;
                double x = center.X + r * Math.Cos(cur);
                double y = center.Y + r * Math.Sin(cur);
                // 切线方向：沿 atan2 角度递增方向 = (-sin, cos)，递减方向 = (sin, -cos)
                // sweep > 0 (CW in WPF y-down) ⇒ 角度递增 ⇒ (-sin, cos)
                // sweep < 0 (CCW) ⇒ 角度递减 ⇒ (sin, -cos)
                double tx = sweep > 0 ? -Math.Sin(cur) : Math.Sin(cur);
                double ty = sweep > 0 ? Math.Cos(cur) : -Math.Cos(cur);
                double angle = Math.Atan2(ty, tx) * 180 / Math.PI;
                return (new Point(x, y), angle);
            }
        }

        /// <summary>把 a2-a1 的"数学角度差"转换为"WPF sweep 弧度差"。
    /// WPF 约定（y-down 坐标系）：
    ///   - SweepDirection.Clockwise (sweep=1)：弧向在 atan2 中是**角度递增**方向
    ///     （不是数学 CCW，但 y-down 使 CW 视觉上 = 角度递增）
    ///   - SweepDirection.Counterclockwise (sweep=0)：弧向在 atan2 中是角度递减方向
    /// POC 限定圆弧 ≤ 180°（rawDiff 已在 (-π, π] 内）。
    /// 例：起 -90° 终 0°，CW (sweepClockwise=true) ⇒ 返回 +π/2（cur 递增）。</summary>
        internal static double NormalizeAngle(double rawDiff, bool sweepClockwise)
        {
            // 取绝对值 + 符号：CW = +, CCW = -
            double diff = Math.Abs(rawDiff);
            if (!sweepClockwise) diff = -diff;
            return diff;
        }
    }

    /// <summary>沿路径等弧长采样 N 个 Placement。
    /// <param name="segments">路径段列表（顺序连接，segments[i].End == segments[i+1].Start）</param>
    /// <param name="count">副本总数</param>
    /// <param name="startArc">第一个副本的累计弧长（创建时 = 源中心在路径上的投影弧长，即"从当前位置开始"）</param>
    /// <param name="wrap">true = 闭合路径：按 totalLen/count 等分并首尾回绕（整圈均分，与起点无关）；
    /// false = 开放路径：在 [startArc, totalLen] 区间内 count 等分（最后一份落在终点）</param>
    /// </summary>
    public static IList<Placement> DistributeAlongPath(
        IReadOnlyList<PathSegment> segments, int count, double startArc, bool wrap)
    {
        var result = new List<Placement>(count);
        if (segments is null || segments.Count == 0 || count <= 0)
            return result;

        // 累计总弧长（仅一次计算）
        double[] segLengths = new double[segments.Count];
        double totalLen = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            segLengths[i] = segments[i].Length();
            totalLen += segLengths[i];
        }
        if (totalLen <= 0) return result;

        // 起点夹到路径范围内（投影点理论上已在范围内，防御浮点误差）
        startArc = Math.Clamp(startArc, 0, totalLen);

        // 闭合路径：整圈等分（step = L/N），起点只决定相位 → N 份绕成一圈，仍有一份落在源当前位置。
        // 开放路径：从起点排到终点（step = 剩余长度/(N-1)）；N=1 时只有一份。
        double step = wrap
            ? totalLen / count
            : (count <= 1 ? 0 : (totalLen - startArc) / (count - 1));

        for (int i = 0; i < count; i++)
        {
            double target = startArc + i * step;
            if (wrap)
            {
                // 首尾回绕：target ∈ [startArc, startArc + L)，最多越过终点一次
                if (target >= totalLen) target -= totalLen;
            }
            else if (target > totalLen) target = totalLen;

            var (pos, ang) = SampleAtArc(segments, segLengths, target);
            result.Add(new Placement(pos.X, pos.Y, ang, target));
        }

        return result;
    }

    /// <summary>按累计弧长在段列表上采样（位置 + 切线角）。target 须已夹到 [0, totalLen]。</summary>
    private static (Point pos, double angleDeg) SampleAtArc(
        IReadOnlyList<PathSegment> segments, double[] segLengths, double target)
    {
        double acc = 0;
        for (int s = 0; s < segments.Count; s++)
        {
            double segLen = segLengths[s];
            if (target <= acc + segLen || s == segments.Count - 1)
            {
                // 夹到 [0, segLen] 再采样：target 恰落在段末时，(target - acc) 可能比 segLen
                // 大 1ulp（浮点 (a+b)-a ≠ b），直接喂给 SampleAt 会撞上它的越界断言 →
                // ArgumentOutOfRangeException。误差量级 1e-13，几何上就是"段末端点"。
                // 多段路径（椭圆 / 折线 / 履带）必然踩到，单段直线恰好相等才躲过。
                double arcInSeg = Math.Clamp(target - acc, 0, segLen);
                return segments[s].SampleAt(arcInSeg);
            }
            acc += segLen;
        }
        // 理论不可达（target ≤ totalLen 且已被夹），兜底取末点避免调用方抛异常
        return segments[segments.Count - 1].SampleAt(segLengths[segLengths.Length - 1]);
    }

    /// <summary>点到路径的投影结果：路径上的最近点、该点的累计弧长、切线角，
    /// 以及点到路径的**有符号**垂直距离（正 = 切线左侧 / y-down 视觉上方）。</summary>
    public readonly struct PathProjection
    {
        /// <summary>路径上的最近点 X。</summary>
        public double X { get; }
        /// <summary>路径上的最近点 Y。</summary>
        public double Y { get; }
        /// <summary>最近点距路径起点的累计弧长（= 路径阵列的起始弧长）。</summary>
        public double ArcLength { get; }
        /// <summary>最近点的路径切线方向（度）。</summary>
        public double AngleDeg { get; }
        /// <summary>点到路径的有符号垂直距离（沿法线 (-sinθ, cosθ) 的投影）。
        /// 应用：副本中心 = 路径点 + 法线 × SignedOffset，故该距离在原位置保持不变。</summary>
        public double SignedOffset { get; }

        public PathProjection(double x, double y, double arcLength, double angleDeg, double signedOffset)
        {
            X = x;
            Y = y;
            ArcLength = arcLength;
            AngleDeg = angleDeg;
            SignedOffset = signedOffset;
        }
    }

    /// <summary>把画布上一点投影到路径上（取所有段中最近的投影点）。
    /// 用途：路径阵列创建时求"源中心到路径的当前距离"（作为默认偏移量）
    /// 与"源中心在路径上的投影弧长"（作为阵列起始点）。
    /// 路径为空 / 总长为 0 → null。</summary>
    public static PathProjection? ProjectOntoPath(IReadOnlyList<PathSegment> segments, Point point)
    {
        if (segments is null || segments.Count == 0) return null;

        PathProjection? best = null;
        double bestDist = double.MaxValue;
        double acc = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var (q, angle, localArc) = ProjectOntoSegment(seg, point);
            double dx = point.X - q.X;
            double dy = point.Y - q.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < bestDist - 1e-9)
            {
                bestDist = dist;
                // 法线 = 切线旋转 +90°（y-down）：n = (-sinθ, cosθ)
                double rad = angle * Math.PI / 180.0;
                double signed = dx * -Math.Sin(rad) + dy * Math.Cos(rad);
                best = new PathProjection(q.X, q.Y, acc + localArc, angle, signed);
            }
            acc += seg.Length();
        }
        return best;
    }

    /// <summary>单段上的最近投影点（返回点、该点切线角、段内弧长）。</summary>
    private static (Point q, double angleDeg, double arc) ProjectOntoSegment(PathSegment seg, Point p)
    {
        if (seg.ArcCenter is not { } center)
        {
            double dx = seg.End.X - seg.Start.X;
            double dy = seg.End.Y - seg.Start.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 > 0
                ? Math.Clamp(((p.X - seg.Start.X) * dx + (p.Y - seg.Start.Y) * dy) / len2, 0, 1)
                : 0;
            var q = new Point(seg.Start.X + dx * t, seg.Start.Y + dy * t);
            double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            return (q, angle, seg.Length() * t);
        }

        double r = Math.Sqrt((seg.Start.X - center.X) * (seg.Start.X - center.X)
                           + (seg.Start.Y - center.Y) * (seg.Start.Y - center.Y));
        if (r <= 0) return (seg.Start, 0, 0);
        double a1 = Math.Atan2(seg.Start.Y - center.Y, seg.Start.X - center.X);
        double a2 = Math.Atan2(seg.End.Y - center.Y, seg.End.X - center.X);
        double sweep = PathSegment.NormalizeAngle(a2 - a1, seg.SweepClockwise);
        double ap = Math.Atan2(p.Y - center.Y, p.X - center.X);
        // 投影角相对起点的有符号角差，方向与 sweep 一致
        double delta = sweep >= 0
            ? NormalizeTo2Pi(ap - a1)
            : -NormalizeTo2Pi(a1 - ap);
        if (sweep >= 0 && delta > sweep) delta = (delta - sweep < 2 * Math.PI - delta) ? sweep : 0;
        else if (sweep < 0 && delta < sweep) delta = (sweep - delta < 2 * Math.PI + delta) ? sweep : 0;

        double cur = a1 + delta;
        var qa = new Point(center.X + r * Math.Cos(cur), center.Y + r * Math.Sin(cur));
        double tx = sweep > 0 ? -Math.Sin(cur) : Math.Sin(cur);
        double ty = sweep > 0 ? Math.Cos(cur) : -Math.Cos(cur);
        double tAngle = Math.Atan2(ty, tx) * 180.0 / Math.PI;
        return (qa, tAngle, Math.Abs(delta) * r);
    }

    /// <summary>角度归一到 [0, 2π)。</summary>
    private static double NormalizeTo2Pi(double angle)
    {
        const double TwoPi = Math.PI * 2;
        double a = angle % TwoPi;
        if (a < 0) a += TwoPi;
        return a;
    }
}
