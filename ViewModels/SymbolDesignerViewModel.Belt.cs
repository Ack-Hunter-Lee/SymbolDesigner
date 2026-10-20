using System;
using System.Collections.Generic;
using System.Linq;
using SymbolDesigner.Converters;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>
/// 传送带（Belt）图元的求解与刷新。
///
/// 职责边界：
///   - 本文件只负责"把画布状态翻译成 BeltSolver 的输入，再把结果写回图元"（ViewModel 层）。
///   - 相切/包络的几何算法全部在 <see cref="BeltSolver"/> 中（纯函数，可独立测试）。
///
/// 核心约定：皮带几何**不落盘**，只持久化"绕行拓扑 + 两个自由端点"。
/// 因此任何滚轮变动都要触发重算，否则会出现"旧几何 + 新滚轮"的不一致。
/// </summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>把画布上全部圆类图元（Ellipse / Ring）解析为滚轮列表（供皮带绕行）。
    /// 排除 excludeId（皮带自身不是滚轮；调用方也可借此排除正在被拖动而尚未定型的圆）。</summary>
    private List<BeltRoller> CollectBeltRollers(Guid excludeId)
    {
        var rollers = new List<BeltRoller>();
        foreach (var p in Primitives)
        {
            if (p.Id == excludeId) continue;
            if (!GeometryMath.IsCircleLike(p)) continue;
            if (!TangentSolver.TryGetCircle(p, out var center, out double radius)) continue;
            rollers.Add(new BeltRoller(p.Id, center, radius));
        }
        return rollers;
    }

    /// <summary>求解一条皮带并把结果写回图元：BeltPathData / X / Y / Width / Height。
    /// <param name="autoEngage">true = 先跑一次自动绕行（收录新挡路的圆、剔除已脱开的圆）。
    /// 仅在"绘制提交"与"端点手柄拖动"时传 true——这两处用户正在有意改变皮带走向；
    /// 滚轮被动变化（拖动滚轮）时传 false，避免拓扑被意外改写。</param>
    /// </summary>
    public void SolveBelt(PrimitiveItem belt, bool autoEngage)
    {
        if (belt is null || belt.TypeKey != nameof(L1Primitive.Belt)) return;

        var rollers = CollectBeltRollers(belt.Id);
        // 模型层拓扑（BeltAnchor）↔ 求解器输入（BeltAnchorSpec）：字段名不同，显式转换
        var anchors = belt.BeltAnchors.Select(a => new BeltAnchorSpec(a.CircleId, a.Wrap)).ToList();

        if (autoEngage)
        {
            var engaged = BeltSolver.AutoEngage(
                new System.Windows.Point(belt.BeltStartX, belt.BeltStartY),
                new System.Windows.Point(belt.BeltEndX, belt.BeltEndY),
                belt.IsClosed, rollers, anchors, _beltWrapMemory);

            // 用自动绕行结果覆盖拓扑；同时把择侧结果记入"拖动期记忆"，供歧义（左右对称）时保持稳定
            belt.BeltAnchors.Clear();
            foreach (var a in engaged)
            {
                belt.BeltAnchors.Add(new BeltAnchor(a.Id, a.Wrap));
                _beltWrapMemory[a.Id] = a.Wrap;
            }
            anchors = engaged.ToList();
        }

        var result = BeltSolver.Solve(
            new System.Windows.Point(belt.BeltStartX, belt.BeltStartY),
            new System.Windows.Point(belt.BeltEndX, belt.BeltEndY),
            belt.IsClosed, rollers, anchors);

        belt.BeltPathData = result.PathData;
        // X/Y/Width/Height = 求解结果包围盒（不是用户摆放位置，与 BooleanPath 同语义）
        belt.X = result.Origin.X;
        belt.Y = result.Origin.Y;
        belt.Width = result.Width;
        belt.Height = result.Height;
    }

    /// <summary>重算所有"引用了 movedId 这个滚轮"的皮带（滚轮被拖动/缩放/改尺寸后调用）。
    /// 不传 autoEngage —— 滚轮被动移动时拓扑保持不变（哪根绕行侧由用户先前拖动决定）。</summary>
    public void SolveBeltsReferencing(Guid movedId)
    {
        foreach (var p in Primitives)
        {
            if (p.TypeKey != nameof(L1Primitive.Belt)) continue;
            if (!p.BeltAnchors.Any(a => a.CircleId == movedId)) continue;
            SolveBelt(p, autoEngage: false);
        }
    }

    /// <summary>重算画布上所有皮带（工程加载 / 撤销重做恢复后调用——几何不落盘，必须重建）。</summary>
    public void SolveAllBelts()
    {
        foreach (var p in Primitives)
        {
            if (p.TypeKey == nameof(L1Primitive.Belt))
                SolveBelt(p, autoEngage: false);
        }
    }

    /// <summary>清空拖动期的择侧记忆（一次拖动结束后调用）。
    /// 记忆只服务于"同一手势内避免左右抖动"，跨手势不再沿用，否则用户想改绕行侧会改不动。</summary>
    public void ClearBeltWrapMemory() => _beltWrapMemory.Clear();

    /// <summary>拖动期择侧记忆：滚轮 Id → Wrap。仅在两个候选路径长度几乎相同（左右歧义）
    /// 时用于保持稳定；否则一律取较短侧（物理紧边绳）。</summary>
    private readonly Dictionary<Guid, int> _beltWrapMemory = new();
}
