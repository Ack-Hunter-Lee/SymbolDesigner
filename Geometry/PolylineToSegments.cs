using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace SymbolDesigner.Converters;

/// <summary>
/// 把 ThickPolyline 的 PathData（"M x,y L x,y L ..."）解析成 PathDistributor.PathSegment 列表。
///
/// 第一阶段（沿路径阵列）：只支持 M + L 命令的直线序列。后续阶段扩展 A（ArcSegment）时
/// 在此处加分支：识别 'A' 后用 ArcSegment 的 4 个 flag 算圆心（IsLargeArc + SweepDirection 4 选 1）。
///
/// 设计要点：
///   - PathData 与 WPF 标准兼容：逗号/空格混用、'M'/'L' 大小写都接受
///   - 解析失败（点数 &lt; 2）→ 返回空列表（让调用方走"无可用路径"分支）
///   - 复用 ThickOutlineBuilder.ParsePathData 的解析逻辑（保证点序列一致）
/// </summary>
public static class PolylineToSegments
{
    /// <summary>把 PathData 字符串转成 PathSegment 列表。
    /// 返回的每个 segment 是直线段（ArcCenter=null），段与段首尾相接（segment[i].End == segment[i+1].Start）。
    /// 返回 IReadOnlyList 以匹配 PathDistributor.DistributeAlongPath 的入参类型（算法侧只读）。</summary>
    public static IReadOnlyList<PathDistributor.PathSegment> Convert(string pathData)
    {
        var segments = new List<PathDistributor.PathSegment>();
        var pts = ThickOutlineBuilder.ParsePathData(pathData);
        for (int i = 0; i + 1 < pts.Count; i++)
            segments.Add(new PathDistributor.PathSegment(pts[i], pts[i + 1]));
        return segments;
    }
}