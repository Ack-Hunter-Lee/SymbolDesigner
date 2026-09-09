using System.Globalization;

namespace SymbolDesigner.Converters;

/// <summary>
/// 扇形（Pie）Path Data 生成器。
///
/// 几何方案：单 Path，闭合填充满扇面。
///   路径：M 弧起点 S  A r,r 0 large-arc-flag,1 弧终点 E  L 圆心 C  Z
///   （Move 到弧起点 → 画弧线 → 直线回圆心 → 闭合回到弧起点 = 两条半径夹一段弧的扇面）
///
/// 角度约定（与 Cavas 坐标一致，y 向下）：
///   - 0° = 东（3 点钟），正角度 = 屏幕顺时针（数学逆时针，因 y 向下翻转为屏显顺时针）。
///   - sweep-flag 恒定 = 1：SVG/WPF 下 1 = 屏幕顺时针 = 正角度方向。
///   - large-arc-flag = |sweep| &gt; 180°。
///
/// 运行时几何（PrimitiveItem.PieGeometry）与导出（XamlExporter.PieElement）共用，
/// 保证画布渲染与导出结果严格一致。
/// </summary>
internal static class PieGeometryBuilder
{
    /// <summary>生成扇形 Path Data 字符串（画布/局部坐标均可，由调用方传绝对或相对圆心半径）。
/// 对称语义：分布关于中轴对称，从 中轴-张角/2 张开到 中轴+张角/2。</summary>
    /// <param name="cx">圆心 X</param>
    /// <param name="cy">圆心 Y</param>
    /// <param name="radius">外圆半径（&gt;0）</param>
    /// <param name="axisDeg">对称中轴指向角（度，0=东，正=屏幕顺时针）。弧区 = [axis-∠/2, axis+∠/2]。</param>
    /// <param name="sweepDeg">张角/总夹角（度，对中轴对称展开；Clamp 到 (0,360) 防整圆退化）</param>
    public static string BuildPath(double cx, double cy, double radius, double axisDeg, double sweepDeg)
    {
        double r = System.Math.Max(0.01, radius);
        double sweep = ClampSweep(sweepDeg);
        double a0 = ToRadians(axisDeg - sweep / 2);
        double a1 = ToRadians(axisDeg + sweep / 2);
        double sx = cx + r * System.Math.Cos(a0);
        double sy = cy + r * System.Math.Sin(a0);
        double ex = cx + r * System.Math.Cos(a1);
        double ey = cy + r * System.Math.Sin(a1);
        int largeArc = sweep > 180 ? 1 : 0; // > 半圆才需要 large-arc 标志
        var sb = new System.Text.StringBuilder(96);
        sb.Append("M ").Append(F(sx)).Append(',').Append(F(sy));
        sb.Append(" A ").Append(F(r)).Append(',').Append(F(r)).Append(" 0 ")
          .Append(largeArc).Append(",1 ").Append(F(ex)).Append(',').Append(F(ey));
        sb.Append(" L ").Append(F(cx)).Append(',').Append(F(cy));
        sb.Append(" Z");
        return sb.ToString();
    }

    /// <summary>把扫掠角夹到 (0, 360)，避免 0/360 全程半径退化与整圆弧段退化。</summary>
    private static double ClampSweep(double deg) => deg is <= 0 or >= 360 ? 359.9999 : deg;

    private static double ToRadians(double deg) => deg * System.Math.PI / 180.0;

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}