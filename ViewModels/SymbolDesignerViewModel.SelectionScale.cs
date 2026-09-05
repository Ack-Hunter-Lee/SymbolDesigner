using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SymbolDesigner.Models;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace SymbolDesigner.ViewModels;

/// <summary>缩放手柄的角位（与 ResizeThumb 的 ResizeDirection 语义一致）。</summary>
public enum ScaleCorner
{
    /// <summary>左上（锚点 = 右下）。</summary>
    NW,
    /// <summary>右上（锚点 = 左下）。</summary>
    NE,
    /// <summary>左下（锚点 = 右上）。</summary>
    SW,
    /// <summary>右下（锚点 = 左上）。</summary>
    SE,
}

/// <summary>选区四角缩放手柄的渲染数据（中心对齐到包围盒角点）。</summary>
public sealed class ScaleHandleInfo
{
    public required ScaleCorner Corner { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
}

/// <summary>
/// 选区交互：右键框选（SelectInRect）+ 选区包围盒四角缩放手柄（围绕对角锚点等比缩放）。
/// 框选结果 = 全新多选集合（Clear 后加入框内相交图元）；手柄随选区变化实时刷新位置。
/// 缩放不取整、保持浮点精度；只作用于"几何可缩放"图元，BooleanPath 跳过（复杂多环骨架不做文本级重建）。
/// </summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>选区四角缩放手柄（画布逻辑坐标，每个中心对齐包围盒角点）。空 = 不显示。</summary>
    public ObservableCollection<ScaleHandleInfo> ScaleSelectionHandles { get; } = new();

    /// <summary>当前是否有可显示的选区缩放手柄（选区非空且非编辑模式）。</summary>
    [ObservableProperty] private bool _hasScaleSelectionHandles;

    /// <summary>当前选区包围盒（画布逻辑坐标；仅手柄可见时有意义）。</summary>
    [ObservableProperty] private Rect _scaleSelectionFrame = Rect.Empty;

    // —— 缩放手势现场（DragStarted 时快照，DragDelta 用快照 + 当前 scale 重算，避免累积漂移） ——

    private sealed class ScaleSnapshot
    {
        public double X, Y, W, H;
        public string PathData = string.Empty;
        public bool PathDataClosed;         // 原 PathData 以 Z 结尾（重建时保留）
        public double CornerRadius, InnerRadius, Thickness;
        public double[] VertexIn = Array.Empty<double>();
        public double[] VertexOut = Array.Empty<double>();
    }

    private ScaleCorner _scaleCorner;
    private Point _scaleAnchor;         // 对角固定锚点（画布坐标）
    private double _scaleBaseDistance;  // 手势开始时鼠标到锚点的距离
    private readonly List<(PrimitiveItem Item, ScaleSnapshot Snap)> _scaleTargets = new();
    private bool _isScaling;

    /// <summary>缩放手势的最小缩放比例（防止拖到锚点导致尺寸归零/翻转）。</summary>
    private const double MinScaleFactor = 0.02;

    /// <summary>刷新选区包围盒 + 四角缩放手柄（选区变化、图元几何变化时调用）。
    /// 手指标心 = 包围盒四个角点；手柄自身 2×2 逻辑像素由 View 层居中。</summary>
    public void RefreshScaleSelectionHandles()
    {
        // 手柄显示条件：仅当"正在绘制"(IsDrawing) 或"厚度/轮廓编辑模式"时隐藏。
        // Note: 选中 Polyline/ThickPolyline 会自动进入对应 DrawMode（选中联动），那不是"主动绘制"，
        // 不应清掉选区缩放手柄（否则导入 SVG 全 Polyline，框选后无法缩放）。ActiveToolKey 有值 = 用户点了工具栏开始画新图元。
        if (IsDrawing || IsThicknessEditMode || IsOutlineEditMode || !string.IsNullOrEmpty(ActiveToolKey))
        {
            ClearScaleHandles();
            return;
        }

        var items = ScaleTargetItems();
        if (items.Count == 0)
        {
            ClearScaleHandles();
            return;
        }

        // 普通形状（矩形/椭圆/三角/圆环/圆角矩形/直线）单选仍由 ResizeThumb 提供四角缩放，
        // 选区缩放手柄只在"多选"或"PathData 型单选"（ResizeThumb 无效）时显示，避免两套手柄重叠。
        if (items.Count == 1 && !HasPathDataGeometry(items[0]))
        {
            ClearScaleHandles();
            return;
        }

        if (GetUnionBounds(items) is not Rect b)
        {
            ClearScaleHandles();
            return;
        }

        ScaleSelectionFrame = b;
        ScaleSelectionHandles.Clear();
        AddHandle(ScaleCorner.NW, b.Left, b.Top);
        AddHandle(ScaleCorner.NE, b.Right, b.Top);
        AddHandle(ScaleCorner.SW, b.Left, b.Bottom);
        AddHandle(ScaleCorner.SE, b.Right, b.Bottom);
        HasScaleSelectionHandles = true;
    }

    private void AddHandle(ScaleCorner corner, double x, double y)
        => ScaleSelectionHandles.Add(new ScaleHandleInfo { Corner = corner, X = x, Y = y });

    /// <summary>该图元是否走 PathData 几何（ResizeThumb 的 W/H 缩放对其无效,必须用选区缩放手柄等比缩放）。
    /// 有 PathData 或属于路径类（Line/Polyline/ThickPolyline/BooleanPath）即算。</summary>
    private static bool HasPathDataGeometry(PrimitiveItem item)
    {
        if (!string.IsNullOrEmpty(item.PathData)) return true;
        return item.TypeKey == nameof(L1Primitive.Line)
            || item.TypeKey == nameof(L1Primitive.Polyline)
            || item.TypeKey == nameof(L1Primitive.ThickPolyline)
            || item.TypeKey == nameof(L1Primitive.BooleanPath);
    }

    /// <summary>取当前参与缩放/框选的图元集合 = SelectedItem ∪ MultiSelected（去重,不含阵列副本）。</summary>
    private List<PrimitiveItem> ScaleTargetItems()
    {
        var list = new List<PrimitiveItem>();
        if (SelectedItem is { ArrayOwner: null } s && !list.Contains(s)) list.Add(s);
        foreach (var p in MultiSelected)
            if (p.ArrayOwner is null && !list.Contains(p)) list.Add(p);
        return list;
    }

    /// <summary>计算一组图元的整体包围盒（画布坐标）。
    /// PathData 型图元用解析点求真实范围（X/Y 定位 + 局部点平移），其余用 X/Y/Width/Height。
    /// 纯靠 Width/Height 会忽略 PathData 局部越界；防越界见 Converters.GeometryMath.RecalcBoundingBox。</summary>
    private static Rect? GetUnionBounds(IReadOnlyList<PrimitiveItem> items)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        void Inflate(double x1, double y1, double x2, double y2)
        {
            if (x1 < minX) minX = x1;
            if (y1 < minY) minY = y1;
            if (x2 > maxX) maxX = x2;
            if (y2 > maxY) maxY = y2;
        }

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.PathData))
            {
                var pts = SymbolDesigner.Converters.ThickOutlineBuilder.ParsePathData(item.PathData);
                bool any = false;
                foreach (var p in pts)
                {
                    Inflate(item.X + p.X, item.Y + p.Y, item.X + p.X, item.Y + p.Y);
                    any = true;
                }
                if (!any) continue;
            }
            else
            {
                Inflate(item.X, item.Y, item.X + item.Width, item.Y + item.Height);
            }
        }

        return minX == double.MaxValue
            ? null
            : new Rect(minX, minY, Math.Max(0.01, maxX - minX), Math.Max(0.01, maxY - minY));
    }

    /// <summary>右键框选：清除旧多选，把与矩形相交（含内部）的图元全部加入多选，单选焦点 = 框内第一个。
    /// 不改变图元任何几何数据,只改选中集。交点判定用整体包围盒（宽松相交,框到一部分即选中）。</summary>
    public void SelectInRect(Rect r)
    {
        // 与 DraggablePrimitive.OnSelfClick 相同优先级语义:格式刷待命时右键框选 = 退出应用待命
        if (IsFormatPainterActive)
            ClearFormatPainter();

        var selected = new List<PrimitiveItem>();
        foreach (var p in Primitives)
        {
            if (p.ArrayOwner is not null || !p.IsVisible) continue;
            if (GetItemBounds(p) is { } b && r.IntersectsWith(b))
                selected.Add(p);
        }

        // 清空旧多选（保持与 AddToMultiSelect 一致的高亮刷新路径）
        foreach (var p in MultiSelected)
            p.IsSelected = false;
        MultiSelected.Clear();
        _pickCanvasByItem.Clear();
        _pickKindByItem.Clear();

        PrimitiveItem? first = null;
        foreach (var p in selected)
        {
            if (first is null) first = p;
            MultiSelected.Add(p);
            p.IsSelected = true;
        }

        // 单选焦点取第一个（属性面板随之切换）
        SelectedItem = first;
        // 显式清空多选的子对象识别标记 + 刷新 CanApplyTangent 等
        RebuildPickMarkers();
        OnPropertyChanged(nameof(CanApplyTangent));
        OnPropertyChanged(nameof(CanApplyCoincident));
        OnPropertyChanged(nameof(CanBooleanOp));
        MergeShapesCommand.NotifyCanExecuteChanged();
        SubtractShapesCommand.NotifyCanExecuteChanged();
        IntersectShapesCommand.NotifyCanExecuteChanged();

        RefreshScaleSelectionHandles();
        System.Diagnostics.Debug.WriteLine($"[SelectInRect] {selected.Count} items selected, rect={r}");
    }

    /// <summary>单图元包围盒（画布坐标）：PathData 型解析点范围,其余 X/Y/W/H。</summary>
    private static Rect? GetItemBounds(PrimitiveItem item)
    {
        if (!string.IsNullOrEmpty(item.PathData))
        {
            var pts = SymbolDesigner.Converters.ThickOutlineBuilder.ParsePathData(item.PathData);
            if (pts.Count == 0) return null;
            double bx1 = pts[0].X, by1 = pts[0].Y, bx2 = pts[0].X, by2 = pts[0].Y;
            foreach (var p in pts)
            {
                if (p.X < bx1) bx1 = p.X;
                if (p.Y < by1) by1 = p.Y;
                if (p.X > bx2) bx2 = p.X;
                if (p.Y > by2) by2 = p.Y;
            }
            return new Rect(item.X + bx1, item.Y + by1, bx2 - bx1, by2 - by1);
        }
        return new Rect(item.X, item.Y, item.Width, item.Height);
    }

    // —— 缩放手势：DragStarted / DragDelta / DragCompleted 三阶段 ——

    /// <summary>多选整体平移：拖动 MultiSelected 中任一元件 → 全体成员同增量平移（相对位置不变）。
    /// 仅当 MultiSelected.Count ≥ 2 且目标在其中时生效；否则返回 false，由 DraggablePrimitive 走单图元拖动。
    /// 整体平移语义下不应用单图元约束（相切/重合等）；X/Y 绑定自动刷新画布位置 + 选区包围盒。</summary>
    public bool TryMoveMultiSelection(PrimitiveItem primary, double dx, double dy)
    {
        if (MultiSelected.Count < 2 || !MultiSelected.Contains(primary)) return false;
        foreach (var p in MultiSelected)
        {
            p.X += dx;
            p.Y += dy;
        }
        RefreshScaleSelectionHandles();
        return true;
    }

    /// <summary>缩放手势开始：快照所有目标图元几何 + 保存对角锚点 + 起始鼠标距离。
    /// 一次性 BeginEdit,整个拖动手势合并为一次撤销步骤。</summary>
    public void BeginScaleSelection(ScaleCorner corner, Point anchor, Point startMouse)
    {
        var items = ScaleTargetItems();
        if (items.Count == 0) return;
        BeginEdit();

        _scaleCorner = corner;
        _scaleAnchor = anchor;
        _scaleBaseDistance = Dist(startMouse, anchor);
        if (_scaleBaseDistance < 0.5) _scaleBaseDistance = 0.5;  // 防除零

        _scaleTargets.Clear();
        foreach (var item in items)
        {
            string pd = item.PathData ?? string.Empty;
            _scaleTargets.Add((item, new ScaleSnapshot
            {
                X = item.X,
                Y = item.Y,
                W = item.Width,
                H = item.Height,
                PathData = pd,
                PathDataClosed = pd.Length > 0 && (pd[^1] == 'Z' || pd[^1] == 'z'),
                CornerRadius = item.CornerRadius,
                InnerRadius = item.InnerRadius,
                Thickness = item.Thickness,
                VertexIn = CollectVertexIn(item),
                VertexOut = CollectVertexOut(item),
            }));
        }
        _isScaling = true;
    }

    private static double[] CollectVertexIn(PrimitiveItem item)
    {
        var vs = item.VertexThicknesses;
        var a = new double[vs.Count];
        for (int i = 0; i < vs.Count; i++) a[i] = vs[i].IncomingThickness;
        return a;
    }

    private static double[] CollectVertexOut(PrimitiveItem item)
    {
        var vs = item.VertexThicknesses;
        var a = new double[vs.Count];
        for (int i = 0; i < vs.Count; i++) a[i] = vs[i].OutgoingThickness;
        return a;
    }

    private static double Dist(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>缩放手势更新：scale = 当前鼠标到锚点距离 / 起始距离（等比）。
    /// 用快照重算写回,不累积,拖再远也不漂移。scale 下限保护防翻转。</summary>
    public void UpdateScaleSelection(Point mouse)
    {
        if (!_isScaling || _scaleTargets.Count == 0) return;
        double d = Dist(mouse, _scaleAnchor);
        double scale = Math.Max(MinScaleFactor, d / _scaleBaseDistance);

        foreach (var (item, snap) in _scaleTargets)
        {
            ApplyScale(item, snap, _scaleAnchor, scale);
        }
        RefreshScaleSelectionHandles();
    }

    /// <summary>缩放手势结束：提交一次撤销快照。</summary>
    public void EndScaleSelection()
    {
        if (!_isScaling) return;
        _isScaling = false;
        _scaleTargets.Clear();
        EndEdit();
    }

    /// <summary>按快照把单个图元围绕锚点等比缩放 scale 倍。
    /// - 矩形/椭圆/三角/圆环/圆角矩：改 X/Y/W/H（锚点侧不动,对侧移动）+ 辅助量（InnerRadius/CornerRadius）。
    /// - Line/Polyline/ThickPolyline：PathData 局部点围绕锚点缩放 → 重新对齐 minX/minY 作为新 X/Y。
    /// - BooleanPath：跳过（复杂多环骨架,不做文本级缩放,保持原样）。
    /// - ThickPolyline：逐顶点厚度同比例缩放（外观等比）。
    /// - StrokeWidth 不随几何缩放（描边保持可见,属于外观属性）。</summary>
    private static void ApplyScale(PrimitiveItem item, ScaleSnapshot snap, Point anchor, double scale)
    {
        switch (item.TypeKey)
        {
            case nameof(L1Primitive.Rectangle):
            case nameof(L1Primitive.RoundedRectangle):
            {
                item.X = anchor.X + (snap.X - anchor.X) * scale;
                item.Y = anchor.Y + (snap.Y - anchor.Y) * scale;
                item.Width = Math.Max(0.01, snap.W * scale);
                item.Height = Math.Max(0.01, snap.H * scale);
                if (item.TypeKey == nameof(L1Primitive.RoundedRectangle))
                    item.CornerRadius = snap.CornerRadius * scale;
                break;
            }

            case nameof(L1Primitive.Ellipse):
            case nameof(L1Primitive.Triangle):
            case nameof(L1Primitive.Ring):
            {
                item.X = anchor.X + (snap.X - anchor.X) * scale;
                item.Y = anchor.Y + (snap.Y - anchor.Y) * scale;
                item.Width = Math.Max(0.01, snap.W * scale);
                item.Height = Math.Max(0.01, snap.H * scale);
                if (item.TypeKey == nameof(L1Primitive.Ring))
                    item.InnerRadius = Math.Max(0.01, snap.InnerRadius * scale);
                break;
            }

            case nameof(L1Primitive.Line):
            case nameof(L1Primitive.Polyline):
            case nameof(L1Primitive.ThickPolyline):
                ScalePathData(item, snap, anchor, scale);
                break;

            // BooleanPath：复杂多环骨架（含 F0 前缀 / 弧段）,不做文本级缩放,跳过保持原样。
            default:
                break;
        }
    }

    /// <summary>PathData 型图元缩放：局部点 → 画布坐标 → 围绕锚点缩放 → 新局部 min 对齐 → 重建 PathData + 更新 X/Y。</summary>
    private static void ScalePathData(PrimitiveItem item, ScaleSnapshot snap, Point anchor, double scale)
    {
        if (string.IsNullOrEmpty(snap.PathData)) return;
        var pts = SymbolDesigner.Converters.ThickOutlineBuilder.ParsePathData(snap.PathData);

        // 画布坐标缩放
        double bx1 = double.MaxValue, by1 = double.MaxValue;
        var scaled = new List<Point>(pts.Count);
        foreach (var p in pts)
        {
            double wx = snap.X + p.X, wy = snap.Y + p.Y;
            double nx = anchor.X + (wx - anchor.X) * scale;
            double ny = anchor.Y + (wy - anchor.Y) * scale;
            if (nx < bx1) bx1 = nx;
            if (ny < by1) by1 = ny;
            scaled.Add(new Point(nx, ny));
        }
        if (scaled.Count == 0) return;

        // 重建 PathData（局部坐标 = 画布坐标 - 新原点）
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < scaled.Count; i++)
        {
            if (i == 0)
                sb.Append('M').Append(' ').Append(Fmt(scaled[i].X - bx1)).Append(',').Append(Fmt(scaled[i].Y - by1));
            else
                sb.Append('L').Append(' ').Append(Fmt(scaled[i].X - bx1)).Append(',').Append(Fmt(scaled[i].Y - by1));
        }
        if (snap.PathDataClosed)
            sb.Append('Z');

        item.X = bx1;
        item.Y = by1;
        item.PathData = sb.ToString();
        item.Width = Math.Max(0.01, snap.W * scale);
        item.Height = Math.Max(0.01, snap.H * scale);

        // ThickPolyline：逐顶点厚度同比例缩放（保守不处理 VertexThicknesses 数量,只缩放值）
        if (item.TypeKey == nameof(L1Primitive.ThickPolyline))
        {
            item.Thickness = snap.Thickness * scale;
            int n = Math.Min(item.VertexThicknesses.Count, snap.VertexIn.Length);
            for (int i = 0; i < n; i++)
            {
                item.VertexThicknesses[i].IncomingThickness = snap.VertexIn[i] * scale;
                item.VertexThicknesses[i].OutgoingThickness = snap.VertexOut[i] * scale;
            }
        }
    }

    private static string Fmt(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private void ClearScaleHandles()
    {
        if (ScaleSelectionHandles.Count > 0) ScaleSelectionHandles.Clear();
        HasScaleSelectionHandles = false;
        ScaleSelectionFrame = Rect.Empty;
    }
}