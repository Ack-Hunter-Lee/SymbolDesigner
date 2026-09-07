namespace SymbolDesigner.Models;

/// <summary>
/// 带厚度折线（BandPolyline）的转角连接方式。控制中间顶点处左右偏移线的衔接形态。
///
/// 三种风格的视觉对照（同一段两端厚度）：
///   - Miter：尖角，相邻两段偏移线延长相交于一点；外尖距 = half / sin(θ/2)，θ 很小时尖角很长
///   - Bevel：台阶封口，段 i 末端偏移点 → 段 i+1 起点偏移点 直接画斜封口线；不出现尖角
///   - Round：圆滑过渡，miter 尖点位置换成一段与两端偏移线相切的圆弧（半径由该顶点处的厚度决定）
///
/// 默认 Miter（保持尖角，与老版本默认行为一致）。
/// </summary>
public enum JoinStyle
{
    /// <summary>尖角：相邻段偏移线求交点塌缩为单一 miter 顶点。</summary>
    Miter,

    /// <summary>斜切：段末端偏移点和下一段起点偏移点之间画一条直线封口（保留厚度差）。</summary>
    Bevel,

    /// <summary>圆角：在 miter 位置插入一段与两端偏移线相切的圆弧。</summary>
    Round,
}
