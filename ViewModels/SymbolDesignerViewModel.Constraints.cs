using System.Collections.Generic;
using System.Collections.ObjectModel;
using SymbolDesigner.Converters;
using SymbolDesigner.Models;
using Point = System.Windows.Point;

namespace SymbolDesigner.ViewModels;

/// <summary>持久约束：相切 / 重合（AutoCAD 关联语义）+ 拖动时的约束求解与收敛检测。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>几何约束集合（持久约束）。使用 IGeometricConstraint 抽象：
    /// 同一集合容纳多种约束类型（直线-圆相切、圆-圆相切等），
    /// 拓扑（N 圆之间的多条切线）由"多个约束实例"表达。
    /// 添加/删除由 ApplyTangentCommand / 图元删除联动。</summary>
    public ObservableCollection<Models.IGeometricConstraint> Constraints { get; } = new();

    // ==================== 相切约束（路线 B：持久相切） ====================

    /// <summary>
    /// 建立相切约束（选中一条直线 + 一个圆时调用）。
    /// 遵循 AutoCAD GCTANGENT：先选 = 锚（不动），后选 = 调整对象（被移动去凑相切）。
    ///  - anchorIsLine=true（先选直线）→ 移动圆使其与直线相切；
    ///  - anchorIsLine=false（先选圆）→ 移动直线使其与圆相切。
    /// 相切侧按当前圆相对直线的有向距离符号决定（±1 保持建立时的侧）。
    /// </summary>
    public void ApplyTangent(PrimitiveItem line, PrimitiveItem circle, bool anchorIsLine)
    {
        // 锁定保护：任一参与图元锁定 → 拒绝建立相切（相切会移动图元，视为编辑操作）
        if (line.IsLocked || circle.IsLocked) return;

        // 只拦截"同类型"重复（重复建两条相切无意义）；不同类型（如已有重合约束）允许共存。
        if (Constraints.Any(c => c is LineCircleTangentConstraint
            && c.Participants.Contains(line.Id) && c.Participants.Contains(circle.Id)))
        {
            return;
        }

        // 双重约束：若本参与集已有"端点落圆弧"重合，升级为"锁定切点"，以便与相切共存
        //（端点唯一合法位置=切点，相切 + 端点落圆弧同时成立，不再互相拉扯）。
        var existingCo = Constraints
            .OfType<LineCircleCoincidentConstraint>()
            .FirstOrDefault(c => c.Mode == CoincidentMode.EndPointOnArc
                && c.Participants.Contains(line.Id) && c.Participants.Contains(circle.Id));
        if (existingCo is not null)
            existingCo.LockToTangentPoint = true;

        if (!Converters.TangentSolver.TryGetLineEndpoints(line, out var a, out var b))
        {
            return;
        }
        if (!Converters.TangentSolver.TryGetCircle(circle, out var c, out _))
        {
            return;
        }

        // 相切侧：按圆心相对直线的有向距离符号决定（±1 保持建立时的侧）
        var dirX = b.X - a.X;
        var dirY = b.Y - a.Y;
        double lenSq = dirX * dirX + dirY * dirY;
        if (lenSq < 1e-9) return;
        double len = System.Math.Sqrt(lenSq);
        double nx = -dirY / len, ny = dirX / len;
        int side = ((c.X - a.X) * nx + (c.Y - a.Y) * ny) >= 0 ? 1 : -1;

        Constraints.Add(new LineCircleTangentConstraint
        {
            LineId = line.Id,
            CircleId = circle.Id,
            Side = side,
            AnchorIsLine = anchorIsLine,
        });

        if (anchorIsLine)
        {
            // 锚 = 直线：圆移动到新位置凑相切
            var newCenter = Converters.TangentSolver.SolveTangency(line, circle, side);
            if (newCenter is { } center)
                Converters.TangentSolver.PlaceCircleByCenter(circle, center);
        }
        else
        {
            // 锚 = 圆：直线移动到新位置凑相切
            Converters.TangentSolver.MoveLineToTangent(circle, line, side);
        }

        // 双重约束收尾：若升级了已有端点落圆弧重合，把该端点吸附到当前切点
        if (existingCo is not null)
        {
            var tp = Converters.TangentSolver.SolveEndpointAtTangentPoint(circle, line, existingCo.EndpointIndex);
            if (tp is { } t)
                Converters.TangentSolver.SetLineEndpointAt(line, existingCo.EndpointIndex, t);
        }
    }

    /// <summary>
    /// 建立重合约束（选中一条直线 + 一个圆，AutoCAD GCCOINCIDENT 点-曲线重合）。
    /// 模式由"用户点选命中的子对象"智能识别（点击位置的子对象分类）：
    ///  直线命中 端点0/端点1/线段，圆命中 圆心/圆弧，组合决定效果：
    ///   - 直线端点 + 圆弧        → EndPointOnArc（该端点投影锁定到圆弧上）
    ///   - 圆心     + 直线(线段)  → CenterOnLine（圆心在直线上滑动）
    ///   - 直线端点 + 圆心        → PointCoincide（端点与圆心重合，点-点）
    ///   - 无命中点/其余组合       → 回退几何启发：端点落圆弧（取更接近圆的一端）。
    /// 求解幂等，拖动任一参与图元时由 OnItemGeometryChanged 统一 Resolve 保持约束。
    /// </summary>
    public void ApplyCoincident(PrimitiveItem line, PrimitiveItem circle)
    {
        // 只拦截"同类型"重复；不同类型（如已有相切约束）允许共存（端点在圆上的相切切点自洽）。
        if (Constraints.Any(c => c is LineCircleCoincidentConstraint
            && c.Participants.Contains(line.Id) && c.Participants.Contains(circle.Id)))
        {
            return;
        }

        if (!Converters.TangentSolver.TryGetLineEndpoints(line, out var a, out var b))
        {
            return;
        }
        if (!Converters.TangentSolver.TryGetCircle(circle, out var c, out double r))
        {
            return;
        }

        // 1) 子对象识别：用多选时已识别的命中类别（圆心/端点/圆弧/线段）。
        // 圆弧与线段都不产出命中标记 → lineEndIdx=-1 / circleIsCenter=false。
        int lineEndIdx = _pickKindByItem.TryGetValue(line.Id, out var lk)
            ? lk == PickKind.EndPoint0 ? 0 : lk == PickKind.EndPoint1 ? 1 : -1
            : -1;
        bool circleIsCenter = _pickKindByItem.TryGetValue(circle.Id, out var ck) && ck == PickKind.Center;

        // 2) 组合决定模式
        if (lineEndIdx >= 0 && circleIsCenter)
        {
            // 直线端点 + 圆心 → 点-点重合
            Constraints.Add(new LineCircleCoincidentConstraint
            {
                LineId = line.Id,
                CircleId = circle.Id,
                Mode = CoincidentMode.PointCoincide,
                EndpointIndex = lineEndIdx,
            });
            Converters.TangentSolver.PlaceLineEndpointAtCircleCenter(circle, line, lineEndIdx);
        }
        else if (lineEndIdx < 0 && circleIsCenter)
        {
            // 圆心 + 直线(线段) → 圆心落直线。
            // 与相切约束冲突（相切要求圆心在直线法向距离 = r；圆心落直线要求圆心恰在直线上，
            // 仅 r=0 时可同时满足）→ 已有相切时不主动建，避免无解组合。
            if (Constraints.Any(c => c is LineCircleTangentConstraint cast
                && cast.Participants.Contains(line.Id) && cast.Participants.Contains(circle.Id)))
            {
                return;
            }
            Constraints.Add(new LineCircleCoincidentConstraint
            {
                LineId = line.Id,
                CircleId = circle.Id,
                Mode = CoincidentMode.CenterOnLine,
            });
            Converters.TangentSolver.PlaceCircleCenterOnLine(line, circle);
        }
        else if (lineEndIdx >= 0)
        {
            // 直线端点 + 圆弧（或未见圆命中分类）→ 端点落圆弧
            // 双重约束：若本参与集已有相切，把端点直接吸附并锁定到切点（而非投影到圆弧任意点），
            // 使"相切 + 端点落圆弧"同时成立且不再互相拉扯。
            bool hasTangent = Constraints.Any(c => c is LineCircleTangentConstraint _t
                && _t.Participants.Contains(line.Id) && _t.Participants.Contains(circle.Id));
            Constraints.Add(new LineCircleCoincidentConstraint
            {
                LineId = line.Id,
                CircleId = circle.Id,
                Mode = CoincidentMode.EndPointOnArc,
                EndpointIndex = lineEndIdx,
                LockToTangentPoint = hasTangent,
            });
            if (hasTangent)
            {
                var tp = Converters.TangentSolver.SolveEndpointAtTangentPoint(circle, line, lineEndIdx);
                if (tp is { } t)
                    Converters.TangentSolver.SetLineEndpointAt(line, lineEndIdx, t);
            }
            else
            {
                var newP = Converters.TangentSolver.SolveEndpointOnArc(circle, line, lineEndIdx);
                if (newP is { } p)
                    Converters.TangentSolver.SetLineEndpointAt(line, lineEndIdx, p);
            }
        }
        else
        {
            // 回退：无可用命中 / 命中线段+圆弧 → 端点落圆弧（取更接近圆弧的端点）
            double dA = System.Math.Abs(Converters.GeometryMath.Dist(c, a) - r);
            double dB = System.Math.Abs(Converters.GeometryMath.Dist(c, b) - r);
            int endpointIndex = dA <= dB ? 0 : 1;
            bool hasTangent2 = Constraints.Any(c => c is LineCircleTangentConstraint __t
                && __t.Participants.Contains(line.Id) && __t.Participants.Contains(circle.Id));
            Constraints.Add(new LineCircleCoincidentConstraint
            {
                LineId = line.Id,
                CircleId = circle.Id,
                Mode = CoincidentMode.EndPointOnArc,
                EndpointIndex = endpointIndex,
                LockToTangentPoint = hasTangent2,
            });
            if (hasTangent2)
            {
                var tp2 = Converters.TangentSolver.SolveEndpointAtTangentPoint(circle, line, endpointIndex);
                if (tp2 is { } t2)
                    Converters.TangentSolver.SetLineEndpointAt(line, endpointIndex, t2);
            }
            else
            {
                var proj = Converters.TangentSolver.SolveEndpointOnArc(circle, line, endpointIndex);
                if (proj is { } pp)
                    Converters.TangentSolver.SetLineEndpointAt(line, endpointIndex, pp);
            }
        }
    }

    /// <summary>选中项变化 / 图元删除时清理失效约束（任一参与图元被删除 → 移除约束）。
    /// 调用时机：图元删除、清空画布、加载工程前统一收敛。</summary>
    public void PruneConstraints()
    {
        for (int i = Constraints.Count - 1; i >= 0; i--)
        {
            var c = Constraints[i];
            bool anyMissing = false;
            foreach (var pid in c.Participants)
            {
                if (Primitives.All(p => p.Id != pid)) { anyMissing = true; break; }
            }
            if (anyMissing)
                Constraints.RemoveAt(i);
        }
    }

    /// <summary>
    /// 任一图元几何被拖动改变后调用：对全部约束做**迭代求解直到收敛**（接口驱动，VM 不感知具体类型）。
    ///
    /// 为什么需要迭代：单遍 Resolve 无法处理约束链。例如"直线两端点分别贴圆A、圆B"，
    /// 拖圆B → Resolve(端点B贴圆B) 改动直线 → 端点A随直线位移 → 下一遍 Resolve(端点A贴圆A)。
    /// 每一遍只按"当前其它图元位置"局部分解，遍历多遍直到无变化才收敛，
    /// 才真正满足所有约束（CAD 参数化约束求解的标准做法）。
    ///
    /// 收敛性：每条 Resolve 幂等（已在约束位置则不动），因此能达到稳定点；
    /// maxIter 兜底防止死循环（个别无解组合会来回跳，超限即停）。
    /// </summary>
    public void OnItemGeometryChanged(PrimitiveItem moved)
    {
        // 传送带：任何滚轮（圆/圆环）几何变化都要重算引用它的皮带（几何不落盘 → 必须重算）。
        // 必须放在约束早退之前 —— 皮带与约束系统无关，无约束时也要刷新。
        if (GeometryMath.IsCircleLike(moved))
            SolveBeltsReferencing(moved.Id);

        const int maxIter = 10;
        var involved = new HashSet<Guid>();
        foreach (var c in Constraints)
            foreach (var pid in c.Participants)
                involved.Add(pid);

        // 无约束 → 直接返回（避免白跑快照）
        var involvedList = Primitives.Where(p => involved.Contains(p.Id)).ToList();
        if (involvedList.Count == 0) return;

        // 几何签名字典只分配一次（每轮 Capture 复用同一个实例，避免 maxIter 次重分配）。
        // 收敛比较带容差，避免浮点振荡下精确 == 永不命中而跑满 maxIter。
        var before = CaptureGeometry(involvedList);
        for (int iter = 0; iter < maxIter; iter++)
        {
            CaptureGeometry(before, involvedList); // 原地覆写，不新建字典
            // Resolve 只改写 PrimitiveItem、不增删 Constraints 集合 → 直接枚举，无需 ToList 拷贝
            foreach (var c in Constraints)
                c.Resolve(Primitives);
            if (GeometryUnchanged(before, involvedList))
                break; // 已收敛
        }
    }

    /// <summary>取参与约束图元的几何签名（求解收敛检测用，入口每次新建）。</summary>
    private static Dictionary<Guid, (double X, double Y, double W, double H, string? P)> CaptureGeometry(List<PrimitiveItem> items)
    {
        var d = new Dictionary<Guid, (double, double, double, double, string?)>(items.Count);
        CaptureGeometry(d, items);
        return d;
    }

    /// <summary>把当前几何签名原地写入既有字典（避免迭代轮次反复分配）。
    /// 覆写后既有 Key 全部保留，几何值更新为“本轮求解前”的状态。</summary>
    private static void CaptureGeometry(
        Dictionary<Guid, (double X, double Y, double W, double H, string? P)> d, List<PrimitiveItem> items)
    {
        d.Clear();
        foreach (var p in items)
            d[p.Id] = (p.X, p.Y, p.Width, p.Height, p.PathData);
    }

    /// <summary>浮点容差（几何坐标收敛判定）。求解过程会把坐标写成接近值，
    /// 精确 == 在振荡/舍入下永不命中而跑满 maxIter；容差内视为已收敛。</summary>
    private const double GeometryTolerance = 1e-3;

    private static bool GeometryUnchanged(Dictionary<Guid, (double X, double Y, double W, double H, string? P)> before, List<PrimitiveItem> items)
    {
        foreach (var p in items)
        {
            if (!before.TryGetValue(p.Id, out var s)) return false;
            if (System.Math.Abs(s.X - p.X) < GeometryTolerance
                && System.Math.Abs(s.Y - p.Y) < GeometryTolerance
                && System.Math.Abs(s.W - p.Width) < GeometryTolerance
                && System.Math.Abs(s.H - p.Height) < GeometryTolerance
                && s.P == p.PathData) continue;
            return false;
        }
        return true;
    }

    /// <summary>拖动圆时的相切处理（双向可带动，贴近 CAD 对称语义）。
    /// 由 DraggablePrimitive 拖动圆时在应用 X/Y 后调用。
    ///  - 圆是调整对象（锚=直线，AnchorIsLine=true）→ 不再把圆锁回直线固定轨道，
    ///    而是让圆跟手 + 直线整体平移保持相切（TranslateLineToKeepTangent），返回 null。
    ///  - 锚=圆时圆自由移动，直线由 Resolve/ConstrainLineDrag 跟随。</summary>
    public Point? ConstrainCircleDrag(PrimitiveItem circle, Point recommendedCenter)
    {
        foreach (var c in Constraints)
        {
            if (c is LineCircleTangentConstraint lc && lc.CircleId == circle.Id && lc.AnchorIsLine)
            {
                var line = Primitives.FirstOrDefault(p => p.Id == lc.LineId);
                if (line is null) continue;
                // 圆保留当前（推荐）位置，直线平移保持相切 → 返回 null，不再回写圆
                Converters.TangentSolver.TranslateLineToKeepTangent(line, circle, lc.Side);
                return null;
            }
        }
        return null;
    }

    /// <summary>拖动直线时约束其只能沿相切轨道滑动（法线锁在距离=r）。
    /// 由 DraggablePrimitive 拖动直线时在应用 X/Y 后调用；返回修正后的左上角。
    /// 仅"直线是调整对象（锚=圆）"才锁轨道；锚=直线时直线自由移动，圆由 Resolve 跟随。</summary>
    public Point? ConstrainLineDrag(PrimitiveItem line, Point recommendedXY)
    {
        foreach (var c in Constraints)
        {
            if (c is LineCircleTangentConstraint lc && lc.LineId == line.Id && !lc.AnchorIsLine)
            {
                var circle = Primitives.FirstOrDefault(p => p.Id == lc.CircleId);
                if (circle is null) continue;
                return Converters.TangentSolver.ConstrainLineAlongTangent(circle, line, recommendedXY);
            }
        }
        return null;
    }
}