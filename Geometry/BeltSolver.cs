using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;

namespace SymbolDesigner.Converters;

/// <summary>皮带绕行的圆滚轮（画布坐标圆心 + 半径）。</summary>
public readonly record struct BeltRoller(Guid Id, Point Center, double Radius);

/// <summary>皮带绕行拓扑的一项：绕哪个圆（Id）+ 绕行方向。
/// Wrap = +1：弧按 atan2 角度递增方向走（WPF sweep = 1 / Clockwise）；
/// Wrap = -1：角度递减方向走（WPF sweep = 0 / Counterclockwise）。</summary>
public readonly record struct BeltAnchorSpec(Guid Id, int Wrap);

/// <summary>皮带求解结果：局部坐标 mini-language（M / L / A / Z）+ 包围盒 + 是否退化。</summary>
public sealed record BeltSolveResult(string PathData, Point Origin, double Width, double Height, bool Degenerate);

/// <summary>
/// 传送带（Belt）几何求解器 —— 物理"紧边绳绕滑轮"模型。
///
/// 模型：一条无限柔软的绳/皮带，由两个自由端点（A / B）+ 若干被绕行的圆（滚轮）定义。
///   - 滚轮之间：两圆公切线（相邻两轮绕行方向相同 → 外公切线；方向相反 → 内公切线 = 交叉带）
///   - 滚轮上：从入射接触点到出射接触点的包络圆弧
///   - 自由端点：点到圆的切点（A 进第一轮 / 最后一轮出到 B）
///   - 闭合（isClosed）：滚轮串成环（单轮 = 整圈）
/// 输出一条连续的 M/L/A 路径（真弧，不摊平），可直接 Geometry.Parse 渲染 + 导出 + 做流动动画。
///
/// 绕行方向（Wrap）是拓扑的一部分，由调用方给定并持久化 —— 求解器不擅自改侧，
/// 只在"几何上必然失败"时降级（移除该滚轮）。
/// </summary>
public static class BeltSolver
{
    /// <summary>绕行弧小于此角度的滚轮视为"其实没绕"（自动脱开）。</summary>
    public const double MinWrapAngle = 2.0 * Math.PI / 180.0;

    /// <summary>绕行弧大于此角度视为"当前绕行侧几何上不合理"（自动绕行时尝试翻侧）。</summary>
    public const double MaxWrapAngle = 200.0 * Math.PI / 180.0;

    private const double Eps = 1e-9;

    // ============================ 公开入口 ============================

    /// <summary>按给定拓扑求解一条皮带的几何。</summary>
    /// <param name="a">自由端点 A（画布坐标）。</param>
    /// <param name="b">自由端点 B（画布坐标）。</param>
    /// <param name="isClosed">true = 两端闭合，滚轮串成环（忽略 A / B）。</param>
    /// <param name="rollers">画布上全部可用滚轮。</param>
    /// <param name="anchors">有序绕行拓扑（Id 解析不到的项自动跳过）。</param>
    public static BeltSolveResult Solve(Point a, Point b, bool isClosed,
        IReadOnlyList<BeltRoller> rollers, IReadOnlyList<BeltAnchorSpec> anchors)
    {
        var nodes = ResolveNodes(anchors, rollers);
        return Serialize(Build(a, b, isClosed, nodes));
    }

    /// <summary>自动绕行：收录"挡路但未收录"的圆、剔除"已不再阻挡"的圆，并保证拓扑自洽。
    /// 这是"只绕挡路的"+"拖动时由用户决定并记住"的实现：
    /// 首次收录某圆的绕行侧取两条候选路径中较短者（物理紧边绳），已收录的圆沿用已记住的侧。</summary>
    /// <param name="remembered">本项在拖动期间记住的择侧结果（Id → Wrap）。仅在两个候选长度几乎相同
    /// （&lt;1%，左右歧义）时用于避免抖动；为 null 则直接用较短候选。</param>
    public static IReadOnlyList<BeltAnchorSpec> AutoEngage(Point a, Point b, bool isClosed,
        IReadOnlyList<BeltRoller> rollers, IReadOnlyList<BeltAnchorSpec> current,
        IReadOnlyDictionary<Guid, int>? remembered = null)
    {
        var anchors = new List<BeltAnchorSpec>(current);
        var flipped = new HashSet<Guid>();

        // 闭合且尚无拓扑：闭环没有"自由端点"可作为橡皮筋初始线段的依据（A/B 不参与闭合几何），
        // 故以全部滚轮作为初始环（按 X/Y 排序保证不交叉）—— 一次收录后交给下面的迭代收敛/剔除。
        // GUI 的常规路径（先画开放绳再拖到闭合）已有拓扑，不走这里；此分支覆盖
        // "加载/CLI 直接创建 closed 皮带"与用户调整滚轮后闭环需重建的情形。
        if (isClosed && anchors.Count == 0 && rollers.Count > 0)
        {
            foreach (var r in rollers.OrderBy(x => x.Center.X).ThenBy(x => x.Center.Y))
                anchors.Add(new BeltAnchorSpec(r.Id, 1));
        }

        for (int iter = 0; iter < 32; iter++)
        {
            var asm = Build(a, b, isClosed, ResolveNodes(anchors, rollers));

            // —— (1) 剔除"其实没绕"的滚轮（弧 < 2°）+ 组装时被降级丢弃的滚轮 ——
            // 同一滚轮允许在拓扑里出现多次（S 形回程二次接触），故不再按 Id 一刀切全部剔除：
            // 统计每个 Id 的"真绕弧数量"当额度，按出现顺序保留前 quota 次，多出的（回程只是擦过）剔除。
            var quota = new Dictionary<Guid, int>();
            foreach (var op in asm.Ops)
                if (op.Kind == OpKind.Arc && op.NodeId != Guid.Empty && op.SweepRad >= MinWrapAngle)
                    quota[op.NodeId] = quota.GetValueOrDefault(op.NodeId) + 1;

            var kept = new List<BeltAnchorSpec>(anchors.Count);
            bool dropped = false;
            foreach (var x in anchors)
            {
                int q = quota.GetValueOrDefault(x.Id);
                if (q > 0) { quota[x.Id] = q - 1; kept.Add(x); }
                else dropped = true;
            }
            if (dropped)
            {
                anchors = kept;
                continue;
            }

            // —— (2) 已收录滚轮的绕行弧过大且翻侧确实更短（几何已变）→ 翻侧并记住 ——
            if (TryFlipOversized(a, b, isClosed, rollers, asm, ref anchors, flipped))
                continue;

            // —— (3) 找"挡路最深"的未收录圆并收录 ——
            var blocker = FindBlocker(a, b, isClosed, rollers, anchors, asm);
            if (blocker is null) break;
            int wrap = ChooseWrap(a, b, isClosed, rollers, anchors, blocker.Index, blocker.Roller, remembered);
            anchors.Insert(blocker.Index, new BeltAnchorSpec(blocker.Roller.Id, wrap));
        }

        return anchors;
    }

    // ============================ 内部数据 ============================

    private enum OpKind { Line, Arc }

    /// <summary>路径中的一段（画布坐标）。Line 段额外携带"插入位置"（自动绕行时插入到拓扑的哪个下标）。</summary>
    private sealed class Op
    {
        public OpKind Kind;
        public Point Start;
        public Point End;
        /// <summary>Arc：圆心 / 半径 / 扫掠角（正值，按 Wrap 方向）/ WPF sweep 标志（1=CW）。</summary>
        public Point Center;
        public double Radius;
        public double SweepRad;
        public int SweepFlag;
        /// <summary>该段所属的滚轮 Id（Arc 用；Line 为 Guid.Empty）。</summary>
        public Guid NodeId = Guid.Empty;
        /// <summary>Line 段违反时应在拓扑中的插入下标。</summary>
        public int InsertIndex;
    }

    private sealed class Node
    {
        public Guid Id;
        public Point C;
        public double Radius;
        public int Wrap = 1;
    }

    private sealed class Assembly
    {
        public readonly List<Node> Nodes = new();
        public readonly List<Op> Ops = new();
        public Point FigureStart;
        public bool Closed;
        public bool Degenerate;
    }

    // ============================ 组装 ============================

    private static Assembly Build(Point a, Point b, bool isClosed, IReadOnlyList<Node> nodes)
    {
        var asm = new Assembly { Closed = isClosed };

        // 没有滚轮：一条直线（闭合无意义 → 视为退化）
        if (nodes.Count == 0)
        {
            asm.Ops.Add(new Op { Kind = OpKind.Line, Start = a, End = b, InsertIndex = 0 });
            asm.FigureStart = a;
            asm.Closed = false;
            asm.Degenerate = isClosed;
            return asm;
        }

        // 闭合 + 单个滚轮：整圈
        if (isClosed && nodes.Count == 1)
        {
            var n = nodes[0];
            var p0 = new Point(n.C.X + n.Radius, n.C.Y);
            var p1 = new Point(n.C.X - n.Radius, n.C.Y);
            int sweep = n.Wrap > 0 ? 1 : 0;
            asm.Ops.Add(new Op
            {
                Kind = OpKind.Arc, Start = p0, End = p1, Center = n.C, Radius = n.Radius,
                SweepRad = Math.PI, SweepFlag = sweep, NodeId = n.Id,
            });
            asm.Ops.Add(new Op
            {
                Kind = OpKind.Arc, Start = p1, End = p0, Center = n.C, Radius = n.Radius,
                SweepRad = Math.PI, SweepFlag = sweep, NodeId = n.Id,
            });
            asm.Nodes.Add(n);
            asm.FigureStart = p0;
            return asm;
        }

        // 相切无解时降级：逐个移除"导致失败的那个滚轮"再试
        var live = new List<Node>(nodes);
        for (int attempt = 0; attempt <= nodes.Count; attempt++)
        {
            int fail = -1;
            var ops = isClosed
                ? AssembleClosed(live, out fail)
                : AssembleOpen(a, b, live, out fail);
            if (ops is not null)
            {
                asm.Nodes.AddRange(live);
                asm.Ops.AddRange(ops);
                asm.FigureStart = isClosed ? ops[0].Start : a;
                return asm;
            }

            if (live.Count == 0) break;
            if (fail >= 0 && fail < live.Count) live.RemoveAt(fail);
            else live.RemoveAt(live.Count - 1);
        }

        // 彻底无解：退回一条直线（闭合无意义 → 退化）
        asm.Nodes.Clear();
        asm.Ops.Clear();
        asm.Ops.Add(new Op { Kind = OpKind.Line, Start = a, End = b, InsertIndex = 0 });
        asm.FigureStart = a;
        asm.Closed = false;
        asm.Degenerate = true;
        return asm;
    }

    /// <summary>开放绳：A → 若干滚轮 → B。返回 null = 某相邻两轮无可行公切线（failIndex 输出肇事滚轮下标）。</summary>
    private static List<Op>? AssembleOpen(Point a, Point b, List<Node> nodes, out int failIndex)
    {
        failIndex = -1;
        int n = nodes.Count;
        var entries = new Point[n];
        var exits = new Point[n];

        // 入口：A 到第一轮的切点（A 落在圆内 → 取最近侧兜底）。皮带从 A 走向切点 → isStartPoint
        if (!PointTangent(a, nodes[0], isStartPoint: true, out entries[0]))
            entries[0] = NearSide(a, nodes[0]);

        // 相邻两轮的公切线：前轮的出射点 + 后轮的入射点
        for (int i = 0; i + 1 < n; i++)
        {
            if (!CircleTangent(nodes[i], nodes[i + 1], out exits[i], out entries[i + 1]))
            {
                failIndex = i + 1;
                return null;
            }
        }

        // 出口：最后一轮到 B 的切点（皮带从切点走向 B → 非 isStartPoint）
        if (!PointTangent(b, nodes[n - 1], isStartPoint: false, out exits[n - 1]))
            exits[n - 1] = NearSide(b, nodes[n - 1]);

        var ops = new List<Op>(2 * n + 1);
        var cur = a;
        for (int i = 0; i < n; i++)
        {
            ops.Add(new Op { Kind = OpKind.Line, Start = cur, End = entries[i], InsertIndex = i });
            var arc = MakeArc(nodes[i], entries[i], exits[i]);
            if (arc is not null) ops.Add(arc);
            cur = exits[i];
        }
        ops.Add(new Op { Kind = OpKind.Line, Start = cur, End = b, InsertIndex = n });
        return ops;
    }

    /// <summary>闭合环：滚轮串成环（相邻两轮的公切线 + 每轮绕行弧）。返回 null = 无解。</summary>
    private static List<Op>? AssembleClosed(List<Node> nodes, out int failIndex)
    {
        failIndex = -1;
        int n = nodes.Count;
        var entries = new Point[n];
        var exits = new Point[n];

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (!CircleTangent(nodes[i], nodes[j], out exits[i], out entries[j]))
            {
                failIndex = j;
                return null;
            }
        }

        // 起点 = 最后一轮的出射点（环上第一个 Line 的起点）
        var ops = new List<Op>(2 * n);
        for (int i = 0; i < n; i++)
        {
            int prev = (i - 1 + n) % n;
            ops.Add(new Op { Kind = OpKind.Line, Start = exits[prev], End = entries[i], InsertIndex = i });
            var arc = MakeArc(nodes[i], entries[i], exits[i]);
            if (arc is not null) ops.Add(arc);
        }
        return ops;
    }

    /// <summary>绕行弧（入射点 → 出射点，按 Wrap 方向）。弧角 < 2° 视为"没绕"，返回 null。</summary>
    private static Op? MakeArc(Node node, Point pin, Point pout)
    {
        double delta = ArcDelta(node, pin, pout);
        if (delta < MinWrapAngle) return null;
        return new Op
        {
            Kind = OpKind.Arc,
            Start = pin,
            End = pout,
            Center = node.C,
            Radius = node.Radius,
            SweepRad = delta,
            SweepFlag = node.Wrap > 0 ? 1 : 0,
            NodeId = node.Id,
        };
    }

    private static double ArcDelta(Node node, Point pin, Point pout)
    {
        double a0 = Math.Atan2(pin.Y - node.C.Y, pin.X - node.C.X);
        double a1 = Math.Atan2(pout.Y - node.C.Y, pout.X - node.C.X);
        return node.Wrap > 0 ? Nor(a1 - a0) : Nor(a0 - a1);
    }

    // ============================ 相切几何 ============================

    /// <summary>外部点 P 到圆的切点（两个候选里选"相切连续"的那个）。返回 false = P 在圆内/圆上（无切线）。
    ///
    /// <paramref name="isStartPoint"/> 决定行进方向的读取：皮带在切点处必须与 Wrap 绕行方向相切连续。
    /// - true（P = 起点 A）：皮带从 P 走向切点，行进方向 = 切点 - P；
    /// - false（P = 终点 B）：皮带从切点走向 P，行进方向 = P - 切点。
    /// 两者取反 —— 早期只按"切点 - P"判定，导致 B 侧选到圆的另一面切点，
    /// 圆上绕行弧从最小弧膨胀成大半个圆（单圆场景表现为两段切线明显不对称）。</summary>
    private static bool PointTangent(Point p, Node node, bool isStartPoint, out Point t)
    {
        t = default;
        double L = Dist(p, node.C);
        if (L <= node.Radius + Eps) return false;

        double baseAngle = Math.Atan2(p.Y - node.C.Y, p.X - node.C.X);
        double delta = Math.Acos(Clamp(node.Radius / L, -1, 1));

        for (int sign = 1; sign >= -1; sign -= 2)
        {
            double psi = baseAngle + sign * delta;
            var nrm = new Point(Math.Cos(psi), Math.Sin(psi));
            var cand = new Point(node.C.X + node.Radius * nrm.X, node.C.Y + node.Radius * nrm.Y);
            var travel = isStartPoint
                ? Normalize(new Point(cand.X - p.X, cand.Y - p.Y))
                : Normalize(new Point(p.X - cand.X, p.Y - cand.Y));
            var expected = node.Wrap > 0 ? new Point(-nrm.Y, nrm.X) : new Point(nrm.Y, -nrm.X);
            if (Dot(travel, expected) > 0)
            {
                t = cand;
                return true;
            }
        }
        return false;
    }

    /// <summary>圆上离 P 最近的点（P 落在圆内时的退化兜底）。</summary>
    private static Point NearSide(Point p, Node node)
    {
        var v = new Point(p.X - node.C.X, p.Y - node.C.Y);
        double L = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        if (L < 1e-6) return new Point(node.C.X + node.Radius, node.C.Y);
        return new Point(node.C.X + node.Radius * v.X / L, node.C.Y + node.Radius * v.Y / L);
    }

    /// <summary>相邻两圆的公切线接触点。绕行方向相同 → 外公切线；不同 → 内公切线（交叉带）。
    /// 返回 false = 该组合（如两圆相交/内含）无可行公切线。</summary>
    private static bool CircleTangent(Node n1, Node n2, out Point p1, out Point p2)
    {
        p1 = default;
        p2 = default;
        var d = new Point(n2.C.X - n1.C.X, n2.C.Y - n1.C.Y);
        double D = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (D < Eps) return false;
        double theta = Math.Atan2(d.Y, d.X);

        bool external = n1.Wrap == n2.Wrap;
        if (external)
        {
            // 外公切线：原点法线 n 满足 (C2-C1)·n = r1 - r2
            if (Math.Abs(n1.Radius - n2.Radius) >= D - 1e-6) return false;
            double delta = Math.Acos(Clamp((n1.Radius - n2.Radius) / D, -1, 1));
            for (int sign = 1; sign >= -1; sign -= 2)
            {
                double psi = theta + sign * delta;
                var nrm = new Point(Math.Cos(psi), Math.Sin(psi));
                var c1 = new Point(n1.C.X + n1.Radius * nrm.X, n1.C.Y + n1.Radius * nrm.Y);
                var c2 = new Point(n2.C.X + n2.Radius * nrm.X, n2.C.Y + n2.Radius * nrm.Y);
                if (DirectionOk(n1, nrm, c1, c2, d)) { p1 = c1; p2 = c2; return true; }
            }
            return false;
        }

        // 内公切线：C1 + r1·n 与 C2 - r2·n 在切线上 → (C2-C1)·n = r1 + r2
        if (n1.Radius + n2.Radius >= D - 1e-6) return false;
        double deltaIn = Math.Acos(Clamp((n1.Radius + n2.Radius) / D, -1, 1));
        for (int sign = 1; sign >= -1; sign -= 2)
        {
            double psi = theta + sign * deltaIn;
            var nrm = new Point(Math.Cos(psi), Math.Sin(psi));
            var c1 = new Point(n1.C.X + n1.Radius * nrm.X, n1.C.Y + n1.Radius * nrm.Y);
            var c2 = new Point(n2.C.X - n2.Radius * nrm.X, n2.C.Y - n2.Radius * nrm.Y);
            if (DirectionOk(n1, nrm, c1, c2, d)) { p1 = c1; p2 = c2; return true; }
        }
        return false;
    }

    /// <summary>校验候选切线的行进方向与"前一圆按 Wrap 绕出的切线方向"一致，且确实朝后一圆前进。</summary>
    private static bool DirectionOk(Node n1, Point nrm, Point c1, Point c2, Point delta)
    {
        var travel = Normalize(new Point(c2.X - c1.X, c2.Y - c1.Y));
        if (travel.X == 0 && travel.Y == 0) return false;
        var expected = n1.Wrap > 0 ? new Point(-nrm.Y, nrm.X) : new Point(nrm.Y, -nrm.X);
        return Dot(travel, expected) > 0 && Dot(travel, delta) > 0;
    }

    // ============================ 自动绕行辅助 ============================

    private sealed record Blocker(double Depth, int Index, BeltRoller Roller);

    /// <summary>找"穿入当前路径最深"的未收录圆（只绕挡路的）。</summary>
    private static Blocker? FindBlocker(Point a, Point b, bool isClosed,
        IReadOnlyList<BeltRoller> rollers, IReadOnlyList<BeltAnchorSpec> anchors, Assembly asm)
    {
        // 已收录的圆集合：只用于下面"端点落在圆内"那一条
        // （避免同一圆因端点被反复插入成相邻重复项 —— 相邻重复必然无解）
        var engaged = new HashSet<Guid>(anchors.Select(x => x.Id));
        foreach (var n in asm.Nodes) engaged.Add(n.Id);

        double bestDepth = 0;
        bool found = false;
        Blocker? best = null;

        foreach (var r in rollers)
        {
            if (r.Radius <= 0) continue;
            // 与"别的"已收录滚轮相交 → 几何上不可行，跳过（否则会来回插入）。
            // 同一个圆自身允许再次收录（蛇形 / S 形回程二次接触：[大圆, 小圆, 大圆]），故按 Id 排除自身。
            if (asm.Nodes.Any(n => n.Id != r.Id && Dist(n.C, r.Center) < n.Radius + r.Radius - 0.5)) continue;

            // 被线段穿过 → 即使该圆已收录也允许再收录一次（回程段穿过 → 插入第二次）
            foreach (var op in asm.Ops)
            {
                if (op.Kind != OpKind.Line) continue;
                double depth = r.Radius - DistPointToSegment(r.Center, op.Start, op.End);
                if (depth > bestDepth)
                {
                    bestDepth = depth;
                    best = new Blocker(depth, op.InsertIndex, r);
                    found = true;
                }
            }

            // 端点落在圆内：只对"尚未收录"的圆判（已收录的圆若在此重复插入会造出相邻重复项，几何必然无解）
            if (!isClosed && !engaged.Contains(r.Id))
            {
                double depthA = r.Radius - Dist(r.Center, a);
                if (depthA > bestDepth)
                {
                    bestDepth = depthA;
                    best = new Blocker(depthA, 0, r);
                    found = true;
                }
                double depthB = r.Radius - Dist(r.Center, b);
                if (depthB > bestDepth)
                {
                    bestDepth = depthB;
                    best = new Blocker(depthB, asm.Nodes.Count, r);
                    found = true;
                }
            }
        }
        return found ? best : null;
    }

    /// <summary>已收录滚轮的绕行弧过大（&gt;200°）且翻侧后总路径更短 → 翻侧（几何已变，旧的择侧失效）。</summary>
    private static bool TryFlipOversized(Point a, Point b, bool isClosed, IReadOnlyList<BeltRoller> rollers,
        Assembly asm, ref List<BeltAnchorSpec> anchors, HashSet<Guid> flipped)
    {
        double current = OpLengths(asm.Ops);
        foreach (var op in asm.Ops)
        {
            if (op.Kind != OpKind.Arc || op.NodeId == Guid.Empty) continue;
            if (op.SweepRad <= MaxWrapAngle || flipped.Contains(op.NodeId)) continue;

            int idx = anchors.FindIndex(x => x.Id == op.NodeId);
            if (idx < 0) continue;
            // 同一滚轮在拓扑中出现多次（S 形）时，Arc 只带 Id 分不清是第几次 → 翻错了会破坏已收敛拓扑，保守跳过
            if (anchors.FindIndex(idx + 1, x => x.Id == op.NodeId) >= 0) continue;

            var trial = new List<BeltAnchorSpec>(anchors);
            trial[idx] = new BeltAnchorSpec(op.NodeId, -anchors[idx].Wrap);
            var trialAssembly = Build(a, b, isClosed, ResolveNodes(trial, rollers));
            if (trialAssembly.Nodes.Count != trial.Count) continue;
            if (trialAssembly.Degenerate && isClosed) continue;

            if (OpLengths(trialAssembly.Ops) < current)
            {
                anchors = trial;
                flipped.Add(op.NodeId);
                return true;
            }
        }
        return false;
    }

    /// <summary>首次收录某滚轮时择侧：取两条候选路径中较短者（物理紧边绳）。
    /// 两者长度差 &lt;1%（左右歧义）时优先沿用 remembered 中记住的侧，避免拖动时抖动。</summary>
    private static int ChooseWrap(Point a, Point b, bool isClosed, IReadOnlyList<BeltRoller> rollers,
        List<BeltAnchorSpec> anchors, int insertIndex, BeltRoller roller, IReadOnlyDictionary<Guid, int>? remembered)
    {
        double posLen = double.PositiveInfinity, negLen = double.PositiveInfinity;
        foreach (int w in new[] { 1, -1 })
        {
            var trial = new List<BeltAnchorSpec>(anchors);
            trial.Insert(Math.Clamp(insertIndex, 0, anchors.Count), new BeltAnchorSpec(roller.Id, w));
            double? len = TryLength(a, b, isClosed, rollers, trial);
            if (!len.HasValue) continue;
            if (w > 0) posLen = len.Value; else negLen = len.Value;
        }

        if (double.IsPositiveInfinity(posLen) && double.IsPositiveInfinity(negLen)) return 1;
        if (double.IsPositiveInfinity(posLen)) return -1;
        if (double.IsPositiveInfinity(negLen)) return 1;

        double minLen = Math.Min(posLen, negLen);
        double diff = Math.Abs(posLen - negLen);
        if (remembered is not null && minLen > 1e-6 && diff / minLen < 0.01
            && remembered.TryGetValue(roller.Id, out int prev))
            return prev >= 0 ? 1 : -1;

        return posLen <= negLen ? 1 : -1;
    }

    private static double? TryLength(Point a, Point b, bool isClosed, IReadOnlyList<BeltRoller> rollers,
        List<BeltAnchorSpec> anchors)
    {
        var nodes = ResolveNodes(anchors, rollers);
        if (nodes.Count != anchors.Count) return null;    // 有 Id 解析不到 → 该候选无效
        var asm = Build(a, b, isClosed, nodes);
        if (asm.Nodes.Count != nodes.Count) return null;  // 组装时被降级 → 该候选无效
        if (asm.Degenerate && isClosed) return null;
        return OpLengths(asm.Ops);
    }

    private static double OpLengths(List<Op> ops)
    {
        double total = 0;
        foreach (var op in ops)
            total += op.Kind == OpKind.Line ? Dist(op.Start, op.End) : Math.Abs(op.SweepRad) * op.Radius;
        return total;
    }

    private static List<Node> ResolveNodes(IReadOnlyList<BeltAnchorSpec> anchors, IReadOnlyList<BeltRoller> rollers)
    {
        // 不去重：同一个滚轮允许在拓扑中出现多次（蛇形 / S 形回程二次接触，如 [大圆, 小圆, 大圆]）。
        // 相邻重复项（[大圆, 大圆, ...]）会在 CircleTangent 里因 D≈0 无解，由 Build 的降级逻辑剔除。
        var list = new List<Node>(anchors.Count);
        foreach (var spec in anchors)
        {
            for (int i = 0; i < rollers.Count; i++)
            {
                if (rollers[i].Id != spec.Id) continue;
                if (rollers[i].Radius <= 0) continue;
                list.Add(new Node
                {
                    Id = spec.Id,
                    C = rollers[i].Center,
                    Radius = rollers[i].Radius,
                    Wrap = spec.Wrap >= 0 ? 1 : -1,
                });
                break;
            }
        }
        return list;
    }

    // ============================ 序列化 ============================

    /// <summary>把组装结果转成局部坐标 mini-language + 包围盒（弧按采样点计入包围盒）。</summary>
    private static BeltSolveResult Serialize(Assembly asm)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void AddPoint(Point p)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        AddPoint(asm.FigureStart);
        foreach (var op in asm.Ops)
        {
            AddPoint(op.Start);
            AddPoint(op.End);
            if (op.Kind != OpKind.Arc || op.Radius <= 0 || op.SweepRad <= 0) continue;
            int steps = Math.Max(2, (int)Math.Ceiling(op.SweepRad / (5.0 * Math.PI / 180.0)));
            double a0 = Math.Atan2(op.Start.Y - op.Center.Y, op.Start.X - op.Center.X);
            double dir = op.SweepFlag == 1 ? 1 : -1;
            for (int i = 1; i < steps; i++)
            {
                double ang = a0 + dir * op.SweepRad * i / steps;
                AddPoint(new Point(op.Center.X + op.Radius * Math.Cos(ang),
                                   op.Center.Y + op.Radius * Math.Sin(ang)));
            }
        }
        if (minX > maxX) { minX = minY = maxX = maxY = 0; }

        var origin = new Point(minX, minY);
        var sb = new StringBuilder();
        sb.AppendFormat(CultureInfo.InvariantCulture, "M {0:0.###},{1:0.###}",
            asm.FigureStart.X - origin.X, asm.FigureStart.Y - origin.Y);

        foreach (var op in asm.Ops)
        {
            if (op.Kind == OpKind.Line)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, " L {0:0.###},{1:0.###}",
                    op.End.X - origin.X, op.End.Y - origin.Y);
                continue;
            }
            if (op.SweepRad < 1e-9) continue;   // 零弧：跳过该命令（前后两点几乎重合）
            int largeArc = op.SweepRad > Math.PI ? 1 : 0;
            sb.AppendFormat(CultureInfo.InvariantCulture, " A {0:0.###},{0:0.###} 0 {1},{2} {3:0.###},{4:0.###}",
                op.Radius, largeArc, op.SweepFlag, op.End.X - origin.X, op.End.Y - origin.Y);
        }

        if (asm.Closed) sb.Append(" Z");

        return new BeltSolveResult(
            sb.ToString(),
            origin,
            Math.Max(1, maxX - minX),
            Math.Max(1, maxY - minY),
            asm.Degenerate);
    }

    // ============================ 数值工具 ============================

    private static double Nor(double x)
    {
        const double twoPi = 2 * Math.PI;
        while (x < 0) x += twoPi;
        while (x >= twoPi) x -= twoPi;
        return x;
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    private static double Dot(Point u, Point v) => u.X * v.X + u.Y * v.Y;

    private static double Dist(Point p, Point q)
    {
        double dx = p.X - q.X, dy = p.Y - q.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Point Normalize(Point v)
    {
        double L = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return L < 1e-12 ? new Point(0, 0) : new Point(v.X / L, v.Y / L);
    }

    private static double DistPointToSegment(Point p, Point s0, Point s1)
    {
        double dx = s1.X - s0.X, dy = s1.Y - s0.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return Dist(p, s0);
        double t = Clamp(((p.X - s0.X) * dx + (p.Y - s0.Y) * dy) / lenSq, 0, 1);
        return Dist(p, new Point(s0.X + t * dx, s0.Y + t * dy));
    }
}
