using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SymbolDesigner.Converters;

/// <summary>
/// 把任意图元的几何（画布坐标）转成"可用于沿路径分布"的直线段序列。
///
/// 设计要点：
///   - **不按图元类型分支**：圆轮廓、矩形轮廓、履带、布尔结果一律走同一入口 ——
///     只要能产出 Geometry 就能当路径。
///   - **曲线交给 WPF 摊平**：弧 / 贝塞尔由 GetFlattenedPathGeometry 自动摊成折线段，
///     不手写 A 命令解析（那才是多余的"弯弯绕绕"）。
///   - **只取最长轮廓**：圆环 / 布尔结果等多轮廓图形（外圈 + 内孔）若整条串起来，
///     跨轮廓处会出现一条假直线段，副本会被平白撒在两个环之间；取最长轮廓即取外圈。
///   - 输出段首尾相接（segment[i].End == segment[i+1].Start）。
/// </summary>
public static class PolylineToSegments
{
    /// <summary>任意几何 → 直线段序列（只取最长轮廓）。几何为空 / 无轮廓 → 空列表，
    /// 让调用方走"无可用路径"分支。</summary>
    public static IReadOnlyList<PathDistributor.PathSegment> Convert(Geometry? geometry)
    {
        if (geometry is null) return Array.Empty<PathDistributor.PathSegment>();
        if (LongestContour(geometry.GetFlattenedPathGeometry()) is not { } contour)
            return Array.Empty<PathDistributor.PathSegment>();

        var pts = ContourPoints(contour);
        var segments = new List<PathDistributor.PathSegment>(pts.Count);
        for (int i = 0; i + 1 < pts.Count; i++)
            segments.Add(new PathDistributor.PathSegment(pts[i], pts[i + 1]));
        return segments;
    }

    /// <summary>长度最长的轮廓；无有效轮廓 → null。</summary>
    private static PathFigure? LongestContour(PathGeometry flattened)
    {
        PathFigure? best = null;
        double bestLength = -1;
        foreach (var figure in flattened.Figures)
        {
            if (figure.Segments.Count == 0) continue;
            double length = ContourLength(figure);
            if (length > bestLength)
            {
                bestLength = length;
                best = figure;
            }
        }
        return best;
    }

    /// <summary>轮廓折线总长。</summary>
    private static double ContourLength(PathFigure figure)
    {
        var pts = ContourPoints(figure);
        double length = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
            length += (pts[i + 1] - pts[i]).Length;
        return length;
    }

    /// <summary>轮廓的点序列：起点 + 各段折线点；闭合轮廓补回收尾边（末点未回到起点时）。
    /// 摊平后只会出现 PolyLineSegment / LineSegment，故只处理这两种。</summary>
    private static List<Point> ContourPoints(PathFigure figure)
    {
        var pts = new List<Point> { figure.StartPoint };
        foreach (var segment in figure.Segments)
        {
            switch (segment)
            {
                case PolyLineSegment poly:
                    pts.AddRange(poly.Points);
                    break;
                case LineSegment line:
                    pts.Add(line.Point);
                    break;
            }
        }
        if (figure.IsClosed && pts.Count > 1 && (pts[pts.Count - 1] - pts[0]).Length > 1e-6)
            pts.Add(pts[0]);
        return pts;
    }
}
