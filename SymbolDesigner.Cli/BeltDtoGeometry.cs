using System.Windows;
using System.Windows.Media;
using SymbolDesigner.Converters;
using SymbolDesigner.Models;

namespace SymbolDesigner.Cli;

/// <summary>CLI 侧传送带（Belt）几何：从纯 DTO 还原滚轮并调用 BeltSolver 求解 / 自动绕行。
///
/// 为什么需要它：皮带几何**不落盘**（工程里只存绕行拓扑 + 两个自由端点），
/// 求解需要"圆类图元 → 圆心 + 半径"，而 GUI 侧的 TangentSolver.TryGetCircle 吃 PrimitiveItem，
/// CLI 只有 DTO —— 故在此按同一语义重建（圆类 = Ellipse/Ring，半径 = min(w,h)/2，Ring 扣描边）。
/// 供 DslToProject（创建时写拓扑/包围盒）与 Renderer（离屏渲染）共用，避免两处各写一份。</summary>
internal static class BeltDtoGeometry
{
    /// <summary>Ring 描边外扩修正（与 PrimitiveItem.RingStrokeHalfThickness 对齐，Renderer 也复用本常量）。</summary>
    public const double RingStrokeHalfThickness = 0.4;

    private const string BeltTypeKey = "Belt";

    public static bool IsBelt(PrimitiveItemDto d) => d.TypeKey == BeltTypeKey;

    /// <summary>DTO 版圆心/半径（与 TangentSolver.TryGetCircle 语义一致）。false = 该图元不是圆类。</summary>
    public static bool TryGetCircle(PrimitiveItemDto d, out Point center, out double radius)
    {
        center = default;
        radius = 0;
        if (d.TypeKey is not ("Ellipse" or "Ring")) return false;

        center = new Point(d.X + d.Width / 2.0, d.Y + d.Height / 2.0);
        radius = Math.Max(1, Math.Min(d.Width, d.Height) / 2.0);
        if (d.TypeKey == "Ring") radius -= RingStrokeHalfThickness;
        return radius > 0;
    }

    /// <summary>收集全部可用滚轮（跳过皮带自身；Id 非合法 Guid 的项跳过 —— GUI 侧同样无法解析）。</summary>
    public static List<BeltRoller> CollectRollers(IEnumerable<PrimitiveItemDto> all, string? excludeId)
    {
        var rollers = new List<BeltRoller>();
        foreach (var d in all)
        {
            if (d.Id == excludeId || !Guid.TryParse(d.Id, out var id)) continue;
            if (TryGetCircle(d, out var center, out double radius))
                rollers.Add(new BeltRoller(id, center, radius));
        }
        return rollers;
    }

    /// <summary>解析落盘的绕行拓扑（CircleId 解析失败 / 图元已删 → 跳过，自愈）。</summary>
    public static List<BeltAnchorSpec> ReadAnchors(PrimitiveItemDto belt)
    {
        var specs = new List<BeltAnchorSpec>();
        foreach (var a in belt.BeltAnchors ?? new List<BeltAnchorDto>())
            if (Guid.TryParse(a.CircleId, out var id))
                specs.Add(new BeltAnchorSpec(id, a.Wrap >= 0 ? 1 : -1));
        return specs;
    }

    /// <summary>按落盘拓扑求解皮带几何（局部坐标 M/L/A/Z，原点 = 求解包围盒左上角 = DTO.X/Y）。null = 退化/无解。</summary>
    public static Geometry? SolveGeometry(PrimitiveItemDto belt, IReadOnlyList<PrimitiveItemDto> all)
    {
        var res = BeltSolver.Solve(
            new Point(belt.BeltStartX, belt.BeltStartY),
            new Point(belt.BeltEndX, belt.BeltEndY),
            belt.IsClosed,
            CollectRollers(all, belt.Id),
            ReadAnchors(belt));
        if (res.Degenerate || string.IsNullOrWhiteSpace(res.PathData)) return null;
        try { return Geometry.Parse(res.PathData); }
        catch (FormatException) { return null; }
    }

    /// <summary>对工程内所有"尚无拓扑"的皮带跑一次自动绕行，写回拓扑 + 包围盒（new/add 组装工程后调用）。
    /// 已有拓扑的皮带（老文件 / 已解决）不覆盖。</summary>
    public static void AutoEngageAll(IReadOnlyList<PrimitiveItemDto> all)
    {
        foreach (var belt in all.Where(IsBelt))
        {
            if (belt.BeltAnchors is { Count: > 0 }) continue;

            var rollers = CollectRollers(all, belt.Id);
            var a = new Point(belt.BeltStartX, belt.BeltStartY);
            var b = new Point(belt.BeltEndX, belt.BeltEndY);

            var anchors = BeltSolver.AutoEngage(a, b, belt.IsClosed, rollers, Array.Empty<BeltAnchorSpec>());
            belt.BeltAnchors = anchors
                .Select(x => new BeltAnchorDto { CircleId = x.Id.ToString(), Wrap = x.Wrap })
                .ToList();

            // X/Y/W/H = 求解包围盒（与 GUI 的 SolveBelt 语义一致；渲染平移依赖 DTO.X/Y）
            var res = BeltSolver.Solve(a, b, belt.IsClosed, rollers, anchors);
            if (!res.Degenerate)
            {
                belt.X = res.Origin.X;
                belt.Y = res.Origin.Y;
                belt.Width = res.Width;
                belt.Height = res.Height;
            }
        }
    }
}
