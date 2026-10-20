using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>画布选中图元的各类拖拽手柄（顶点/段/带宽线/圆环/轮廓接缝）的集合维护与刷新。</summary>
public partial class SymbolDesignerViewModel
{
    // ============== 顶点编辑（BandPolyline 选中后可拖拽顶点改长度） ==============

    /// <summary>当前选中 BandPolyline 的顶点手柄集合（画布坐标）。
    /// 仅当 SelectedItem 是 BandPolyline 时填充，否则为空。
    /// View 用 ItemsControl + Canvas 渲染每个手柄。</summary>
    public ObservableCollection<VertexHandle> VertexHandles { get; } = new();

    /// <summary>根据 SelectedItem.PathData 刷新 VertexHandles。
    /// 解析 PathData 顶点（局部坐标），加上 SelectedItem.X/Y 转画布坐标。
    /// 适用范围：任意具有 PathData 的图元（BandPolyline / 画线工具产物的 Polyline / Line）。
    /// 工具栏拖入的 Line/Polyline（PathData 为空）不出端点手柄——其端点依赖 LineAxis+Rotation 解析，
    /// 拖动改 X/Y/W/H 时易与渲染错位，保持现状仅支持画线工具产物。
    /// BooleanPath：轮廓点不可拖拽（需求），骨架模式下不显示可拖手柄；
    /// 选中/设样式（Miter/Round）/圆角一律走轮廓模式的 OutlineCornerHandles（见 RefreshOutlineCornerHandles）。</summary>
    private void RefreshVertexHandles()
    {
        VertexHandles.Clear();
        if (SelectedItem is not { } item) return;
        if (item.IsLocked) return;   // 锁定图元不渲染顶点手柄
        if (string.IsNullOrEmpty(item.PathData)) return;

        // BooleanPath：骨架模式不出拖拽手柄（轮廓点不允许拖拽）
        if (item.TypeKey == nameof(L1Primitive.BooleanPath)) return;

        var pts = Converters.BandOutlineBuilder.ParsePathData(item.PathData);
        for (int i = 0; i < pts.Count; i++)
            VertexHandles.Add(new VertexHandle(i, item.X + pts[i].X, item.Y + pts[i].Y));
    }

    /// <summary>顶点手柄被拖拽时调用：更新顶点坐标到 SelectedItem.PathData。
    /// index=顶点序号（与 RefreshVertexHandles 同序），newCanvasX/Y=画布坐标（含 SelectedItem.X/Y 偏移）。
    /// BooleanPath：布尔轮廓点不允许拖拽 → 直接忽略（样式/圆角走轮廓模式 OutlineCornerHandle）。</summary>
    public void UpdateVertexPosition(int index, double newCanvasX, double newCanvasY)
    {
        if (SelectedItem is not { } item) return;
        if (item.IsLocked) return;   // 锁定图元禁止顶点编辑
        if (string.IsNullOrEmpty(item.PathData)) return;
        if (item.TypeKey == nameof(L1Primitive.BooleanPath))
            return;  // 布尔结果轮廓点不可拖拽（需求），骨架模式也不显示手柄，此分支为防御

        // 把画布坐标转回 Path 局部坐标
        var localX = newCanvasX - item.X;
        var localY = newCanvasY - item.Y;

        // 重写 PathData；通过 _suppressVertexRefresh 跳过 OnSelectedItemPathDataChanged，
        // 因为手柄位置已经在 View 端通过 TwoWay 绑定跟随鼠标更新，不需要重建集合。
        _suppressVertexRefresh = true;
        try
        {
            var pts = Converters.BandOutlineBuilder.ParsePathData(item.PathData);
            if (index < 0 || index >= pts.Count) return;
            pts[index] = new Point(localX, localY);

            item.PathData = Converters.GeometryMath.BuildPathData(pts);
            // 顶点数不变时 SyncVertexBandWidths 为 no-op；防御性调用，避免未来加/删顶点逻辑不同步
            item.SyncVertexBandWidths(pts.Count, item.BandWidth);
        }
        finally
        {
            _suppressVertexRefresh = false;
        }
        // 顶点移动后，附近段的中点也跟着变→刷新段手柄
        RefreshSegmentHandles();
        // 顶点移动后接缝外尖点也跟着变→刷新轮廓手柄
        if (OutlineEditMode == OutlineEditMode.Outline)
            RefreshOutlineCornerHandles();
        RefreshBandWidthHandles();

        // 顶点移动后，包围盒（X/Y/Width/Height）可能过小（顶点超出原框）。
        // 重算 bbox，让 X/Y 跟随顶点位置，确保所有顶点在可见范围内。
        Converters.GeometryMath.RecalcBoundingBox(item);

        // 持久约束：直线端点被拖动 → 圆跟随保持相切
        OnItemGeometryChanged(item);
    }

    // ============== 段拖拽（BandPolyline 选中后可拖拽整段线段平移） ==============

    /// <summary>当前选中 BandPolyline 的段拖拽手柄集合（画布坐标的中点）。
    /// 与 VertexHandles 同步刷新（同一 SelectedItem 选中时）。</summary>
    public ObservableCollection<SegmentDragHandle> SegmentHandles { get; } = new();

    /// <summary>带宽线端点手柄集合：每个段 4 个（L/R × Start/End）。
    /// 仅在 OutlineEditMode.BandWidth 且 SelectedItem 是 BandPolyline 时填充。
    /// 阶段 3 实现：手算端点（顶点 + 段方向法向量 × 半带宽）。后续可复用 CachedOutlineSteps 取真实端点。</summary>
    public ObservableCollection<BandWidthHandle> BandWidthHandles { get; } = new();

    /// <summary>圆环拖拽手柄集合：中心 1 + 外圆 1（东侧）+ 内圆 1（东侧）= 3 个。
    /// 简化设计：水平方向各 1 个手柄足够调节半径（拖动距离 = 半径增量），上下/半径跳跃需更复杂
    /// 抽象；用户需求：少而精，不做 8 个。
    /// 仅 SelectedItem 为 L1 Ring 时填充（画布坐标，跟随圆心/半径变化刷新）。</summary>
    public ObservableCollection<RingHandle> RingHandles { get; } = new();

    /// <summary>当前高亮的骨点序号（点击"骨点 i"组标题后高亮对应 Slider 的两个端点）。
    /// -1 = 全部不亮。仅 BandWidth 模式下有效。</summary>
    [ObservableProperty] private int _selectedBonePointIndex = -1;

    /// <summary>刷新 SegmentHandles：每段一个，中点为该段两端顶点的平均。</summary>
    private void RefreshSegmentHandles()
    {
        SegmentHandles.Clear();
        if (SelectedItem is not { } item) return;
        if (item.IsLocked) return;   // 锁定图元不渲染段手柄
        if (item.TypeKey != nameof(L1Primitive.BandPolyline))
            return;
        if (string.IsNullOrEmpty(item.PathData)) return;

        var pts = Converters.BandOutlineBuilder.ParsePathData(item.PathData);
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var midX = item.X + (pts[i].X + pts[i + 1].X) * 0.5;
            var midY = item.Y + (pts[i].Y + pts[i + 1].Y) * 0.5;
            SegmentHandles.Add(new SegmentDragHandle(i, midX, midY));
        }
    }

    /// <summary>刷新 BandWidthHandles：每段 4 个端点（L/R × Start/End）。
    /// 几何：段 i 方向 d = (pts[i+1] - pts[i]).Normalize()；左侧法线 n = (d.Y, -d.X)。
    ///   LeftStart  = pts[i]   + n × (vt[i].OutgoingBandWidth / 2)
    ///   LeftEnd    = pts[i+1] + n × (vt[i+1].IncomingBandWidth / 2)
    ///   RightStart = pts[i]   - n × ...
    ///   RightEnd   = pts[i+1] - n × ...
    /// 缺 vt[i]/vt[i+1] 时回退 item.BandWidth（与 OutlineGeometry 兜底一致）。
    /// 阶段 3 仅显示，不带拖拽；坐标更新 = SelectedItem 变化 / PathData 改 / 带宽改 / 模式切到 BandWidth。</summary>
    private void RefreshBandWidthHandles()
    {
        BandWidthHandles.Clear();
        if (SelectedItem is not { } item) return;
        if (item.IsLocked) return;   // 锁定图元不渲染带宽手柄
        if (item.TypeKey != nameof(L1Primitive.BandPolyline))
            return;
        if (string.IsNullOrEmpty(item.PathData)) return;
        if (item.BandWidth <= 0) return;

        var pts = Converters.BandOutlineBuilder.ParsePathData(item.PathData);
        if (pts.Count < 2) return;

        int n = pts.Count;
        // 优先使用逐顶点带宽（与 OutlineGeometry 同一来源）；不足回退整体 BandWidth
        var vt = item.VertexBandWidths;
        double HalfAt(int vi) => (vi >= 0 && vi < vt.Count ? vt[vi].OutgoingBandWidth : item.BandWidth) * 0.5;
        double HalfIncoming(int vi) => (vi >= 0 && vi < vt.Count ? vt[vi].IncomingBandWidth : item.BandWidth) * 0.5;

        // 同步每个顶点的"入端/出端"显示标志：
        // - 首顶点：无入端；末顶点：无出端
        // - 合并模式（CollinearSplitPairs=false）下，共线同向直通接缝的中间顶点只有出端
        //   （几何合并后取后段首端=出端，入端被合并掉，UI 不应再显示）
        bool splitPairs = item.CollinearSplitPairs;
        for (int i = 0; i < vt.Count && i < n; i++)
        {
            bool isCollinearMergeVertex = false;
            if (!splitPairs && i > 0 && i < n - 1)
            {
                var v1 = new Vector(pts[i].X - pts[i - 1].X, pts[i].Y - pts[i - 1].Y);
                var v2 = new Vector(pts[i + 1].X - pts[i].X, pts[i + 1].Y - pts[i].Y);
                // 与 Builder 判定一致：共线（行列式≈0）且同向（点积>0）
                if (Math.Abs(Vector.Determinant(v1, v2)) < 1e-9 && v1 * v2 > 0)
                    isCollinearMergeVertex = true;
            }
            vt[i].ShowIncoming = i > 0 && !isCollinearMergeVertex;
            vt[i].ShowOutgoing = i < n - 1;
        }

        for (int i = 0; i < pts.Count - 1; i++)
        {
            var dx = pts[i + 1].X - pts[i].X;
            var dy = pts[i + 1].Y - pts[i].Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;  // 退化段：与 Builder 一致跳过
            // 左侧法线（顺时针 90°）= (d.Y, -d.X) / len
            double nx = dy / len;
            double ny = -dx / len;
            double halfStart = HalfAt(i);          // 段 i 首端带宽半值（= vt[i].Outgoing）
            double halfEnd = HalfIncoming(i + 1);  // 段 i 末端带宽半值（= vt[i+1].Incoming）

            // Left Start（映射骨点 i，出端 ▼）
            BandWidthHandles.Add(new BandWidthHandle(i, "L", "Start", i,
                item.X + pts[i].X + nx * halfStart,
                item.Y + pts[i].Y + ny * halfStart));
            // Right Start（出端 ▼）
            BandWidthHandles.Add(new BandWidthHandle(i, "R", "Start", i,
                item.X + pts[i].X - nx * halfStart,
                item.Y + pts[i].Y - ny * halfStart));
            // 入端端点（▲）：顶点 i+1 的入端被隐藏（首顶点 / 合并共线顶点）时整体跳过
            bool showIn = i + 1 < vt.Count && vt[i + 1].ShowIncoming;
            if (!showIn) continue;
            // Left End（映射骨点 i+1，入端 ▲）
            BandWidthHandles.Add(new BandWidthHandle(i, "L", "End", i + 1,
                item.X + pts[i + 1].X + nx * halfEnd,
                item.Y + pts[i + 1].Y + ny * halfEnd));
            // Right End（入端 ▲）
            BandWidthHandles.Add(new BandWidthHandle(i, "R", "End", i + 1,
                item.X + pts[i + 1].X - nx * halfEnd,
                item.Y + pts[i + 1].Y - ny * halfEnd));
        }
    }

    // ============== 圆环（L1 Ring）拖拽手柄 ==============

    /// <summary>刷新 RingHandles：中心 1 + 外圆 1（东侧）+ 内圆 1（东侧）= 3 个。
    /// 水平方向 1 个手柄即可调整半径（拖动距离 = 圆心到鼠标的水平距离 = 半径）。
    /// SelectedItem 为 L1 Ring 时按当前圆心/半径填充；其它情况清空。
    /// 调用时机：SelectedItem 变化 / X·Y·Width·Height·InnerRadius 变化 / 加载完成。</summary>
    private void RefreshRingHandles()
    {
        RingHandles.Clear();
        if (SelectedItem is not { } item) return;
        if (item.IsLocked) return;   // 锁定图元不渲染圆环手柄
        if (item.TypeKey != nameof(L1Primitive.Ring)) return;
        if (item.Width <= 0 || item.Height <= 0 || item.InnerRadius <= 0) return;

        double cx = item.X + item.Width / 2;
        double cy = item.Y + item.Height / 2;
        double outer = Math.Min(item.Width, item.Height) / 2;
        double inner = Math.Clamp(item.InnerRadius, 0.01, outer - 0.01);

        // 中心手柄（拖动 = 平移）
        RingHandles.Add(new RingHandle(RingHandleKind.Center, cx, cy));
        // 外圆手柄：东侧（拖动水平距离 = 外径，最直观）
        RingHandles.Add(new RingHandle(RingHandleKind.Outer, cx + outer, cy, RingHandleDirection.E));
        // 内圆手柄：东侧（拖动水平距离 = 内径，最直观）
        RingHandles.Add(new RingHandle(RingHandleKind.Inner, cx + inner, cy, RingHandleDirection.E));
    }

    /// <summary>圆环手柄拖拽：根据手柄类型 + 鼠标在画布上的位置，更新圆环几何。
    /// Center = 平移圆心（X/Y 整体移动，保持半径）；Outer = 改外径（Width/Height 等变，圆心不动）；
    /// Inner = 改内径（InnerRadius，现对同一方向手柄来说以"对侧角度手柄"为参考）。
    /// 拖拽期间手柄集合不重建（保持 9 个元素的引用稳定，避免 ItemsControl 闪烁）。</summary>
    public void UpdateRingHandle(RingHandleKind kind, double mouseX, double mouseY)
    {
        if (SelectedItem is not { } item) return;
        if (item.IsLocked) return;   // 锁定图元禁止圆环手柄拖拽
        if (item.TypeKey != nameof(L1Primitive.Ring)) return;

        double halfW = item.Width / 2;
        double halfH = item.Height / 2;
        double cx = item.X + halfW;
        double cy = item.Y + halfH;

        switch (kind)
        {
            case RingHandleKind.Center:
                // 平移圆心：半尺寸不变，直接改 X/Y（图形跟着中心动）
                item.X = mouseX - halfW;
                item.Y = mouseY - halfH;
                break;

            case RingHandleKind.Outer:
                // 改外径：以圆心为中心对称扩展（半径 = 圆心到鼠标的距离）
                {
                    double dX = mouseX - cx;
                    double dY = mouseY - cy;
                    double r = Math.Max(2, Math.Sqrt(dX * dX + dY * dY));
                    double half = r;
                    item.X = cx - half;
                    item.Y = cy - half;
                    item.Width = r * 2;
                    item.Height = r * 2;
                    // 内径不能超过外径
                    if (item.InnerRadius >= r)
                    {
                        item.InnerRadius = r * 0.5;
                    }
                }
                break;

            case RingHandleKind.Inner:
                // 改内径：内圆半径 = 圆心到鼠标的距离，且必须小于外径
                {
                    double dX = mouseX - cx;
                    double dY = mouseY - cy;
                    double r = Math.Sqrt(dX * dX + dY * dY);
                    double outer = Math.Min(item.Width, item.Height) / 2;
                    item.InnerRadius = Math.Clamp(r, 1, Math.Max(1.5, outer - 0.5));
                }
                break;
        }

        // 同步手柄位置（集合不重建，直接更新各手柄的 X/Y）
        // 手柄布局：Center(0=中) + Outer East(1=外圆东) + Inner East(2=内圆东)
        for (int i = 0; i < RingHandles.Count; i++)
        {
            var h = RingHandles[i];
            if (h.Kind == RingHandleKind.Center)
            {
                h.X = item.X + item.Width / 2;
                h.Y = item.Y + item.Height / 2;
            }
            else // Outer / Inner 都在东侧
            {
                double ox = item.X + item.Width / 2;
                double oy = item.Y + item.Height / 2;
                double r = h.Kind == RingHandleKind.Outer
                    ? Math.Min(item.Width, item.Height) / 2
                    : Math.Clamp(item.InnerRadius, 0.01, Math.Min(item.Width, item.Height) / 2 - 0.01);
                h.X = ox + r;
                h.Y = oy;
            }
        }
    }

    // ============== 扇形（L1 Pie）拖拽手柄 ==============

    /// <summary>扇形的选中拖拽手柄集合：中心 1 + 半径 1（对称中轴鼻尖）+ 起始/终止弧端点 2 = 4 个。
    /// 显示在画布上（仅选中 Pie 时），用于平移/改半径/对称扫掠角调整。</summary>
    public ObservableCollection<PieHandle> PieHandles { get; } = new();

    /// <summary>刷新 PieHandles：中心 1 + 半径 1（对称中轴鼻尖）+ 起始/终止弧端点 2 = 4 个。
    /// 对称模型：StartAngle=对称中轴角，SweepAngle=总张角，弧区 = [中轴-张角/2, 中轴+张角/2]。
    /// 半径手柄放在中轴鼻尖（角度=中轴），起始/终止手柄放在两个真实弧端点，全部贴合扇面（避免飘出）。
    /// SelectedItem 为 Pie 时按当前圆心/半径/角度填充；其它情况清空。
    /// 调用时机：SelectedItem 变化 / X·Y·Width·Height·StartAngle·SweepAngle 变化 / 加载完成。</summary>
    private void RefreshPieHandles()
    {
        PieHandles.Clear();
        if (SelectedItem is not { } item) return;
        if (item.IsLocked) return;   // 锁定图元不渲染扇形手柄
        if (item.TypeKey != nameof(L1Primitive.Pie)) return;
        if (item.Width <= 0 || item.Height <= 0) return;

        double cx = item.X + item.Width / 2;
        double cy = item.Y + item.Height / 2;
        double r = Math.Min(item.Width, item.Height) / 2;
        double axis = item.StartAngle * Math.PI / 180;
        double half = item.SweepAngle * Math.PI / 180 / 2;
        double a0 = axis - half; // 起始弧端点（中轴-张角/2）
        double a1 = axis + half; // 终止弧端点（中轴+张角/2）

        PieHandles.Add(new PieHandle(PieHandleKind.Center, cx, cy));
        PieHandles.Add(new PieHandle(PieHandleKind.Radius, cx + r * Math.Cos(axis), cy + r * Math.Sin(axis)));
        PieHandles.Add(new PieHandle(PieHandleKind.Start, cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)));
        PieHandles.Add(new PieHandle(PieHandleKind.End, cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)));
    }

    /// <summary>扇形手柄拖拽：根据手柄类型 + 鼠标在画布上的位置，更新扇形几何。
    /// Center = 平移圆心（X/Y 整体移动，半径/角度不变）；
    /// Radius = 改半径（Width/Height 等比变，圆心不动，手柄在中轴鼻尖）；
    /// Start / End = 对称扫掠角调整：固定对称中轴，张角 = 2 × |鼠标方位 − 中轴| 的短夹角（两端对称扩张）。</summary>
    public void UpdatePieHandle(PieHandleKind kind, double mouseX, double mouseY)
    {
        if (SelectedItem is not { } item) return;
        if (item.IsLocked) return;   // 锁定图元禁止扇形手柄拖拽
        if (item.TypeKey != nameof(L1Primitive.Pie)) return;

        double halfW = item.Width / 2;
        double halfH = item.Height / 2;
        double cx = item.X + halfW;
        double cy = item.Y + halfH;

        switch (kind)
        {
            case PieHandleKind.Center:
                item.X = mouseX - halfW;
                item.Y = mouseY - halfH;
                break;

            case PieHandleKind.Radius:
                {
                    double dX = mouseX - cx;
                    double dY = mouseY - cy;
                    double r = Math.Max(2, Math.Sqrt(dX * dX + dY * dY));
                    item.X = cx - r;
                    item.Y = cy - r;
                    item.Width = r * 2;
                    item.Height = r * 2;
                }
                break;

            case PieHandleKind.Start:
            case PieHandleKind.End:
                {
                    // 固定对称中轴（StartAngle），被拖弧端点方位 a 决定张角的一半。
                    double a = AngleOf(cx, cy, mouseX, mouseY);
                    double axis = item.StartAngle;
                    // 中轴到端点的短夹角（范围 (-180,180]），对称张角 = 2×|短夹角|
                    double half = Math.Abs((a - axis + 180) % 360 - 180);
                    item.SweepAngle = Math.Clamp(half * 2, 1, 359.999);
                }
                break;
        }

        // 同步手柄位置（集合不重建，直接更新各手柄 X/Y）
        for (int i = 0; i < PieHandles.Count; i++)
        {
            var h = PieHandles[i];
            double r = Math.Min(item.Width, item.Height) / 2;
            double kx = item.X + item.Width / 2;
            double ky = item.Y + item.Height / 2;
            switch (h.Kind)
            {
                case PieHandleKind.Center:
                    h.X = kx;
                    h.Y = ky;
                    break;
                case PieHandleKind.Radius:
                    {
                        double ax = item.StartAngle * Math.PI / 180;
                        h.X = kx + r * Math.Cos(ax);
                        h.Y = ky + r * Math.Sin(ax);
                        break;
                    }
                default:
                    {
                        double off = (h.Kind == PieHandleKind.Start ? -0.5 : 0.5) * item.SweepAngle;
                        double ang = (item.StartAngle + off) * Math.PI / 180;
                        h.X = kx + r * Math.Cos(ang);
                        h.Y = ky + r * Math.Sin(ang);
                        break;
                    }
            }
        }
    }

    /// <summary>计算鼠标相对圆心 (cx,cy) 的方位角（度，0=东，屏幕顺时针正值，y 向下）。</summary>
    private static double AngleOf(double cx, double cy, double mx, double my)
    {
        double deg = Math.Atan2(my - cy, mx - cx) * 180 / Math.PI;
        return (deg % 360 + 360) % 360;
    }

    /// <summary>扇形对称中轴朝向吸附：吸到 45°倍数 ∪ 30°倍数（含水平/竖直 0/90/180/270、45/135/225/315、30/60/120/150/...）。
    /// 容差 8°；命中时返回最近的特殊朝向，否则返回原角度。绘制朝向 / 手柄搬运共用，保证预览与提交一致。</summary>
    private static double SnapPieAxis(double deg)
    {
        int[] specials = { 0, 30, 45, 60, 90, 120, 135, 150, 180, 210, 225, 240, 270, 300, 315, 330 };
        double best = deg;
        double bestDelta = 8.0;
        foreach (int s in specials)
        {
            double d = Math.Abs((deg - s + 540) % 360 - 180); // 最小角度差 (0,180]
            if (d < bestDelta)
            {
                bestDelta = d;
                best = s;
            }
        }
        return best;
    }

    // ============== 轮廓接缝编辑（OutlineEditMode.Outline 模式下显示） ==============

    /// <summary>当前选中 BandPolyline 的轮廓接缝手柄集合（画布坐标）。
    /// 仅在 OutlineEditMode.Outline 且 SelectedItem 是 BandPolyline 且至少一个内部接缝时填充。
    /// 每个手柄对应一个内部接缝的外尖点（miter 顶点）。点击手柄 → SelectedOutlineCorner = handle。</summary>
    public ObservableCollection<OutlineCornerHandle> OutlineCornerHandles { get; } = new();

    /// <summary>根据 SelectedItem.PathData + BandWidth 刷新 OutlineCornerHandles。
    /// 调用 BuildOutlineVertices 获取轮廓线所有连接点，每个点创建一个手柄。</summary>
    private void RefreshOutlineCornerHandles()
    {
        OutlineCornerHandles.Clear();
        if (SelectedItem is not { } item) return;
        if (item.IsLocked) return;   // 锁定图元不渲染轮廓手柄
        bool isBand = item.TypeKey == nameof(L1Primitive.BandPolyline);
        bool isClosedPolyline = item.TypeKey == nameof(L1Primitive.Polyline) && item.IsClosed;
        bool isBoolean = item.TypeKey == nameof(L1Primitive.BooleanPath);
        // 轮廓手柄：BandPolyline（带宽双环）/ 已闭合的 Polyline（单环骨架点）/ BooleanPath（多环骨架）可用
        if (!isBand && !isClosedPolyline && !isBoolean) return;
        if (string.IsNullOrEmpty(item.PathData)) return;
        if (item.BandWidth <= 0 && !isClosedPolyline && !isBoolean) return;

        var pts = Converters.BandOutlineBuilder.ParsePathData(item.PathData);
        int n;
        if (isBoolean)
        {
            // 布尔快照带 F0 前缀：ParsePathData 解析不出骨架，段结构骨架点 = 每环段端点
            n = Converters.BooleanOps.GetSkeletonCount(item.PathData);
            if (n < 2) return;
        }
        else
        {
            if (pts.Count < 2) return;
            n = pts.Count;
        }

        // 直接复用渲染用的缓存步骤（CachedOutlineSteps）：每个步骤的 Point 就是轮廓上的真实点，
        // 已包含逐顶点带宽 / 接缝样式 / 圆角化改动，与画布上看到的轮廓保证一致，无需再按带宽重算。
        // 先强制触发一次 OutlineGeometry：若输入（带宽/样式/PathData）已变，getter 会先重算步骤再返回。
        var _ = item.OutlineGeometry;
        var steps = item.CachedOutlineSteps;
        if (steps is null || steps.Count == 0)
        {
            // 兜底：缓存不可用（如尚未渲染）时按当前输入现算。
            if (isBoolean)
            {
                // 布尔结果：骨架点步骤（段端点，弧不摊平）——仅作手柄定位，不参与渲染
                steps = Converters.BandOutlineBuilder.BuildClosedShapeSegmentsSteps(item.PathData, null);
            }
            else if (isClosedPolyline)
            {
                steps = Converters.BandOutlineBuilder.BuildClosedShapeSteps(item.PathData, null);
            }
            else
            {
                // 带宽必须用逐顶点值（与 OutlineGeometry 相同逻辑），否则共享顶点两侧等宽 → 偏移点重合 → 手柄少显示。
                var vt = new VertexBandWidth[n];
                if (item.VertexBandWidths.Count == n)
                {
                    for (int i = 0; i < n; i++) vt[i] = item.VertexBandWidths[i];
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        bool isStart = i == 0;
                        bool isEnd = i == n - 1;
                        vt[i] = new VertexBandWidth(i, item.BandWidth, item.BandWidth, isStart, isEnd);
                    }
                }
                var jss = new JoinStyle[n];
                for (int i = 0; i < n; i++)
                {
                    if (i == 0 || i == n - 1)
                        jss[i] = JoinStyle.Miter;
                    else
                        jss[i] = item.GetVertexJoinStyle(i) ?? item.OutlineJoinStyle;
                }
                steps = Converters.BandOutlineBuilder.BuildOutlineSteps(item.PathData, vt, jss, item.CollinearSplitPairs);
            }
        }

        // 每个步骤占 1 个手柄槽（PointStep / BevelStep / ArcStep 都用基类 Point 定位）。
        // 优点：手柄索引 StepIndex 严格 = CachedOutlineSteps[i]，圆角化操作零成本、零歧义。
        // 全部 2N 个 emit 步骤都建手柄（含首末点左右两侧），便于用户编辑任意接缝。
        // 闭合多边形（Polyline 单环 / BooleanPath 多环）无左右侧：统一 Side="L"（对应 LeftCornerRadius 圆角源）。
        int halfSteps = steps.Count / 2;

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            string side = (isClosedPolyline || isBoolean) ? "L" : i < halfSteps ? "L" : "R";

            // 估算对应骨架顶点 vi：优先用步骤自带的 BoneIndex（ComputeMiterOutline 显式记录）。
            // 旧公式 i→i / n-1-(i-halfSteps) 在拆双对模式下（lefts 长 2n-2 > n）会越界/错位，
            // 导致手柄绑定到错误的 VertexBandWidth 半径字段。
            int vi = step.BoneIndex;
            if (vi < 0)
                vi = i < halfSteps ? i : (n - 1 - (i - halfSteps));

            string stepKind = step switch
            {
                Converters.ArcStep => "ArcStep",
                Converters.BevelStep => "BevelStep",
                _ => "PointStep",
            };

            // 沿 emit 顺序算 InDir（前一边方向）/ OutDir（后一边方向）。
            // emit 序列是闭合的：最后一步的"后边"= 起点闭合方向（步骤0-最后一步方向）；
            // 步骤0的"前边"= 闭合方向（最后一步→步骤0）。
            // 单段折线 steps.Count=4：闭合方向 = step[0].Point - step[3].Point。
            Vector inDir, outDir;
            if (i > 0)
                inDir = step.Point - steps[i - 1].Point;
            else
                inDir = step.Point - steps[steps.Count - 1].Point;  // 闭合起点
            if (i < steps.Count - 1)
                outDir = steps[i + 1].Point - step.Point;
            else
                outDir = steps[0].Point - step.Point;  // 闭合终点→起点
            inDir.Normalize();
            outDir.Normalize();

            double canvasX = item.X + step.Point.X;
            double canvasY = item.Y + step.Point.Y;
            JoinStyle? perStyle = item.GetVertexJoinStyle(vi);
            JoinStyle effectiveStyle = perStyle ?? item.OutlineJoinStyle;
            // 圆角半径 getter/setter：lambda 捕获 item，按 IsCorner/Side 路由到不同源字段。
            // XAML TwoWay 写值时调 ApplyRadiusToSelectedHandle → 改 VertexBandWidth 字段 + 刷 OutlineGeometry。
            Func<OutlineCornerHandle, double> getRadius = h =>
            {
                int v = h.Index;
                if (v < 0 || v >= item.VertexBandWidths.Count) return 0;
                var vt = item.VertexBandWidths[v];
                if (h.IsCorner) return vt.JoinInnerRadius;
                return h.Side == "L" ? vt.LeftCornerRadius : vt.RightCornerRadius;
            };
            Action<OutlineCornerHandle, double> setRadius = (h, r) => ApplyRadiusToSelectedHandle(item, h, r);
            // 接缝样式写入：写 per-vertex override（value==全局时清除，保持一致）；触发 OutlineGeometry 刷新。
            // 语义统一：切 Miter/Bevel → 该接缝所有圆角（含非转角点的 Left/RightCornerRadius）清除；
            // 切 Round → 保留半径字段（>0 时 Builder 圆角）。否则普通点圆角只由半径字段驱动、样式管不到。
            Action<OutlineCornerHandle, JoinStyle> setStyle = (h, js) =>
            {
                if (js == item.OutlineJoinStyle) item.ClearVertexJoinStyle(h.Index);
                else item.SetVertexJoinStyle(h.Index, js);
                // 非 Round → 清该接缝对应顶点的圆角半径字段（Corner 点 JoinInnerRadius；Normal 点 L/R CornerRadius），
                // 视觉立即恢复尖角，不残留"样式变了但圆弧还在"的状态。
                if (js != JoinStyle.Round && h.Index >= 0 && h.Index < item.VertexBandWidths.Count)
                {
                    var vtr = item.VertexBandWidths[h.Index];
                    if (h.IsCorner)
                    {
                        if (vtr.JoinInnerRadius != 0) vtr.JoinInnerRadius = 0;
                    }
                    else if (h.Side == "L")
                    {
                        if (vtr.LeftCornerRadius != 0) vtr.LeftCornerRadius = 0;
                    }
                    else
                    {
                        if (vtr.RightCornerRadius != 0) vtr.RightCornerRadius = 0;
                    }
                }
                // 立刻更新当前手柄的有效样式字段，避免 XAML 看到旧值（Refresh 异步时）
                h.CurrentStyleEffective = js;
                h.NotifyRadiusChanged();
                item.NotifyOutlineGeometryChanged();
                // 刷新手柄集合 → 同步所有其他 handle 的样式
                RefreshOutlineCornerHandles();
            };
            // 透传 Kind：骨线转折产生的转角点 (Corner) vs 普通轮廓点 (Normal)
            var handle = new OutlineCornerHandle(vi, canvasX, canvasY, side, perStyle, effectiveStyle,
                i, stepKind, inDir, outDir,
                isCorner: step.Kind == Converters.OutlinePointKind.Corner,
                getRadius: getRadius, setRadius: setRadius, setCurrentStyle: setStyle);
            // 订阅对应 VertexBandWidth 字段变化 → 联动发 CurrentRadius 属性通知给 UI（属性面板数值/Slider 实时跟随）
            AttachCornerRadiusAutoSync(handle, item);
            OutlineCornerHandles.Add(handle);
        }

        // Refresh 末尾：按等价键 (Index + Side + StepIndex + IsCorner) 把 SelectedOutlineCorner 重指到新 handle，
        // 避免 XAML 绑定的属性面板 ComboBox/Slider 在 PathData/BandWidth/样式变化后指向游离对象而显示陈旧值。
        if (_selectedCornerKey is { } k)
        {
            var match = OutlineCornerHandles.FirstOrDefault(h =>
                h.Index == k.Index && h.Side == k.Side && h.StepIndex == k.StepIndex && h.IsCorner == k.IsCorner);
            if (match != null && !ReferenceEquals(match, SelectedOutlineCorner))
                SelectedOutlineCorner = match;
        }
    }

    /// <summary>当前选中接缝的等价键：用于 Refresh 后重新定位到新集合里的对应 handle。</summary>
    private (int Index, string Side, int StepIndex, bool IsCorner)? _selectedCornerKey;

    /// <summary>写入接缝"圆角半径"（接缝属性面板 Slider TwoWay 用）：
    /// 按 IsCorner/Side 把值落到 VertexBandWidth 对应字段，触发 OutlineGeometry 重算。
    /// 半径字段与样式（CurrentStyle）是两个独立属性：
    /// - 样式只控制转角点（Corner）的 join 行为（Round/Miter/Bevel）
    /// - 半径字段只控制非转角点（Normal，端点偏移 / 段末端 / 共线直通点）的圆角
    /// 所以 Slider 写值**不联动样式**，ComboBox 切样式**不清半径字段**。</summary>
    private void ApplyRadiusToSelectedHandle(PrimitiveItem item, OutlineCornerHandle h, double radius)
    {
        if (radius < 0) return;
        int v = h.Index;
        if (v < 0 || v >= item.VertexBandWidths.Count) return;
        var vt = item.VertexBandWidths[v];
        if (h.IsCorner)
        {
            if (vt.JoinInnerRadius != radius) vt.JoinInnerRadius = radius;
        }
        else if (h.Side == "L")
        {
            if (vt.LeftCornerRadius != radius) vt.LeftCornerRadius = radius;
        }
        else
        {
            if (vt.RightCornerRadius != radius) vt.RightCornerRadius = radius;
        }
        // 让对应手柄的 CurrentRadius 立刻刷新（Source Generator 不会自动给派生属性发通知）
        h.NotifyRadiusChanged();
        item.NotifyOutlineGeometryChanged();
    }

    /// <summary>把手柄对应的 VertexBandWidth 字段变化映射到 OutlineCornerHandle.CurrentRadius 通知。
    /// 订阅 VertexBandWidths[vi].PropertyChanged（JoinInnerRadius/Left/RightCornerRadius 任一变化 → 通知 UI 重新读）。
    /// 闭包持有 h/vt 引用，旧手柄 Refresh 后随 OutlineCornerHandles.Clear() 一起被 GC，自动解绑，无需手动反订阅。</summary>
    private static void AttachCornerRadiusAutoSync(OutlineCornerHandle h, PrimitiveItem item)
    {
        int v = h.Index;
        if (v < 0 || v >= item.VertexBandWidths.Count) return;
        var vt = item.VertexBandWidths[v];
        vt.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(VertexBandWidth.JoinInnerRadius)
                or nameof(VertexBandWidth.LeftCornerRadius)
                or nameof(VertexBandWidth.RightCornerRadius))
            {
                h.NotifyRadiusChanged();
            }
        };
    }

    /// <summary>段拖拽手柄被拖拽时调用：段两端顶点同步移动（保持段长度不变）。
    /// AxisSnapEnabled=true 时：当段是纯水平/竖直，拖拽只允许垂直于段方向平移（保持正交）。
    /// verticalToSegment=false 时：自由平移（任意方向）。</summary>
    public void UpdateSegmentPosition(int segmentIndex, double newCanvasX, double newCanvasY)
    {
        if (SelectedItem is not { } item) return;
        if (item.IsLocked) return;   // 锁定图元禁止段编辑
        if (string.IsNullOrEmpty(item.PathData)) return;

        var pts = Converters.BandOutlineBuilder.ParsePathData(item.PathData);
        if (segmentIndex < 0 || segmentIndex >= pts.Count - 1) return;

        // 段 i 两端画布坐标
        var startCanvasX = item.X + pts[segmentIndex].X;
        var startCanvasY = item.Y + pts[segmentIndex].Y;
        var endCanvasX = item.X + pts[segmentIndex + 1].X;
        var endCanvasY = item.Y + pts[segmentIndex + 1].Y;

        // 计算 dx/dy delta（基于中点 delta）
        var midX = (startCanvasX + endCanvasX) * 0.5;
        var midY = (startCanvasY + endCanvasY) * 0.5;
        var dx = newCanvasX - midX;
        var dy = newCanvasY - midY;

        // AxisSnapEnabled=true 时，若段是纯水平/竖直，拖拽只允许垂直于段的方向
        if (AxisSnapEnabled)
        {
            var segDx = endCanvasX - startCanvasX;
            var segDy = endCanvasY - startCanvasY;
            // 段长度近 0（退化）时不约束
            var segLen = Math.Sqrt(segDx * segDx + segDy * segDy);
            if (segLen > 1e-6)
            {
                // 水平段：仅允许 dy 方向（垂直于段）
                if (Math.Abs(segDx) > Math.Abs(segDy))
                    dx = 0;
                // 竖直段：仅允许 dx 方向（垂直于段）
                else if (Math.Abs(segDy) > Math.Abs(segDx))
                    dy = 0;
            }
        }

        // 两端顶点同步平移
        pts[segmentIndex] = new Point(
            pts[segmentIndex].X + dx, pts[segmentIndex].Y + dy);
        pts[segmentIndex + 1] = new Point(
            pts[segmentIndex + 1].X + dx, pts[segmentIndex + 1].Y + dy);

        _suppressVertexRefresh = true;
        try
        {
            item.PathData = Converters.GeometryMath.BuildPathData(pts);
            item.SyncVertexBandWidths(pts.Count, item.BandWidth);
        }
        finally
        {
            _suppressVertexRefresh = false;
        }
        // 段两端顶点移动后，同步刷新顶点手柄位置
        RefreshVertexHandles();
        // 段移动后接缝外尖点也跟着变→刷新轮廓手柄
        if (OutlineEditMode == OutlineEditMode.Outline)
            RefreshOutlineCornerHandles();
    }
}