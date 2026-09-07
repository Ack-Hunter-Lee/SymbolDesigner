using System;
using System.Collections.Generic;

namespace SymbolDesigner.Models;

/// <summary>
/// 重合约束模式（AutoCAD GCCOINCIDENT 的 point-on-curve 两种典型形态）。
/// 选中"直线 + 圆"时，按当前几何"更接近哪种重合"智能选择。
/// </summary>
public enum CoincidentMode
{
    /// <summary>直线端点落在圆弧上（点落在曲线上）。需指定 EndpointIndex。</summary>
    EndPointOnArc = 0,

    /// <summary>圆心落在直线上（点落在直线上）。无需端点索引。</summary>
    CenterOnLine = 1,

    /// <summary>点-点重合：直线端点与圆心重合（AutoCAD 两个点重合）。需指定 EndpointIndex。</summary>
    PointCoincide = 2,
}

/// <summary>
/// 重合约束（遵循 AutoCAD GCCOINCIDENT：强制一个点落在曲线/曲线上）。
///
/// 参与图元固定为"直线"（Line/Polyline/BandPolyline）与"圆"（Ellipse/Ring），两种模式：
///  - EndPointOnArc：直线的某个端点（PathData 首/末点）投影到圆弧上，
///                    拖动圆时该端点跟随保持落在圆弧上；拖动线时该端点被拉回圆弧。
///  - CenterOnLine ：圆心的投影落在直线上（腰垂足），圆心只能沿直线滑动。
///
/// 求解动作（幂等）：
///  - EndPointOnArc：把端点 P 径向投影到圆上最近点 newP = C + (P-C)/|P-C| * r；
///  - CenterOnLine ：把圆心投影到直线 AB 的垂足。
/// 两者都由 VM 的 OnItemGeometryChanged 在任一参与图元变化时统一调用 Resolve。
/// </summary>
public sealed class LineCircleCoincidentConstraint : GeometricConstraint
{
    /// <summary>直线图元 Id。</summary>
    public Guid LineId { get; init; }

    /// <summary>圆图元 Id。</summary>
    public Guid CircleId { get; init; }

    /// <summary>重合模式：端点落圆弧 / 圆心落直线。</summary>
    public CoincidentMode Mode { get; init; } = CoincidentMode.EndPointOnArc;

    /// <summary>EndPointOnArc 时使用的端点索引：0=PathData 首点，1=PathData 末点。</summary>
    public int EndpointIndex { get; init; }

    /// <summary>
    /// 是否把端点"锁定到切点"（用于"相切 + 端点落圆弧"双重约束）。
    /// 当同一线圆对已建立相切约束时置 true：此时端点的唯一合法位置是切点，
    /// Resolve 把端点解到切点（SolveEndpointAtTangentPoint）而非圆弧上任意最近点，
    /// 使两条约束同时满足（CAD 的一致性冗余解）。可写，允许后建的相切升级已有重合。</summary>
    public bool LockToTangentPoint { get; set; }

    /// <inheritdoc />
    public override IEnumerable<Guid> Participants => new[] { LineId, CircleId };

    /// <inheritdoc />
    public override void Resolve(IEnumerable<PrimitiveItem> allItems)
    {
        var line = Find(allItems, LineId);
        var circle = Find(allItems, CircleId);
        if (line is null || circle is null) return;

        if (Mode == CoincidentMode.CenterOnLine)
        {
            Converters.TangentSolver.PlaceCircleCenterOnLine(line, circle);
        }
        else if (Mode == CoincidentMode.PointCoincide)
        {
            Converters.TangentSolver.PlaceLineEndpointAtCircleCenter(circle, line, EndpointIndex);
        }
        else if (LockToTangentPoint)
        {
            // 双重约束（相切+端点落圆弧）：端点钉在切点
            var tp = Converters.TangentSolver.SolveEndpointAtTangentPoint(line, circle, EndpointIndex);
            if (tp is { } t)
                Converters.TangentSolver.SetLineEndpointAt(line, EndpointIndex, t);
        }
        else
        {
            var newP = Converters.TangentSolver.SolveEndpointOnArc(circle, line, EndpointIndex);
            if (newP is { } p)
                Converters.TangentSolver.SetLineEndpointAt(line, EndpointIndex, p);
        }
    }

    private static PrimitiveItem? Find(IEnumerable<PrimitiveItem> items, Guid id)
    {
        foreach (var p in items)
            if (p.Id == id) return p;
        return null;
    }
}