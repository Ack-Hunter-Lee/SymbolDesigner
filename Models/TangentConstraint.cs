using System;
using System.Collections.Generic;

namespace SymbolDesigner.Models;

/// <summary>
/// 相切约束（遵循 AutoCAD GCTANGENT 规则：第二个对象向第一个对象调整）。
///
/// 几何语义（角色由选择顺序决定）：
///  - 先选的图元 = 锚（Anchor）：该图元保持不动（拖动锚时作为参考基准）；
///  - 后选的图元 = 调整对象（Follower）：建立约束时移动使其与锚相切，
///    后续拖动锚时跟随保持相切。
///
/// 参与图元固定为一"线"一"圆"（Line/Polyline/BandPolyline × Ellipse/Ring）：
///  - AnchorIsLine=true  → 锚是直线，圆被调整（圆移动到与直线相切）；
///  - AnchorIsLine=false → 锚是圆，直线被调整（直线移动到与圆相切）。
///
/// 持久约束行为（拖动时）：
///  - 拖动锚 → 另一图元自动跟随移动，保持相切；
///  - 拖动调整对象 → 其自由度被限制（只能沿切向轨道滑动），法线方向锁死。
/// </summary>
public sealed class LineCircleTangentConstraint : GeometricConstraint
{
    /// <summary>直线图元 Id。</summary>
    public Guid LineId { get; init; }

    /// <summary>圆图元 Id。</summary>
    public Guid CircleId { get; init; }

    /// <summary>锚是否为直线（先选直线 = true；先选圆 = false）。
    /// 决定建立约束时谁被移动凑相切、拖动时谁是基准。</summary>
    public bool AnchorIsLine { get; init; } = true;

    /// <summary>相切侧：+1 = 圆心在直线法线正方向一侧；-1 = 负方向一侧。
    /// 建立约束时按当前圆相对直线的位置决定，求解时保持该侧。</summary>
    public int Side { get; set; } = 1;

    /// <inheritdoc />
    public override IEnumerable<Guid> Participants => AnchorIsLine ? new[] { LineId, CircleId } : new[] { CircleId, LineId };

    /// <inheritdoc />
    public override void Resolve(IEnumerable<PrimitiveItem> allItems)
    {
        var line = Find(allItems, LineId);
        var circle = Find(allItems, CircleId);
        if (line is null || circle is null) return;

        if (AnchorIsLine)
        {
            // 锚 = 直线：圆移动到新位置保持相切
            var center = Converters.TangentSolver.SolveTangency(line, circle, Side);
            if (center is { } c)
                Converters.TangentSolver.PlaceCircleByCenter(circle, c);
        }
        else
        {
            // 锚 = 圆：直线移动到新位置保持相切
            Converters.TangentSolver.MoveLineToTangent(circle, line, Side);
        }
    }

    private static PrimitiveItem? Find(IEnumerable<PrimitiveItem> items, Guid id)
    {
        foreach (var p in items)
            if (p.Id == id) return p;
        return null;
    }
}

/// <summary>约束的可序列化 DTO（SymbolProject.Constraints 列表项）。
/// Kind 字段用于区分具体约束子类；旧文件无 Kind → 默认按 LineCircleTangent 解析。
/// AnchorIsLine 缺省 → true（旧文件无该字段 ⇒ 默认先选直线，即旧行为）。</summary>
public class TangencyConstraintDto
{
    public string Id { get; set; } = string.Empty;
    public GeometricConstraintKind Kind { get; set; } = GeometricConstraintKind.LineCircleTangent;
    public string LineId { get; set; } = string.Empty;
    public string CircleId { get; set; } = string.Empty;
    public int Side { get; set; } = 1;
    public bool AnchorIsLine { get; set; } = true;
    // 重合约束专用（LineCircleCoincident 时使用）；缺省 EndPointOnArc 兼容旧文件
    public string? CoincidentModeStr { get; set; }
    public int EndpointIndex { get; set; }
}