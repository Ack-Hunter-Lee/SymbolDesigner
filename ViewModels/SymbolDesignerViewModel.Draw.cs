using System.Collections.Generic;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>画布绘制状态机：起笔 / 连续加点 / 橡皮线 / 形状两段式预览 / 提交与取消。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>顶部"轴对齐"开关。勾选后画线工具强制水平/竖直（按住 Shift 切水平）；
    /// 未勾选则自由画线，但绘制时若当前段方向接近水平/竖直（Polar Tracking 风格）
    /// 会自动吸附+显示提示线，提交时按吸附后的位置落点。默认关闭（CAD 默认状态）。</summary>
    [ObservableProperty] private bool _axisSnapEnabled = false;

    /// <summary>在指定坐标位置添加一个 L1 图形实例。</summary>
    public void AddL1(L1Primitive primitive, double x, double y)
    {
        var (w, h) = primitive switch
        {
            L1Primitive.Rectangle        => (40.0, 24.0),
            L1Primitive.Ellipse          => (24.0, 24.0),
            L1Primitive.Line             => (30.0, 0.0),
            L1Primitive.RoundedRectangle  => (40.0, 24.0),
            L1Primitive.Triangle         => (20.0, 20.0),
            L1Primitive.Ring             => (40.0, 40.0),
            L1Primitive.Pie              => (40.0, 40.0),
            _ => (30.0, 30.0),
        };

        // 居中放置（鼠标点 → 图形中心）。无限画布：不做任何上下界限制
        x = x - w / 2;
        y = y - h / 2;

        var item = PrimitiveItem.Create(primitive, x, y, w, h);
        // 直线特殊处理：拖入时根据 AxisSnapEnabled 决定初始轴对齐（默认 Free）
        if (primitive == L1Primitive.Line)
            item.LineAxis = LineAxisMode.Free;
        // 圆环：默认内圆半径 = 外径的一半（环带宽 = 外径的一半）
        if (primitive == L1Primitive.Ring)
            item.InnerRadius = System.Math.Min(w, h) / 4;
        item.ZIndex = Primitives.Count; // 新元素置顶
        Primitives.Add(item);
        ItemCount = Primitives.Count;
        SelectedItem = item;
    }

    /// <summary>画布鼠标点击（CAD 风格连续绘制）：
    /// 路径类（Polyline/BandPolyline/Line）：首次点击起笔，后续每次点击追加一个点；Esc 结束。
    /// 形状类（Rectangle/Ellipse/Ring/Triangle/RoundedRectangle）：两次点击 = 中心点 + 第二点（对角点/圆周点/角点）；
    /// 第二次点击立即提交并退出绘制。
    /// Esc 取消未提交草稿。</summary>
    public bool ClickDraw(double x, double y, bool shiftKey)
    {
        if (DrawMode == DrawMode.None) return false;
        var clamped = ClampToCanvas(x, y);
        _shiftKey = shiftKey;

        // 形状类两段式：IsDrawing=true 时 DraftPoints 含 [中心点]；第二次点击 → 提交 + 退出
        if (IsShapeDrawMode(DrawMode))
        {
            // 形状类：直接走"中心 + 第二点"两段，第二次点击由 MouseDown 走 IsDrawing 分支或显式调用
            // 这里复用 IsDrawing 状态：首次进入后 IsDrawing=true（让橡皮线显示中心 → 当前鼠标位置）
            // 第二次 ClickDraw 时 DraftPoints 已含中心点，我们直接提交并清空
            if (IsDrawing)
            {
                // 第二次点击：DraftPoints[0] = 中心点；当前 clamped 是第二点 → 提交
                // 若按住 Shift 且不是 Ellipse/Ring，则锁正形：用 ConstrainShapeToSquare 等比化后再写入 DraftPoints
                var center = DraftPoints[0];
                bool isCircleLike = DrawMode is DrawMode.Ellipse or DrawMode.Ring or DrawMode.Pie;
                if (_shiftKey && !isCircleLike)
                    clamped = Converters.GeometryMath.ConstrainShapeToSquare(center, clamped);
                DraftPoints.Add(clamped);
                EndDraw();
            }
            else
            {
                // 第 1 次点击 = 中心点
                DraftPoints.Clear();
                DraftPoints.Add(clamped);
                IsDrawing = true;
                DraftPreviewPoint = clamped;
                UpdateDraftPathData();
                UpdateShapePreview();
            }
            return true;
        }

        // 路径类：起笔 + 连续加点
        if (!IsDrawing)
        {
            _draftCloseRequested = false;
            DraftSnapToStart = false;
            DraftPoints.Clear();
            DraftPoints.Add(clamped);
            IsDrawing = true;
        }
        else
        {
            // 起点吸附命中（MoveDraw 已置 DraftSnapToStart）：点击 → 闭环提交。
            // 不追加新点（起点已在 DraftPoints[0]），标记闭合后直接 EndDraw。
            // 用鼠标当前位置实时判定（而非只信 MoveDraw 缓存），点按与移动位置一致才稳定。
            UpdateDraftStartSnap(clamped);
            if (_draftSnapToStart && DraftPoints.Count >= 2)
            {
                _draftCloseRequested = true;
                DraftPreviewPoint = new System.Windows.Point(double.NaN, double.NaN);
                EndDraw();
                return true;
            }
            var anchor = DraftPoints[^1];
            var p = ConstrainPoint(anchor, clamped);
            DraftPoints.Add(p);
        }
        DraftPreviewPoint = new System.Windows.Point(double.NaN, double.NaN);
        UpdateDraftPathData();

        // Line 模式只需要两个点：第二次点击直接提交并退出绘制。
        // Polyline/BandPolyline 走 Enter 手动结束。
        if (DrawMode == DrawMode.Line && DraftPoints.Count >= 2)
        {
            EndDraw();
        }
        return true;
    }

    /// <summary>判断当前 DrawMode 是否为"中心点 + 第二点"两段式形状绘制。</summary>
    private static bool IsShapeDrawMode(DrawMode mode) => mode is DrawMode.Rectangle
        or DrawMode.RoundedRectangle
        or DrawMode.Ellipse
        or DrawMode.Ring
        or DrawMode.Triangle
        or DrawMode.Pie;

    /// <summary>画布鼠标移动：更新橡皮线终点（最后一个已确认点到当前鼠标位置的临时连线）。
    /// 路径类且已起笔时，若鼠标接近起点（首顶点），置 DraftSnapToStart 并让橡皮线终点吸附到起点，
    /// 提示"点击此处闭环"。</summary>
    public void MoveDraw(double x, double y, bool shiftKey)
    {
        if (!IsDrawing || DraftPoints.Count == 0) return;
        _shiftKey = shiftKey;
        var clamped = ClampToCanvas(x, y);
        var anchor = DraftPoints[^1];
        if (IsShapeDrawMode(DrawMode))
        {
            // 形状模式：
            // - Ellipse/Ring：第二点=半径点。鼠标位置必须直接作为圆周点，
            //   不能经过 ConstrainPoint（AxisSnapEnabled=true 时会投影到水平/竖直轴线），
            //   否则预览圆的半径被算成 |dx| 或 |dy|，鼠标不在圆弧上。
            // - 其它形状（Rect/RoundedRect/Triangle）：Shift 锁等比（正方形/正三角形）
            bool isCircleLike = DrawMode is DrawMode.Ellipse or DrawMode.Ring or DrawMode.Pie;
            if (!isCircleLike && shiftKey)
                DraftPreviewPoint = Converters.GeometryMath.ConstrainShapeToSquare(anchor, clamped);
            else
                DraftPreviewPoint = clamped;
        }
        else
        {
            // 路径类：先判定起点吸附再走 ConstrainPoint（正交/极轴吸附）。
            UpdateDraftStartSnap(clamped);
            if (DraftSnapToStart)
            {
                // 吸附到起点：橡皮线终点 = 起点坐标，视觉闭环
                DraftPreviewPoint = DraftPoints[0];
            }
            else
            {
                DraftPreviewPoint = ConstrainPoint(anchor, clamped);
            }
        }
        UpdateDraftPathData();
        // 形状模式：刷新实时形状预览（中心 → 当前鼠标的轮廓）
        if (IsShapeDrawMode(DrawMode))
            UpdateShapePreview();
    }

    /// <summary>重置所有"绘制吸附"的可视化状态：橡皮线变色提示 + 对象追踪贯穿虚线。
    /// 用于草稿结束（Enter/Esc 提交或 Backspace 撤销最后一点），避免最后一点的吸附提示线
    /// 残留在画布上。IsObjectSnapTrackingX/Y 置 false 后 XAML 的两根 Line 立即 Collapsed。
    /// 同时清零 IsPathSnappingActive（轴对齐绿线），保证提交/取消后画布干净。</summary>
    private void ResetDraftSnapVisuals()
    {
        IsPathSnappingActive = false;
        IsObjectSnapTrackingX = false;
        IsObjectSnapTrackingY = false;
        ObjectSnapTrackingX = 0;
        ObjectSnapTrackingY = 0;
    }

    /// <summary>起点吸附判定：Polyline 绘制中，鼠标位置与首顶点距离 &lt; 阈值 → DraftSnapToStart=true。
    /// 仅当已画了 2 个以上点（有可闭合的边）时参与吸附，避免起笔点附近也会误触发高亮。
    /// 注意：闭合语义只对 Polyline 生效（BandPolyline 的 IsClosed 仍走开放式轮廓管道），
    /// 因此 BandPolyline 绘制时不启用本吸附，防止"点击起点却得到重合点"。</summary>
    private void UpdateDraftStartSnap(System.Windows.Point clamped)
    {
        if (DrawMode != DrawMode.Polyline)
        {
            DraftSnapToStart = false;
            return;
        }
        if (DraftPoints.Count < 2)
        {
            DraftSnapToStart = false;
            return;
        }
        var startPt = DraftPoints[0];
        double dx = clamped.X - startPt.X;
        double dy = clamped.Y - startPt.Y;
        var dist = System.Math.Sqrt(dx * dx + dy * dy);
        // 容差按屏幕绝对像素固定（CloseSnapThreshold=8px，见常量定义注释），换算成画布逻辑单位
        double scale = CanvasScale > 1e-9 ? CanvasScale : 1.0;
        DraftSnapToStart = dist < CloseSnapThreshold / scale;
    }

    /// <summary>Esc 键统一入口。按优先级逐级退出：
    ///   1) 格式刷待命 → 退出格式刷
    ///   2) 工具栏有选中（ActiveToolKey != null 且不在绘制中）→ 退出工具栏激活态（用户期望：点错工具后按 Esc 能取消高亮）
    ///   3) 正在连续绘制 → 放弃当前草稿（不生成新图元），同时退出工具栏激活态
    /// 否则若有选中则取消选中。Esc 只"取消/放弃",绝不提交、也绝不清空画布
    /// （清空画布仅通过工具栏"全部清空"按钮,避免误触清掉整张图）。</summary>
    [RelayCommand]
    public void CancelOrClear()
    {
        // 格式刷"应用待命" → 优先退出（Esc = 放弃，不应用；与 Office 行为一致）
        if (IsFormatPainterActive)
        {
            ClearFormatPainter();
            return;
        }

        // 工具栏工具激活态（用户点错工具后按 Esc 取消高亮 + 画布退出绘制分支）
        // 注意：SelectTool 同时设 ActiveToolKey + DrawMode，两个必须一起退，否则光高亮退了、画布还在按 DrawMode 走 → 还能画
        if (!IsDrawing && ActiveToolKey is not null)
        {
            ActiveToolKey = null;
            DrawMode = DrawMode.None;
            _userChangedDrawMode = true;
            return;
        }

        if (IsDrawing)
        {
            // 正在连续绘制 → 放弃本次草稿,不生成图元
            IsDrawing = false;
            DraftPoints.Clear();
            DraftPathData = string.Empty;
            DraftPreviewPoint = new System.Windows.Point(double.NaN, double.NaN);
            DraftShapeData = string.Empty;
            IsShapePreviewVisible = false;
            _draftCloseRequested = false;
            DraftSnapToStart = false;
            // 同步隐藏"对象追踪贯穿虚线"（最后一点吸附命中后停留在画布上的孤儿提示）
            ResetDraftSnapVisuals();
            DrawMode = DrawMode.None;
            ActiveToolKey = null; // Esc 放弃草稿 → 退出工具栏工具激活态（高亮复位）
            return;
        }

        if (SelectedItem is not null)
            SelectedItem = null; // 取消选中（OnSelectedItemChanged 内会同步清空多选/手柄）

        // 无绘制且无选中 → 什么都不做
    }

    /// <summary>按 Enter 时的处理:正在绘制则把当前草稿提交为正式图元(BandPolyline)。
    /// Enter 永远表示"完成/提交",绝不取消。</summary>
    [RelayCommand]
    public void FinishDraw()
    {
        if (!IsDrawing) return;
        EndDraw();
    }

    /// <summary>按 Backspace 时的处理:撤销最后加入的顶点。
    /// 起笔后按一下 = 取消整段;中途按一下 = 撤销一个点;只剩最后一个点时按 = 取消整段。</summary>
    [RelayCommand]
    public void UndoLastPoint()
    {
        if (!IsDrawing) return;
        if (DraftPoints.Count == 0) return;
        // 只剩一个点(起笔点)→ 等同取消整段,避免产生 0 段折线
        if (DraftPoints.Count == 1)
        {
            CancelOrClear();
            return;
        }
        DraftPoints.RemoveAt(DraftPoints.Count - 1);
        DraftPreviewPoint = new System.Windows.Point(double.NaN, double.NaN);
        UpdateDraftPathData();
        // 撤销最后一点：对象追踪命中可能指向上一点（P_{n-1}），提示线位置已无意义，重置干净
        ResetDraftSnapVisuals();
    }

    /// <summary>画布鼠标松开：提交草稿为正式图元（折线/直线/形状），清空草稿。</summary>
    public void EndDraw()
    {
        if (!IsDrawing) return;
        IsDrawing = false;

        // 形状类两段式：DraftPoints[0]=中心点，[1]=第二点
        // - Rectangle / RoundedRectangle / Triangle：第二点 = 对角点（W/H=2*|dx|,2*|dy|）
        // - Ellipse / Ring：第二点 = 圆周点（W=H=2*r，r=sqrt(dx²+dy²)）—— 始终正圆
        if (IsShapeDrawMode(DrawMode) && DraftPoints.Count >= 2)
        {
            var center = DraftPoints[0];
            var p2 = DraftPoints[1];
            double dx = p2.X - center.X;
            double dy = p2.Y - center.Y;
            bool isCircleLike = DrawMode is DrawMode.Ellipse or DrawMode.Ring or DrawMode.Pie;
            double w, h;
            if (isCircleLike)
            {
                // 半径点语义：r = sqrt(dx²+dy²)，W = H = 2r
                double r = System.Math.Sqrt(dx * dx + dy * dy);
                double size = System.Math.Max(2, r * 2);
                w = size;
                h = size;
            }
            else
            {
                w = System.Math.Max(2, System.Math.Abs(dx) * 2);
                h = System.Math.Max(2, System.Math.Abs(dy) * 2);
            }
            double x = center.X - w / 2;
            double y = center.Y - h / 2;
            // 无限画布：不做夹紧，图元落点自由

            var primitive = DrawMode switch
            {
                DrawMode.Rectangle         => L1Primitive.Rectangle,
                DrawMode.RoundedRectangle  => L1Primitive.RoundedRectangle,
                DrawMode.Ellipse           => L1Primitive.Ellipse,
                DrawMode.Ring              => L1Primitive.Ring,
                DrawMode.Triangle          => L1Primitive.Triangle,
                DrawMode.Pie               => L1Primitive.Pie,
                _                          => L1Primitive.Rectangle,
            };

            var item = PrimitiveItem.Create(primitive, x, y, w, h);
            // 圆环默认内径 = 外径 1/4（与 AddL1 拖入语义一致）
            if (primitive == L1Primitive.Ring)
                item.InnerRadius = System.Math.Min(w, h) / 4;
            // 扇形：中心→第二点方向 = 对称中轴线（对称张角 60°）；第二点距中心的距离 = 半径（扇形的"鼻尖"）
            if (primitive == L1Primitive.Pie)
                item.StartAngle = AngleOf(center.X, center.Y, p2.X, p2.Y);
            item.ZIndex = Primitives.Count;
            Primitives.Add(item);
            ItemCount = Primitives.Count;
            SelectedItem = item;

            DraftPoints.Clear();
            DraftPathData = string.Empty;
            DraftPreviewPoint = new System.Windows.Point(double.NaN, double.NaN);
            DraftShapeData = string.Empty;
            IsShapePreviewVisible = false;
            // 形状提交：擦除对象追踪提示线（如果路径类被走过，状态可能残留）
            ResetDraftSnapVisuals();
            DrawMode = DrawMode.None;
            ActiveToolKey = null; // 形状提交 → 退出工具栏工具激活态（高亮复位）
            return;
        }

        if (DraftPoints.Count >= 2)
        {
            // 计算包围盒 → 创建 PrimitiveItem，X/Y/Width/Height 对齐 bbox
            double minX = DraftPoints[0].X, minY = DraftPoints[0].Y;
            double maxX = minX, maxY = minY;
            foreach (var p in DraftPoints)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            double w = System.Math.Max(2, maxX - minX);
            double h = System.Math.Max(2, maxY - minY);
            // 把所有点平移到 (0,0) 局部坐标，方便 PathData 写入
            var shifted = new System.Collections.Generic.List<System.Windows.Point>(DraftPoints.Count);
            foreach (var p in DraftPoints)
                shifted.Add(new System.Windows.Point(p.X - minX, p.Y - minY));

            var pathData = Converters.GeometryMath.BuildPathData(shifted);
            // 画线工具提交：根据 DrawMode 决定目标 primitive 类型。
            //   DrawMode.BandPolyline → BandPolyline（带带宽的连贯图形）
            //   DrawMode.Polyline     → Polyline（无带宽的兄弟类型，纯描边骨架）
            //   DrawMode.Line         → 暂未实现，预留
            var primitive = DrawMode switch
            {
                DrawMode.Line          => L1Primitive.Line,
                DrawMode.Polyline      => L1Primitive.Polyline,
                DrawMode.BandPolyline  => L1Primitive.BandPolyline,
                _                      => L1Primitive.BandPolyline,
            };

            var item = PrimitiveItem.Create(primitive, minX, minY, w, h);
            item.PathData = pathData;
            // 画线工具产物的 LineAxis：统一设为 Free（精确的折线/直线由 Path 渲染，
            // 模板里的 Line 几何只在工具栏拖入的 Line 上用，避免重叠显示两条）。
            item.LineAxis = LineAxisMode.Free;
            if (primitive == L1Primitive.BandPolyline)
            {
                item.BandWidth = 6; // 默认带宽 6（与 LinePath 默认对齐）
                item.ZIndex = Primitives.Count;
                // 初始化 VertexBandWidths（每顶点一个 VertexBandWidth，顶点数 = 段数 + 1）
                // 首顶点 IsStart、末顶点 IsEnd 自动标记；带宽默认 6
                item.SyncVertexBandWidths(shifted.Count, item.BandWidth);
            }
            else if (primitive == L1Primitive.Polyline)
            {
                // Polyline 不带带宽：BandWidth 保持 0、VertexBandWidths 留空。
                item.BandWidth = 0;
                item.ZIndex = Primitives.Count;
                // 起点吸附点击 → 绘制的折线首尾闭合（骨架点直连单环，可填充）
                if (_draftCloseRequested)
                    item.IsClosed = true;
            }
            else if (primitive == L1Primitive.Line)
            {
                // Line 是单段直线，无带宽概念（LineGeometry 直接画）；BandWidth=0、VertexBandWidths 留空。
                item.BandWidth = 0;
                item.ZIndex = Primitives.Count;
            }
            Primitives.Add(item);
            ItemCount = Primitives.Count;
            SelectedItem = item;
        }

        DraftPoints.Clear();
        DraftPathData = string.Empty;
        DraftPreviewPoint = new System.Windows.Point(double.NaN, double.NaN);
        // 起点吸附闭合请求：本次提交已消费（Polyline 分支读 IsClosed），复位等待下次绘制
        _draftCloseRequested = false;
        DraftSnapToStart = false;
        // 路径提交：擦除对象追踪提示线（最后一点吸附命中可能把 IsObjectSnapTrackingX/Y 留在 true）
        ResetDraftSnapVisuals();
        // 画完自动退出 画线模式，避免后续点击画布又加点
        DrawMode = DrawMode.None;
        ActiveToolKey = null; // 路径类（折线）提交 → 退出工具栏工具激活态（高亮复位）
    }

    /// <summary>起点吸附闭合判定阈值（画布逻辑坐标）。绘制吸附要让用户"看得到"，阈值取 8.0，
    /// 比 View 手柄拖拽吸附（CloseSnapThreshold=3.0）更宽松，鼠标移到起点附近即触发绿色高亮。</summary>
    public const double CloseSnapThreshold = 8.0;

    /// <summary>当前 Shift 键状态（由 BeginDraw/UpdateDraw 调用方传入）。</summary>
    private bool _shiftKey;

    /// <summary>本次绘制的折线是否在"起点吸附"命中时点击 → 提交时闭合。
    /// 由 ClickDraw 在吸附命中时置 true，EndDraw 里消费并复位。仅 Polyline 闭合语义。</summary>
    private bool _draftCloseRequested;

    /// <summary>绘制路径模式下的点吸附决策（CAD Object Snap Tracking + Polar Tracking）：
    /// 优先级（先到先得）：
    /// 1) Object Snap Tracking：与任一已画点 P_i 的 X 或 Y 同坐标（在 Xt/Yt 容差内）
    ///    → 投影到 (P_i.X, target.Y) 或 (target.X, P_i.Y)，用于"和已画点对齐"，
    ///    XAML 同步贯穿虚线提示。X、Y 可同时命中。
    /// 2) AxisSnapEnabled=true（顶部开关开）：强制水平/竖直（Shift 切水平）
    /// 3) AxisSnapEnabled=false（默认）：Polar Tracking 风格——
    ///    当前段方向与水平/竖直夹角 ≤ 5° 则吸附成水平/竖直线
    /// 4) 自由落点（return target）
    ///
    /// 副作用：
    /// - IsPathSnappingActive：轴对齐激活（XAML 橡皮线变绿）
    /// - IsObjectSnapTrackingX / Y + ObjectSnapTrackingX / Y：对象追踪激活（XAML 贯穿虚线）
    /// 形状模式（Ellipse/Ring）不调用本方法，避免被投影到轴线上。</summary>
    private System.Windows.Point ConstrainPoint(System.Windows.Point anchor, System.Windows.Point target)
    {
        // 默认未吸附（让 XAML 走非绿色 Stroke / 隐藏贯穿虚线）
        IsPathSnappingActive = false;
        IsObjectSnapTrackingX = false;
        IsObjectSnapTrackingY = false;

        // ── 1. 共线吸附（统一原 Object Snap Tracking + Polar 角度吸附为同一管道）：
        //    扫描全部已画点（含最后一点 anchor）的 X 竖直线 / Y 水平线基准，用固定屏幕像素容差判定：
        //    换算成画布逻辑单位 = snapTolPx / CanvasScale，缩放越大逻辑容差越小（精细操作不随缩放漂移）。
        //    - 命中某已画点的 X/Y 基准线 → 锁定该坐标 + 橙虚线贯穿提示（对象追踪）；
        //    - 命中的基准线来自 anchor（当前段起点）→ 段锁定竖直/水平（IsPathSnappingActive=true，
        //      橡皮线变绿，即原"Polar 角度≤5° 吸附"的等价视觉）。
        //    距离守卫：鼠标距 anchor < snapTol 时 anchor 不参与吸附，避免刚起笔就把橡皮线钉死在起点上。
        const double snapTolPx = 6.0;
        double scale = CanvasScale > 1e-9 ? CanvasScale : 1.0;
        double snapTol = snapTolPx / scale;

        if (DraftPoints.Count >= 1)  // 至少已确认 1 个点（含 anchor；画第二段时只有 anchor 一个基准也生效）
        {
            double dxa = target.X - anchor.X;
            double dya = target.Y - anchor.Y;
            double distToAnchor = System.Math.Sqrt(dxa * dxa + dya * dya);
            int anchorIndex = DraftPoints.Count - 1;

            double bestDx = snapTol, bestDy = snapTol;
            int hitX = -1, hitY = -1;
            for (int i = 0; i <= anchorIndex; i++)
            {
                // 距离守卫：鼠标贴着 anchor 时不吸附（否则起点附近的误差会把橡皮线钉死在起点）
                if (i == anchorIndex && distToAnchor < snapTol) continue;
                double dxi = System.Math.Abs(target.X - DraftPoints[i].X);
                double dyi = System.Math.Abs(target.Y - DraftPoints[i].Y);
                if (dxi < bestDx) { bestDx = dxi; hitX = i; }
                if (dyi < bestDy) { bestDy = dyi; hitY = i; }
            }
            double? snappedX = null, snappedY = null;
            if (hitX >= 0) { snappedX = DraftPoints[hitX].X; ObjectSnapTrackingX = snappedX.Value; IsObjectSnapTrackingX = true; }
            if (hitY >= 0) { snappedY = DraftPoints[hitY].Y; ObjectSnapTrackingY = snappedY.Value; IsObjectSnapTrackingY = true; }
            // 至少命中一个 → 应用吸附；都没有命中才走下一步
            if (snappedX.HasValue || snappedY.HasValue)
            {
                // 基准线来自 anchor → 段锁定竖直/水平（绿色）；否则仅坐标对齐、方向仍自由（橙色，CAD 对象追踪语义）
                if (hitX == anchorIndex || hitY == anchorIndex) IsPathSnappingActive = true;
                return new System.Windows.Point(
                    snappedX ?? target.X,
                    snappedY ?? target.Y);
            }
        }

        // ── 2. 顶部开关开：强制水平/竖直（沿用旧语义，Shift 切水平）
        if (AxisSnapEnabled)
        {
            IsPathSnappingActive = true;
            return _shiftKey
                ? new System.Windows.Point(target.X, anchor.Y)
                : new System.Windows.Point(anchor.X, target.Y);
        }

        return target;
    }

    /// <summary>无限画布下的坐标透传：CAD 模型空间无边界，图元可画到任意位置（含负坐标）。
    /// 保留此方法避免入侵绘图调用点，坐标不过任何钳制。</summary>
    private System.Windows.Point ClampToCanvas(double x, double y)
        => new(x, y);

    /// <summary>刷新 PathData 绑定（草稿实时预览，包含已确认点 + 橡皮线到当前鼠标位置）。</summary>
    private void UpdateDraftPathData()
    {
        // 合并已确认点 + 橡皮线终点
        var combined = new System.Collections.Generic.List<System.Windows.Point>(DraftPoints);
        if (IsDrawing
            && !double.IsNaN(DraftPreviewPoint.X)
            && !double.IsNaN(DraftPreviewPoint.Y)
            && DraftPoints.Count > 0)
        {
            combined.Add(DraftPreviewPoint);
        }
        DraftPathData = Converters.GeometryMath.BuildPathData(combined);
    }

    /// <summary>形状绘制实时预览：依据 DraftPoints[0]=中心点 + DraftPreviewPoint=当前鼠标位置，
    /// 生成对应形状的 PathGeometry 字符串（Rectangle/RoundedRectangle/Ellipse/Ring/Triangle）。
    /// 阶段 3：让用户在第 2 次点击前就能看到即将生成的形状轮廓。
    /// 坐标系：画布逻辑坐标（与 PrimitiveItem.X/Y 一致），单位 px。</summary>
    private void UpdateShapePreview()
    {
        if (!IsShapeDrawMode(DrawMode) || DraftPoints.Count == 0)
        {
            DraftShapeData = string.Empty;
            IsShapePreviewVisible = false;
            return;
        }
        var center = DraftPoints[0];
        var p = DraftPreviewPoint;
        // 避免 NaN
        if (double.IsNaN(p.X) || double.IsNaN(p.Y))
        {
            DraftShapeData = string.Empty;
            IsShapePreviewVisible = false;
            return;
        }
        double dx = p.X - center.X;
        double dy = p.Y - center.Y;
        // Ellipse/Ring 用半径点语义（始终正圆）；其它形状用对角点语义（矩形包围盒）
        bool isCircleLike = DrawMode is DrawMode.Ellipse or DrawMode.Ring or DrawMode.Pie;
        double halfW, halfH;
        if (isCircleLike)
        {
            double r = System.Math.Sqrt(dx * dx + dy * dy);
            double halfR = System.Math.Max(1, r);
            halfW = halfR;
            halfH = halfR;
        }
        else
        {
            halfW = System.Math.Max(1, System.Math.Abs(dx));
            halfH = System.Math.Max(1, System.Math.Abs(dy));
        }
        // 矩形包围盒
        double x = center.X - halfW;
        double y = center.Y - halfH;
        double w = halfW * 2;
        double h = halfH * 2;

        var sb = new System.Text.StringBuilder();
        switch (DrawMode)
        {
            case DrawMode.Rectangle:
                // M x,y H x+w V y+h H x Z
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "M {0:0.##},{1:0.##} H {2:0.##} V {3:0.##} H {0:0.##} Z", x, y, x + w, y + h);
                break;

            case DrawMode.RoundedRectangle:
                // 默认圆角半径 = min(w,h)/8（与图元默认 CornerRadius 接近，避免预览与提交视觉差）
                {
                    double r = System.Math.Min(w, h) / 8;
                    r = System.Math.Min(r, System.Math.Min(w, h) / 2);
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "M {0:0.##},{1:0.##} ", x + r, y);
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "H {0:0.##} ", x + w - r);
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "A {0:0.##},{0:0.##} 0 0 1 {1:0.##},{2:0.##} ", r, x + w, y + r);
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "V {0:0.##} ", y + h - r);
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "A {0:0.##},{0:0.##} 0 0 1 {1:0.##},{2:0.##} ", r, x + w - r, y + h);
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "H {0:0.##} ", x + r);
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "A {0:0.##},{0:0.##} 0 0 1 {1:0.##},{2:0.##} ", r, x, y + h - r);
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "V {0:0.##} ", y + r);
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "A {0:0.##},{0:0.##} 0 0 1 {1:0.##},{2:0.##} Z ", r, x + r, y);
                }
                break;

            case DrawMode.Ellipse:
                // 椭圆：M cx-rx,cy A rx,ry 0 1 0 cx+rx,cy A rx,ry 0 1 0 cx-rx,cy Z
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "M {0:0.##},{1:0.##} ", center.X - halfW, center.Y);
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "A {0:0.##},{1:0.##} 0 1 0 {2:0.##},{3:0.##} ", halfW, halfH, center.X + halfW, center.Y);
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "A {0:0.##},{1:0.##} 0 1 0 {2:0.##},{3:0.##} Z ", halfW, halfH, center.X - halfW, center.Y);
                break;

            case DrawMode.Ring:
                // 圆环预览：WPF Path 的 FillRule 属性不在 presentation 命名空间，
                // 字符串 Data 绑定下默认 Nonzero 无法做减法镂空。
                // 折中：预览只画外圈椭圆（半透明蓝填充），让用户看到外径；
                // 真实镂空由图元模板渲染。半透明暗示"中间会镂空"。
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "M {0:0.##},{1:0.##} ", center.X - halfW, center.Y);
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "A {0:0.##},{1:0.##} 0 1 0 {2:0.##},{3:0.##} ", halfW, halfH, center.X + halfW, center.Y);
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "A {0:0.##},{1:0.##} 0 1 0 {2:0.##},{3:0.##} Z ", halfW, halfH, center.X - halfW, center.Y);
                break;

            case DrawMode.Triangle:
                // 三角形：用包围盒三个关键点（上中、左下、右下），与"中心点+对角点"矩形包围盒几何一致。
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "M {0:0.##},{1:0.##} ", center.X, y);            // 上中
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "L {0:0.##},{1:0.##} ", x, y + h);                // 左下
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "L {0:0.##},{1:0.##} Z ", x + w, y + h);          // 右下
                break;

            case DrawMode.Pie:
                // 扇形预览：半径 = 圆心到鼠标距离；对称中轴 = 圆心指向鼠标方向（第二点方向 = 对称中轴线），
                // 对称张角默认 60°（与提交后的成品几何一致，鼻尖朝向鼠标）。
                sb.Append(Converters.PieGeometryBuilder.BuildPath(center.X, center.Y,
                    halfW, AngleOf(center.X, center.Y, p.X, p.Y), 60));
                break;
        }

        DraftShapeData = sb.ToString();
        IsShapePreviewVisible = true;
    }

    /// <summary>旧版 AddL2 已移除（L2Composite 枚举随 FanBlade 一并废弃）。
    /// 仅 AddL1 一个工厂入口——所有图元都通过 L1Primitive 枚举 + Create 静态工厂创建。</summary>
}