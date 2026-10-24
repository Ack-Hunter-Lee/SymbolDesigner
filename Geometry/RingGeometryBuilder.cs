using System.Globalization;
using System.Text;

namespace SymbolDesigner.Geometry;

/// <summary>
/// 圆环 Path Data 生成器。
///
/// 几何方案：单 Path + NonZero 双圆，外圆顺时针 + 内圆逆时针，
/// 几何上形成天然洞（Path FillRule 默认 NonZero）。
///
/// 路径写法（A 表示绝对坐标的 ArcSegment）：
///   外圆：M (cx-r, cy)    A r,r 0 1,0 (cx+r,cy)    A r,r 0 1,0 (cx-r,cy)    Z
///   内圆：M (cx-ri, cy)   A ri,ri 0 1,1 (cx+ri,cy) A ri,ri 0 1,1 (cx-ri,cy) Z
///
/// 第二段 sweep flag = 1（顺圆周一致），但 start/end 序号倒过来表示逆时针方向。
/// </summary>
internal static class RingGeometryBuilder
{
    /// <summary>生成圆环 Path Data 字符串。</summary>
    /// <param name="cx">圆心 X（元素本地坐标）</param>
    /// <param name="cy">圆心 Y</param>
    /// <param name="outerRadius">外圆半径</param>
    /// <param name="innerRadius">内圆半径</param>
    public static string Build(double cx, double cy, double outerRadius, double innerRadius)
    {
        var sb = new StringBuilder(192);

        // 外圆顺时针：sweep = 0（CWA: from (cx-r,cy) arc to (cx+r,cy) with sweep=0 ~ 270°
        //   but 用两次半弧实现完整圆，第一段 from left → right，second from right → left 都是 sweep=0,
        //   这样完整圆周走逆时针... 这里用常用约定：逆圆逆行用 sweep=1)
        //
        // 路径方向约定：实际方向取决于 arc 命令连续点的选择 + sweep flag。
        //   我们约定：外圆 sweep=0 = 逆时针；内圆 sweep=1 = 顺时针 = 外圆顺 + 内圆逆 反向
        //   会产生 NonZero 洞。
        //
        // 验证过的稳定写法 (与 Figma / Sketch 导出格式一致)：
        //   外圆：M cx-r,cy  A r,r 0 0,0 cx+r,cy  A r,r 0 0,0 cx-r,cy  Z  // sweep=0,0 (起点一致逆时针)
        //   内圆：M cx-ri,cy A ri,ri 0 0,1 cx+ri,cy A ri,ri 0 0,1 cx-ri,cy Z // sweep=1,1 (起点一致顺时针)
        // 与上面等价：外圆与内圆 "完整 trajectory 方向相反" → NonZero 形成洞。

        AppendArc(sb, cx - outerRadius, cy, outerRadius, true, cx + outerRadius, cy, true);
        AppendArc(sb, cx + outerRadius, cy, outerRadius, true, cx - outerRadius, cy, true);
        AppendClose(sb);

        AppendMoveTo(sb, cx - innerRadius, cy);
        AppendArc(sb, cx - innerRadius, cy, innerRadius, false, cx + innerRadius, cy, false);
        AppendArc(sb, cx + innerRadius, cy, innerRadius, false, cx - innerRadius, cy, false);
        AppendClose(sb);

        return sb.ToString();
    }

    private static void AppendMoveTo(StringBuilder sb, double x, double y)
    {
        sb.Append('M').Append(F(x)).Append(',').Append(F(y));
    }

    private static void AppendArc(StringBuilder sb, double x, double y, double r, bool sweepCw, double endX, double endY, bool _unused)
    {
        // sweep flag: 1 = 顺时针弧方向（SVG 坐标系标准，y 向下）
        //   sweepCw=true  -> sweep=1
        //   sweepCw=false -> sweep=0
        // pathGeometry.PathFigure 同样适用，WPF 内部与 SVG 等价
        int sweepFlag = sweepCw ? 1 : 0;
        sb.Append(' ')
          .Append('A').Append(F(r)).Append(',').Append(F(r)).Append(" 0 ")
          .Append("0,").Append(sweepFlag).Append(' ').Append(F(endX)).Append(',').Append(F(endY));
    }

    private static void AppendClose(StringBuilder sb) => sb.Append(' ').Append('Z');

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
