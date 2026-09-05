using System;
using System.Collections.Generic;

namespace SymbolDesigner.Models;

/// <summary>
/// 几何约束的公共接口（统一约束集合的抽象）。
///
/// 设计目标：让"新增一种约束类型"成为最小工作——
///  - 派生子类：声明参与图元（Participants）、实现求解（Resolve）；
///  - VM 的 OnItemGeometryChanged 完全不感知具体约束类型，统一遍历调用。
///
/// 多切线场景（两圆之间 0~4 条切线、N 个圆的组合切线）由"约束集合"
/// 持有多个约束实例表达，图元本身不承载约束身份 → 拓扑自由。
/// </summary>
public interface IGeometricConstraint
{
    /// <summary>约束唯一 ID（持久化 / 撤销引用用）。</summary>
    Guid Id { get; }

    /// <summary>参与该约束的图元 Id 列表（被"钉住"的对象）。</summary>
    IEnumerable<Guid> Participants { get; }

    /// <summary>
    /// 求解约束：在"其它图元当前位置"下，把本约束牵涉的图元调整到满足约束。
    /// 由 VM 在任一参与图元发生几何变化时调用（OnItemGeometryChanged）。
    /// 实现须具备幂等性：重复调用不产生累积偏差。
    /// </summary>
    void Resolve(IEnumerable<PrimitiveItem> allItems);
}

/// <summary>几何约束抽象基类（默认提供 Guid Id）。</summary>
public abstract class GeometricConstraint : IGeometricConstraint
{
    /// <inheritdoc />
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <inheritdoc />
    public abstract IEnumerable<Guid> Participants { get; }

    /// <inheritdoc />
    public abstract void Resolve(IEnumerable<PrimitiveItem> allItems);
}

/// <summary>约束类型判别（序列化 / 反序列化区分具体子类用）。</summary>
public enum GeometricConstraintKind
{
    /// <summary>直线 ⊙ 圆（圆沿法线移动使相切）。</summary>
    LineCircleTangent = 0,

    /// <summary>圆 ⊙ 圆（两圆保持外切/内切，后续扩展）。</summary>
    CircleCircleTangent = 1,

    /// <summary>直线 ⊙ 圆（重合：直线端点落在圆弧上 / 圆心落在直线上）。</summary>
    LineCircleCoincident = 2,
}