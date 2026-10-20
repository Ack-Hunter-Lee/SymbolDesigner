namespace SymbolDesigner.Models;

/// <summary>传送带（Belt）绕行拓扑的一项：一个被皮带绕行的圆滚轮 + 绕行方向。
/// Wrap = +1：圆弧按角度递增方向走（WPF sweep = Clockwise）；Wrap = -1：角度递减方向走。
/// 顺序语义：在 <see cref="PrimitiveItem.BeltAnchors"/> 中，从自由端点 A 到 B 依次穿行。
/// 持久化字段；几何由 BeltSolver 实时重算，不落盘。</summary>
/// <param name="CircleId">被绕行的圆类图元（Ellipse / Ring）的 Id。</param>
/// <param name="Wrap">绕行方向：+1 / -1。</param>
public sealed record BeltAnchor(Guid CircleId, int Wrap);
