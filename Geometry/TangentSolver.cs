using System;
using System.Collections.Generic;
using System.Windows;
using SymbolDesigner.Models;

namespace SymbolDesigner.Converters;

/// <summary>
/// 相切约束求解器（用户选定的模式：固定直线为锚，移动圆以满足相切）。
///
/// 几何：
///  - 直线由两个端点 A,B 定义（画布坐标）。
///  - 圆由圆心 C 与半径 r 定义（Ellipse/Ring）。
///  - 相切条件：|C 到直线 AB 的有向距离| == r。
///
/// 求解动作：
///  - SolveTangency(line, circle, side)：
///      圆圆心沿法线方向平移，使有向距离变为 side*r（side=±1 保持建立时的相切侧）。
///      即把 C 投影到直线 AB 上，再把投影点沿法线移出 r。
///  - MoveCircleAlongTangent(personDrag delta)：
///     拖动圆时，法线分量被锁定（保持距离=r），只允许沿直线方向滑动，
///     实现"圆沿相切轨道滑动"的持久约束。
///
/// 端点取法：优先用 PathData（画线工具产物），无 PathData 时回退到
/// X/Y/Width/Height 的包围盒相对边（工具栏拖入的 Line 用对角线）。
/// </summary>
public static class TangentSolver
{
    /// <summary>取直线的两个端点（画布坐标）。返回 false = 无法解析。</summary>
    public static bool TryGetLineEndpoints(PrimitiveItem line, out Point a, out Point b)
    {
        // 路径类（Line/Polyline/BandPolyline 由画线工具产生）优先用 PathData 首末点
        if (!string.IsNullOrEmpty(line.PathData))
        {
            var pts = BandOutlineBuilder.ParsePathData(line.PathData);
            if (pts.Count >= 2)
            {
                a = new Point(line.X + pts[0].X, line.Y + pts[0].Y);
                b = new Point(line.X + pts[^1].X, line.Y + pts[^1].Y);
                return true;
            }
        }

        // 回退：Rectangle/Line 模板的对角线近似；无 PathData 的 Line 用包围盒
        a = new Point(line.X, line.Y + line.Height);
        b = new Point(line.X + line.Width, line.Y);
        return true;
    }

    /// <summary>取圆心与半径。返回 false = 该图元不是圆类目标。</summary>
    public static bool TryGetCircle(PrimitiveItem circle, out Point center, out double radius)
    {
        center = default;
        radius = 0;
        if (circle.TypeKey is not (nameof(L1Primitive.Ellipse) or nameof(L1Primitive.Ring)))
            return false;

        center = new Point(circle.X + circle.Width / 2.0, circle.Y + circle.Height / 2.0);
        radius = Math.Max(1, Math.Min(circle.Width, circle.Height) / 2.0);
        if (circle.TypeKey == nameof(L1Primitive.Ring))
            radius -= PrimitiveItem.RingStrokeHalfThickness; // Ring 描边外扩修正
        return radius > 0;
    }

    /// <summary>
    /// 计算圆的新圆心位置，使直线与圆相切（保持 side 侧）。
    /// 不动直线；圆沿法线方向移动到距离 = r。
    /// 返回 null = 无法求解（退化直线/圆）。
    /// </summary>
    public static Point? SolveTangency(PrimitiveItem line, PrimitiveItem circle, int side)
    {
        if (!TryGetLineEndpoints(line, out var a, out var b)) return null;
        if (!TryGetCircle(circle, out var c, out double r)) return null;

        var dirX = b.X - a.X;
        var dirY = b.Y - a.Y;
        double lenSq = dirX * dirX + dirY * dirY;
        if (lenSq < 1e-9) return null; // 退化成点

        // 单位方向 u，法线 n = (-u.Y, u.X)（左侧法线）。side 翻转符号。
        double len = Math.Sqrt(lenSq);
        double ux = dirX / len, uy = dirY / len;
        double nx = -uy, ny = ux;

        // 圆心到 A 的向量在方向轴上的投影长度 t（垂足 = A + t*u）
        double t = (c.X - a.X) * ux + (c.Y - a.Y) * uy;
        double footX = a.X + t * ux;
        double footY = a.Y + t * uy;

        // 新圆心 = 垂足 + 法线 × r × side
        return new Point(footX + nx * r * side, footY + ny * r * side);
    }

    /// <summary>
    /// 拖动圆（锚 = 直线、圆 = 调整对象）时让直线整体平移以保持相切（双向可带动，CAD 对称语义）。
    /// 圆保持用户拖到的位置不动，直线沿法线平移，使"圆心到直线所在直线有向距离 == side*r"。
    /// 与 ConstrainCircleAlongTangent（圆被锁回直线固定轨道）相反：这里优先让直线动、圆跟手。
    /// </summary>
    public static void TranslateLineToKeepTangent(PrimitiveItem line, PrimitiveItem circle, int side)
    {
        if (!TryGetLineEndpoints(line, out var a, out var b)) return;
        if (!TryGetCircle(circle, out var c, out double r)) return;

        var dirX = b.X - a.X;
        var dirY = b.Y - a.Y;
        double lenSq = dirX * dirX + dirY * dirY;
        if (lenSq < 1e-9) return;

        double len = Math.Sqrt(lenSq);
        double nx = -dirY / len, ny = dirX / len; // 单位法线

        double d = (c.X - a.X) * nx + (c.Y - a.Y) * ny; // 圆心到直线当前有向距离
        double target = side * r;
        double t = d - target; // 直线沿法线需平移量，使有向距离变为 target
        line.X += nx * t;
        line.Y += ny * t;
    }

    /// <summary>
    /// 移动直线使圆与直线相切（锚 = 圆的场景）。
    /// 直线保持方向不变，整体沿法线平移，直到"圆心到直线所在直线的有向距离 == side * r"。
    /// 由于直线整体平移等价于 X/Y 平移（PathData 为局部坐标、模板 Line 用包围盒对角线），
    /// 只更新 line.X/Y，端点、渲染、序列化全部自动跟随。
    /// </summary>
    public static void MoveLineToTangent(PrimitiveItem circle, PrimitiveItem line, int side)
    {
        if (!TryGetCircle(circle, out var c, out double r)) return;
        if (!TryGetLineEndpoints(line, out var a, out var b)) return;

        var dirX = b.X - a.X;
        var dirY = b.Y - a.Y;
        double lenSq = dirX * dirX + dirY * dirY;
        if (lenSq < 1e-9) return; // 退化成点

        double len = Math.Sqrt(lenSq);
        double ux = dirX / len, uy = dirY / len;
        double nx = -uy, ny = ux; // 左侧法线

        // 当前圆心到直线的有向距离（沿法线方向）
        double dist = (c.X - a.X) * nx + (c.Y - a.Y) * ny;
        double target = side * r;
        // 平移量 v = n*delta：新距离 = dist - v·n = dist - delta，令其 = target → delta = dist - target
        double delta = dist - target;

        // 整体平移直线（局部坐标不动，仅移动锚点 X/Y）
        line.X += nx * delta;
        line.Y += ny * delta;
    }

    /// <summary>
    /// 拖动直线（锚 = 圆的调整对象）时的持久约束：
    /// 直线只能沿自身方向滑动（切向），法线方向锁在"到圆心距离 = r"的轨道上。
    /// 输入 recommendedXY = 用户拖动后直线的左上角（绝对坐标）→ 输出修正后的左上角。
    /// 返回 null = 无法求解（退化为不约束）。
    /// </summary>
    public static Point? ConstrainLineAlongTangent(PrimitiveItem circle, PrimitiveItem line, Point recommendedXY)
    {
        if (!TryGetCircle(circle, out var c, out double r)) return null;
        if (!TryGetLineEndpoints(line, out var a, out var b)) return null;

        var dirX = b.X - a.X;
        var dirY = b.Y - a.Y;
        double lenSq = dirX * dirX + dirY * dirY;
        if (lenSq < 1e-9) return null;

        double len = Math.Sqrt(lenSq);
        double ux = dirX / len, uy = dirY / len;
        double nx = -uy, ny = ux;

        // 当前圆心到直线的有向距离，确定相切侧（保持当前侧）
        double dist = (c.X - a.X) * nx + (c.Y - a.Y) * ny;
        double side = dist >= 0 ? 1 : -1;

        // 用户推荐位移 = 新左上角 - 旧左上角（整体平移，端点增量与左上角增量一致）
        var originX = line.X;
        var originY = line.Y;
        double dvx = recommendedXY.X - originX;
        double dvy = recommendedXY.Y - originY;

        // 切向分量（允许），法线分量修正到"距离 = side*r"轨道：
        // 新距离 = dist - v·n = dist - nShift，令其 = finalDist → nShift = dist - finalDist
        double t = dvx * ux + dvy * uy;                       // 沿直线方向投影
        double finalDist = side * r;                          // 目标有向距离
        double nShift = dist - finalDist;                     // 法线修正量

        return new Point(originX + ux * t + nx * nShift,
                         originY + uy * t + ny * nShift);
    }

    /// <summary>取直线某个端点的画布坐标。index=0 取 PathData 首点，1 取末点；
    /// 无 PathData 时按包围盒对角线（index0=左下 A，index1=右上 B）。返回 false = 无法解析。</summary>
    public static bool TryGetLineEndpoint(PrimitiveItem line, int index, out Point p)
    {
        p = default;
        if (!string.IsNullOrEmpty(line.PathData))
        {
            var pts = BandOutlineBuilder.ParsePathData(line.PathData);
            if (pts.Count >= 2)
            {
                var v = pts[index == 0 ? 0 : pts.Count - 1];
                p = new Point(line.X + v.X, line.Y + v.Y);
                return true;
            }
        }
        // 回退：包围盒对角线
        if (index == 0) p = new Point(line.X, line.Y + line.Height);
        else p = new Point(line.X + line.Width, line.Y);
        return true;
    }

    /// <summary>重写直线某个端点为 canvasPoint（画布坐标）。
    /// 仅 PathData 型直线（画线工具产物）可改端点；无 PathData 返回 false。
    /// **只改目标端点的局部坐标，其它端点保持绝对位置不动，不重置 PathData 原点**
    /// （避免把未参与约束的另一端点整体平移带走——那是"另一端点绕圆旋转"的根源）。
    /// 仅更新包围盒 Width/Height（命中 / 选择框用），Path 渲染按局部坐标自然贴位。</summary>
    public static bool SetLineEndpointAt(PrimitiveItem line, int index, Point canvasPoint)
    {
        if (string.IsNullOrEmpty(line.PathData)) return false;
        var pts = BandOutlineBuilder.ParsePathData(line.PathData);
        int target = index == 0 ? 0 : pts.Count - 1;

        // 保持基准 line.X/Y 不动，只把目标端点设为该基准下的新局部坐标
        pts[target] = new Point(canvasPoint.X - line.X, canvasPoint.Y - line.Y);

        // 重算包围盒尺寸（仅更新 Width/Height，不平移原点、不移动其它端点）
        double minX = pts[0].X, minY = pts[0].Y;
        double maxX = minX, maxY = minY;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        line.Width = Math.Max(2, maxX - minX);
        line.Height = Math.Max(2, maxY - minY);

        line.PathData = BuildPathDataLiteral(pts);
        return true;
    }

    /// <summary>把 PathData 点序列序列化为 "M x,y L x,y ..." 字符串（InvariantCulture）。</summary>
    private static string BuildPathDataLiteral(List<Point> pts)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pts.Count; i++)
        {
            if (i == 0) sb.Append("M ");
            else sb.Append(" L ");
            sb.Append(pts[i].X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(pts[i].Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>把直线端点 P 径向投影到圆上最近点（重合：端点落在圆弧上）。
    /// 返回新的端点画布坐标；P 已在圆上 → 原样返回（幂等）。退化 P==圆心 → 取 +x 方向。</summary>
    public static Point? SolveEndpointOnArc(PrimitiveItem circle, PrimitiveItem line, int endpointIndex)
    {
        if (!TryGetCircle(circle, out var c, out double r)) return null;
        if (!TryGetLineEndpoint(line, endpointIndex, out var p)) return null;

        double dx = p.X - c.X, dy = p.Y - c.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return new Point(c.X + r, c.Y); // 端点恰在圆心 → 取 +x 出圆
        return new Point(c.X + dx / len * r, c.Y + dy / len * r);
    }

    /// <summary>把圆心投影到直线 AB 上（重合：圆心落在直线上）。
    /// 直线保持不动；圆移到垂足处。仅改 circle.X/Y（宽度不变）。</summary>
    public static void PlaceCircleCenterOnLine(PrimitiveItem line, PrimitiveItem circle)
    {
        if (!TryGetLineEndpoints(line, out var a, out var b)) return;
        if (!TryGetCircle(circle, out var c, out _)) return;

        var dirX = b.X - a.X;
        var dirY = b.Y - a.Y;
        double lenSq = dirX * dirX + dirY * dirY;
        if (lenSq < 1e-9) return;

        double t = ((c.X - a.X) * dirX + (c.Y - a.Y) * dirY) / lenSq;
        PlaceCircleByCenter(circle, new Point(a.X + t * dirX, a.Y + t * dirY));
    }

    /// <summary>把直线某个端点移动到圆心（点-点重合）。仅 PathData 型直线可改端点。</summary>
    public static void PlaceLineEndpointAtCircleCenter(PrimitiveItem circle, PrimitiveItem line, int endpointIndex)
    {
        if (!TryGetCircle(circle, out var c, out _)) return;
        SetLineEndpointAt(line, endpointIndex, c);
    }

    /// <summary>判断直线端点的画布坐标（用于求解后定位椭圆中心到圆心偏移）。</summary>
    public static Point GetCircleCanvasCenter(PrimitiveItem circle)
        => new(circle.X + circle.Width / 2.0, circle.Y + circle.Height / 2.0);

    /// <summary>根据新圆心反算 X/Y（保持 Width/Height 不变，圆心=X+W/2）。</summary>
    public static void PlaceCircleByCenter(PrimitiveItem circle, Point center)
    {
        circle.X = center.X - circle.Width / 2.0;
        circle.Y = center.Y - circle.Height / 2.0;
    }

    /// <summary>
    /// 把直线端点解到"圆与直线的切点"（用于双重约束：相切 + 端点落圆弧）。
    /// 当圆已与直线相切（圆到直线距离 = r）时，直线与圆唯一公共点即切点 = 圆心沿法线
    /// 回到直线方向的点（垂足）。此方法把端点吸到该切点位置，使其既是圆上点又满足相切。
    /// 返回 null = 无法解析（退化直线/圆）。
    /// </summary>
    public static Point? SolveEndpointAtTangentPoint(PrimitiveItem line, PrimitiveItem circle, int endpointIndex)
    {
        _ = endpointIndex; // 切点与选哪个端点无关：就是把"那个端点"移动到切点
        if (!TryGetLineEndpoints(line, out var a, out var b)) return null;
        if (!TryGetCircle(circle, out var c, out double r)) return null;

        var dirX = b.X - a.X;
        var dirY = b.Y - a.Y;
        double lenSq = dirX * dirX + dirY * dirY;
        if (lenSq < 1e-9) return null;

        double len = Math.Sqrt(lenSq);
        double ux = dirX / len, uy = dirY / len;
        double nx = -uy, ny = ux; // 单位法线

        // 有向距离 d（±）；切点 = 圆上沿"离开直线方向"外移 r 的点 = c - n*d（|d|=r 时即垂足）
        double d = (c.X - a.X) * nx + (c.Y - a.Y) * ny;
        double f = Math.Clamp(Math.Abs(d), 0, r) * (d >= 0 ? 1 : -1); // 限制在圆内法向范围，保证落在圆上
        return new Point(c.X - nx * f, c.Y - ny * f);
    }
}