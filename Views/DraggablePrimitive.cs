using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SymbolDesigner.Models;
using SymbolDesigner.ViewModels;

namespace SymbolDesigner.Views;

/// <summary>
/// 可拖动的 PrimitiveItem 容器（Thumb 实现）。
///
/// 关键设计：
/// - Thumb.DragDelta 直接更新数据源 PrimitiveItem.X/Y；
/// - Canvas.Left/Top 通过 OneWay Binding 自动跟随 X/Y 更新（位置自然流畅）；
/// - 不直接操作 Canvas.Left/Top（避免 Binding 与 SetValue 冲突导致"跳到左上角"）。
/// - DragCompleted 时判断是否真为拖动（HorizontalChange/VerticalChange == 0 → 触发 Click 事件）。
/// - Click 事件 handler 通过 OnApplyTemplate 阶段订阅（避免 ResourceDictionary 无法处理事件）。
/// </summary>
public class DraggablePrimitive : ContentControl
{
    private Thumb? _thumb;

    /// <summary>本次按下的画布点击坐标（逻辑坐标）。用于重合约束等子对象识别
    /// （端点 / 圆心 / 圆弧 / 线段）。按下时记录，非纯点击则为最后一次拖动手势起点。</summary>
    private Point _clickCanvasPoint;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _thumb = GetTemplateChild("PART_Thumb") as Thumb;
        if (_thumb != null)
        {
            _thumb.DragStarted += OnDragStarted;
            _thumb.DragDelta += OnDragDelta;
            _thumb.DragCompleted += OnDragCompleted;
        }

        // 自订阅 Click 事件
        Click -= OnSelfClick;
        Click += OnSelfClick;
    }

    /// <summary>
    /// 拖动开始:通知 VM 记录手势起点 → 整个拖动手势合并为一次撤销步骤。
    /// </summary>
    private void OnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (DataContext is not PrimitiveItem item) return;
        var view = FindAncestor<SymbolDesignerView>(this);
        if (view?.DataContext is SymbolDesignerViewModel vm)
            vm.BeginEdit();
        else
            System.Diagnostics.Debug.WriteLine("[DraggablePrimitive] 未找到 VM,跳过 BeginEdit");

        // 记录按下时的画布逻辑坐标（用于子对象识别）。局部坐标 + 自身 Canvas 定位 = 画布坐标。
        var local = System.Windows.Input.Mouse.GetPosition(this);
        _clickCanvasPoint = new Point(item.X + local.X, item.Y + local.Y);

        // "空白彻底无反应"：按下起点不在实体几何上 → 取消整个拖动手势（既不拖动也不触发点击）。
        if (!HitEntity(item, _clickCanvasPoint))
            _thumb?.CancelDrag();
    }

    private void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DataContext is not PrimitiveItem item) return;
        var view = FindAncestor<SymbolDesignerView>(this);
        var vm = view?.DataContext as SymbolDesignerViewModel;

        // 阵列副本：拖动 = 整体平移阵列（不改克隆自身位置，由 MoveArrayElement 重建全部克隆；
        // OffsetX/Y 累加到 ArrayInstance，源/路径图元毫发无损）
        if (item.ArrayOwner is { } arr && vm is not null)
        {
            vm.MoveArrayElement(arr, e.HorizontalChange, e.VerticalChange);
            return;
        }

        // 直接改数据源（OneWay Binding 会自动把 Canvas.Left/Top 同步过来）
        item.X += e.HorizontalChange;
        item.Y += e.VerticalChange;

        if (vm is null) return;

        // 持久相切约束：
        //  - 拖动圆（锚=直线）→ 圆锁在相切轨道（只沿直线方向滑动）
        //  - 拖动直线（锚=圆）→ 直线锁在相切轨道（只沿自身方向滑动）
        //  - 拖动锚 → 另一图元由 OnItemGeometryChanged → Resolve 跟随保持相切
        var constrained = vm.ConstrainCircleDrag(item, new Point(item.X + item.Width / 2.0, item.Y + item.Height / 2.0));
        if (constrained is { } nc)
        {
            item.X = nc.X - item.Width / 2.0;
            item.Y = nc.Y - item.Height / 2.0;
        }
        var constrainedLine = vm.ConstrainLineDrag(item, new Point(item.X, item.Y));
        if (constrainedLine is { } nl)
        {
            item.X = nl.X;
            item.Y = nl.Y;
        }
        vm.OnItemGeometryChanged(item);
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        // 通知 VM 手势结束:若确实发生了移动,提交一次撤销快照
        if (DataContext is not PrimitiveItem item) return;
        var view = FindAncestor<SymbolDesignerView>(this);
        if (view?.DataContext is SymbolDesignerViewModel vm)
        {
            // 先提交拖动手势(无论是否纯点击,EndEdit 内部无变化则不落栈)
            vm.EndEdit();
            // 阵列副本跳过布尔源逻辑：克隆不是画布集合成员，不可能是布尔源
            if (item.ArrayOwner is null)
                vm.NotifyBooleanSourceDragEnded(item);
            // HorizontalChange/VerticalChange 是"本次拖动总累积量"
            // 等于 0 表示没有实际拖动，是纯点击
            if (!e.Canceled && System.Math.Abs(e.HorizontalChange) < 0.5 && System.Math.Abs(e.VerticalChange) < 0.5)
            {
                Click?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Click 事件 handler（在 OnApplyTemplate 里订阅，避开 ResourceDictionary 不能用 EventSetter 的限制）。
    /// 点击图元 → 累积式多选（AddOrSelect），同时更新单选焦点（属性面板用）；
    /// 无需 Ctrl 即可连续点选多个图元组成"直线+圆"等组合。
    /// 只响应"命中实体图形"的点击：包围盒内空白（椭圆四角、三角形外、线段旁空白、圆环内孔等）不选中。
    /// </summary>
    private void OnSelfClick(object? sender, EventArgs e)
    {
        if (DataContext is not PrimitiveItem item) return;
        // 实体命中判定：点击空白处不入选中（也避免子对象识别/约束误触发）
        if (!HitEntity(item, _clickCanvasPoint))
            return;
        // 向上找 SymbolDesignerView 拿 VM
        var view = FindAncestor<SymbolDesignerView>(this);
        if (view?.DataContext is SymbolDesignerViewModel vm)
        {
            // 阵列副本：点击任一副本 → 选中整个阵列（AutoCAD 关联阵列语义，整体高亮 + 整体操作）
            if (item.ArrayOwner is { } arr)
            {
                vm.SelectArray(arr);
                return;
            }
            bool ctrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
            // 格式刷"应用待命" → 点击任意图元即应用样式（优先级最高，盖过选中和 Ctrl 多选）。
            // 单次模式：应用完自动退出；连刷模式（_formatPainterLockMode）保持激活可继续点击下一个。
            if (vm.IsFormatPainterActive)
            {
                vm.ApplyFormatPainterTo(item);
                // 应用完：若不是连刷模式 → 已经退出；连刷模式下保留激活让用户继续点别的图元
                return;
            }
            if (ctrl)
                vm.ToggleMultiSelect(item, _clickCanvasPoint);
            else
                vm.AddToMultiSelect(item, _clickCanvasPoint);
        }
    }

    /// <summary>实体命中容差（画布逻辑单位）。线状图元点到线段 ≤ 此值视为命中。
    /// 相对视图缩放在 160×96 附近，与线宽 0.8 相比足够宽松，点击容易命中。</summary>
    private const double EntityHitTolerance = 3.0;

    /// <summary>圆环"圆心十字区"拾取半径：与 VM 子对象识别一致（视图中心区域用于拾取圆心）。</summary>
    private const double CenterPickRadius = 6.0;

    /// <summary>判断点击的画布坐标是否落在该图元的实体几何上（而非包围盒空白处）。
    /// 按模板实际渲染几何判定：矩形/圆角矩形=矩形域；椭圆=椭圆域；三角=三角形域；
    /// 圆环=环带（含圆心十字区，供约束拾取圆心）；直线/Polyline/ThickPolyline=骨架折线 ± 容差。
    /// 未知类型默认命中（保守，不阻止选中）。</summary>
    private static bool HitEntity(PrimitiveItem item, Point canvas)
    {
        double lx = canvas.X - item.X;
        double ly = canvas.Y - item.Y;
        var p = new Point(lx, ly);

        switch (item.TypeKey)
        {
            case nameof(L1Primitive.Rectangle):
            case nameof(L1Primitive.RoundedRectangle):
            {
                double r = item.CornerRadius;
                var g = new RectangleGeometry(new Rect(0, 0, item.Width, item.Height), r, r);
                return g.FillContains(p);
            }

            case nameof(L1Primitive.Ellipse):
            {
                var g = new EllipseGeometry(new Point(item.Width / 2, item.Height / 2),
                                            item.Width / 2, item.Height / 2);
                return g.FillContains(p);
            }

            case nameof(L1Primitive.Triangle):
            {
                // 模板 Points="0.5,0 1,1 0,1" Stretch=Fill → 局部三角 (0.5W,0)(W,H)(0,H)
                return PointInTriangle(p,
                    new Point(0.5 * item.Width, 0),
                    new Point(item.Width, item.Height),
                    new Point(0, item.Height));
            }

            case nameof(L1Primitive.Ring):
            {
                var c = new Point(item.Width / 2, item.Height / 2);
                // 圆心十字区：供重合约束"点选圆心"（与 VM 子对象识别一致）
                if (Dist(p, c) <= CenterPickRadius) return true;
                // 环带：优先用现成 RingGeometry 填充判定（外顺内逆 Nonzero = 环带）
                if (item.RingGeometry is { } ringGeom)
                    return ringGeom.FillContains(p);
                // 回退：外圆内且内圆外
                double outer = Math.Min(item.Width, item.Height) / 2 - PrimitiveItem.RingStrokeHalfThickness;
                double inner = Math.Max(0.01, Math.Min(item.InnerRadius, outer - 0.01));
                double d = Dist(p, c);
                return d <= outer && d >= inner;
            }

            case nameof(L1Primitive.Line):
            case nameof(L1Primitive.Polyline):
            case nameof(L1Primitive.ThickPolyline):
            {
                // 用画布坐标做点到折线段距离（端点解析进的是画布坐标）
                var pts = SymbolDesigner.Converters.ThickOutlineBuilder.ParsePathData(item.PathData);
                if (pts.Count < 2)
                {
                    // 无 PathData（模板对角线回退 / 矩形线）：用包围盒对角线近似
                    pts = new List<Point>
                    {
                        new Point(item.X, item.Y + item.Height),
                        new Point(item.X + item.Width, item.Y),
                    };
                }
                double tol = EntityHitTolerance;
                if (item.TypeKey == nameof(L1Primitive.ThickPolyline))
                    tol = Math.Max(EntityHitTolerance, item.Thickness / 2);
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    if (DistToSegment(canvas,
                        new Point(item.X + pts[i].X, item.Y + pts[i].Y),
                        new Point(item.X + pts[i + 1].X, item.Y + pts[i + 1].Y)) <= tol)
                        return true;
                }
                return false;
            }

            default:
                return true; // 未知类型保守命中
        }
    }

    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>点到线段距离。</summary>
    private static double DistToSegment(Point p, Point a, Point b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double lenSq = abx * abx + aby * aby;
        if (lenSq < 1e-12) return Dist(p, a);
        double t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lenSq;
        t = Math.Max(0, Math.Min(1, t));
        var proj = new Point(a.X + t * abx, a.Y + t * aby);
        return Dist(p, proj);
    }

    /// <summary>点在三角形内（含边界）。</summary>
    private static bool PointInTriangle(Point p, Point a, Point b, Point c)
    {
        double d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
        bool neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
        bool pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
        return !(neg && pos);

        static double Sign(Point p1, Point p2, Point p3) =>
            (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
    }

    /// <summary>沿视觉树向上查找指定类型的祖先。</summary>
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>点击（未拖动）事件。</summary>
    public event EventHandler? Click;
}