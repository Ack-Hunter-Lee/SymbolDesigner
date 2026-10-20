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
        private static double NormalizeAngle(double rawDiff, bool sweepClockwise)
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
    /// <param name="includeSource">true = 第一份在 t=0（路径起点），false = 第一份在 t = (弧长 / (count)) 处</param>
    /// </summary>
    public static IList<Placement> DistributeAlongPath(
        IReadOnlyList<PathSegment> segments, int count, bool includeSource)
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

        // 等弧长步进
        // includeSource=true:  t_i = i * (totalLen / (count - 1))         for i=0..count-1
        //                       第一个 t=0（路径起点），最后一个 t=totalLen（终点）
        // includeSource=false: t_i = (i + 1) * (totalLen / (count + 1))    for i=0..count-1
        //                       第一个 t>0，最后一个 t<totalLen
        double step = includeSource
            ? totalLen / Math.Max(1, count - 1)
            : totalLen / (count + 1);

        for (int i = 0; i < count; i++)
        {
            double target = includeSource
                ? i * step
                : (i + 1) * step;

            // clamp 到 [0, totalLen]
            if (target < 0) target = 0;
            if (target > totalLen) target = totalLen;

            // 找 target 落在哪一段
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
                    var (pos, ang) = segments[s].SampleAt(arcInSeg);
                    result.Add(new Placement(pos.X, pos.Y, ang, target));
                    break;
                }
                acc += segLen;
            }
        }

        return result;
    }
}
